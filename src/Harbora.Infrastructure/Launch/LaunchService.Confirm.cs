using System.Security.Cryptography;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Services;
using Harbora.Shared.Inspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Launch;

/// <summary>
/// Creating what was agreed to, on the request, because each of these services judges the signed-in
/// user. Everything is done through the service the forms use, and every id is on the row the moment
/// it exists: that row is what a retry reads to know what not to create again.
/// </summary>
public sealed partial class LaunchService
{
    internal const string AppKeyName = "APP_KEY";
    internal const string LaravelAppKeyNote = "laravel-app-key";

    /// <summary>
    /// Long enough for the validators that demand 32 characters of a JWT secret, which a shorter
    /// <see cref="ServiceCredentials.Generate"/> fails at startup with a message about the secret.
    /// </summary>
    private const int GeneratedSecretLength = 48;

    public async Task<ServiceOutcome<LaunchView>> ConfirmAsync(
        Guid launchId, LaunchChoices choices, string? clientIp, CancellationToken ct)
    {
        var launch = await db.AppLaunches.AsNoTracking().FirstOrDefaultAsync(l => l.Id == launchId, ct);
        if (launch is null) return ServiceOutcome<LaunchView>.Refused(NotFound);

        if (launch.Status != LaunchStatus.AwaitingConfirmation)
            return AlreadyConfirmed(launch.Status)
                ? ServiceOutcome<LaunchView>.Success(await ViewAsync(launchId, ct))
                : ServiceOutcome<LaunchView>.Refused(NoLongerConfirmable);

        // Refused here, nothing has changed and the person is back on their screen. From the move on,
        // a refusal is a step that failed, with what exists named on the row. Asked once, so the host
        // these checks approved is the one everything is then told to land on.
        var server = await SettleServerAsync(launch.WorkspaceId, choices, ct);
        var refusals = await CheckChoicesAsync(launch, choices, server, ct);
        if (refusals.Count > 0) return ServiceOutcome<LaunchView>.Refused(refusals);

        // A database ticked by hand arrives with no aliases of its own; what the example file says it
        // is read under is settled here, so the retry and the timeline both read one answer.
        var inspection = LaunchJson.ReadInspection(launch.InspectionJson);
        var settled = choices with
        {
            ServerId = server.ServerId,
            Services = choices.Services
                .Select(s => s with { Aliases = s.Aliases ?? AliasesFor(inspection, s.Type) })
                .ToList()
        };

        // The claim. A second submit in the same instant matches nothing here and creates nothing.
        if (!await LaunchMoves.TryMoveAsync(db, launchId, LaunchStatus.AwaitingConfirmation, LaunchStatus.Creating, clock.UtcNow, ct))
            return ServiceOutcome<LaunchView>.Success(await ViewAsync(launchId, ct));

        // From here on the work is not abandoned because the browser went away: half of a launch
        // cancelled by a closed tab would be the worst of both outcomes.
        await UpdateAsync(launchId, l => l.ChoicesJson = LaunchJson.WriteChoices(settled), CancellationToken.None);

        var answers = choices.EnvVars
            .GroupBy(v => v.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        await CreateWhatIsMissingAsync(launchId, settled, answers, clientIp);

        return ServiceOutcome<LaunchView>.Success(await ViewAsync(launchId, CancellationToken.None));
    }

    public async Task<ServiceOutcome<LaunchView>> RetryAsync(Guid launchId, string? clientIp, CancellationToken ct)
    {
        var launch = await db.AppLaunches.AsNoTracking().FirstOrDefaultAsync(l => l.Id == launchId, ct);
        if (launch is null) return ServiceOutcome<LaunchView>.Refused(NotFound);

        if (!LaunchAdvance.CanRetry(launch, clock.UtcNow))
            return ServiceOutcome<LaunchView>.Refused(new ServiceRefusal(
                launch.FailureMessage ?? "There is nothing to try again here.",
                launch.FailureMessageFa ?? "چیزی برای تلاش دوباره نیست."));

        var to = launch.FailedStep switch
        {
            LaunchStepKeys.Deploy => LaunchStatus.Deploying,
            LaunchStepKeys.Verify => LaunchStatus.Verifying,
            _ => LaunchStatus.Creating
        };

        var choices = LaunchJson.ReadChoices(launch.ChoicesJson);
        if (to == LaunchStatus.Creating && choices is null)
            return ServiceOutcome<LaunchView>.Refused(NoLongerConfirmable);

        var now = clock.UtcNow;
        if (!await LaunchMoves.TryMoveAsync(db, launchId, LaunchStatus.Failed, to, now, ct))
            return ServiceOutcome<LaunchView>.Success(await ViewAsync(launchId, ct));

        await UpdateAsync(launchId, l =>
        {
            l.FailedStep = null;
            l.FailureMessage = null;
            l.FailureMessageFa = null;
            // Forgotten so the tick queues a fresh one; a deployment that failed is not one to adopt.
            if (to == LaunchStatus.Deploying) l.DeploymentId = null;
            if (to == LaunchStatus.Verifying) l.VerifyStartedAt = now;
        }, CancellationToken.None);

        await audit.LogAsync("launch.retried", AuditTarget, launchId.ToString(), clientIp,
            metadataJson: JsonSerializer.Serialize(new { step = launch.FailedStep }),
            workspaceId: launch.WorkspaceId, ct: CancellationToken.None);

        if (to == LaunchStatus.Creating)
        {
            await ProvisionFailedServicesAgainAsync(launch, CancellationToken.None);

            // What was typed on the confirm screen was not kept (see LaunchJson.WriteChoices), so a
            // retry works from the example file and the generator, and names what it could not set.
            await CreateWhatIsMissingAsync(launchId, choices!, new Dictionary<string, string?>(), clientIp);
        }

        return ServiceOutcome<LaunchView>.Success(await ViewAsync(launchId, CancellationToken.None));
    }

    /// <summary>
    /// A failed database asks to be built again the way its own page does. The row reads Provisioning
    /// from this moment rather than when the job is claimed, or the next tick reads the old Failed and
    /// fails the launch before the second attempt has begun.
    /// </summary>
    private async Task ProvisionFailedServicesAgainAsync(AppLaunch launch, CancellationToken ct)
    {
        foreach (var serviceId in LaunchJson.ReadIds(launch.ServiceIdsJson))
        {
            var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
            if (service is null) continue;

            var failed = service.Status == ServiceStatus.Failed;
            if (failed && await access.CanTouchServiceAsync(serviceId, Capabilities.DatabasesManage, ct))
            {
                service.Status = ServiceStatus.Provisioning;
                service.ErrorMessage = null;
                await db.SaveChangesAsync(ct);
                await engine.QueueProvisionAsync(serviceId, ct);
            }

            db.Entry(service).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Databases, then the app, then the attachments, then the variables, skipping whatever the row says
    /// already exists. Nothing is rolled back on a refusal: a database that exists is named, not
    /// silently deleted, and the launch stops with the step that refused.
    /// </summary>
    private async Task CreateWhatIsMissingAsync(
        Guid launchId, LaunchChoices choices, IReadOnlyDictionary<string, string?> answers, string? clientIp)
    {
        var ct = CancellationToken.None;
        var step = LaunchStepKeys.Services;
        string? token = null;
        try
        {
            var launch = await db.AppLaunches.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == launchId, ct);
            var inspection = LaunchJson.ReadInspection(launch.InspectionJson);
            token = TokenOf(launch);

            // 1. Databases. In choice order, so the ids on the row say which choices are done. The
            // server was settled before the first of these and every one is told it.
            var serviceIds = LaunchJson.ReadIds(launch.ServiceIdsJson);
            var names = await ServiceNamesAsync(
                choices with { Services = choices.Services.Skip(serviceIds.Count).ToList() }, ct);
            var serverId = choices.ServerId;
            if (serverId is null && serviceIds.Count > 0)
                serverId = await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.Id == serviceIds[0]).Select(s => (Guid?)s.ServerId).FirstOrDefaultAsync(ct);

            for (var i = serviceIds.Count; i < choices.Services.Count; i++)
            {
                var name = names[i - (choices.Services.Count - names.Count)];
                var created = await databases.CreateAsync(DatabaseRequest(launch, choices, i, name, serverId, clientIp), ct);
                if (!created.Ok)
                {
                    await FailCreatingAsync(launchId, step, created.Refusals[0].Message, created.Refusals[0].MessageFa, answers, ct);
                    return;
                }

                serviceIds.Add(created.Value!.ServiceId);
                serverId ??= created.Value.ServerId;
                var written = LaunchJson.Write(serviceIds);
                await UpdateAsync(launchId, l => l.ServiceIdsJson = written, ct);
            }

            // 2. The app. The token is handed over here and goes from the row with the same write.
            step = LaunchStepKeys.App;
            var appId = launch.AppId;
            if (appId is null)
            {
                var created = await apps.CreateAsync(AppRequest(launch, choices, inspection, serverId, token, clientIp), ct);
                if (!created.Ok)
                {
                    await FailCreatingAsync(launchId, step, created.Refusals[0].Message, created.Refusals[0].MessageFa, answers, ct);
                    return;
                }

                appId = created.Value!.AppId;
                var newAppId = appId;
                await UpdateAsync(launchId, l =>
                {
                    l.AppId = newAppId;
                    l.EncryptedGitToken = null;
                }, ct);
            }

            // 3. The attachments.
            var attached = await db.AppManagedServices.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.AppId == appId).Select(a => a.ManagedServiceId).ToListAsync(ct);
            for (var i = 0; i < serviceIds.Count; i++)
            {
                if (attached.Contains(serviceIds[i])) continue;

                var outcome = await attachments.AttachAsync(
                    serviceIds[i], appId.Value, null, null, clientIp, ct, choices.Services[i].Aliases);
                if (!outcome.Ok)
                {
                    await FailCreatingAsync(launchId, step, outcome.Refusals[0].Message, outcome.Refusals[0].MessageFa, answers, ct);
                    return;
                }
            }

            // 4. The variables.
            if (await SetVariablesAsync(launch.WorkspaceId, appId.Value, choices, answers, inspection, clientIp, ct) is { } refusal)
            {
                await FailCreatingAsync(launchId, step, refusal.Message, refusal.MessageFa, answers, ct);
                return;
            }

            await audit.LogAsync("launch.confirmed", AuditTarget, launchId.ToString(), clientIp,
                metadataJson: JsonSerializer.Serialize(new { appId, services = serviceIds, serverId }),
                workspaceId: launch.WorkspaceId, ct: ct);

            var next = serviceIds.Count > 0 ? LaunchStatus.WaitingForServices : LaunchStatus.Deploying;
            await UpdateAsync(launchId, l =>
            {
                l.EncryptedGitToken = null;
                l.Status = next;
                l.StatusChangedAt = clock.UtcNow;
            }, ct);
        }
        catch (Exception ex)
        {
            // A create can also throw: the app's unique slug lost to another workspace in this instant,
            // a default project past the plan's ceiling. Whatever it left tracked must not go out with
            // the write below, and the launch must not stay Creating with nobody coming back to it.
            db.ChangeTracker.Clear();
            var reason = Scrub(ex.Message, token);
            logger.LogError("Launch {LaunchId} failed at {Step}: {Reason}", launchId, step, reason);
            await FailCreatingAsync(launchId, step, reason, (ex as QuotaRefusedException)?.ReasonFa ?? $"ساختن متوقف شد: {reason}", answers, ct);
        }
    }

