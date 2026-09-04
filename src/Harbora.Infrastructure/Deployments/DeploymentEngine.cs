using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Jobs;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Creates the immutable <see cref="Deployment"/> record and hands the heavy lifting to a
/// queued <see cref="DeploymentPipeline"/> so the HTTP request returns immediately.
///
/// <para>
/// P7 (2026-08-17 app-environment-management design): two pre-flight checks live here rather than
/// at each of the eleven places that call <see cref="QueueDeploymentAsync"/>, the same reasoning
/// the PAYG start gate's own comment gives for its own single home. <paramref name="scheduler"/>
/// and <paramref name="monitoringOptions"/> are optional constructor parameters — both are always
/// resolved by DI in production, and staying optional is what lets the many direct-construction
/// unit tests of this class keep passing three arguments without also having to fabricate a node
/// and a disk figure they were never testing.
/// </para>
///
/// <para>
/// 5.2 (2026-09 market-gaps round two, "approval gate on deploying to a protected environment"):
/// the protected-environment gate lives here too, for the identical reason the P7 checks do — every
/// one of those eleven call sites (a webhook push, the CLI, the panel's Deploy button, a rollback, a
/// promotion) must be gated the same way, and a check that only some of them remembered to make
/// would be the exact bypass this feature exists to close. <paramref name="approverFinder"/>,
/// <paramref name="approvalOptions"/>, <paramref name="git"/> and <paramref name="protector"/> stay
/// optional for the same test-construction reason as <paramref name="scheduler"/> above; none of
/// them are consulted unless the target environment is actually protected, so a fixture that never
/// exercises 5.2 never has to learn about it.
/// </para>
/// </summary>
public sealed class DeploymentEngine(
    HarboraDbContext db,
    IJobQueue jobs,
    ISystemClock clock,
    IQuotaService? quota = null,
    ISchedulerService? scheduler = null,
    IOptions<Monitoring.MonitoringOptions>? monitoringOptions = null,
    IAuditLogger? audit = null,
    INotificationService? notifications = null,
    DeploymentApproverFinder? approverFinder = null,
    IOptions<DeploymentApprovalOptions>? approvalOptions = null,
    IGitService? git = null,
    ISecretProtector? protector = null) : IDeploymentEngine
{
    public async Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct)
    {
        // Queuing runs for whoever asked: a controller that has already checked ownership and
        // capability, or a webhook, which has no session at all. Under the tenant filter that second
        // caller sees no apps, so a push deployed nothing and said "App not found" about an app that
        // exists. The workspace is not assumed — it is read off the app below and stamped on the
        // deployment, so the row still belongs to exactly one tenant.
        //
        var app = await db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == request.AppId, ct)
                  ?? throw new InvalidOperationException("App not found.");

        // 5.2: a second, deliberately separate query rather than an Include on the one above — an
        // Include tied Environment's own IgnoreQueryFilters to App's in a way the in-memory provider
        // (this class's own test suite) resolves differently than Npgsql does, and a query this
        // central is not the place to find that out from a flaky assumption. GitRepository/Provider
        // are loaded the same deliberate way, inside ResolvePinnedGitRefAsync, only when actually
        // needed rather than on every deploy this method ever queues.
        var environment = await db.Environments.IgnoreQueryFilters()
            .Where(e => e.Id == app.EnvironmentId)
            .Select(e => new { e.IsProtected, e.Name })
            .FirstOrDefaultAsync(ct);
        await using var quotaReservation = quota is null
            ? NoopQuotaReservation.Instance
            : await quota.AcquireCreationLockAsync(app.WorkspaceId, ct);

        // At most one active deployment per app (H3), widened by 5.2 to also cover a deployment
        // sitting PendingApproval: a second request for the same app while one is still waiting on a
        // person must be coalesced/refused exactly like one arriving mid-build already is, or a
        // protected environment could pile up parallel approval requests for the same app.
        var unsettledStatuses = DeploymentStateMachine.Unsettled.ToArray();
        var inFlight = await db.Deployments.IgnoreQueryFilters()
            .Where(d => d.AppId == app.Id && unsettledStatuses.Contains(d.Status))
            .OrderByDescending(d => d.Number)
            .Select(d => new { d.Id, d.Number, d.RolledBackFromId })
            .FirstOrDefaultAsync(ct);

        if (inFlight is not null)
        {
            var inFlightIsRollback = inFlight.RolledBackFromId is not null;
            var requestIsRollback = request.RollbackToDeploymentId is not null;

            if (inFlightIsRollback != requestIsRollback)
                throw new InvalidOperationException(
                    requestIsRollback
                        ? $"Deployment #{inFlight.Number} is still running. Wait for it to finish or cancel it, then roll back."
                        : $"A rollback (deployment #{inFlight.Number}) is still running. Wait for it to finish, then deploy.");

            return inFlight.Id;
        }

        var requiresApproval = environment?.IsProtected == true;

        // Not protected: the ordinary path, unchanged from before 5.2.
        if (!requiresApproval)
        {
            await PreflightAsync(app, ct);
            var deployment = await CreateDeploymentRowAsync(app, request, DeploymentStatus.Queued, ct);
            await jobs.EnqueueExclusiveAsync(
                JobKind.Deployment, deployment.Id, exclusiveWith: app.Id, workspaceId: app.WorkspaceId, ct);
            await quotaReservation.CommitAsync(ct);
            return deployment.Id;
        }

        // 5.2: protected. Resource checks (quota/capacity/disk) are deliberately NOT run here —
        // asking permission to deploy costs nothing, and a wait for approval can stretch long enough
        // that a check made now would be stale by the time it matters. ApproveAsync runs the same
        // PreflightAsync this method would have, at the moment the deployment is actually about to
        // queue.
        IReadOnlyList<Guid> eligibleApprovers = approverFinder is null
            ? []
            : await approverFinder.EligibleApproversAsync(
                app.Id, app.WorkspaceId, Capabilities.AppsDeploy, request.TriggeredByUserId, ct);

        if (DeploymentApprovalPlan.AutoApproveForLackOfSecondApprover(eligibleApprovers.Count))
        {
            // Nobody else in the workspace could ever approve this — see DeploymentApprovalPlan's own
            // doc for why this deploys immediately rather than block for ever or let the requester
            // approve their own release. Still resource-checked, exactly like an unprotected deploy.
            await PreflightAsync(app, ct);
            var deployment = await CreateDeploymentRowAsync(app, request, DeploymentStatus.Queued, ct);

            db.DeploymentApprovals.Add(new DeploymentApproval
            {
                DeploymentId = deployment.Id,
                WorkspaceId = app.WorkspaceId,
                RequestedAt = clock.UtcNow,
                ExpiresAt = clock.UtcNow,
                Decision = DeploymentApprovalDecision.Approved,
                DecidedAt = clock.UtcNow,
                AutoApprovedNoSecondApprover = true,
                ReasonText = "No second eligible approver exists in this workspace, so the protected-" +
                             "environment gate approved this deploy itself rather than block it forever."
            });
            await db.SaveChangesAsync(ct);

            if (audit is not null)
                await audit.LogAsync("deployment.approval.autoapproved", "deployment", deployment.Id.ToString(),
                    workspaceId: app.WorkspaceId,
                    metadataJson: $"{{\"appId\":\"{app.Id}\",\"reason\":\"no_second_approver\"}}", ct: ct);

            await jobs.EnqueueExclusiveAsync(
                JobKind.Deployment, deployment.Id, exclusiveWith: app.Id, workspaceId: app.WorkspaceId, ct);
            await quotaReservation.CommitAsync(ct);
            return deployment.Id;
        }

        // The ordinary protected path: sits PendingApproval, no Job, until a person decides.
        var (pinnedRef, pinned) = await ResolvePinnedGitRefAsync(app, request, ct);
        var pending = await CreateDeploymentRowAsync(
            app, request with { GitRef = pinnedRef }, DeploymentStatus.PendingApproval, ct);

        var window = approvalOptions?.Value.ExpiryWindow ?? new DeploymentApprovalOptions().ExpiryWindow;
        db.DeploymentApprovals.Add(new DeploymentApproval
        {
            DeploymentId = pending.Id,
            WorkspaceId = app.WorkspaceId,
            RequestedAt = clock.UtcNow,
            ExpiresAt = clock.UtcNow + window,
            Decision = DeploymentApprovalDecision.Pending,
            CommitPinned = pinned
        });
        await db.SaveChangesAsync(ct);
        await quotaReservation.CommitAsync(ct);

        if (audit is not null)
            await audit.LogAsync("deployment.approval.requested", "deployment", pending.Id.ToString(),
                workspaceId: app.WorkspaceId, metadataJson: $"{{\"appId\":\"{app.Id}\"}}", ct: ct);

        if (notifications is not null)
            await notifications.NotifyAsync(app.WorkspaceId,
                NotificationEventData.Create(AlertEvent.DeploymentPendingApproval,
                    ("AppName", app.Name), ("DeploymentNumber", pending.Number.ToString()),
                    ("EnvironmentName", environment?.Name ?? "")),
                AlertSeverity.Warning, ct);

        return pending.Id;
    }

    /// <summary>
    /// Approves a deployment waiting on a protected environment's gate. Re-runs the exact resource
    /// checks an unprotected deploy would have run at queue time — see the class doc on why they are
    /// deferred this far.
    /// </summary>
    public async Task ApproveAsync(Guid deploymentId, Guid approverUserId, CancellationToken ct)
    {
        var deployment = await db.Deployments.Include(d => d.App).ThenInclude(a => a!.Environment)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
            ?? throw new InvalidOperationException("Deployment not found.");
        var approval = await db.DeploymentApprovals.FirstOrDefaultAsync(a => a.DeploymentId == deploymentId, ct)
            ?? throw new InvalidOperationException(
                $"Deployment #{deployment.Number} was never gated on approval.");

        var refusal = DeploymentApprovalPlan.RefuseDecision(
            deployment.TriggeredByUserId, approverUserId, approval.Decision);
        if (refusal is not null) throw new InvalidOperationException(refusal);

        if (deployment.Status != DeploymentStatus.PendingApproval)
            throw new InvalidOperationException(
                $"Deployment #{deployment.Number} is {deployment.Status}, not pending approval.");

        var app = deployment.App ?? throw new InvalidOperationException("The app this deployment belongs to no longer exists.");
        await using var reservation = quota is null
            ? NoopQuotaReservation.Instance
            : await quota.AcquireCreationLockAsync(app.WorkspaceId, ct);

        // Thrown before anything below is mutated: a resource refusal here leaves the deployment
        // exactly PendingApproval and the approval exactly Pending, so trying again later — once
        // capacity frees up — is a plain retry, not a second request.
        await PreflightAsync(app, ct);

        DeploymentStateMachine.Transition(deployment, DeploymentStatus.Queued, clock.UtcNow);
        approval.Decision = DeploymentApprovalDecision.Approved;
        approval.DecidedByUserId = approverUserId;
        approval.DecidedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
        await jobs.EnqueueExclusiveAsync(
            JobKind.Deployment, deployment.Id, exclusiveWith: app.Id, workspaceId: app.WorkspaceId, ct);
        await reservation.CommitAsync(ct);

        if (audit is not null)
            await audit.LogAsync("deployment.approval.approved", "deployment", deployment.Id.ToString(),
                userIdOverride: approverUserId, workspaceId: app.WorkspaceId,
                metadataJson: $"{{\"appId\":\"{app.Id}\"}}", ct: ct);
    }

    /// <summary>Rejects a deployment waiting on a protected environment's gate. Ends it Cancelled —
    /// the same terminal status a requester's own withdrawal uses — with the reason recorded on the
    /// approval row, which is what tells the two apart on screen.</summary>
    public async Task RejectAsync(Guid deploymentId, Guid approverUserId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A rejection must say why.", nameof(reason));

        var deployment = await db.Deployments.FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
            ?? throw new InvalidOperationException("Deployment not found.");
        var approval = await db.DeploymentApprovals.FirstOrDefaultAsync(a => a.DeploymentId == deploymentId, ct)
            ?? throw new InvalidOperationException(
                $"Deployment #{deployment.Number} was never gated on approval.");

        var refusal = DeploymentApprovalPlan.RefuseDecision(
            deployment.TriggeredByUserId, approverUserId, approval.Decision);
        if (refusal is not null) throw new InvalidOperationException(refusal);

        if (deployment.Status != DeploymentStatus.PendingApproval)
            throw new InvalidOperationException(
                $"Deployment #{deployment.Number} is {deployment.Status}, not pending approval.");

        DeploymentStateMachine.Transition(deployment, DeploymentStatus.Cancelled, clock.UtcNow);
        deployment.ErrorMessage = $"Rejected by an approver: {reason}";

        approval.Decision = DeploymentApprovalDecision.Rejected;
        approval.DecidedByUserId = approverUserId;
        approval.DecidedAt = clock.UtcNow;
        approval.ReasonText = reason;

        await db.SaveChangesAsync(ct);

        if (audit is not null)
            await audit.LogAsync("deployment.approval.rejected", "deployment", deployment.Id.ToString(),
                userIdOverride: approverUserId, workspaceId: deployment.WorkspaceId,
                metadataJson: $"{{\"reason\":{System.Text.Json.JsonSerializer.Serialize(reason)}}}", ct: ct);
    }

    /// <summary>
    /// Cancels a deployment. Goes through <see cref="DeploymentStateMachine"/> like every other
    /// status change (ADR-004) rather than writing the column directly, so an already-terminal
    /// deployment is a silent no-op instead of an illegal backwards transition.
    ///
    /// <para>
    /// 5.2: also the withdraw path for a deployment still PendingApproval — there is no Job to
    /// signal in that case (none was ever enqueued), so <see cref="IJobQueue.RequestCancellationAsync"/>
    /// below is a harmless no-op for it, exactly as it already is for any other id with no live job.
    /// </para>
    /// </summary>
    public async Task CancelAsync(Guid deploymentId, CancellationToken ct)
    {
        var deployment = await db.Deployments.FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (deployment is null) return;
        if (!DeploymentStateMachine.CanTransition(deployment.Status, DeploymentStatus.Cancelled)) return;

        DeploymentStateMachine.Transition(deployment, DeploymentStatus.Cancelled, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        // Stop the work as well as the record: a queued job is settled before it starts, and a
        // running pipeline is signalled through its cancellation token.
        await jobs.RequestCancellationAsync(JobKind.Deployment, deploymentId, ct);
    }

    /// <summary>
    /// The three resource refusals an ordinary deploy checks at queue time (P7). Factored out of
    /// <see cref="QueueDeploymentAsync"/> so <see cref="ApproveAsync"/> can run the identical checks
    /// again, right before a protected deploy actually queues, instead of trusting a snapshot taken
    /// whenever the request happened to be made.
    /// </summary>
    private async Task PreflightAsync(App app, CancellationToken ct)
    {
        if (quota is not null)
        {
            var mayQueue = await quota.CanQueueDeploymentAsync(app.WorkspaceId, ct);
            if (!mayQueue.Allowed) throw new QuotaRefusedException(mayQueue);
        }

        // P7: "SchedulerService.CheckAsync already exists and is called once. Calling it at queue
        // time is the whole of the item." The node this app was placed on when it was created can
        // have filled up since — another app's growth, a host that shrank its own headroom — and
        // building a release nobody can run is a worse failure than refusing before the build ever
        // starts.
        if (scheduler is not null)
        {
            var placement = await scheduler.CheckAsync(app.ServerId, app.MemoryLimitBytes, app.CpuLimit, ct);
            if (!placement.Ok) throw new CapacityRefusedException(placement);
        }

        // P7, the owner's answer to §7 Q5: refuses rather than warns.
        if (monitoringOptions is not null)
        {
            var threshold = monitoringOptions.Value.DeployMinFreeDiskBytes;
            var freeBytes = await db.Nodes.AsNoTracking()
                .Where(n => n.ServerId == app.ServerId).Select(n => (long?)n.FreeDiskBytes).FirstOrDefaultAsync(ct);

            if (freeBytes is > 0 && freeBytes < threshold)
            {
                var freeText = Services.StorageMeasurement.Describe(freeBytes);
                var thresholdText = Services.StorageMeasurement.Describe(threshold);
                throw new LowDiskRefusedException(freeBytes.Value, threshold,
                    reason: $"Only {freeText} free on this node; deploys are refused below {thresholdText}.",
                    reasonFa: $"تنها {freeText} فضای آزاد روی این سرور مانده؛ استقرار زیر {thresholdText} رد می‌شود.");
            }
        }
    }

    private async Task<Deployment> CreateDeploymentRowAsync(
        App app, DeploymentRequest request, DeploymentStatus status, CancellationToken ct)
    {
        // Filtered, this returns nothing for a webhook and every push is "deployment #1", colliding
        // with the numbers the panel already showed.
        var nextNumber = await db.Deployments.IgnoreQueryFilters().Where(d => d.AppId == app.Id)
            .Select(d => (int?)d.Number).MaxAsync(ct) ?? 0;

        var deployment = new Deployment
        {
            AppId = app.Id,
            WorkspaceId = app.WorkspaceId,
            Number = nextNumber + 1,
            Status = status,
            Trigger = request.Trigger,
            GitRef = request.GitRef ?? app.GitRef,
            CommitSha = request.CommitSha,
            TriggeredByUserId = request.TriggeredByUserId,
            RolledBackFromId = request.RollbackToDeploymentId,
            SourceArchivePath = request.SourceArchivePath,
            ForceRebuild = request.ForceRebuild,
            // An explicit image is recorded up front so the pipeline releases exactly it.
            ImageTag = request.ImageOverride,
            CreatedAt = clock.UtcNow
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct);
        return deployment;
    }

    /// <summary>
    /// Resolves <paramref name="request"/>'s git ref to the exact commit it names right now, so that
    /// however long a protected deploy waits for approval, it releases the commit that was reviewed —
    /// never a later push to the same branch. "Approving a deploy and getting a different one" is
    /// exactly the defect class this codebase exists to avoid (see <c>PromotionPlan</c>'s own doc for
    /// the identical argument about a promoted image).
    ///
    /// <para>
    /// Three ways this can honestly end. <b>Already exact</b>: <paramref name="request"/> already
    /// carries a <c>CommitSha</c> — a webhook push already resolved one — so that is what is pinned,
    /// no git call needed. <b>Resolved</b>: no exact commit was given, but the app has a linked Git
    /// repository, so its ref is listed and the matching SHA is pinned. <b>Not pinned</b>: no Git
    /// repository at all (an image, an upload, a static bundle — already one fixed artifact by
    /// construction), or the listing failed. The last case never blocks the request — a git remote
    /// being briefly unreachable must not be why an approval request cannot even be filed — it is
    /// instead reported honestly: <see cref="DeploymentApproval.CommitPinned"/> stays false and the
    /// panel says plainly that this deploy will build whatever the ref names when it actually runs.
    /// </para>
    /// </summary>
    private async Task<(string? GitRef, bool Pinned)> ResolvePinnedGitRefAsync(
        App app, DeploymentRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.CommitSha))
            return (request.CommitSha, true);

        if (app.GitRepositoryId is not { } repoId || git is null)
            return (request.GitRef, false);

        try
        {
            // Loaded here, deliberately not on QueueDeploymentAsync's own query — see that method's
            // own comment on why Environment moved off an Include, and this never even runs unless
            // the deploy is both protected and Git-sourced.
            var repo = await db.GitRepositories.AsNoTracking().Include(r => r.Provider)
                .FirstOrDefaultAsync(r => r.Id == repoId, ct);
            if (repo is null) return (request.GitRef, false);

            var refName = request.GitRef ?? app.GitRef ?? repo.DefaultBranch;
            var token = repo.Provider?.EncryptedCredential is { Length: > 0 } enc
                ? SafeUnprotect(enc) : null;

            var refs = await git.ListRefsAsync(repo.CloneUrl, token, ct);
            var match = refs.FirstOrDefault(r => string.Equals(r.Name, refName, StringComparison.Ordinal));
            return match is not null ? (match.Sha, true) : (refName, false);
        }
        catch
        {
            // Best-effort: a git remote being briefly unreachable must not be why a protected
            // deployment cannot even be requested. Falls back to the ref as given, honestly marked
            // unpinned — see this method's own doc.
            return (request.GitRef, false);
        }
    }

    private string? SafeUnprotect(string value)
    {
        if (protector is null) return null;
        try { return protector.Unprotect(value); }
        catch { return null; }
    }
}
