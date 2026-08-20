using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Networking;
using Harbora.Domain.Status;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Status;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="StatusPageDomainService"/> (sub-project 8, 2026-08-20 platform-options plan) — proven
/// against <see cref="RecordingProxyEngine"/> exactly as <c>AppOperationsServiceMaintenanceModeTests</c>
/// proves <c>SetMaintenanceModeAsync</c>, because it is the same apply path
/// (<c>IProxyEngine.ApplyAllAsync</c>) and the same honesty rule: a failed apply must not leave a route
/// behind that only the database knows about. No Docker or live Traefik is used or needed here — what
/// is asserted is the rendered route (<c>TargetService</c>/<c>TargetPort</c>), not merely a flag.
/// <c>StatusPageDomainHttpTests</c> proves the same behaviour through the real
/// <c>TraefikProxyEngine</c> writing to disk.
/// </summary>
public sealed class StatusPageDomainServiceTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("status-page-domain-" + Guid.NewGuid()).Options);

    private static (StatusPageDomainService Service, RecordingProxyEngine Proxy) NewService(HarboraDbContext db)
    {
        var proxy = new RecordingProxyEngine(() => db.Routes.IgnoreQueryFilters().AsNoTracking().ToList());
        var service = new StatusPageDomainService(db, proxy, Options.Create(new HarboraRuntimeOptions()));
        return (service, proxy);
    }

    // ---- the platform subdomain ----

    [Fact]
    public async Task Enabling_creates_a_route_pointed_at_the_panel_and_publishes_it()
    {
        using var db = NewDb();
        var (service, proxy) = NewService(db);
        var workspaceId = Guid.CreateVersion7();

        var result = await service.EnsurePlatformRouteAsync(workspaceId, "status-acme.apps.example.com", default);

        result.Success.Should().BeTrue();
        proxy.ApplyCount.Should().Be(1);
        var route = db.Routes.IgnoreQueryFilters().Single();
        route.Host.Should().Be("status-acme.apps.example.com");
        route.AppId.Should().BeNull();
        route.TargetService.Should().Be("harbora-panel");
        route.TargetPort.Should().Be(8080);
        route.IsEnabled.Should().BeTrue();
        proxy.Live.Should().ContainSingle(a => a.Host == "status-acme.apps.example.com" && a.TargetService == "harbora-panel");
    }

    [Fact]
    public async Task Enabling_twice_refreshes_the_same_row_instead_of_creating_a_second_one()
    {
        using var db = NewDb();
        var (service, _) = NewService(db);
        var workspaceId = Guid.CreateVersion7();

        await service.EnsurePlatformRouteAsync(workspaceId, "status-twice.apps.example.com", default);
        await service.EnsurePlatformRouteAsync(workspaceId, "status-twice.apps.example.com", default);

        db.Routes.IgnoreQueryFilters().Count(r => r.Host == "status-twice.apps.example.com").Should().Be(1);
    }

    [Fact]
    public async Task A_failed_apply_undoes_the_new_route_rather_than_leaving_it_only_in_the_database()
    {
        using var db = NewDb();
        var (service, proxy) = NewService(db);
        proxy.Result = new(false, "unrelated route elsewhere failed validation", false);
        var workspaceId = Guid.CreateVersion7();

        var result = await service.EnsurePlatformRouteAsync(workspaceId, "status-fails.apps.example.com", default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unrelated route");
        db.Routes.IgnoreQueryFilters().Any(r => r.Host == "status-fails.apps.example.com").Should().BeFalse(
            "a route that could never actually reach Traefik must not be left behind for the flag to lie about");
    }

    [Fact]
    public async Task Disabling_removes_the_route_and_republishes()
    {
        using var db = NewDb();
        var (service, proxy) = NewService(db);
        var workspaceId = Guid.CreateVersion7();
        await service.EnsurePlatformRouteAsync(workspaceId, "status-off.apps.example.com", default);

        await service.RemovePlatformRouteAsync(workspaceId, "status-off.apps.example.com", default);

        db.Routes.IgnoreQueryFilters().Any(r => r.Host == "status-off.apps.example.com").Should().BeFalse();
        proxy.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_route_that_was_never_created_is_a_quiet_no_op()
    {
        using var db = NewDb();
        var (service, proxy) = NewService(db);

        await service.RemovePlatformRouteAsync(Guid.CreateVersion7(), "status-never-existed.apps.example.com", default);

        proxy.ApplyCount.Should().Be(0, "nothing changed, so there is nothing to publish");
    }

    // ---- the customer's own custom domain ----

    [Fact]
    public async Task Attaching_creates_both_the_domain_and_the_route_pointed_at_the_panel()
    {
        using var db = NewDb();
        var (service, proxy) = NewService(db);
        var workspaceId = Guid.CreateVersion7();
        var statusPageId = Guid.CreateVersion7();
        db.StatusPages.Add(new StatusPage { Id = statusPageId, WorkspaceId = workspaceId, IsEnabled = true });
        await db.SaveChangesAsync();

        var result = await service.AttachCustomDomainAsync(workspaceId, statusPageId, "status.acme.example", default);

        result.Success.Should().BeTrue();
        var domain = db.Domains.IgnoreQueryFilters().Single();
        domain.Host.Should().Be("status.acme.example");
        domain.StatusPageId.Should().Be(statusPageId);
        domain.AppId.Should().BeNull();
        domain.SslEnabled.Should().BeTrue();

        var route = db.Routes.IgnoreQueryFilters().Single();
        route.Host.Should().Be("status.acme.example");
        route.TargetService.Should().Be("harbora-panel");
        route.TargetPort.Should().Be(8080);
        proxy.Live.Should().ContainSingle(a => a.Host == "status.acme.example");
    }

    [Fact]
    public async Task A_failed_attach_leaves_neither_the_domain_nor_the_route_behind()
    {
        using var db = NewDb();
        var (service, proxy) = NewService(db);
        proxy.Result = new(false, "validation failed", false);
        var workspaceId = Guid.CreateVersion7();
        var statusPageId = Guid.CreateVersion7();
        db.StatusPages.Add(new StatusPage { Id = statusPageId, WorkspaceId = workspaceId, IsEnabled = true });
        await db.SaveChangesAsync();

        var result = await service.AttachCustomDomainAsync(workspaceId, statusPageId, "status.broken.example", default);

        result.Success.Should().BeFalse();
        db.Domains.IgnoreQueryFilters().Any().Should().BeFalse();
        db.Routes.IgnoreQueryFilters().Any().Should().BeFalse();
    }

    [Fact]
    public async Task Removing_the_custom_domain_deletes_the_route_too_and_republishes()
    {
        using var db = NewDb();
        var (service, proxy) = NewService(db);
        var workspaceId = Guid.CreateVersion7();
        var statusPageId = Guid.CreateVersion7();
        db.StatusPages.Add(new StatusPage { Id = statusPageId, WorkspaceId = workspaceId, IsEnabled = true });
        await db.SaveChangesAsync();
        await service.AttachCustomDomainAsync(workspaceId, statusPageId, "status.leaving.example", default);

        await service.RemoveCustomDomainAsync(workspaceId, statusPageId, default);

        db.Domains.IgnoreQueryFilters().Any().Should().BeFalse();
        db.Routes.IgnoreQueryFilters().Any().Should().BeFalse(
            "removal must leave no route behind, not merely delete the domain row");
        proxy.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task Attaching_a_second_status_pages_domain_never_touches_the_first_workspaces_route()
    {
        using var db = NewDb();
        var (service, _) = NewService(db);
        var workspaceA = Guid.CreateVersion7();
        var pageA = Guid.CreateVersion7();
        var workspaceB = Guid.CreateVersion7();
        var pageB = Guid.CreateVersion7();
        db.StatusPages.Add(new StatusPage { Id = pageA, WorkspaceId = workspaceA, IsEnabled = true });
        db.StatusPages.Add(new StatusPage { Id = pageB, WorkspaceId = workspaceB, IsEnabled = true });
        await db.SaveChangesAsync();
        await service.AttachCustomDomainAsync(workspaceA, pageA, "status.tenant-a.example", default);

        await service.AttachCustomDomainAsync(workspaceB, pageB, "status.tenant-b.example", default);
        await service.RemoveCustomDomainAsync(workspaceB, pageB, default);

        db.Domains.IgnoreQueryFilters().Single().Host.Should().Be("status.tenant-a.example");
        db.Routes.IgnoreQueryFilters().Single().Host.Should().Be("status.tenant-a.example");
    }
}
