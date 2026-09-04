using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 5.2 (2026-09 market-gaps round two, "approval gate on deploying to a protected environment"): a
/// deploy to a protected environment enters <see cref="DeploymentStatus.PendingApproval"/> with no
/// <c>Job</c> behind it, and only <see cref="DeploymentEngine.ApproveAsync"/> ever creates one.
///
/// <para>
/// Asserted at the job-queue seam rather than only the status column, because a status the panel
/// prints and a job the queue actually runs are two different facts — <c>Job.Kind == Deployment</c>
/// existing at all is the one thing that could ever reach <c>DeploymentPipeline</c> and, through it,
/// the container engine. Zero rows there is the proof nothing downstream was ever asked to build
/// anything, which a status field alone could not show if <see cref="DeploymentEngine.QueueDeploymentAsync"/>
/// ever grew a code path that set the column without going through the queue.
/// </para>
/// </summary>
public class DeploymentApprovalGateTests
{
    /// <summary>
    /// Writes a real <see cref="Job"/> row into the same in-memory database the test asserts against
    /// — the seam this whole file cares about. A queue that only counted calls could not tell "asked
    /// to enqueue" from "a row now exists that <c>JobWorker</c>/<c>DeploymentPipeline</c> could claim",
    /// and those are exactly the two facts a status column alone cannot distinguish either.
    /// </summary>
    private sealed class NoopQueue(HarboraDbContext db) : IJobQueue
    {
        public int Enqueued;
        public Task<Guid> EnqueueAsync(JobKind kind, Guid targetId, Guid? workspaceId = null, CancellationToken ct = default) =>
            Add(kind, targetId, null, workspaceId);
        public Task<Guid> EnqueueExclusiveAsync(JobKind kind, Guid targetId, Guid exclusiveWith, Guid? workspaceId = null, CancellationToken ct = default) =>
            Add(kind, targetId, exclusiveWith, workspaceId);
        public Task<bool> RequestCancellationAsync(JobKind kind, Guid targetId, CancellationToken ct = default) => Task.FromResult(true);

        private Task<Guid> Add(JobKind kind, Guid targetId, Guid? exclusiveWith, Guid? workspaceId)
        {
            Enqueued++;
            var job = new Job
            {
                Kind = kind, TargetId = targetId, ExclusiveWith = exclusiveWith, WorkspaceId = workspaceId,
                Status = JobStatus.Pending, CreatedAt = DateTimeOffset.UtcNow
            };
            db.Jobs.Add(job);
            db.SaveChanges();
            return Task.FromResult(job.Id);
        }
    }

