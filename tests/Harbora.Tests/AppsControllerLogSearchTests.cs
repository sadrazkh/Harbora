using System.Text;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Project = Harbora.Domain.Projects.Project;
using Environment = Harbora.Domain.Projects.Environment;

namespace Harbora.Tests;

/// <summary>
/// The single-app Logs tab, now able to honor a time window and to say what a search actually
/// covered — the two things it could not do before this feature, on top of the search it already had
/// (<c>LogFilterTests</c> pins that matching rule; this pins the controller wiring around it).
///
/// The unfiltered live tail — no search, no problems, no window, the thing auto-refresh polls every
/// few seconds — is asserted to be untouched: this feature must not make the common path slower or
/// change what it returns.
/// </summary>
public class AppsControllerLogSearchTests
{
    private static string Stamp(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z";

    private sealed record Fixture(HarboraDbContext Db, AppsController Controller, Guid AppId, FakeDockerEngine Docker);

    private static Fixture Build()
    {
        var workspaceId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("apps-log-search-" + Guid.NewGuid()).Options,
            new FixedWorkspaceScope(workspaceId));

        var project = new Project { Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, Name = "Shop", Slug = "shop" };
        var environment = new Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        db.Projects.Add(project);
        db.Environments.Add(environment);

        var app = new App
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, EnvironmentId = environment.Id,
            ServerId = Guid.CreateVersion7(), Name = "api", Slug = "api", Status = AppStatus.Running
        };
        db.Apps.Add(app);
        db.Users.Add(new User { Id = userId, Email = "me@example.com", DisplayName = "Tester" });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Admin, ScopedToProjects = false
        });
        db.SaveChanges();

        var docker = new FakeDockerEngine();
        var containerId = docker.SeedContainer("harbora-api-1", "api", workspaceId: workspaceId);

        var currentUser = new Caller(workspaceId, userId);
        var ingress = new NodeIngressRegistry(
            Options.Create(new NodeAgentControlPlaneOptions()), NullLogger<NodeIngressRegistry>.Instance);
        var ops = new AppOperationsService(
            db, new FakeServerEngineFactory(docker), new RecordingProxyEngine(() => []),
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, ingress, NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);

        var controller = new AppsController(
            db,
            deployEngine: new Unused(),
            ops: ops,
            quota: new Unused(),
            scheduler: new Unused(),
            protector: new PassthroughProtector(),
            audit: new SilentAudit(),
            rollbackPlanner: new Unused(),
            domains: new Unused(),
            projects: new Harbora.Infrastructure.Projects.ProjectService(db, new FixedClock(DateTimeOffset.UnixEpoch)),
            access: new Harbora.Infrastructure.Security.ProjectAccessService(db, currentUser),
            serviceUsage: new Harbora.Infrastructure.Services.ServiceUsageService(db, new PassthroughProtector()),
            engines: new FakeServerEngineFactory(docker),
            proxy: new Unused(),
            logger: NullLogger<AppsController>.Instance,
            jobs: new NoopJobQueue(),
            config: new ConfigurationBuilder().Build(),
            currentUser: currentUser,
            creationBilling: new Harbora.Infrastructure.Billing.ResourceCreationBilling(
                db, new FixedClock(DateTimeOffset.UnixEpoch),
                Options.Create(new BillingOptions { Enabled = false })),
            runtimeOptions: Options.Create(new HarboraRuntimeOptions()),
            storageOptions: Options.Create(new Harbora.Infrastructure.Storage.ObjectStorageOptions()),
            addresses: new Harbora.Infrastructure.Networking.AppAddressAssigner(db, new ConfigurationBuilder().Build()),
            backupSnapshots: null!,
            lifecycle: new Harbora.Infrastructure.Monitoring.LifecycleHistory(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return new Fixture(db, controller, app.Id, docker);
    }

    private static (string Body, IHeaderDictionary Headers) Read(Fixture f, IActionResult result)
    {
        var content = result.Should().BeOfType<ContentResult>().Which;
        return (content.Content ?? string.Empty, f.Controller.Response.Headers);
    }

    // ---- the unfiltered tail is unchanged ----

    [Fact]
    public async Task The_plain_tail_is_untouched_and_reports_how_many_lines_it_covered()
    {
        var f = Build();
        f.Docker.ContainerLogsById[ContainerIdOf(f)] = "one\ntwo\nthree";

        var result = await f.Controller.LogsData(f.AppId, tail: 200, search: null, problems: false, minutes: 0, default);

        var (body, headers) = Read(f, result);
        body.Should().Be("one\ntwo\nthree", "the unfiltered tail must be exactly what the engine returned");
        headers["X-Log-Lines-Scanned"].ToString().Should().Be("3");
        headers.Should().NotContainKey("X-Log-Time-Window-Honored", "no window was requested");
    }

    // ---- a search that matches nothing still says how much it covered ----

    [Fact]
    public async Task A_search_that_matches_nothing_reports_the_lines_it_scanned()
    {
        var f = Build();
        f.Docker.ContainerLogsById[ContainerIdOf(f)] = "one\ntwo\nthree";

        var result = await f.Controller.LogsData(
            f.AppId, tail: 200, search: "nowhere-to-be-found", problems: false, minutes: 0, default);

        var (body, headers) = Read(f, result);
        body.Should().Be("No lines match that filter.");
        headers["X-Log-Lines-Scanned"].ToString().Should().Be("3",
            "a caller must be able to tell 'searched 3 lines, found nothing' from 'searched nothing'");
    }

    // ---- time window ----

    [Fact]
    public async Task A_time_window_the_engine_can_honor_narrows_the_pane_and_says_so()
    {
        var f = Build();
        var now = DateTimeOffset.UtcNow;
        f.Docker.ContainerLogsById[ContainerIdOf(f)] =
            $"{Stamp(now.AddHours(-3))} ancient history\n" +
            $"{Stamp(now.AddMinutes(-5))} just now";

        var result = await f.Controller.LogsData(
            f.AppId, tail: 200, search: null, problems: false, minutes: 60, default);

        var (body, headers) = Read(f, result);
        body.Should().Be("just now");
        headers["X-Log-Time-Window-Honored"].ToString().Should().Be("true");
    }

    [Fact]
    public async Task A_host_that_cannot_honor_the_window_still_returns_its_tail_and_says_the_window_was_not_honored()
    {
        var f = Build();
        var containerId = ContainerIdOf(f);
        f.Docker.ContainerLogsById[containerId] = "something the engine cannot time-bound";
        f.Docker.TimeWindowUnsupportedFor.Add(containerId);

        var result = await f.Controller.LogsData(
            f.AppId, tail: 200, search: null, problems: false, minutes: 15, default);

        var (body, headers) = Read(f, result);
        body.Should().Be("something the engine cannot time-bound",
            "a host that cannot honor the window still has a tail worth showing");
        headers["X-Log-Time-Window-Honored"].ToString().Should().Be("false");
    }

    // ---- download respects the same window ----

    [Fact]
    public async Task Downloading_with_a_time_window_only_includes_lines_inside_it()
    {
        var f = Build();
        var now = DateTimeOffset.UtcNow;
        f.Docker.ContainerLogsById[ContainerIdOf(f)] =
            $"{Stamp(now.AddHours(-3))} ancient history\n" +
            $"{Stamp(now.AddMinutes(-5))} just now";

        var result = await f.Controller.LogsDownload(
            f.AppId, tail: 2000, search: null, problems: false, minutes: 60, default);

        var file = result.Should().BeOfType<FileContentResult>().Which;
        Encoding.UTF8.GetString(file.FileContents).Should().Be("just now");
    }

    /// <summary>
    /// FakeDockerEngine does not expose its id map directly; ListContainersAsync does, and it is
    /// exactly what AppOperationsService itself resolves the container id through.
    /// </summary>
    private static string ContainerIdOf(Fixture f) =>
        f.Docker.ListContainersAsync(null, default).GetAwaiter().GetResult().Single().Id;

    private sealed class Caller(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public string? Email => "me@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    private sealed class SilentAudit : IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Unused :
        IDeploymentEngine, IRollbackPlanner, IDomainInspector, IQuotaService, ISchedulerService, IProxyEngine
    {
        private static NotSupportedException NotNeeded() =>
            new("This dependency is not reached by the logs actions and should not have been called.");

        public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct) => throw NotNeeded();
        public Task CancelAsync(Guid deploymentId, CancellationToken ct) => throw NotNeeded();
        public Task<RollbackPlan> PrepareAsync(Guid appId, Guid targetDeploymentId, CancellationToken ct) => throw NotNeeded();
        public Task<DomainStatus> InspectAsync(string host, CancellationToken ct) => throw NotNeeded();
        public Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct) => throw NotNeeded();
        public Task<QuotaCheck> CanAddAppAsync(Guid workspaceId, string? instanceSizeKey, Guid? excludeAppId, CancellationToken ct) => throw NotNeeded();
        public Task<QuotaCheck> CanAddServiceAsync(Guid workspaceId, string? instanceSizeKey, CancellationToken ct) => throw NotNeeded();
        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? requiredPool, CancellationToken ct) => throw NotNeeded();
        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct) => throw NotNeeded();
        public ProxyConfigPreview Preview(IReadOnlyList<Route> routes) => throw NotNeeded();
        public ProxyValidationResult Validate(IReadOnlyList<Route> routes) => throw NotNeeded();
        public Task<ProxyApplyResult> ApplyAllAsync(Guid? callerWorkspaceId, CancellationToken ct) => throw NotNeeded();
    }
}
