using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Networking;
using Harbora.Domain.Projects;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

public sealed class WorkspaceGovernanceQuotaTests : IDisposable
{
    private readonly HarboraDbContext _db;
    private readonly Guid _workspace = Guid.CreateVersion7();
    private readonly Plan _plan = new() { Name = "Governed", IsEnabled = true };

    public WorkspaceGovernanceQuotaTests()
    {
        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("governance-" + Guid.NewGuid()).Options);
        _db.Plans.Add(_plan);
        _db.Workspaces.Add(new Workspace
        {
            Id = _workspace, Name = "Team", Slug = "team", PlanId = _plan.Id
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private QuotaService Service(bool billing = true) =>
        new(_db, Options.Create(new BillingOptions { Enabled = billing }));

    [Theory]
    [InlineData("src/Harbora.Web/Controllers/AppsController.cs")]
    [InlineData("src/Harbora.Web/Controllers/DatabasesController.cs")]
    [InlineData("src/Harbora.Web/Controllers/BackupsController.cs")]
    [InlineData("src/Harbora.Web/Controllers/UsersController.cs")]
    [InlineData("src/Harbora.Web/Controllers/TenantsController.cs")]
    [InlineData("src/Harbora.Infrastructure/Projects/ProjectService.cs")]
    [InlineData("src/Harbora.Infrastructure/Projects/EnvironmentCloner.cs")]
    [InlineData("src/Harbora.Infrastructure/Projects/PreviewEnvironmentService.cs")]
    [InlineData("src/Harbora.Infrastructure/Templates/TemplateDeploymentService.cs")]
    [InlineData("src/Harbora.Infrastructure/Security/WorkspaceAccountService.cs")]
    public void Every_quota_guarded_creation_surface_takes_the_workspace_creation_lock(string relativePath)
    {
        var root = Path.GetFullPath(Path.Combine(TestPaths.WebRoot, "..", ".."));
        File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Should().Contain("AcquireCreationLockAsync",
                $"{relativePath} creates quota-counted resources and must hold the workspace lock from check through save");
    }

    [Fact]
    public async Task Usage_reports_every_governed_resource_and_pending_seat()
    {
        var user = new User { Email = "member@example.com", DisplayName = "Member", PasswordHash = "hash" };
        _db.Users.Add(user);
        _db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspace, User = user });
        _db.WorkspaceInvitations.AddRange(
            new WorkspaceInvitation
            {
                WorkspaceId = _workspace, Email = "pending@example.com", TokenHash = "pending",
                TokenHint = "pendin", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            },
            new WorkspaceInvitation
            {
                WorkspaceId = _workspace, Email = "expired@example.com", TokenHash = "expired",
                TokenHint = "expire", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
        var project = new Project { WorkspaceId = _workspace, Name = "Product", Slug = "product" };
        _db.Projects.Add(project);
        _db.Environments.Add(new Harbora.Domain.Projects.Environment
        {
            WorkspaceId = _workspace, Project = project, Name = "Production", Slug = "production"
        });
        var app = new App { WorkspaceId = _workspace, Name = "Web", Slug = "web" };
        app.Domains.Add(new DomainName { Host = "web.example.com" });
        app.Volumes.Add(new Volume { Name = "web-data", MountPath = "/data" });
        _db.Apps.Add(app);
        _db.BackupSchedules.Add(new BackupSchedule
        {
            WorkspaceId = _workspace, DestinationId = Guid.CreateVersion7(),
            Type = BackupType.AppConfig, TargetRef = app.Id.ToString()
        });
        await _db.SaveChangesAsync();

        var usage = await Service().GetUsageAsync(_workspace, default);

        usage.Members.Should().Be(2, "one membership and one live invitation reserve seats");
        usage.Projects.Should().Be(1);
        usage.Environments.Should().Be(1);
        usage.Domains.Should().Be(1);
        usage.Volumes.Should().Be(1);
        usage.BackupSchedules.Should().Be(1);
    }

    [Fact]
    public async Task Pending_invitations_reserve_member_capacity()
    {
        _plan.MaxMembers = 1;
        _db.WorkspaceInvitations.Add(new WorkspaceInvitation
        {
            WorkspaceId = _workspace, Email = "pending@example.com", TokenHash = "hash",
            TokenHint = "hint12", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        await _db.SaveChangesAsync();

        var result = await Service().CanAddGovernedResourcesAsync(_workspace,
            new GovernanceQuotaDelta(Members: 1), default);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("member");
        result.ReasonFa.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_multi_resource_operation_is_checked_as_one_batch()
    {
        _plan.MaxApps = 2;
        _plan.MaxServices = 1;
        _plan.MaxMemoryBytes = 1024;
        _plan.MaxCpuCores = 1;
        _db.Apps.Add(new App
        {
            WorkspaceId = _workspace, Name = "Existing", Slug = "existing",
            MemoryLimitBytes = 512, CpuLimit = .5
        });
        await _db.SaveChangesAsync();

        var result = await Service().CanAddWorkloadsAsync(_workspace,
            new WorkloadQuotaDelta(Apps: 2, Services: 1, MemoryBytes: 1024, CpuCores: 1), default);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("app");
    }

    [Fact]
    public async Task Governance_caps_remain_walls_even_on_an_overage_plan()
    {
        _plan.MaxProjects = 1;
        _plan.AllowsOverage = true;
        _db.Projects.Add(new Project { WorkspaceId = _workspace, Name = "One", Slug = "one" });
        await _db.SaveChangesAsync();

        (await Service(billing: true).CanAddGovernedResourcesAsync(_workspace,
            new GovernanceQuotaDelta(Projects: 1), default)).Allowed.Should().BeFalse();
        (await Service(billing: false).CanAddGovernedResourcesAsync(_workspace,
            new GovernanceQuotaDelta(Projects: 1), default)).Allowed.Should().BeFalse();
    }
}
