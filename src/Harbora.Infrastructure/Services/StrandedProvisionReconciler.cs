using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// What becomes of a database that reads Provisioning with nothing setting it up, and the sentences it
/// is left with. Pure, so every case is a plain test.
///
/// <para>
/// <b>Setting it going again is the answer wherever that is safe, and it nearly always is.</b>
/// <c>ManagedServiceEngine.ProvisionAsync</c> is written to be run again — the Rebuild button is
/// exactly that, and the job worker already re-runs it after a graceful shutdown and on a transient
/// failure: every network and volume is ensured rather than created, the old container is removed
/// by name and a new one started on the <i>same</i> data volume, which is never removed. A database
/// whose engine already initialised that volume starts on it as it is.
/// </para>
///
/// <para>
/// <b>Three cases are not set going again.</b> A read replica that was still copying its primary
/// (<see cref="ManagedService.PrimaryManagedServiceId"/> set, <see cref="ManagedService.RunningImage"/>
/// still null): a new attempt wipes the copy and starts it from nothing, which is long by nature and
/// loads the primary the whole time, and the copy the restart cut off may still be running on its
/// host — so that is the person's call, not a startup's. A database whose setup a restart has now
/// cut off twice in a row: the next restart is not going to be different, and a provision that is
/// what brings the panel down must not be what it does every time it comes back. And a setup that
/// ended on its own — cancelled before it ran, or failed in a way the engine never caught — which
/// would end the same way again.
/// </para>
/// </summary>
public static class StrandedProvision
{
    /// <param name="SetGoingAgain">Queue the provision again; otherwise mark it Failed with <paramref name="Reason"/>.</param>
    /// <param name="Reason">The English sentence stored as the row's reason, when it is failed.</param>
    public sealed record Decision(bool SetGoingAgain, string? Reason);

    public const string ReplicaInterrupted =
        "Copying the primary's data into this replica was interrupted by a restart of the panel. It was not " +
        "started again on its own, because a copy starts from nothing and loads the primary while it runs. " +
        "Press Retry when that suits you.";

    private const string ReplicaInterruptedFa =
        "کپی دادهٔ دیتابیس اصلی در این رپلیکا با راه‌اندازی دوبارهٔ پنل نیمه‌کاره ماند. خودبه‌خود دوباره " +
        "شروع نشد، چون کپی از صفر شروع می‌شود و تا تمام شود به دیتابیس اصلی فشار می‌آورد. هر وقت مناسب بود " +
        "«تلاش دوباره» را بزن.";

    public const string InterruptedTwice =
        "Setting this database up was cut off by a restart of the panel twice in a row, so it was not started " +
        "a third time on its own. Press Retry to set it up again; nothing in it was removed.";

    private const string InterruptedTwiceFa =
        "راه‌اندازی این دیتابیس دو بار پشت سر هم با راه‌اندازی دوبارهٔ پنل نیمه‌کاره ماند، پس بار سوم " +
        "خودبه‌خود شروع نشد. «تلاش دوباره» را بزن تا دوباره راه‌اندازی شود؛ چیزی از آن پاک نشد.";

    public const string Cancelled =
        "Setting this database up was cancelled before it finished. Press Retry to set it up.";

    private const string CancelledFa =
        "راه‌اندازی این دیتابیس پیش از تمام شدن لغو شد. برای راه‌اندازی، «تلاش دوباره» را بزن.";

    private const string StoppedPrefix = "Setting this database up stopped before it finished: ";
    private const string StoppedSuffix = " Press Retry to try again.";
    private const string StoppedPrefixFa = "راه‌اندازی این دیتابیس پیش از تمام شدن متوقف شد: ";
    private const string StoppedSuffixFa = " برای امتحان دوباره، «تلاش دوباره» را بزن.";

    /// <param name="provisionJobsNewestFirst">This database's ServiceProvision jobs, none of them live.</param>
    public static Decision Decide(ManagedService service, IReadOnlyList<Job> provisionJobsNewestFirst)
    {
        var latest = provisionJobsNewestFirst.FirstOrDefault();

        // Nothing was ever queued (a create that died between its commit and its enqueue), or the last
        // setup finished and something marked it Provisioning again without asking for another: in
        // neither case did anything start that a new attempt could collide with.
        if (latest is null || latest.Status == JobStatus.Succeeded) return new Decision(true, null);

        if (latest.Status == JobStatus.Cancelled) return new Decision(false, Cancelled);

        if (IsRestart(latest))
        {
            if (service.PrimaryManagedServiceId is not null && service.RunningImage is null)
                return new Decision(false, ReplicaInterrupted);

            return provisionJobsNewestFirst.Count > 1 && IsRestart(provisionJobsNewestFirst[1])
                ? new Decision(false, InterruptedTwice)
                : new Decision(true, null);
        }

        var said = string.IsNullOrWhiteSpace(latest.Error) ? "it gave no reason." : latest.Error.Trim();
        if (!said.EndsWith('.')) said += ".";
        return new Decision(false, StoppedPrefix + said + StoppedSuffix);
    }

