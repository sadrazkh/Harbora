using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Verification;

/// <summary>
/// See <see cref="IAppVerifier"/>. Reads what is really there, in the order a person would ask: is it
/// running, has it stayed up, do its databases answer, does its address work, is its log complaining.
///
/// <para>
/// One check's trouble is that check's result and never the run's: a v1 node that cannot run a one-off
/// says so on the database check, and the address is still asked. Only the caller's own cancellation
/// propagates.
/// </para>
///
/// <para>
/// Every read here ignores the workspace filter and is pinned to this app instead, because the launch
/// tick has no session and a filtered read there would come back empty and look like "nothing wrong".
/// </para>
/// </summary>
public sealed class AppVerifier(
    HarboraDbContext db,
    IServerEngineFactory engines,
    IManagedServiceEngine services,
    IUptimeProbe uptime,
    ISecretProtector protector,
    ISecretRedactor redactor,
    ISystemClock clock,
    ILogger<AppVerifier> logger) : IAppVerifier
{
    private const string RunningKey = "running";
    private const string RestartsKey = "restarts";
    private const string UrlKey = "url";
    private const string LogsKey = "logs";
    private const string ServiceKeyPrefix = "service:";

    /// <summary>The label Compose containers carry; a stack's service names exist nowhere else after the deploy.</summary>
    private const string ComposeServiceLabel = "harbora.compose.service";

    /// <summary>
    /// What the probe is told to expect. Anything else comes back Down with the real status on it, which
    /// is what the root rule below reads, so a 404 there is judged here rather than by the probe.
    /// </summary>
    private const int OrdinaryAnswer = 200;

    private const string RunsAndExits =
        "It runs and exits by design, so there is no container to find between runs.";

    private const string RunsAndExitsFa =
        "طوری ساخته شده که اجرا شود و تمام شود، پس بین اجراها کانتینری برای پیدا کردن نیست.";

    private const int LogLinesRead = 200;
    private const int TailLinesQuoted = 8;
    private const int MaxTailChars = 600;

    /// <summary>
    /// A container that crashes on its first query looks fine until then, so a fresh start is not yet
    /// evidence of having stayed up.
    /// </summary>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(30);

    /// <summary>Each one can pull the engine's image before it runs, so this is generous. Past it, a hung daemon.</summary>
    internal TimeSpan ConnectionTestTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>All of an app's databases together, so ten of them behind a hung daemon cannot hold a request for forty minutes.</summary>
    internal TimeSpan ServicesBudget { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>One question to a daemon: a listing, a lifecycle, a log tail.</summary>
    internal TimeSpan EngineTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>What one request to the app's own address may take. The probe enforces it itself.</summary>
    internal TimeSpan AddressTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// By name, which is what <see cref="AppCheckStatus"/> promises. The stored shape is only ever read
    /// back by <see cref="ReadChecks"/>.
    /// </summary>
    private static readonly JsonSerializerOptions StoredShape = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>What one run knows before it asks anything.</summary>
    private sealed record Run(
        App App,
        Deployment? Deployment,
        IReadOnlyList<AttachedService> Services,
        AppVerificationReport? Previous,
        DateTimeOffset GiveUpAt,
        IReadOnlyList<string> Secrets)
    {
        public string DeploymentsPath => $"/apps/{App.Id}/deployments";
        public string LogsPath => $"/apps/{App.Id}/logs";
        public string OverviewPath => $"/apps/details/{App.Id}";
    }

    /// <summary>
    /// One database as this app sees it. Two logical databases on one instance are two attachments and
    /// one check: the probe logs in with the instance's own credentials, so it cannot tell them apart,
    /// and a second container would learn nothing.
    /// </summary>
    private sealed record AttachedService(ManagedService Service, bool HasUnpublishedChanges);

    /// <summary>The wording a check falls back to when it could not be asked at all.</summary>
    private sealed record Asking(string Key, string Title, string TitleFa);

    public async Task<AppVerificationReport> RunAsync(
        Guid appId, AppVerificationReport? previous, DateTimeOffset giveUpAt, CancellationToken ct)
    {
        var started = clock.UtcNow;
        try
        {
            var app = await LoadAppAsync(appId, ct);
            if (app is null) return Gone(appId, previous, started);

            var deployment = await ServingDeploymentAsync(app, ct);
            var attached = await AttachedServicesAsync(app, ct);

            // "The last report of this run": one from before a redeploy, or another app's, is not. The
            // launch tick reads it back with GetLatestAsync, so its first tick can be handed a report
            // that predates the launch, and a pass carried over from there would be a claim nobody
            // re-checked.
            var sameRun = previous is { } p && p.AppId == appId && p.DeploymentId == deployment?.Id ? p : null;

            var run = new Run(app, deployment, attached, sameRun, giveUpAt, SecretsOf(app, attached));

            var checks = new List<AppCheck>
            {
                await RunningAsync(run, ct),
                await RestartsAsync(run, ct)
            };
            checks.AddRange(await ServicesAsync(run, ct));
            if (ServicePlan.HasPublicTraffic(app.Kind)) checks.Add(await AddressAsync(run, ct));
            checks.Add(await LogsAsync(run, ct));

            var report = new AppVerificationReport(appId, deployment?.Id, sameRun?.StartedAt ?? started, null, checks);
            report = report with { CompletedAt = report.IsComplete ? clock.UtcNow : null };

            await StoreAsync(app, report, ct);
            return report;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Past every per-check guard, so this is the panel's own database rather than anything
            // about the app. It still comes back as an answer: the launch tick moves on one report at
            // a time, and a throw here would look like the launch failing.
            logger.LogError(ex, "The checks for app {AppId} could not run.", appId);
            return new AppVerificationReport(appId, previous?.DeploymentId, previous?.StartedAt ?? started, clock.UtcNow,
            [
                new AppCheck(RunningKey, AppCheckStatus.Skipped,
                    "The checks could not run", "بررسی‌ها اجرا نشدند",
                    "The panel itself had a problem, so this says nothing about the app. Try again in a moment.",
                    "خود پنل مشکلی داشت، پس این نتیجه چیزی دربارهٔ اپ نمی‌گوید. کمی بعد دوباره امتحان کنید.")
            ]);
        }
    }

    public async Task<AppVerificationReport?> GetLatestAsync(Guid appId, CancellationToken ct)
    {
        var row = await db.AppVerifications.AsNoTracking().FirstOrDefaultAsync(v => v.AppId == appId, ct);
        if (row is null) return null;

        var checks = ReadChecks(row.ChecksJson);
        if (checks.Count == 0)
        {
            // A report with no checks reads as complete and without failures, and the launch page would
            // take that as good news. "We have nothing readable" is the same as never having asked.
            logger.LogWarning("The stored checks for app {AppId} could not be read.", appId);
            return null;
        }

        return new AppVerificationReport(row.AppId, row.DeploymentId, row.StartedAt, row.CompletedAt, checks);
    }

    // ---- is it running ----

    private async Task<AppCheck> RunningAsync(Run run, CancellationToken ct)
    {
        var app = run.App;

        if (!ServicePlan.IsLongRunning(app.Kind))
            return Skipped(RunningKey,
                "Not meant to keep running", "قرار نیست همیشه روشن بماند",
                RunsAndExits, RunsAndExitsFa);

        if (run.Deployment is not { } deployment)
            return new AppCheck(RunningKey, AppCheckStatus.Failed,
                "Nothing is deployed yet", "هنوز چیزی مستقر نشده",
                "This app has no active deployment, so nothing can be running.",
                "این اپ استقرار فعالی ندارد، پس چیزی در حال اجرا نیست.",
                "Deploy it, or see why the last deployment failed.",
                "آن را مستقر کنید، یا ببینید استقرار قبلی چرا شکست خورد.",
                run.DeploymentsPath);

        return await AskAsync(new Asking(RunningKey, "Could not tell whether it is running", "نشد فهمید در حال اجراست یا نه"),
            EngineTimeout, ct, async token =>
            {
                var docker = await engines.ResolveAsync(app.ServerId, token);
                var names = await ContainerNamesAsync(docker, app, deployment, token);
                var listed = await docker.ListContainersAsync(DeploymentPlanning.AppLabel, token);

                // The label alone is not enough to say it is this one: containers are listed host-wide,
                // and every name below carries the workspace and the deployment number.
                var mine = listed.Where(c => names.Contains(c.Name)).ToList();
                var running = mine.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));

                if (names.Count > 0 && running == names.Count)
                    return new AppCheck(RunningKey, AppCheckStatus.Passed,
                        "It is running", "در حال اجراست",
                        names.Count > 1 ? $"All {names.Count} of its containers are running." : null,
                        names.Count > 1 ? $"هر {names.Count} کانتینرش در حال اجرا هستند." : null);

                if (running > 0)
                    return new AppCheck(RunningKey, AppCheckStatus.Warning,
                        $"Only {running} of its {names.Count} containers are running",
                        $"فقط {running} کانتینر از {names.Count} کانتینرش در حال اجراست",
                        null, null,
                        "Read its log to see why the others stopped. Deploying again starts them all.",
                        "لاگ را بخوانید تا ببینید بقیه چرا ایستاده‌اند. استقرار دوباره همه را راه می‌اندازد.",
                        run.LogsPath);

                var stopped = app.Status == AppStatus.Stopped;
                var state = mine.FirstOrDefault();
                return new AppCheck(RunningKey, AppCheckStatus.Failed,
                    "It is not running", "در حال اجرا نیست",
                    state is null
                        ? "Its container could not be found on the server."
                        : $"Its container says: {state.Status}.",
                    state is null
                        ? "کانتینرش روی سرور پیدا نشد."
                        : $"وضعیت کانتینرش: {state.Status}.",
                    stopped ? "It was stopped. Start it from this page." : "Read its log to see why it stopped, then deploy again.",
                    stopped ? "متوقف شده است. از همین صفحه آن را روشن کنید." : "لاگ را بخوانید تا ببینید چرا ایستاده، بعد دوباره مستقر کنید.",
                    run.LogsPath);
            });
    }

    // ---- has it stayed up ----

    private async Task<AppCheck> RestartsAsync(Run run, CancellationToken ct)
    {
        var app = run.App;

        const string notWatched = "Not watched for restarts", notWatchedFa = "ری‌استارت‌هایش بررسی نمی‌شود";

        if (!ServicePlan.IsLongRunning(app.Kind))
            return Skipped(RestartsKey, notWatched, notWatchedFa, RunsAndExits, RunsAndExitsFa);

        if (run.Deployment is not { } deployment)
            return Skipped(RestartsKey, notWatched, notWatchedFa,
                "Nothing is deployed, so there is nothing to watch.",
                "چیزی مستقر نشده، پس چیزی برای دیدن نیست.");

        const string unknown = "Could not tell whether it has stayed up", unknownFa = "نشد فهمید بالا مانده یا نه";

        return await AskAsync(new Asking(RestartsKey, unknown, unknownFa), EngineTimeout, ct, async token =>
        {
            var docker = await engines.ResolveAsync(app.ServerId, token);
            var names = await ContainerNamesAsync(docker, app, deployment, token);

            var figures = new List<(string Name, ContainerLifecycle? Lifecycle)>();
            foreach (var name in names)
                figures.Add((name, await docker.GetLifecycleAsync(name, token)));

            // Before the settling window, because a restart resets StartedAt: a crash loop's container
            // has always "just started", and asking the window first would call it too early to tell
            // until the give-up time.
            var restarts = figures.Sum(f => f.Lifecycle?.RestartCount ?? 0);
            if (restarts > 0)
            {
                var crashed = figures.First(f => f.Lifecycle?.RestartCount > 0).Name;
                var tail = await TryTailAsync(docker, crashed, run, token);
                return new AppCheck(RestartsKey, AppCheckStatus.Failed,
                    "It keeps crashing", "مدام از کار می‌افتد",
                    $"It has crashed and been restarted {restarts} time(s) since this deployment started."
                        + (tail is null ? "" : $"\nThe last lines of its log:\n{tail}"),
                    $"از شروع این استقرار {restarts} بار از کار افتاده و دوباره راه افتاده."
                        + (tail is null ? "" : $"\nآخرین خط‌های لاگش:\n{tail}"),
                    "Read its log: the reason is almost always in the last lines, usually a missing variable or a service it cannot reach.",
                    "لاگ را بخوانید: دلیلش تقریباً همیشه در خط‌های آخر است، معمولاً یک متغیر جاافتاده یا سرویسی که به آن نمی‌رسد.",
                    run.LogsPath);
            }

            // Null is "nobody answered". Zero would be a claim.
            if (figures.Count == 0 || figures.Any(f => f.Lifecycle?.RestartCount is null || f.Lifecycle.StartedAt is null))
                return Skipped(RestartsKey, unknown, unknownFa,
                    "This server did not say how often it has restarted.",
                    "این سرور نگفت چند بار ری‌استارت شده.");

            var newestStart = figures.Max(f => f.Lifecycle!.StartedAt!.Value);
            var settled = clock.UtcNow - newestStart >= SettleWindow;

            if (!settled && clock.UtcNow < run.GiveUpAt)
                return new AppCheck(RestartsKey, AppCheckStatus.Pending,
                    "Watching whether it stays up", "دارد می‌بیند بالا می‌ماند یا نه",
                    "It started moments ago, and a container that crashes on its first request looks fine until then.",
                    "همین چند لحظه پیش شروع شده، و کانتینری که روی اولین درخواست از کار می‌افتد تا آن موقع سالم به نظر می‌رسد.");

            return new AppCheck(RestartsKey, AppCheckStatus.Passed,
                "It has stayed up", "بالا مانده",
                settled
                    ? "It has not restarted since this deployment started."
                    : "It has not restarted so far, but it started less than half a minute ago.",
                settled
                    ? "از شروع این استقرار ری‌استارت نشده."
                    : "تا اینجا ری‌استارت نشده، ولی کمتر از نیم دقیقه است که شروع شده.");
        });
    }

    // ---- do its databases answer ----

    private async Task<IReadOnlyList<AppCheck>> ServicesAsync(Run run, CancellationToken ct)
    {
        var budget = Stopwatch.StartNew();
        var checks = new List<AppCheck>(run.Services.Count);

        foreach (var attached in run.Services)
        {
            var remaining = ServicesBudget - budget.Elapsed;
            var svc = attached.Service;
            var check = await AskAsync(
                new Asking(ServiceKey(svc), $"Database «{svc.Name}» was not checked", $"دیتابیس «{svc.Name}» بررسی نشد"),
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct,
                token => ServiceAsync(run, attached, token),
                timedOut: (
                    "The connection test did not finish in time, so this database was not checked. It may still be downloading its client: check again in a few minutes.",
                    "آزمایش اتصال به‌موقع تمام نشد، پس این دیتابیس بررسی نشد. شاید هنوز دارد کلاینتش را دانلود می‌کند؛ چند دقیقه بعد دوباره بررسی کنید."));

            checks.Add(attached.HasUnpublishedChanges ? WithUnpublishedChanges(check, svc, run) : check);
        }

        return checks;
    }

    private async Task<AppCheck> ServiceAsync(Run run, AttachedService attached, CancellationToken token)
    {
        var app = run.App;
        var svc = attached.Service;
        var key = ServiceKey(svc);
        var servicePath = $"/databases/{svc.Id}";

        if (svc.Status == ServiceStatus.Provisioning)
        {
            if (clock.UtcNow < run.GiveUpAt)
                return new AppCheck(key, AppCheckStatus.Pending,
                    $"Database «{svc.Name}» is still being set up", $"دیتابیس «{svc.Name}» هنوز دارد آماده می‌شود",
                    LinkPath: servicePath);

            return new AppCheck(key, AppCheckStatus.Failed,
                $"Database «{svc.Name}» was never ready", $"دیتابیس «{svc.Name}» آماده نشد",
                "It was still being set up when the checks stopped waiting for it.",
                "وقتی بررسی‌ها دیگر منتظر نماندند، هنوز داشت آماده می‌شد.",
                "Open its page to see how far it got.",
                "صفحه‌اش را باز کنید تا ببینید تا کجا رسیده.",
                servicePath);
        }

        if (svc.Status != ServiceStatus.Running)
        {
            var why = Clean(svc.ErrorMessage, run);
            return new AppCheck(key, AppCheckStatus.Failed,
                $"Database «{svc.Name}» is not running", $"دیتابیس «{svc.Name}» در حال اجرا نیست",
                why.Length > 0 ? why : null,
                why.Length > 0 ? why : null,
                "Open its page and start it, or read why it failed.",
                "صفحه‌اش را باز کنید و روشنش کنید، یا ببینید چرا شکست خورده.",
                servicePath);
        }

        // Both of these would otherwise be a false pass, and they are the reason this feature exists: the
        // probe runs on the database's server and on the database's own environment network. It answers
        // happily there while this app, somewhere else, is holding an address that cannot resolve.
        if (svc.ServerId != app.ServerId)
            return new AppCheck(key, AppCheckStatus.Failed,
                $"Database «{svc.Name}» is on a different server", $"دیتابیس «{svc.Name}» روی سرور دیگری است",
                "An app and its database have to share a server: private networks do not reach across servers, so the address this app was given cannot work.",
                "اپ و دیتابیسش باید روی یک سرور باشند: شبکهٔ خصوصی از یک سرور به سرور دیگر نمی‌رسد، پس آدرسی که به این اپ داده شده کار نمی‌کند.",
                "Create the database on this app's server, or move the app to the database's server.",
                "دیتابیس را روی سرور همین اپ بسازید، یا اپ را به سرور دیتابیس ببرید.",
                servicePath);

        // Not healed, unlike the same-environment case below: joining one environment's database to
        // another's network would quietly undo the isolation an environment exists for, and the probe
        // could not prove it worked anyway, since it would still run on the database's own network.
        var wiring = NetworkWiring.CanAttach(app.EnvironmentId, svc.EnvironmentId);
        if (!wiring.Allowed)
            return new AppCheck(key, AppCheckStatus.Failed,
                $"Database «{svc.Name}» is in another environment", $"دیتابیس «{svc.Name}» در محیط دیگری است",
                wiring.Reason,
                "این دو در محیط‌های متفاوتی هستند و هر محیط شبکهٔ خصوصی جداگانه‌ای است؛ اپ نام میزبانی می‌گیرد که نمی‌تواند آن را پیدا کند.",
                "Move the app or the database so they share one environment.",
                "اپ یا دیتابیس را جابه‌جا کنید تا هر دو در یک محیط باشند.",
                servicePath);

        if (ConnectionProbe.WhyUnsupported(svc.Type) is { } unsupported)
            return new AppCheck(key, AppCheckStatus.Skipped,
                $"Database «{svc.Name}» was not tested", $"دیتابیس «{svc.Name}» آزمایش نشد",
                unsupported, ConnectionProbe.InPersian(unsupported), LinkPath: servicePath);

        // Below the status checks on purpose: only the container is what is worth saving. A database
        // that stopped between two ticks must not go on reading as a pass.
        if (run.Previous?.Checks.FirstOrDefault(c => c.Key == key) is { } earlier
            && (earlier.Status == AppCheckStatus.Passed
                || (earlier.Status == AppCheckStatus.Warning && attached.HasUnpublishedChanges)))
            return earlier;

        var failure = await TestConnectionAsync(svc.Id, token);
        var healed = false;

        if (failure is not null)
        {
            await ConnectToAppNetworkAsync(app, svc, token);

            // ConnectNetworkAsync swallows its own failure, so this second answer is the only proof.
            failure = await TestConnectionAsync(svc.Id, token);
            healed = failure is null;
        }

        if (failure is not null)
        {
            var english = Clean(failure, run);
            return new AppCheck(key, AppCheckStatus.Failed,
                $"Database «{svc.Name}» does not answer on this app's network",
                $"دیتابیس «{svc.Name}» روی شبکهٔ این اپ جواب نمی‌دهد",
                english,
                Clean(ConnectionProbe.InPersian(failure), run),
                "Open its page and check that it is running. Its Test connection button repeats this test.",
                "صفحه‌اش را باز کنید و ببینید روشن است یا نه. دکمهٔ آزمایش اتصال همان‌جا همین آزمایش را تکرار می‌کند.",
                servicePath);
        }

        // Exactly what was proved and no more. The probe logs in with the instance's own credentials, so
        // it says the database is reachable from here; whether the app's code reads the right variable
        // is what the log check is for.
        const string proved =
            "Harbora logged in with the database's own username and password, from this app's private network, and it answered. " +
            "That shows it can be reached from here, not that the app itself has connected.";
        const string provedFa =
            "هاربورا با نام کاربری و رمز خود دیتابیس، از شبکهٔ خصوصی این اپ وصل شد و جواب گرفت. " +
            "این نشان می‌دهد از اینجا در دسترس است، نه اینکه خود اپ حتماً به آن وصل شده باشد.";

        return new AppCheck(key, AppCheckStatus.Passed,
            $"Database «{svc.Name}» answers on this app's network",
            $"دیتابیس «{svc.Name}» روی شبکهٔ این اپ جواب می‌دهد",
            healed
                ? "It was not on this app's private network, so Harbora added it there. Nothing was restarted. " + proved
                : proved,
            healed
                ? "روی شبکهٔ خصوصی این اپ نبود، پس هاربورا آن را به این شبکه وصل کرد. چیزی ری‌استارت نشد. " + provedFa
                : provedFa);
    }

    private async Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(ConnectionTestTimeout);
        return await services.TestConnectionAsync(serviceId, bounded.Token);
    }

    /// <summary>
    /// Additive: it adds one network to a running container and restarts nothing, so the other apps on
    /// this database do not notice. It cannot be the reason a check fails: whatever goes wrong here, the
    /// test that follows is what decides.
    /// </summary>
    private async Task ConnectToAppNetworkAsync(App app, ManagedService svc, CancellationToken token)
    {
        try
        {
            var network = await EnvironmentNetworkResolver.ForAsync(db, app.EnvironmentId, token);
            var docker = await engines.ResolveAsync(svc.ServerId, token);
            await docker.ConnectNetworkAsync(svc.ContainerName, network, token);
            logger.LogInformation(
                "Connected database {Container} to network {Network} while checking app {AppId}.",
                svc.ContainerName, network, app.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not connect database {Container} to app {AppId}'s network.", svc.ContainerName, app.Id);
        }
    }

    /// <summary>
    /// The container is running with the variables it was given when it started, so a database that
    /// answers, or that could not be tested, is not yet a database this app has. A failure says more
    /// than this does and stays what it is, and a wait is still a wait.
    /// </summary>
    private static AppCheck WithUnpublishedChanges(AppCheck check, ManagedService svc, Run run)
    {
        if (check.Status is not (AppCheckStatus.Passed or AppCheckStatus.Skipped)) return check;

        const string stale = "The running container does not have this database's current variables yet.";
        const string staleFa = "کانتینر در حال اجرا هنوز متغیرهای فعلی این دیتابیس را ندارد.";

        return check with
        {
            Status = AppCheckStatus.Warning,
            Title = $"The app does not have database «{svc.Name}»'s details yet",
            TitleFa = $"اپ هنوز مشخصات دیتابیس «{svc.Name}» را ندارد",
            Detail = check.Detail is { Length: > 0 } d ? $"{stale} {d}" : stale,
            DetailFa = check.DetailFa is { Length: > 0 } f ? $"{staleFa} {f}" : staleFa,
            Fix = "Redeploy the app so it picks them up.",
            FixFa = "اپ را دوباره مستقر کنید تا آن‌ها را بگیرد.",
            LinkPath = run.DeploymentsPath
        };
    }

    // ---- does its address work from outside ----

    private async Task<AppCheck> AddressAsync(Run run, CancellationToken ct)
    {
        const string noAddress = "No address to check", noAddressFa = "آدرسی برای بررسی نیست";
        const string notAsked = "Could not check its address", notAskedFa = "نشد آدرسش را بررسی کرد";

        // A little past the probe's own bound, which it enforces itself: this is only for a probe that
        // has somehow not kept that promise.
        return await AskAsync(new Asking(UrlKey, notAsked, notAskedFa), AddressTimeout + TimeSpan.FromSeconds(5), ct, async token =>
        {
            var app = run.App;

            var domain = await db.Domains.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.AppId == app.Id)
                .OrderByDescending(d => d.IsPrimary)
                .FirstOrDefaultAsync(token);

            if (domain is null)
                return Skipped(UrlKey, noAddress, noAddressFa,
                    "This app has no domain yet, so there is nothing to ask from outside.",
                    "این اپ هنوز دامنه‌ای ندارد، پس چیزی برای پرسیدن از بیرون نیست.",
                    run.OverviewPath);

            if (run.Deployment is null)
                return Skipped(UrlKey, noAddress, noAddressFa,
                    "Nothing is deployed, so its address has nothing to serve.",
                    "چیزی مستقر نشده، پس آدرسش چیزی برای نشان دادن ندارد.");

            if (app.MaintenanceMode)
                return Skipped(UrlKey, notAsked, notAskedFa,
                    "It is in maintenance mode, so its address shows the maintenance page on purpose.",
                    "در حالت تعمیر و نگهداری است، پس آدرسش عمداً صفحهٔ تعمیرات را نشان می‌دهد.");

            // The scheme follows SslEnabled, as UptimeChecker's does: a domain with SSL turned off would
            // fail every https probe and blame the app for it.
            var url = new Uri($"{(domain.SslEnabled ? "https" : "http")}://{domain.Host}/");

            var answer = await uptime.ProbeAsync(url, OrdinaryAnswer, bodyContains: null, AddressTimeout, token);

            switch (answer.Outcome)
            {
                case ProbeOutcome.Up:
                    return AddressPassed(url, answer.HttpStatus ?? OrdinaryAnswer);

                case ProbeOutcome.CouldNotRun:
                    // A fault in the checker, not a fact about the app.
                    return Skipped(UrlKey, notAsked, notAskedFa, Clean(answer.Detail, run), Clean(answer.Detail, run));
            }

            if (answer.HttpStatus is { } status)
            {
                if (HealthProbeRule.Accepts(healthPath: null, status)) return AddressPassed(url, status);

                return new AppCheck(UrlKey, AppCheckStatus.Failed,
                    "Its address answers with an error", "آدرسش خطا برمی‌گرداند",
                    $"{url} answered {status}.",
                    $"{url} با کد {status} جواب داد.",
                    "The address reaches the app and the app is failing. Read its log.",
                    "آدرس به اپ می‌رسد و خود اپ خطا می‌دهد. لاگش را بخوانید.",
                    run.LogsPath);
            }

            // No answer at all. The first request is what makes Traefik ask for a certificate, and that
            // takes a while, so this is only a failure once the time to wait has run out.
            if (clock.UtcNow < run.GiveUpAt)
                return new AppCheck(UrlKey, AppCheckStatus.Pending,
                    "Waiting for its address to answer", "منتظر جواب آدرسش",
                    "No answer yet. A certificate is issued on the first request, and that takes a while.",
                    "هنوز جوابی نیامده. گواهی با اولین درخواست صادر می‌شود و کمی طول می‌کشد.");

            var detail = Clean(answer.Detail, run);
            return new AppCheck(UrlKey, AppCheckStatus.Failed,
                "Its address does not answer from outside", "آدرسش از بیرون جواب نمی‌دهد",
                detail, detail,
                "Check that the domain's DNS points at this server and that port 80 is open: without both, no certificate can be issued and nothing reaches the app.",
                "بررسی کنید DNS دامنه به همین سرور اشاره کند و پورت 80 باز باشد: بدون این دو، گواهی صادر نمی‌شود و چیزی به اپ نمی‌رسد.",
                run.OverviewPath);
        });
    }

    private static AppCheck AddressPassed(Uri url, int status)
    {
        // The same rule the deploy's health gate applies to the root: a 404 there says something is
        // serving, and only that, so the sentence says only that.
        var noRootPage = status >= 400;
        return new AppCheck(UrlKey, AppCheckStatus.Passed,
            "Its address works from outside", "آدرسش از بیرون کار می‌کند",
            noRootPage
                ? $"{url} answered {status}: there is no page at the root, but something is serving there."
                : $"{url} answered {status}.",
            noRootPage
                ? $"{url} با کد {status} جواب داد: صفحه‌ای در ریشه نیست، ولی چیزی آنجا جواب می‌دهد."
                : $"{url} با کد {status} جواب داد.");
    }

    // ---- is its log complaining ----

    private async Task<AppCheck> LogsAsync(Run run, CancellationToken ct)
    {
        var app = run.App;

        const string unread = "Could not read its log", unreadFa = "نشد لاگش را خواند";

        if (!ServicePlan.IsLongRunning(app.Kind))
            return Skipped(LogsKey, unread, unreadFa,
                "It runs and exits, so its output is in the history of each run rather than in a running container's log.",
                "اجرا می‌شود و تمام می‌شود، پس خروجی‌اش در تاریخچهٔ هر اجراست، نه در لاگ یک کانتینر روشن.");

        if (run.Deployment is not { } deployment)
            return Skipped(LogsKey, unread, unreadFa,
                "Nothing is deployed, so there is no log to read.",
                "چیزی مستقر نشده، پس لاگی برای خواندن نیست.");

        return await AskAsync(new Asking(LogsKey, unread, unreadFa), EngineTimeout, ct, async token =>
        {
            var docker = await engines.ResolveAsync(app.ServerId, token);
            var names = await ContainerNamesAsync(docker, app, deployment, token);

            var lines = new List<string>();
            foreach (var name in names)
                lines.AddRange((await docker.GetLogsAsync(name, LogLinesRead, token)).Split('\n'));

            var findings = AppLogDiagnosis.Find(lines);
            if (findings.Count == 0)
            {
                var wroteNothing = lines.All(string.IsNullOrWhiteSpace);
                return new AppCheck(LogsKey, AppCheckStatus.Passed,
                    "Nothing wrong in its log", "در لاگش مشکلی دیده نشد",
                    wroteNothing
                        ? "It has not written anything to its log yet."
                        : $"Its last {LogLinesRead} lines hold none of the failures Harbora recognises.",
                    wroteNothing
                        ? "هنوز چیزی در لاگش ننوشته."
                        : $"در {LogLinesRead} خط آخر لاگش هیچ‌کدام از خطاهایی که هاربورا می‌شناسد نیست.");
            }

            string Quote(AppLogFinding f, string template)
            {
                var fragment = Clean(f.Fragment, run);
                return fragment.Length == 0 ? "" : string.Format(template, fragment);
            }

            return new AppCheck(LogsKey, AppCheckStatus.Warning,
                "Its log points at a problem", "لاگش به یک مشکل اشاره می‌کند",
                string.Join("\n", findings.Select(f => f.Message + Quote(f, " In its log: \"{0}\""))),
                string.Join("\n", findings.Select(f => f.MessageFa + Quote(f, " در لاگش: «{0}»"))),
                "The whole log is on its Logs tab.",
                "همهٔ لاگ در زبانهٔ لاگ‌هاست.",
                run.LogsPath);
        });
    }

    // ---- what they share ----

    /// <summary>
    /// Asks one question with a bound of its own and turns anything that stops it into that check's
    /// answer. A check that could not be asked is <see cref="AppCheckStatus.Skipped"/>, with the reason,
    /// and never a pass: "we could not check" and "it works" are different sentences.
    /// </summary>
    private async Task<AppCheck> AskAsync(
        Asking asking, TimeSpan bound, CancellationToken ct, Func<CancellationToken, Task<AppCheck>> ask,
        (string Detail, string DetailFa)? timedOut = null)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(bound);

        try
        {
            return await ask(bounded.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var (detail, detailFa) = timedOut ?? (
                "The server did not answer in time, so this was not checked.",
                "سرور به‌موقع جواب نداد، پس این مورد بررسی نشد.");
            return Skipped(asking.Key, asking.Title, asking.TitleFa, detail, detailFa);
        }
        catch (NotSupportedException ex)
        {
            logger.LogInformation("Check {Key} is not available on this server: {Reason}", asking.Key, ex.Message);
            return Skipped(asking.Key, asking.Title, asking.TitleFa,
                "This server cannot answer this yet, so it was not checked.",
                "این سرور هنوز نمی‌تواند به این سؤال جواب بدهد، پس بررسی نشد.");
        }
        catch (Exception ex)
        {
            // The exception's own words stay in the log. They can name a host path or a token, and the
            // person reading the page can do nothing with a stack of Docker's vocabulary.
            logger.LogWarning(ex, "Check {Key} could not run.", asking.Key);
            return Skipped(asking.Key, asking.Title, asking.TitleFa,
                "The server could not be asked, so this says nothing either way about the app.",
                "نشد از سرور پرسید، پس این نتیجه چیزی دربارهٔ اپ نمی‌گوید.");
        }
    }

    private static AppCheck Skipped(
        string key, string title, string titleFa, string detail, string detailFa, string? link = null) =>
        new(key, AppCheckStatus.Skipped, title, titleFa, detail, detailFa, LinkPath: link);

    private static string ServiceKey(ManagedService svc) => $"{ServiceKeyPrefix}{svc.Id}";

    /// <summary>
    /// The containers of the deployment being served. A Compose stack's come from the host, because
    /// its service names were in a file parsed at deploy time and survive only on the containers.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ContainerNamesAsync(
        IDockerEngine docker, App app, Deployment deployment, CancellationToken token)
    {
        if (app.SourceType != AppSourceType.DockerCompose)
            return DeploymentPlanning.ReplicaContainerNames(
                app.WorkspaceId, app.Slug, deployment.Number, app.DesiredReplicas ?? 1);

        var listed = await docker.ListContainersAsync(ComposeServiceLabel, token);
        return listed
            .Where(c => c.Labels.TryGetValue(ComposeServiceLabel, out var service)
                        && c.Name == DeploymentPlanning.ComposeContainerName(app.WorkspaceId, app.Slug, service, deployment.Number))
            .Select(c => c.Name)
            .ToList();
    }

    /// <summary>
    /// A short look at why a container keeps dying. Only an addition to a failure already established,
    /// so not being able to read it costs the sentence and never the finding.
    /// </summary>
    private async Task<string?> TryTailAsync(IDockerEngine docker, string container, Run run, CancellationToken token)
    {
        try
        {
            var logs = await docker.GetLogsAsync(container, TailLinesQuoted, token);
            var lines = logs.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(TailLinesQuoted);
            var tail = Clean(string.Join('\n', lines), run);
            return tail.Length == 0 ? null : tail.Length <= MaxTailChars ? tail : tail[^MaxTailChars..];
        }
        catch (Exception ex)
        {
            // Including a timeout: the caller's own cancellation is seen at the next thing it awaits.
            logger.LogDebug(ex, "Could not read the log tail of {Container}.", container);
            return null;
        }
    }

    /// <summary>
    /// Everything that came from outside this class passes through here before a person sees it. The
    /// redactor knows this app's own secrets; the mask catches a password the platform has never heard
    /// of, written into a URL by the customer's own code.
    /// </summary>
    private string Clean(string? text, Run run) =>
        LogText.Clean(AppLogDiagnosis.MaskCredentials(redactor.Redact(text ?? "", run.Secrets))).Trim();

    private Task<App?> LoadAppAsync(Guid appId, CancellationToken ct) =>
        db.Apps.IgnoreQueryFilters().AsNoTracking()
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.ConfigGroups).ThenInclude(g => g.ConfigGroup!).ThenInclude(g => g.Entries)
            .FirstOrDefaultAsync(a => a.Id == appId, ct);

    /// <summary>The same rule the Overview and Usage tabs read their container by, so this page cannot disagree with them.</summary>
    private async Task<Deployment?> ServingDeploymentAsync(App app, CancellationToken ct)
    {
        var deployments = db.Deployments.IgnoreQueryFilters().AsNoTracking().Where(d => d.AppId == app.Id);

        var active = app.ActiveDeploymentId is { } activeId
            ? await deployments.FirstOrDefaultAsync(d => d.Id == activeId, ct)
            : null;

        return active ?? await deployments
            .Where(d => d.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(d => d.Number)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<IReadOnlyList<AttachedService>> AttachedServicesAsync(App app, CancellationToken ct)
    {
        var attachments = await db.AppManagedServices.IgnoreQueryFilters().AsNoTracking()
            .Include(a => a.ManagedService)
            .Where(a => a.AppId == app.Id && a.ManagedService!.WorkspaceId == app.WorkspaceId)
            .OrderBy(a => a.AttachOrder)
            .ToListAsync(ct);

        return attachments
            .GroupBy(a => a.ManagedServiceId)
            .Select(g => new AttachedService(g.First().ManagedService!, g.Any(a => a.HasUnpublishedChanges)))
            .ToList();
    }

    /// <summary>
    /// What must never reach a check, longest first: a shorter secret replaced before a longer one that
    /// contains it would leave the rest of the longer one on the page.
    /// </summary>
    private IReadOnlyList<string> SecretsOf(App app, IReadOnlyList<AttachedService> attached)
    {
        var own = app.EnvironmentVariables.Where(e => e.IsSecret).Select(e => e.Value);
        var shared = app.ConfigGroups
            .SelectMany(g => g.ConfigGroup?.Entries ?? [])
            .Where(e => e.IsSecret)
            .Select(e => e.Value);
        var passwords = attached.Select(a => a.Service.EncryptedPassword);

        return own.Concat(shared).Concat(passwords)
            .Select(SafeUnprotect)
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .ToList();
    }

    private string SafeUnprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return protector.Unprotect(value); }
        catch { return ""; }
    }

    private AppVerificationReport Gone(Guid appId, AppVerificationReport? previous, DateTimeOffset started) =>
        new(appId, previous?.DeploymentId, previous?.StartedAt ?? started, clock.UtcNow,
        [
            new AppCheck(RunningKey, AppCheckStatus.Failed,
                "This app no longer exists", "این اپ دیگر وجود ندارد",
                "It was deleted, so there is nothing left to check.",
                "حذف شده، پس چیزی برای بررسی نمانده.")
        ]);

    // ---- the stored row ----

    /// <summary>
    /// One row per app, overwritten. Never the reason a run fails: the person still gets the answer, and
    /// the tick still gets its report, if the panel's own database refuses to keep it.
    /// </summary>
    private async Task StoreAsync(App app, AppVerificationReport report, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var row = await db.AppVerifications.IgnoreQueryFilters().FirstOrDefaultAsync(v => v.AppId == app.Id, ct);
            var isNew = row is null;
            row ??= new AppVerification { AppId = app.Id, WorkspaceId = app.WorkspaceId };

            row.StartedAt = report.StartedAt;
            row.CompletedAt = report.CompletedAt;
            row.DeploymentId = report.DeploymentId;
            row.ChecksJson = JsonSerializer.Serialize(report.Checks, StoredShape);
            if (isNew) db.AppVerifications.Add(row);

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Detached either way: a row left Added in this scope would fail every later save the
                // launch tick makes with the same unique-index error. The usual cause is the button and
                // the tick both writing an app's first row, so the second attempt finds the winner's.
                db.Entry(row).State = EntityState.Detached;
                logger.LogWarning(ex, "Could not store the checks for app {AppId} (attempt {Attempt}).", app.Id, attempt + 1);
            }
        }
    }

    /// <summary>
    /// One bad element costs that element, not the report: this row outlives the code that wrote it. A
    /// status this build has never heard of becomes <see cref="AppCheckStatus.Skipped"/>, never a pass.
    /// </summary>
    private static IReadOnlyList<AppCheck> ReadChecks(string json)
    {
        List<JsonElement>? elements;
        try { elements = JsonSerializer.Deserialize<List<JsonElement>>(json, StoredShape); }
        catch (JsonException) { return []; }

        var checks = new List<AppCheck>();
        foreach (var element in elements ?? [])
        {
            StoredCheck? stored;
            try { stored = element.Deserialize<StoredCheck>(StoredShape); }
            catch (JsonException) { continue; }

            if (string.IsNullOrWhiteSpace(stored?.Key)) continue;

            var status = Enum.TryParse<AppCheckStatus>(stored.Status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : AppCheckStatus.Skipped;

            var title = stored.Title ?? stored.TitleFa ?? stored.Key;
            checks.Add(new AppCheck(stored.Key, status, title, stored.TitleFa ?? title,
                stored.Detail, stored.DetailFa, stored.Fix, stored.FixFa, stored.LinkPath));
        }

        return checks;
    }

    /// <summary>The stored shape with its status left as text, so an unknown one can be judged rather than thrown over.</summary>
    private sealed record StoredCheck(
        string? Key, string? Status, string? Title, string? TitleFa,
        string? Detail, string? DetailFa, string? Fix, string? FixFa, string? LinkPath);
}
