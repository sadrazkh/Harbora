using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="AppOperationsService.SetRateLimitAsync"/> (C3, 2026-08-27 what's-left plan) — proven
/// against <see cref="RecordingProxyEngine"/> exactly as
/// <see cref="AppOperationsServiceMaintenanceModeTests"/> proves the maintenance-mode toggle, because
/// it is the same apply path (<c>IProxyEngine.ApplyAllAsync</c>) and the same honesty rule: a failed
/// apply must not leave anything reading as on. What is asserted is the rendered route
/// (<c>RateLimitEnabled</c>/<c>Average</c>/<c>Burst</c>), not merely a database flag on <c>App</c>.
/// </summary>
public sealed class AppOperationsServiceRateLimitTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("app-ops-ratelimit-" + Guid.NewGuid()).Options);

    private static (AppOperationsService Service, RecordingProxyEngine Proxy)
        NewService(HarboraDbContext db)
    {
        var proxy = new RecordingProxyEngine(() => db.Routes.IgnoreQueryFilters().AsNoTracking().ToList());
        var service = new AppOperationsService(
            db,
            new FakeServerEngineFactory(new FakeDockerEngine()),
            proxy,
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, TestIngress.Registry(), NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);
        return (service, proxy);
    }

    private static (Workspace Workspace, App App) SeedApp(HarboraDbContext db)
    {
        var workspace = new Workspace { Name = "acme", Slug = "acme" };
        var server = new Harbora.Domain.Servers.Server { Name = "local", Hostname = "localhost", IsLocal = true };
        var project = new Harbora.Domain.Projects.Project { WorkspaceId = workspace.Id, Name = "api", Slug = "api" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            WorkspaceId = workspace.Id, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        var app = new App
        {
            WorkspaceId = workspace.Id, ServerId = server.Id, EnvironmentId = environment.Id,
            Name = "api", Slug = "api", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/api:1.0"
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
        TargetService = "harbora-api-1", TargetPort = 3000, IsEnabled = true
    };

    // ---- turning it on applies to every route, through the real apply path ----

    [Fact]
    public async Task Enabling_writes_the_numbers_onto_the_route_and_the_rendered_config_carries_them()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "api.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy) = NewService(db);

        var result = await service.SetRateLimitAsync(app.Id, true, 300, 150, CancellationToken.None);

        result.Success.Should().BeTrue();
        var stored = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        stored.RateLimitEnabled.Should().BeTrue();
        stored.RateLimitAverage.Should().Be(300);
        stored.RateLimitBurst.Should().Be(150);

        // The rendered config — not merely the database flag.
        proxy.Live.Should().ContainSingle(a => a.Host == "api.example.com");
        proxy.ApplyCount.Should().Be(1);
    }

    [Fact]
    public async Task Enabling_covers_every_route_the_app_owns()
    {
        // A custom domain and the platform subdomain both get a Route row per WireProxyAsync's own
        // per-domain loop — the limit has to cover every one of them, not just the first, the same
        // requirement Access protection and Maintenance mode both already carry.
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "api.example.com"));
        db.Routes.Add(AddRoute(db, workspace, app, "api-acme.harbora.example"));
        await db.SaveChangesAsync();
        var (service, _) = NewService(db);

        await service.SetRateLimitAsync(app.Id, true, 300, 150, CancellationToken.None);

        var routes = await db.Routes.AsNoTracking().Where(r => r.AppId == app.Id).ToListAsync();
        routes.Should().HaveCount(2)
            .And.OnlyContain(r => r.RateLimitEnabled && r.RateLimitAverage == 300 && r.RateLimitBurst == 150);
    }

    [Fact]
    public async Task Enabling_writes_the_flag_and_numbers_onto_the_app_too()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "api.example.com"));
        await db.SaveChangesAsync();
        var (service, _) = NewService(db);

        await service.SetRateLimitAsync(app.Id, true, 500, 250, CancellationToken.None);

        var stored = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id);
        stored.RateLimitEnabled.Should().BeTrue();
        stored.RateLimitAverage.Should().Be(500);
        stored.RateLimitBurst.Should().Be(250);
    }

    [Fact]
    public async Task An_app_with_no_domains_yet_still_succeeds()
    {
        await using var db = NewDb();
        var (_, app) = SeedApp(db);
        await db.SaveChangesAsync();
        var (service, proxy) = NewService(db);

        var result = await service.SetRateLimitAsync(app.Id, true, 300, 150, CancellationToken.None);

        result.Success.Should().BeTrue();
        proxy.ApplyCount.Should().Be(1);
        (await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id)).RateLimitEnabled.Should().BeTrue();
    }

    // ---- validation refuses before anything is touched ----

    [Theory]
    [InlineData(0, 150)]
    [InlineData(-1, 150)]
    [InlineData(1_000_001, 150)]
    public async Task An_out_of_range_average_is_refused_before_any_row_changes(int average, int burst)
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "api.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy) = NewService(db);

        var result = await service.SetRateLimitAsync(app.Id, true, average, burst, CancellationToken.None);

        result.Success.Should().BeFalse();
        proxy.ApplyCount.Should().Be(0, "an invalid number must never reach an apply");
        var stored = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        stored.RateLimitEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(300, 0)]
    [InlineData(300, -5)]
    [InlineData(300, 1_000_001)]
    public async Task An_out_of_range_burst_is_refused_before_any_row_changes(int average, int burst)
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "api.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy) = NewService(db);

        var result = await service.SetRateLimitAsync(app.Id, true, average, burst, CancellationToken.None);

        result.Success.Should().BeFalse();
        proxy.ApplyCount.Should().Be(0);
    }

    // ---- turning it off keeps the numbers, for next time ----

    [Fact]
    public async Task Disabling_turns_off_the_flag_but_keeps_the_last_configured_numbers()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "api.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy) = NewService(db);
        await service.SetRateLimitAsync(app.Id, true, 500, 250, CancellationToken.None);

        var result = await service.SetRateLimitAsync(app.Id, false, 0, 0, CancellationToken.None);

        result.Success.Should().BeTrue();
        var storedApp = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id);
        storedApp.RateLimitEnabled.Should().BeFalse();
        storedApp.RateLimitAverage.Should().Be(500, "so turning it back on starts from where it left off");
        storedApp.RateLimitBurst.Should().Be(250);

        var storedRoute = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        storedRoute.RateLimitEnabled.Should().BeFalse();
        storedRoute.RateLimitAverage.Should().Be(500);

        proxy.Live.Should().ContainSingle(a => a.Host == "api.example.com");
    }

    // ---- honesty: a failed apply must not leave the flag reading as on ----

    [Fact]
    public async Task A_failed_apply_on_enable_leaves_RateLimitEnabled_false()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        db.Routes.Add(AddRoute(db, workspace, app, "api.example.com"));
        await db.SaveChangesAsync();
        var (service, proxy) = NewService(db);
        proxy.Result = new Harbora.Application.Abstractions.ProxyApplyResult(false, "permission denied", RolledBack: true);

        var result = await service.SetRateLimitAsync(app.Id, true, 300, 150, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("permission denied");
        var stored = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id);
        stored.RateLimitEnabled.Should().BeFalse("the apply never actually took, so the flag must never claim otherwise");
    }

    [Fact]
    public async Task A_failed_apply_on_enable_puts_the_route_back_to_unlimited()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "api.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy) = NewService(db);
        proxy.Result = new Harbora.Application.Abstractions.ProxyApplyResult(false, "permission denied", RolledBack: true);

        await service.SetRateLimitAsync(app.Id, true, 300, 150, CancellationToken.None);

        var stored = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        stored.RateLimitEnabled.Should().BeFalse(
            "a refused apply must leave the stored route describing what is actually still being served");
        // And the config actually re-published, not merely the row.
        proxy.ApplyCount.Should().Be(2, "the failed attempt, then the revert's own re-publish");
    }

    [Fact]
    public async Task A_failed_apply_on_disable_leaves_it_enabled_and_the_route_still_limited()
    {
        await using var db = NewDb();
        var (workspace, app) = SeedApp(db);
        var route = AddRoute(db, workspace, app, "api.example.com");
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        var (service, proxy) = NewService(db);
        await service.SetRateLimitAsync(app.Id, true, 300, 150, CancellationToken.None);
        proxy.Result = new Harbora.Application.Abstractions.ProxyApplyResult(false, "disk full", RolledBack: false);

        var result = await service.SetRateLimitAsync(app.Id, false, 0, 0, CancellationToken.None);

        result.Success.Should().BeFalse();
        var storedApp = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id);
        storedApp.RateLimitEnabled.Should().BeTrue("visitors past the limit are still getting 429s — turning it off never actually applied");
        var storedRoute = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id);
        storedRoute.RateLimitEnabled.Should().BeTrue();
        storedRoute.RateLimitAverage.Should().Be(300);
    }
}