    /// <summary>
    /// Sets what is not set yet, as one call. A generated secret is made here and nowhere else; an
    /// answer left blank is not written, because an empty variable and an unset one are different to
    /// an app that checks. On a retry nothing is in <paramref name="answers"/>, so an example file's
    /// value is read again and a person's answer stays unset, named on the timeline.
    /// </summary>
    private async Task<ServiceRefusal?> SetVariablesAsync(
        Guid workspaceId, Guid appId, LaunchChoices choices, IReadOnlyDictionary<string, string?> answers,
        SourceInspection inspection, string? clientIp, CancellationToken ct)
    {
        var existing = await db.EnvironmentVariables.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.AppId == appId).Select(v => v.Key).ToListAsync(ct);

        var inputs = new List<AppEnvVarInput>();
        foreach (var variable in choices.EnvVars.DistinctBy(v => v.Key))
        {
            if (existing.Contains(variable.Key)) continue;

            var detected = inspection.EnvVars.FirstOrDefault(v => v.Name == variable.Key);
            var value = answers.GetValueOrDefault(variable.Key);

            if (string.IsNullOrEmpty(value))
            {
                if (detected?.Disposition == EnvVarDisposition.GeneratedSecret)
                {
                    inputs.Add(new AppEnvVarInput(variable.Key, Generate(variable.Key, inspection), IsSecret: true));
                    continue;
                }

                if (answers.Count > 0 || detected?.Disposition != EnvVarDisposition.KeepExample
                    || string.IsNullOrEmpty(detected.ExampleValue))
                    continue;

                value = detected.ExampleValue;
            }

            inputs.Add(new AppEnvVarInput(variable.Key, value, variable.IsSecret));
        }

