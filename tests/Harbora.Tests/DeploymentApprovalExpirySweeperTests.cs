using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Closes an approval request nobody answered (5.2, 2026-09 market-gaps round two) — the bounded
/// backstop <see cref="DeploymentApprovalExpirySweeper"/> is, mirroring
/// <c>IncidentService.ExpireStaleAsync</c>'s own reasoning for the identical shape of problem.
/// </summary>
public class DeploymentApprovalExpirySweeperTests
{
    private sealed class RecordingAudit : IAuditLogger
    {
        public List<string> Actions { get; } = [];
        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, Guid? workspaceId = null, CancellationToken ct = default)
        { Actions.Add(action); return Task.CompletedTask; }
    }

    private sealed class Clock(DateTimeOffset now) : ISystemClock { public DateTimeOffset UtcNow => now; }

    private static (ServiceProvider Provider, HarboraDbContext Db) Build(DateTimeOffset now, RecordingAudit? audit = null)
    {
        var services = new ServiceCollection();
        var dbName = "expiry-" + Guid.NewGuid();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<ISystemClock>(new Clock(now));
        services.AddSingleton(new NullLoggerFactory());
        services.AddLogging();
        if (audit is not null) services.AddSingleton<IAuditLogger>(audit);

        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<HarboraDbContext>();
        return (provider, db);
    }

    private static (App App, Deployment Deployment, DeploymentApproval Approval) SeedPending(
        HarboraDbContext db, DateTimeOffset requestedAt, DateTimeOffset expiresAt)
    {
        var workspaceId = Guid.NewGuid();
        var project = new Harbora.Domain.Projects.Project { WorkspaceId = workspaceId, Name = "p", Slug = "p" };
        var env = new Harbora.Domain.Projects.Environment
        { WorkspaceId = workspaceId, ProjectId = project.Id, Name = "prod", Slug = "prod", IsProtected = true };
        var app = new App { WorkspaceId = workspaceId, EnvironmentId = env.Id, ServerId = Guid.NewGuid(), Name = "web", Slug = "web" };
        var deployment = new Deployment
        {
            AppId = app.Id, WorkspaceId = workspaceId, Number = 1, Status = DeploymentStatus.PendingApproval,
            TriggeredByUserId = Guid.NewGuid()
        };
        var approval = new DeploymentApproval
        {
            DeploymentId = deployment.Id, WorkspaceId = workspaceId,
            RequestedAt = requestedAt, ExpiresAt = expiresAt, Decision = DeploymentApprovalDecision.Pending
        };
        db.Projects.Add(project); db.Environments.Add(env); db.Apps.Add(app);
        db.Deployments.Add(deployment); db.DeploymentApprovals.Add(approval);
        db.SaveChanges();
        return (app, deployment, approval);
    }

    [Fact]
    public async Task A_pending_request_past_its_deadline_expires_and_cancels_the_deployment()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var audit = new RecordingAudit();
        var (provider, db) = Build(now, audit);
        using var _ = provider;
        var (_, deployment, approval) = SeedPending(db, now.AddHours(-25), now.AddHours(-1)); // due an hour ago

        var sweeper = new DeploymentApprovalExpirySweeper(
            provider.GetRequiredService<IServiceScopeFactory>(), new Clock(now),
            NullLogger<DeploymentApprovalExpirySweeper>.Instance);

        var expired = await sweeper.SweepAsync(default);

        expired.Should().Be(1);
        // AsNoTracking: the sweep ran in its own scope against its own DbContext instance, so this
        // context's change tracker is still holding the pre-sweep object it seeded — a tracked read
        // would hand back that stale in-memory copy instead of what was actually persisted.
        var settled = await db.Deployments.AsNoTracking().SingleAsync(d => d.Id == deployment.Id);
        settled.Status.Should().Be(DeploymentStatus.Cancelled);
        settled.ErrorMessage.Should().Contain("expired");

        var settledApproval = await db.DeploymentApprovals.AsNoTracking().SingleAsync(a => a.Id == approval.Id);
        settledApproval.Decision.Should().Be(DeploymentApprovalDecision.Expired);
        settledApproval.DecidedByUserId.Should().BeNull("nobody decided — the clock did");

        audit.Actions.Should().Contain("deployment.approval.expired");
    }

    [Fact]
    public async Task A_request_not_yet_due_is_left_alone()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var (provider, db) = Build(now);
        using var _ = provider;
        var (_, deployment, _) = SeedPending(db, now.AddHours(-1), now.AddHours(23)); // due tomorrow

        var sweeper = new DeploymentApprovalExpirySweeper(
            provider.GetRequiredService<IServiceScopeFactory>(), new Clock(now),
            NullLogger<DeploymentApprovalExpirySweeper>.Instance);

        var expired = await sweeper.SweepAsync(default);

        expired.Should().Be(0);
        var untouched = await db.Deployments.SingleAsync(d => d.Id == deployment.Id);
        untouched.Status.Should().Be(DeploymentStatus.PendingApproval);
    }

    [Fact]
    public async Task An_already_decided_approval_is_never_touched_even_if_its_deadline_passed()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var (provider, db) = Build(now);
        using var _ = provider;
        var (_, deployment, approval) = SeedPending(db, now.AddHours(-25), now.AddHours(-1));
        approval.Decision = DeploymentApprovalDecision.Approved;
        approval.DecidedAt = now.AddHours(-2);
        approval.DecidedByUserId = Guid.NewGuid();
        deployment.Status = DeploymentStatus.Queued;
        db.SaveChanges();

        var sweeper = new DeploymentApprovalExpirySweeper(
            provider.GetRequiredService<IServiceScopeFactory>(), new Clock(now),
            NullLogger<DeploymentApprovalExpirySweeper>.Instance);

        var expired = await sweeper.SweepAsync(default);

        expired.Should().Be(0);
        var untouched = await db.DeploymentApprovals.SingleAsync(a => a.Id == approval.Id);
        untouched.Decision.Should().Be(DeploymentApprovalDecision.Approved);
    }

    [Fact]
    public async Task Sweeping_an_empty_table_is_a_clean_no_op()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, _) = Build(now);
        using var _ = provider;

        var sweeper = new DeploymentApprovalExpirySweeper(
            provider.GetRequiredService<IServiceScopeFactory>(), new Clock(now),
            NullLogger<DeploymentApprovalExpirySweeper>.Instance);

        (await sweeper.SweepAsync(default)).Should().Be(0);
    }
}
