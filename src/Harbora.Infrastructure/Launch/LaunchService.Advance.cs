using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Verification;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Launch;

/// <summary>
/// The tick's side of a launch: read a snapshot, ask <see cref="LaunchAdvance.Decide"/>, do what it
/// said. No session on any of this, so every read is deliberately unfiltered: under the tenant filter
/// the tick would see no launches, move nothing, and report a healthy pass.
/// </summary>
public sealed partial class LaunchService
{
    private static readonly LaunchStatus[] InFlight =
    [
        LaunchStatus.Inspecting, LaunchStatus.AwaitingConfirmation, LaunchStatus.Creating,
        LaunchStatus.WaitingForServices, LaunchStatus.Deploying, LaunchStatus.Verifying
    ];

    /// <summary>Bounds one launch's moves in one pass; there are only so many steps to take without waiting.</summary>
    private const int MaxMovesPerPass = 6;

    public async Task<int> AdvanceAllAsync(CancellationToken ct)
    {
        var ids = await db.AppLaunches.IgnoreQueryFilters().AsNoTracking()
            .Where(l => InFlight.Contains(l.Status)
                        || (l.Status == LaunchStatus.Failed && (l.EncryptedGitToken != null || l.ArchivePath != null)))
            .OrderBy(l => l.StatusChangedAt)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var moved = 0;
        foreach (var id in ids)
        {
            try
            {
                if (await AdvanceOneAsync(id, ct)) moved++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One launch's trouble stays its own: whatever it left half-written must not go out
                // with the next one's save.
                logger.LogError(ex, "Launch {LaunchId} could not be advanced.", id);
                db.ChangeTracker.Clear();
            }
        }

        return moved;
    }

    private async Task<bool> AdvanceOneAsync(Guid launchId, CancellationToken ct)
    {
        var moved = false;
        for (var i = 0; i < MaxMovesPerPass; i++)
        {
            var snapshot = await SnapshotAsync(launchId, ct);
            if (snapshot is null) return moved;

            var move = LaunchAdvance.Decide(snapshot);
            if (move.Kind == LaunchMoveKind.Stay || !await ApplyAsync(launchId, move, ct)) return moved;

            moved = true;
        }

        return moved;
    }

    private async Task<LaunchSnapshot?> SnapshotAsync(Guid launchId, CancellationToken ct)
    {
        var launch = await db.AppLaunches.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == launchId, ct);
        if (launch is null) return null;

        var serviceIds = LaunchJson.ReadIds(launch.ServiceIdsJson);
        var states = await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
            .Where(s => serviceIds.Contains(s.Id))
            .Select(s => new LaunchServiceState(s.Id, s.Name, s.Status, s.ErrorMessage))
            .ToListAsync(ct);