    /// <summary>
    /// The stored reason in the reader's language. <see cref="ManagedService.ErrorMessage"/> holds one
    /// string, the engine's own English words everywhere else; the sentences written here have their
    /// Persian kept beside them, and anything else is shown exactly as it was stored.
    /// </summary>
    public static string? InReadersLanguage(string? stored, bool isFa)
    {
        if (!isFa || string.IsNullOrEmpty(stored)) return stored;

        return stored switch
        {
            ReplicaInterrupted => ReplicaInterruptedFa,
            InterruptedTwice => InterruptedTwiceFa,
            Cancelled => CancelledFa,
            _ when stored.StartsWith(StoppedPrefix, StringComparison.Ordinal) && stored.EndsWith(StoppedSuffix, StringComparison.Ordinal)
                => StoppedPrefixFa + stored[StoppedPrefix.Length..^StoppedSuffix.Length] + StoppedSuffixFa,
            _ => stored
        };
    }

    private static bool IsRestart(Job job) =>
        job.Status == JobStatus.Failed && job.Error == JobReconciler.InterruptedByRestart;
}

/// <summary>
/// Settles every database left reading Provisioning with nothing setting it up: once as the panel
/// starts, before the job worker claims anything, and then every few minutes for the same state
/// reached without a restart.
///
/// <para>
/// <b>Why it is needed.</b> After a hard stop <see cref="JobReconciler"/> marks the provision job that
/// was running Failed and leaves the meaning to each target's own reconciler; deployments had one,
/// databases had none. The row stayed Provisioning for ever, and every deployment in its environment
/// held a worker for ten minutes waiting on it (<c>DeploymentPipeline.WaitForServicesBesideItAsync</c>),
/// and a launch waited twenty before giving up. <see cref="StrandedProvision"/> says what each case
/// becomes.
/// </para>
///
/// <para>
/// <b>The same state with no restart.</b> A provision job cancelled from the activity page before it
/// was claimed is settled Cancelled without ever running, and a provision that throws before
/// <c>ProvisionAsync</c>'s own catch fails only its job row; either leaves the database exactly as a
/// restart does. The periodic pass finds those, and leaves alone anything touched within
/// <see cref="Grace"/>: every path that marks a database Provisioning queues its job a moment later,
/// and a pass landing in between must not mistake that moment for an abandoned setup.
/// </para>
///
/// <para>
/// Sessionless, so every read is unfiltered on purpose: a sweeper that reads an empty table reports
/// that nothing was stranded.
/// </para>
/// </summary>
public sealed class StrandedProvisionReconciler(
    IServiceScopeFactory scopeFactory,
    ISystemClock clock,
    ILogger<StrandedProvisionReconciler> logger) : BackgroundService
{
    /// <summary>How long a database must have sat untouched, with its last setup over, before a periodic pass acts.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    /// <summary>The startup pass, run to completion here so it is over before the job gate opens.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await ReconcileAsync(atStartup: true, cancellationToken); }
        catch (Exception ex)
        {
            // Never block startup on reconciliation; the periodic pass tries again.
            logger.LogError(ex, "Settling databases left being set up failed on startup.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await ReconcileAsync(atStartup: false, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Settling databases left being set up failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// One pass. At startup nothing can be setting anything up, so every Provisioning database with no
    /// live job is stranded; later, only one nobody has touched within <see cref="Grace"/>.
    /// </summary>
    /// <returns>How many were set going again, and how many were marked Failed.</returns>
    public async Task<(int SetGoing, int Failed)> ReconcileAsync(bool atStartup, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var provisioning = await db.ManagedServices.IgnoreQueryFilters()
            .Where(s => s.Status == ServiceStatus.Provisioning)
            .ToListAsync(ct);
        if (provisioning.Count == 0) return (0, 0);

        var ids = provisioning.Select(s => s.Id).ToList();
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.Kind == JobKind.ServiceProvision && ids.Contains(j.TargetId))
            .ToListAsync(ct);

        var now = clock.UtcNow;
        var engine = scope.ServiceProvider.GetRequiredService<ManagedServiceEngine>();
        var (setGoing, failed) = (0, 0);

        foreach (var service in provisioning)
        {
            var mine = jobs.Where(j => j.TargetId == service.Id)
                .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).ToList();

            // Something is still going to set it up, or is doing so now.
            if (mine.Any(j => j.Status is JobStatus.Pending or JobStatus.Running)) continue;

            if (!atStartup)
            {
                var lastTouched = mine.FirstOrDefault() is { } latest
                    ? Max(service.UpdatedAt, latest.FinishedAt ?? latest.CreatedAt)
                    : service.UpdatedAt;
                if (now - lastTouched < Grace) continue;
            }

            var decision = StrandedProvision.Decide(service, mine);
            if (decision.SetGoingAgain)
            {
                await engine.QueueProvisionAsync(service.Id, ct);
                setGoing++;
                logger.LogWarning(
                    "{Name} was left being set up with nothing working on it; its setup was queued again.", service.Name);
            }
            else
            {
                await engine.FailAsync(service, decision.Reason!, ct);
                failed++;
                logger.LogWarning("{Name} was left being set up with nothing working on it: {Reason}", service.Name, decision.Reason);
            }
        }

        if (setGoing + failed > 0)
            logger.LogInformation(
                "Databases left being set up: {SetGoing} set going again, {Failed} marked failed.", setGoing, failed);
        return (setGoing, failed);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