        if (inputs.Count == 0) return null;

        var outcome = await configuration.SetEnvAsync(new AppActor(workspaceId, clientIp), appId, inputs, ct);
        return outcome.Ok ? null : outcome.Refusals[0];
    }

    /// <summary>Laravel refuses to boot on anything but its own <c>base64:</c> form of 32 random bytes.</summary>
    private static string Generate(string key, SourceInspection inspection) =>
        key == AppKeyName && inspection.Notes.Any(n => n.Code == LaravelAppKeyNote)
            ? "base64:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : ServiceCredentials.Generate(GeneratedSecretLength);

    /// <summary>
    /// Stops the launch at this step, and names what is already there, because that is what a person is
    /// paying for while they read this. The token stays only while no app has it: a retry still needs it.
    /// </summary>
    private async Task FailCreatingAsync(
        Guid launchId, string step, string reason, string reasonFa,
        IReadOnlyDictionary<string, string?> answers, CancellationToken ct)
    {
        var launch = await db.AppLaunches.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == launchId, ct);

        var serviceIds = LaunchJson.ReadIds(launch.ServiceIdsJson);
        var created = await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
            .Where(s => serviceIds.Contains(s.Id)).Select(s => s.Name).ToListAsync(ct);
        if (launch.AppId is { } appId)
            created.AddRange(await db.Apps.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Id == appId).Select(a => a.Name).ToListAsync(ct));

        var (message, messageFa) = (reason, reasonFa);
        if (created.Count > 0)
        {
            message += $" Already created, and kept: {string.Join(", ", created)}.";
            messageFa += $" این‌ها ساخته شده‌اند و می‌مانند: {string.Join("، ", created)}.";
        }

        // Answers are not stored anywhere that survives this, and a person should hear that now rather
        // than find a variable missing after the next try. Only what a retry cannot read again: a value
        // left as the example file has it comes back by itself. Names only, never a value.
        var inspection = LaunchJson.ReadInspection(launch.InspectionJson);
        var lost = answers
            .Where(a => !string.IsNullOrEmpty(a.Value)
                        && inspection.EnvVars.FirstOrDefault(v => v.Name == a.Key)?.ExampleValue != a.Value)
            .Select(a => a.Key)
            .ToList();
        if (lost.Count > 0)
        {
            message += $" What you typed for {string.Join(", ", lost)} was not kept: after trying again, set it on the app's page.";
            messageFa += $" آنچه برای {string.Join("، ", lost)} وارد کرده بودید نگه داشته نشد: بعد از تلاش دوباره، آن را در صفحهٔ اپ تنظیم کنید.";
        }

        await UpdateAsync(launchId, l =>
        {
            l.Status = LaunchStatus.Failed;
            l.StatusChangedAt = clock.UtcNow;
            l.FailedStep = step;
            l.FailureMessage = Clip(message);
            l.FailureMessageFa = Clip(messageFa);
        }, ct);
    }
}