    private sealed class Clock(DateTimeOffset now) : ISystemClock { public DateTimeOffset UtcNow { get; set; } = now; }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<(string Action, string? TargetId, Guid? UserId, Guid? WorkspaceId)> Entries { get; } = [];

        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, Guid? workspaceId = null, CancellationToken ct = default)
        {
            Entries.Add((action, targetId, userIdOverride, workspaceId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public List<(Guid WorkspaceId, AlertEvent Type, AlertSeverity Severity)> Notified { get; } = [];

        public Task<int> NotifyAsync(Guid workspaceId, NotificationEventData evt, AlertSeverity severity, CancellationToken ct)
        { Notified.Add((workspaceId, evt.Type, severity)); return Task.FromResult(0); }
        public Task NotifyInAppOnlyAsync(Guid workspaceId, NotificationEventData evt, AlertSeverity severity, CancellationToken ct)
            => Task.CompletedTask;
        public Task<NotificationResult> NotifyRuleAsync(Guid alertId, NotificationEventData evt, AlertSeverity severity, CancellationToken ct)
            => Task.FromResult(NotificationResult.Ok);
        public Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct) => Task.FromResult(NotificationResult.Ok);
        public Task ExecuteQueuedDeliveryAsync(Guid deliveryId, CancellationToken ct) => Task.CompletedTask;
        public Task SendTelegramAsync(string encryptedTarget, string title, string body, CancellationToken ct) => Task.CompletedTask;
    }

    private static HarboraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("approval-" + Guid.NewGuid()).Options);

    private static (App App, Guid WorkspaceId) SeedApp(
        HarboraDbContext db, bool protectedEnvironment, Guid? serverId = null)
    {
        var workspaceId = Guid.NewGuid();
        var project = new Harbora.Domain.Projects.Project { WorkspaceId = workspaceId, Name = "shop", Slug = "shop" };
        var env = new Harbora.Domain.Projects.Environment
        {
            WorkspaceId = workspaceId, ProjectId = project.Id, Name = "production", Slug = "production",
            IsProtected = protectedEnvironment
        };
        var app = new App
        {
            WorkspaceId = workspaceId, EnvironmentId = env.Id, ServerId = serverId ?? Guid.CreateVersion7(),
            Name = "web", Slug = "web"
        };
        db.Projects.Add(project);
        db.Environments.Add(env);
        db.Apps.Add(app);
        db.SaveChanges();
        return (app, workspaceId);
    }

    /// <summary>Adds an active member who can approve/deploy — Admin is unscoped, so it is the
    /// simplest "somebody else eligible" fixture without also seeding a ProjectGrant.</summary>
    private static Guid AddEligibleApprover(HarboraDbContext db, Guid workspaceId, string email = "reviewer@example.com")
    {
        var userId = Guid.NewGuid();
        db.Users.Add(new User { Id = userId, Email = email, DisplayName = "Reviewer", PasswordHash = "x" });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Admin, ScopedToProjects = false
        });
        db.SaveChanges();
        return userId;
    }

    // ---- the gate itself ------------------------------------------------------------------------

    [Fact]
    public async Task A_deploy_to_a_protected_environment_does_not_reach_the_job_queue()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        var requester = Guid.NewGuid();
        AddEligibleApprover(db, workspaceId); // otherwise this auto-approves — see its own test below
        var queue = new NoopQueue(db);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, queue, new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, requester), default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.Status.Should().Be(DeploymentStatus.PendingApproval);

        // The seam: no Job row for this deployment at all, in any status — nothing has been handed
        // to the queue that JobWorker/DeploymentPipeline could ever claim and run.
        db.Jobs.Where(j => j.Kind == JobKind.Deployment && j.TargetId == id).Should().BeEmpty();
        queue.Enqueued.Should().Be(0);
    }

    /// <summary>
    /// PromotionPlan/DeploymentsController.Promote both call
    /// <see cref="DeploymentEngine.QueueDeploymentAsync"/> exactly like every other trigger — a
    /// promotion carries no special path around the gate, so a protected target is gated the same
    /// way a plain redeploy is, with no separate check anyone could forget to add to Promote itself.
    /// </summary>
    [Fact]
    public async Task A_promotion_into_a_protected_environment_is_gated_exactly_like_any_other_deploy()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        AddEligibleApprover(db, workspaceId);
        var queue = new NoopQueue(db);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, queue, new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        // The exact shape DeploymentsController.Promote builds: no GitRef, an exact image instead.
        var id = await engine.QueueDeploymentAsync(new DeploymentRequest(
            app.Id, DeploymentTrigger.Manual, Guid.NewGuid(), ImageOverride: "harbora/shop:build-42"), default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.Status.Should().Be(DeploymentStatus.PendingApproval);
        deployment.ImageTag.Should().Be("harbora/shop:build-42",
            "the promoted artifact is recorded up front — approving it later must release this exact image");
        queue.Enqueued.Should().Be(0);

        var approval = await db.DeploymentApprovals.SingleAsync(a => a.DeploymentId == id);
        // An image promotion already names one fixed artifact, the same reason a webhook's own
        // CommitSha does — there is no branch for a later push to move out from under it, so the
        // commit-pinning question this gate asks for Git sources does not even apply here.
        approval.CommitPinned.Should().BeFalse();
    }

    [Fact]
    public async Task An_unprotected_environment_is_unaffected()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: false);
        AddEligibleApprover(db, workspaceId);
        var queue = new NoopQueue(db);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, queue, new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.Status.Should().Be(DeploymentStatus.Queued);
        queue.Enqueued.Should().Be(1);
        db.DeploymentApprovals.Should().BeEmpty("an unprotected deploy never touches the approval table at all");
    }

    [Fact]
    public async Task Approval_moves_it_into_the_ordinary_queue_and_enqueues_the_job()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        var requester = Guid.NewGuid();
        var approver = AddEligibleApprover(db, workspaceId);
        var queue = new NoopQueue(db);
        var finder = new DeploymentApproverFinder(db);
        var audit = new RecordingAudit();
        var engine = new DeploymentEngine(db, queue, new Clock(DateTimeOffset.UtcNow), approverFinder: finder, audit: audit);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, requester), default);
        queue.Enqueued.Should().Be(0, "still nothing queued before the approval");

        await engine.ApproveAsync(id, approver, default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.Status.Should().Be(DeploymentStatus.Queued);
        queue.Enqueued.Should().Be(1);
        db.Jobs.Should().ContainSingle(j => j.Kind == JobKind.Deployment && j.TargetId == id);

        var approval = await db.DeploymentApprovals.SingleAsync(a => a.DeploymentId == id);
        approval.Decision.Should().Be(DeploymentApprovalDecision.Approved);
        approval.DecidedByUserId.Should().Be(approver);
        approval.DecidedAt.Should().NotBeNull();

        audit.Entries.Should().Contain(e => e.Action == "deployment.approval.approved" && e.UserId == approver);
    }

    [Fact]
    public async Task The_requester_cannot_approve_their_own_deployment()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        var requester = Guid.NewGuid();
        AddEligibleApprover(db, workspaceId); // so this genuinely waits rather than auto-approving
        var queue = new NoopQueue(db);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, queue, new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, requester), default);

        var attempt = () => engine.ApproveAsync(id, requester, default);

        (await attempt.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("cannot also approve");

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.Status.Should().Be(DeploymentStatus.PendingApproval, "a refused approval must not move it");
        queue.Enqueued.Should().Be(0);
    }

    [Fact]
    public async Task The_requester_cannot_reject_their_own_deployment_either()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        var requester = Guid.NewGuid();
        AddEligibleApprover(db, workspaceId);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, requester), default);

        var attempt = () => engine.RejectAsync(id, requester, "not today", default);

        await attempt.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_rejection_ends_the_deployment_cancelled_with_the_reason_recorded_and_audited()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        var requester = Guid.NewGuid();
        var approver = AddEligibleApprover(db, workspaceId);
        var audit = new RecordingAudit();
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder, audit: audit);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, requester), default);

        await engine.RejectAsync(id, approver, "This is production, not a scratch branch.", default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.Status.Should().Be(DeploymentStatus.Cancelled);
        deployment.ErrorMessage.Should().Contain("scratch branch");

        var approval = await db.DeploymentApprovals.SingleAsync(a => a.DeploymentId == id);
        approval.Decision.Should().Be(DeploymentApprovalDecision.Rejected);
        approval.DecidedByUserId.Should().Be(approver);
        approval.ReasonText.Should().Contain("scratch branch");

        audit.Entries.Should().Contain(e => e.Action == "deployment.approval.rejected" && e.UserId == approver);
    }

    [Fact]
    public async Task A_rejection_needs_a_reason()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        var approver = AddEligibleApprover(db, workspaceId);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        var attempt = () => engine.RejectAsync(id, approver, "   ", default);

        await attempt.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Deciding_an_already_decided_approval_is_refused()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        var approver = AddEligibleApprover(db, workspaceId);
        var secondApprover = AddEligibleApprover(db, workspaceId, "second@example.com");
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);
        await engine.ApproveAsync(id, approver, default);

        var attempt = () => engine.ApproveAsync(id, secondApprover, default);
        await attempt.Should().ThrowAsync<InvalidOperationException>();

        var rejectAttempt = () => engine.RejectAsync(id, secondApprover, "too late", default);
        await rejectAttempt.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- the sole-approver exception --------------------------------------------------------------

    [Fact]
    public async Task Nobody_else_eligible_deploys_immediately_and_says_so()
    {
        using var db = NewDb();
        // Deliberately no second approver seeded.
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        var requester = Guid.NewGuid();
        var queue = new NoopQueue(db);
        var finder = new DeploymentApproverFinder(db);
        var audit = new RecordingAudit();
        var engine = new DeploymentEngine(db, queue, new Clock(DateTimeOffset.UtcNow), approverFinder: finder, audit: audit);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, requester), default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.Status.Should().Be(DeploymentStatus.Queued, "blocking forever would be worse than deploying");
        queue.Enqueued.Should().Be(1);

        var approval = await db.DeploymentApprovals.SingleAsync(a => a.DeploymentId == id);
        approval.AutoApprovedNoSecondApprover.Should().BeTrue();
        approval.Decision.Should().Be(DeploymentApprovalDecision.Approved);

        // Loud, not silent: named in the audit trail by a distinct action, not folded into an
        // ordinary "approved" that would hide that nobody actually looked at it.
        audit.Entries.Should().Contain(e => e.Action == "deployment.approval.autoapproved");
    }

    [Fact]
    public async Task No_approver_finder_wired_treats_a_protected_deploy_conservatively()
    {
        // A misconfigured DI container (approverFinder never registered) must not silently bypass the
        // gate — see the class doc. Zero known approvers, by construction of the null case, reads the
        // same as zero eligible: this deploys immediately rather than hang forever with no path out.
        using var db = NewDb();
        var (app, _) = SeedApp(db, protectedEnvironment: true);
        var queue = new NoopQueue(db);
        var engine = new DeploymentEngine(db, queue, new Clock(DateTimeOffset.UtcNow));

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.Status.Should().Be(DeploymentStatus.Queued);
    }

    // ---- coalescing --------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_request_while_one_waits_on_approval_is_coalesced()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        AddEligibleApprover(db, workspaceId);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        var first = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);
        var second = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        second.Should().Be(first, "one app has at most one unsettled deployment, pending approval or not");
        db.Deployments.Count(d => d.AppId == app.Id).Should().Be(1);
    }

    // ---- expiry visibility --------------------------------------------------------------------------

    [Fact]
    public async Task The_expiry_deadline_is_stamped_at_request_time_using_the_configured_window()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        AddEligibleApprover(db, workspaceId);
        var now = new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
        var finder = new DeploymentApproverFinder(db);
        var options = Microsoft.Extensions.Options.Options.Create(
            new DeploymentApprovalOptions { ExpiryWindow = TimeSpan.FromHours(6) });
        var engine = new DeploymentEngine(
            db, new NoopQueue(db), new Clock(now), approverFinder: finder, approvalOptions: options);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        var approval = await db.DeploymentApprovals.SingleAsync(a => a.DeploymentId == id);
        // Visible the moment the request is made — not computed lazily when somebody happens to ask.
        approval.ExpiresAt.Should().Be(now.AddHours(6));
        approval.RequestedAt.Should().Be(now);
    }

    // ---- notification ---------------------------------------------------------------------------

    [Fact]
    public async Task Approvers_are_notified_when_a_deploy_starts_waiting()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        AddEligibleApprover(db, workspaceId);
        var finder = new DeploymentApproverFinder(db);
        var notifications = new RecordingNotifications();
        var engine = new DeploymentEngine(
            db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder, notifications: notifications);

        await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        notifications.Notified.Should().ContainSingle(n =>
            n.WorkspaceId == workspaceId && n.Type == AlertEvent.DeploymentPendingApproval);
    }

    [Fact]
    public async Task Nobody_is_notified_for_an_unprotected_deploy_or_a_solo_auto_approval()
    {
        using var db = NewDb();
        var (unprotectedApp, unprotectedWorkspace) = SeedApp(db, protectedEnvironment: false);
        var notifications = new RecordingNotifications();
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(
            db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder, notifications: notifications);

        await engine.QueueDeploymentAsync(
            new DeploymentRequest(unprotectedApp.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        using var db2 = NewDb();
        var (soloApp, _) = SeedApp(db2, protectedEnvironment: true); // no second approver seeded
        var finder2 = new DeploymentApproverFinder(db2);
        var engine2 = new DeploymentEngine(
            db2, new NoopQueue(db2), new Clock(DateTimeOffset.UtcNow), approverFinder: finder2, notifications: notifications);

        await engine2.QueueDeploymentAsync(
            new DeploymentRequest(soloApp.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        notifications.Notified.Should().BeEmpty(
            "neither an unprotected deploy nor one nobody else could approve ever waits on anyone");
    }

    // ---- commit pinning --------------------------------------------------------------------------

    [Fact]
    public async Task A_webhook_push_with_an_exact_commit_is_pinned_without_any_git_call()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        AddEligibleApprover(db, workspaceId);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.GitPush, Guid.Empty, GitRef: "main", CommitSha: "abc123def"),
            default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.GitRef.Should().Be("abc123def");

        var approval = await db.DeploymentApprovals.SingleAsync(a => a.DeploymentId == id);
        approval.CommitPinned.Should().BeTrue();
    }

    [Fact]
    public async Task An_app_with_no_git_repository_is_never_pinned_and_never_blocked_by_it()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        AddEligibleApprover(db, workspaceId);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid(), GitRef: "main"), default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.GitRef.Should().Be("main", "nothing could resolve it, so the ref is kept as given");

        var approval = await db.DeploymentApprovals.SingleAsync(a => a.DeploymentId == id);
        approval.CommitPinned.Should().BeFalse();
    }

    private sealed class StubGitService(IReadOnlyList<GitRef> refs) : IGitService
    {
        public Task<GitCheckout> CheckoutAsync(string cloneUrl, string gitRef, string? credentialToken,
            string workingDir, IProgress<string> log, CancellationToken ct) =>
            throw new NotSupportedException("The approval gate only lists refs; it never checks out.");

        public Task<IReadOnlyList<GitRef>> ListRefsAsync(string cloneUrl, string? credentialToken, CancellationToken ct) =>
            Task.FromResult(refs);
    }

    [Fact]
    public async Task A_manual_deploy_with_a_linked_repository_is_pinned_to_the_refs_current_sha()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        AddEligibleApprover(db, workspaceId);

        var provider = new Harbora.Domain.Git.GitProvider { WorkspaceId = workspaceId, Name = "gh", Type = GitProviderType.GitHub };
        var repo = new Harbora.Domain.Git.GitRepository
        {
            GitProviderId = provider.Id, FullName = "acme/shop", CloneUrl = "https://example.invalid/acme/shop.git",
            DefaultBranch = "main"
        };
        db.GitProviders.Add(provider);
        db.GitRepositories.Add(repo);
        app.GitRepositoryId = repo.Id;
        app.GitRef = "main";
        db.SaveChanges();

        var git = new StubGitService([new GitRef("main", "branch", "deadbeef1234")]);
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(
            db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder,
            git: git, protector: new PassthroughProtector());

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.GitRef.Should().Be("deadbeef1234");

        var approval = await db.DeploymentApprovals.SingleAsync(a => a.DeploymentId == id);
        approval.CommitPinned.Should().BeTrue();
    }

    [Fact]
    public async Task A_repository_that_cannot_be_listed_falls_back_to_the_unpinned_ref_rather_than_blocking()
    {
        using var db = NewDb();
        var (app, workspaceId) = SeedApp(db, protectedEnvironment: true);
        AddEligibleApprover(db, workspaceId);

        var provider = new Harbora.Domain.Git.GitProvider { WorkspaceId = workspaceId, Name = "gh", Type = GitProviderType.GitHub };
        var repo = new Harbora.Domain.Git.GitRepository
        {
            GitProviderId = provider.Id, FullName = "acme/shop", CloneUrl = "https://example.invalid/acme/shop.git",
            DefaultBranch = "main"
        };
        db.GitProviders.Add(provider);
        db.GitRepositories.Add(repo);
        app.GitRepositoryId = repo.Id;
        app.GitRef = "main";
        db.SaveChanges();

        var git = new ThrowingGitService();
        var finder = new DeploymentApproverFinder(db);
        var engine = new DeploymentEngine(
            db, new NoopQueue(db), new Clock(DateTimeOffset.UtcNow), approverFinder: finder,
            git: git, protector: new PassthroughProtector());

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        var deployment = await db.Deployments.SingleAsync(d => d.Id == id);
        deployment.Status.Should().Be(DeploymentStatus.PendingApproval, "a git remote hiccup must not refuse the request itself");
        deployment.GitRef.Should().Be("main");

        var approval = await db.DeploymentApprovals.SingleAsync(a => a.DeploymentId == id);
        approval.CommitPinned.Should().BeFalse();
    }

    private sealed class ThrowingGitService : IGitService
    {
        public Task<GitCheckout> CheckoutAsync(string cloneUrl, string gitRef, string? credentialToken,
            string workingDir, IProgress<string> log, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitRef>> ListRefsAsync(string cloneUrl, string? credentialToken, CancellationToken ct) =>
            throw new InvalidOperationException("remote unreachable");
    }
}