        var deployment = launch.DeploymentId is { } deploymentId
            ? await db.Deployments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Id == deploymentId)
                .Select(d => new LaunchDeploymentState(d.Status, d.ErrorMessage))
                .FirstOrDefaultAsync(ct)
            : null;

        return new LaunchSnapshot(
            launch.Status, launch.SourceKind, clock.UtcNow, launch.StatusChangedAt, launch.UpdatedAt, launch.VerifyStartedAt,
            launch.AppId, LaunchJson.ReadChoices(launch.ChoicesJson)?.Services.Count ?? serviceIds.Count, states,
            launch.DeploymentId, deployment,
            HoldsLeftovers: launch.EncryptedGitToken is not null || launch.ArchivePath is not null);
    }

    /// <summary>Does what was decided. True when the launch moved, so it may be able to move again.</summary>
    private async Task<bool> ApplyAsync(Guid launchId, LaunchMove move, CancellationToken ct)
    {
        switch (move.Kind)
        {
            case LaunchMoveKind.StartDeploying:
                await SetStatusAsync(launchId, LaunchStatus.Deploying, ct);
                return true;

            case LaunchMoveKind.QueueDeployment:
                return await QueueTheDeploymentAsync(launchId, ct);

            case LaunchMoveKind.StartVerifying:
                await UpdateAsync(launchId, l =>
                {
                    l.Status = LaunchStatus.Verifying;
                    l.StatusChangedAt = clock.UtcNow;
                    l.VerifyStartedAt = clock.UtcNow;
                }, ct);
                return true;

            case LaunchMoveKind.RunChecks:
                return await RunChecksAsync(launchId, ct);

            case LaunchMoveKind.Fail:
                await FailAsync(launchId, move.FailedStep!, move.Message!, move.MessageFa!, ct);
                return true;

            case LaunchMoveKind.FailInspection:
                await EndInspectionAsync(launchId, move.Message!, move.MessageFa!, ct);
                DeleteDirectory(WorkDirFor(launchId));
                return true;

            case LaunchMoveKind.Abandon:
                // Conditional: a confirm at the twenty-fourth hour must not lose its archive to a sweep.
                if (!await LaunchMoves.TryMoveAsync(db, launchId, LaunchStatus.AwaitingConfirmation, LaunchStatus.Abandoned, clock.UtcNow, ct))
                    return false;
                await DropLeftoversAsync(launchId, ct);
                return true;

            case LaunchMoveKind.DropLeftovers:
                // Whatever this takes away is what a retry would have needed, so the message has to stop
                // offering one, and say what is left to do instead.
                await DropLeftoversAsync(launchId, ct, l =>
                {
                    if (l.AppId is null)
                    {
                        l.FailureMessage = Clip($"{l.FailureMessage} This launch has closed: start a new one.");
                        l.FailureMessageFa = Clip($"{l.FailureMessageFa} این راه‌اندازی بسته شد: یکی تازه شروع کنید.");
                    }
                    else if (l.ArchivePath is not null)
                    {
                        l.FailureMessage = Clip($"{l.FailureMessage} The uploaded code has been removed: upload a new version from the app's page.");
                        l.FailureMessageFa = Clip($"{l.FailureMessageFa} کد آپلودشده پاک شد: نسخهٔ تازه را از صفحهٔ اپ آپلود کنید.");
                    }
                });
                return false;

            default:
                return false;
        }
    }

    private Task SetStatusAsync(Guid launchId, LaunchStatus status, CancellationToken ct) =>
        UpdateAsync(launchId, l =>
        {
            l.Status = status;
            l.StatusChangedAt = clock.UtcNow;
        }, ct);

    private Task FailAsync(Guid launchId, string step, string message, string messageFa, CancellationToken ct) =>
        UpdateAsync(launchId, l =>
        {
            l.Status = LaunchStatus.Failed;
            l.StatusChangedAt = clock.UtcNow;
            l.FailedStep = step;
            l.FailureMessage = Clip(message);
            l.FailureMessageFa = Clip(messageFa);
        }, ct);

    /// <summary>
    /// The first deployment, under the name of whoever started the launch. The archive leaves the row
    /// with the same write: from here it belongs to the deployment, which consumes it.
    /// </summary>
    private async Task<bool> QueueTheDeploymentAsync(Guid launchId, CancellationToken ct)
    {
        var launch = await db.AppLaunches.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == launchId, ct);
        var appId = launch.AppId!.Value;

        // A panel that died between the queue and the write below comes back to a launch with no id and
        // an app with a deployment. Queueing again would deploy twice, and an upload's second attempt
        // fails for want of the archive the first consumed.
        var deploymentId = await db.Deployments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.AppId == appId && d.CreatedAt >= launch.StatusChangedAt)
            .OrderByDescending(d => d.Number)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);

        if (deploymentId is null)
        {
            try
            {
                deploymentId = await deployments.QueueDeploymentAsync(new DeploymentRequest(
                    appId, DeploymentTrigger.Manual, launch.UserId,
                    GitRef: launch.ResolvedBranch, SourceArchivePath: launch.ArchivePath), ct);
            }
            catch (QuotaRefusedException ex)
            {
                await FailDeployRefusedAsync(launchId, ex.Message, ex.ReasonFa, ct);
                return true;
            }
            catch (CapacityRefusedException ex)
            {
                await FailDeployRefusedAsync(launchId, ex.Message, ex.ReasonFa, ct);
                return true;
            }
            catch (LowDiskRefusedException ex)
            {
                await FailDeployRefusedAsync(launchId, ex.Message, ex.ReasonFa, ct);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                await FailDeployRefusedAsync(launchId, ex.Message, null, ct);
                return true;
            }
        }

        // Coalesced onto a deployment somebody started by hand in the meantime: that one will not read
        // this archive, so nothing else ever would.
        if (launch.ArchivePath is { } archive && !await db.Deployments.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(d => d.Id == deploymentId && d.SourceArchivePath == archive, ct))
            DeleteArchive(archive);

        await UpdateAsync(launchId, l =>
        {
            l.DeploymentId = deploymentId;
            l.ArchivePath = null;
        }, ct);
        return true;
    }

    /// <summary>The deployment never began, so an uploaded archive is still on the row and a retry can still use it.</summary>
    private Task FailDeployRefusedAsync(Guid launchId, string message, string? messageFa, CancellationToken ct) =>
        FailAsync(launchId, LaunchStepKeys.Deploy,
            $"The app could not be deployed: {message}",
            $"اپ مستقر نشد: {messageFa ?? message}", ct);

    /// <summary>
    /// One run of the checks. The row is touched before the run rather than after it, so a verifier that
    /// keeps throwing is still asked only every <see cref="LaunchAdvance.ChecksEvery"/>.
    /// </summary>
    private async Task<bool> RunChecksAsync(Guid launchId, CancellationToken ct)
    {
        var launch = await db.AppLaunches.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == launchId, ct);
        await UpdateAsync(launchId, l => l.UpdatedAt = clock.UtcNow, ct);

        var verifier = serviceProvider.GetRequiredService<IAppVerifier>();
        var appId = launch.AppId!.Value;

        // The verifier only honours a report for this same app and deployment, so an older one is safe
        // to hand it; keeping the last one here is what stops a passed check costing another container.
        var previous = await verifier.GetLatestAsync(appId, ct);

        var startedAt = launch.VerifyStartedAt ?? launch.StatusChangedAt;
        var report = await verifier.RunAsync(appId, previous, startedAt + LaunchAdvance.ChecksPatience, ct);
        if (!report.IsComplete) return false;

        await SetStatusAsync(launchId, report.HasFailures ? LaunchStatus.SucceededWithProblems : LaunchStatus.Succeeded, ct);
        return true;
    }
}
