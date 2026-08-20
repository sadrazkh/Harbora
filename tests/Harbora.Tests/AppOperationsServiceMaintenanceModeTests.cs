using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Networking;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="AppOperationsService.SetMaintenanceModeAsync"/> (P5, 2026-08-20 platform-options plan)
/// — proven against <see cref="RecordingProxyEngine"/> exactly as <c>ProxyCutoverTests</c> proves
/// <c>DeploymentPipeline</c>'s own proxy step, because it is the same apply path
/// (<c>IProxyEngine.ApplyAllAsync</c>) and the same honesty rule: a failed apply must not leave
/// anything reading as on. No Docker or live Traefik is used or needed — what is asserted here is the
/// rendered route (<c>TargetService</c>/<c>TargetPort</c>), not merely a database flag.
/// </summary>
public sealed class AppOperationsServiceMaintenanceModeTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("app-ops-maintenance-" + Guid.NewGuid()).Options);

    private static (AppOperationsService Service, RecordingProxyEngine Proxy, RecordingEventPublisher Events, FixedClock Clock)
        NewService(HarboraDbContext db)
    {
        var proxy = new RecordingProxyEngine(() => db.Routes.IgnoreQueryFilters().AsNoTracking().ToList());
        var events = new RecordingEventPublisher();
        var clock = new FixedClock();
        var service = new AppOperationsService(
            db,
            new FakeServerEngineFactory(new FakeDockerEngine()),
            proxy,
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, TestIngress.Registry(), NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance,
            clock,
            events,
            Options.Create(new HarboraRuntimeOptions()));
        return (service, proxy, events, clock);
    }

    private static (Workspace Workspace, App App) SeedApp(HarboraDbContext db)
    {
        var workspace = new Workspace { Name = "acme", Slug = "acme" };
        var server = new Harbora.Domain.Servers.Server { Name = "local", Hostname = "localhost", IsLocal = true };
        var project = new Harbora.Domain.Projects.Project { WorkspaceId = workspace.Id, Name = "blog", Slug = "blog" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            WorkspaceId = workspace.Id, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        var app = new App
        {
            WorkspaceId = workspace.Id, ServerId = server.Id, EnvironmentId = environment.Id,
            Name = "blog", Slug = "blog", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/blog:1.0"
        };
        db.Workspaces.Add(workspace);
        db.Servers.Add(server);
        db.Projects.Add(project);
        db.Environments.Add(environment);
        db.Apps.Add(app);
        return (workspace, app);
    }

    private static Route AddRoute(HarboraDbContext db, Workspace workspace, App app, string host) => new()
    {
        WorkspaceId = workspace.Id, AppId = app.Id, Host = host,
        TargetService = "harbora-blog-1", TargetPort = 3000, IsEnabled = true,
        ExtraUpstreamsJson = "[{\"Host\":\"harbora-blog-1b\",\"Port\":3000}]",
        LoadBalancerHealthCheckPath = "/health"
    };

    // ---- turning maintenance on redirects the app's own routes, through the real apply path ----

    [Fact]
    public async Task Enabling_maintenance_points_the_route_at_the_panel_container_and_port()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "blog.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy, _, _) = NewService(db);

        var result = await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        var stored = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        stored.TargetService.Should().Be("harbora-panel");
        stored.TargetPort.Should().Be(8080);
        stored.MaintenanceRedirected.Should().BeTrue();

        // The rendered config — not merely the database flag — actually names the panel as the
        // upstream for this route's host.
        proxy.Live.Should().ContainSingle(a => a.Host == "blog.example.com" && a.TargetService == "harbora-panel" && a.TargetPort == 8080);
    }

    [Fact]
    public async Task Enabling_maintenance_saves_the_real_upstream_so_it_can_be_restored()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "blog.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, _, _, _) = NewService(db);

        await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        var stored = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        stored.SavedTargetService.Should().Be("harbora-blog-1");
        stored.SavedTargetPort.Should().Be(3000);
        stored.SavedExtraUpstreamsJson.Should().Be("[{\"Host\":\"harbora-blog-1b\",\"Port\":3000}]");
        stored.SavedLoadBalancerHealthCheckPath.Should().Be("/health");
    }

    [Fact]
    public async Task Enabling_maintenance_clears_the_extra_upstreams_and_health_check_so_they_are_not_polled_against_the_panel()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "blog.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, _, _, _) = NewService(db);

        await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        var stored = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        stored.ExtraUpstreamsJson.Should().BeNull();
        stored.LoadBalancerHealthCheckPath.Should().BeNull();
    }

    [Fact]
    public async Task Enabling_maintenance_redirects_every_route_the_app_owns()
    {
        // A custom domain and the platform subdomain both get a Route row per WireProxyAsync's own
        // per-domain loop — maintenance has to cover every one of them, not just the first.
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "blog.example.com"));
        db.Routes.Add(AddRoute(db, workspace, app, "blog-acme.harbora.example"));
        await db.SaveChangesAsync();
        var (service, proxy, _, _) = NewService(db);

        await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        proxy.Live.Should().HaveCount(2)
            .And.OnlyContain(a => a.TargetService == "harbora-panel" && a.TargetPort == 8080);
    }

    [Fact]
    public async Task Enabling_maintenance_writes_the_flag_the_message_and_the_since_timestamp()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "blog.example.com"));
        await db.SaveChangesAsync();
        var (service, _, _, clock) = NewService(db);

        await service.SetMaintenanceModeAsync(app.Id, true, "Back soon", "به‌زودی برمی‌گردیم", CancellationToken.None);

        var stored = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id);
        stored.MaintenanceMode.Should().BeTrue();
        stored.MaintenanceMessage.Should().Be("Back soon");
        stored.MaintenanceMessageFa.Should().Be("به‌زودی برمی‌گردیم");
        stored.MaintenanceSince.Should().Be(clock.UtcNow);
    }

    [Fact]
    public async Task Blank_messages_are_stored_as_null_rather_than_empty_strings()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "blog.example.com"));
        await db.SaveChangesAsync();
        var (service, _, _, _) = NewService(db);

        await service.SetMaintenanceModeAsync(app.Id, true, "   ", "", CancellationToken.None);

        var stored = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id);
        stored.MaintenanceMessage.Should().BeNull();
        stored.MaintenanceMessageFa.Should().BeNull();
    }

    [Fact]
    public async Task Enabling_maintenance_twice_does_not_save_the_panels_own_address_as_the_real_upstream()
    {
        // The second enable (a re-submitted toggle, or the message being edited) must not overwrite
        // the saved snapshot with the panel's own TargetService — that would make the app permanently
        // unreachable once maintenance is turned back off.
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "blog.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, _, _, _) = NewService(db);

        await service.SetMaintenanceModeAsync(app.Id, true, "first", null, CancellationToken.None);
        await service.SetMaintenanceModeAsync(app.Id, true, "second", null, CancellationToken.None);

        var stored = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        stored.SavedTargetService.Should().Be("harbora-blog-1");
        stored.SavedTargetPort.Should().Be(3000);
    }

    // ---- turning it off restores exactly what was there ----

    [Fact]
    public async Task Disabling_maintenance_restores_the_real_upstream_and_the_extras()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "blog.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy, _, _) = NewService(db);
        await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        var result = await service.SetMaintenanceModeAsync(app.Id, false, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        var stored = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        stored.TargetService.Should().Be("harbora-blog-1");
        stored.TargetPort.Should().Be(3000);
        stored.ExtraUpstreamsJson.Should().Be("[{\"Host\":\"harbora-blog-1b\",\"Port\":3000}]");
        stored.LoadBalancerHealthCheckPath.Should().Be("/health");
        stored.MaintenanceRedirected.Should().BeFalse();
        stored.SavedTargetService.Should().BeNull();
        proxy.Live.Should().ContainSingle(a => a.Host == "blog.example.com" && a.TargetService == "harbora-blog-1");
    }

    [Fact]
    public async Task Disabling_maintenance_clears_the_flag_the_message_and_the_since_timestamp()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "blog.example.com"));
        await db.SaveChangesAsync();
        var (service, _, _, _) = NewService(db);
        await service.SetMaintenanceModeAsync(app.Id, true, "brb", null, CancellationToken.None);

        await service.SetMaintenanceModeAsync(app.Id, false, null, null, CancellationToken.None);

        var stored = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id);
        stored.MaintenanceMode.Should().BeFalse();
        stored.MaintenanceMessage.Should().BeNull();
        stored.MaintenanceSince.Should().BeNull();
    }

    // ---- honesty: a failed apply must not leave the flag reading as on ----

    [Fact]
    public async Task A_failed_apply_on_enable_leaves_MaintenanceMode_false()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "blog.example.com"));
        await db.SaveChangesAsync();
        var (service, proxy, _, _) = NewService(db);
        proxy.Result = new Harbora.Application.Abstractions.ProxyApplyResult(false, "permission denied", RolledBack: true);

        var result = await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("permission denied");
        var stored = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id);
        stored.MaintenanceMode.Should().BeFalse("the apply never actually took, so the flag must never claim otherwise");
    }

    [Fact]
    public async Task A_failed_apply_on_enable_puts_the_route_back_to_the_real_upstream()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "blog.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy, _, _) = NewService(db);
        proxy.Result = new Harbora.Application.Abstractions.ProxyApplyResult(false, "permission denied", RolledBack: true);

        await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        var stored = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        stored.TargetService.Should().Be("harbora-blog-1",
            "a refused apply must leave the stored route naming the container that is actually still serving");
        stored.TargetPort.Should().Be(3000);
        stored.MaintenanceRedirected.Should().BeFalse();
        // And the config actually re-published, not merely the row: the real cause of the P5 rule
        // this test file exists for is a route reading redirected while nothing said so.
        proxy.Live.Should().ContainSingle(a => a.Host == "blog.example.com" && a.TargetService == "harbora-blog-1");
    }

    [Fact]
    public async Task A_failed_apply_on_disable_leaves_MaintenanceMode_true_and_the_route_still_redirected()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "blog.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy, _, _) = NewService(db);
        await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);
        proxy.Result = new Harbora.Application.Abstractions.ProxyApplyResult(false, "disk full", RolledBack: false);

        var result = await service.SetMaintenanceModeAsync(app.Id, false, null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        var storedApp = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id);
        storedApp.MaintenanceMode.Should().BeTrue("visitors are still seeing the maintenance page — turning it off never actually applied");
        var storedRoute = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        storedRoute.TargetService.Should().Be("harbora-panel", "the route must keep describing what is actually live");
        storedRoute.MaintenanceRedirected.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_apply_does_not_publish_a_maintenance_event()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "blog.example.com"));
        await db.SaveChangesAsync();
        var (service, proxy, events, _) = NewService(db);
        proxy.Result = new Harbora.Application.Abstractions.ProxyApplyResult(false, "permission denied", RolledBack: true);

        await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        events.Events.Should().BeEmpty("nothing here actually happened, so nothing should be reported as having happened");
    }

    // ---- wiring the reserved EventKind members (parallel P6 sub-project) ----

    [Fact]
    public async Task Enabling_maintenance_publishes_MaintenanceOn_for_the_apps_workspace()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "blog.example.com"));
        await db.SaveChangesAsync();
        var (service, _, events, _) = NewService(db);

        await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        var published = events.Events.Should().ContainSingle().Subject;
        published.Kind.Should().Be(EventKind.MaintenanceOn);
        published.Workspace.Should().Be(workspace.Id);
        published.Resource.Should().ContainKey("app").WhoseValue.Should().Be("blog");
    }

    [Fact]
    public async Task Disabling_maintenance_publishes_MaintenanceOff()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "blog.example.com"));
        await db.SaveChangesAsync();
        var (service, _, events, _) = NewService(db);
        await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);
        events.Events.Clear();

        await service.SetMaintenanceModeAsync(app.Id, false, null, null, CancellationToken.None);

        events.Events.Should().ContainSingle().Which.Kind.Should().Be(EventKind.MaintenanceOff);
    }

    // ---- an app with no routes yet is a no-op that still flips the flag ----

    [Fact]
    public async Task Enabling_maintenance_on_an_app_with_no_domains_yet_still_succeeds()
    {
        await using var db = NewDb();
        var (_, app) = SeedApp(db);
        await db.SaveChangesAsync();
        var (service, proxy, _, _) = NewService(db);

        var result = await service.SetMaintenanceModeAsync(app.Id, true, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        proxy.ApplyCount.Should().Be(1);
        (await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id)).MaintenanceMode.Should().BeTrue();
    }
}
