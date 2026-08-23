using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The seam C2 (file overrides, 2026-08-22 config-delivery plan) binds to for "give me the connection
/// string for this app's attachment named X": <see cref="AttachedServiceConnectionResolver.ResolveAsync"/>.
/// Proven directly, against an in-memory <see cref="HarboraDbContext"/>, independent of any HTTP
/// request or view — the shape a value-reference resolver in C2's own override pipeline will call it
/// in.
/// </summary>
public class AttachedServiceConnectionResolverTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("acr-" + Guid.NewGuid()).Options);

    [Fact]
    public async Task Resolves_the_engines_own_connection_string_for_an_apps_attachment_by_alias()
    {
        using var db = NewDb();
        var protector = new PassthroughProtector();
        var appId = Guid.NewGuid();
        var svc = new ManagedService
        {
            WorkspaceId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), ServerId = Guid.NewGuid(),
            Name = "orders", Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
            ContainerName = "harbora-svc-orders", InternalPort = 5432, Username = "harbora",
            EncryptedPassword = protector.Protect("resolver-secret-01"),
            DatabaseName = "orders", VolumeName = "harbora-svc-orders-data", Status = ServiceStatus.Running
        };
        db.ManagedServices.Add(svc);
        db.AppManagedServices.Add(new AppManagedService
        {
            AppId = appId, ManagedServiceId = svc.Id, Alias = "ORDERS", AttachOrder = 1
        });
        await db.SaveChangesAsync();

        var resolver = new AttachedServiceConnectionResolver(db, protector);
        var connectionString = await resolver.ResolveAsync(appId, "ORDERS", default);

        connectionString.Should().Be("postgresql://harbora:resolver-secret-01@harbora-svc-orders:5432/orders");
    }

    [Fact]
    public async Task Alias_lookup_is_case_insensitive()
    {
        using var db = NewDb();
        var protector = new PassthroughProtector();
        var appId = Guid.NewGuid();
        var svc = new ManagedService
        {
            WorkspaceId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), ServerId = Guid.NewGuid(),
            Name = "orders", Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
            ContainerName = "harbora-svc-orders", InternalPort = 5432, Username = "harbora",
            EncryptedPassword = protector.Protect("resolver-secret-02"),
            DatabaseName = "orders", VolumeName = "harbora-svc-orders-data", Status = ServiceStatus.Running
        };
        db.ManagedServices.Add(svc);
        db.AppManagedServices.Add(new AppManagedService
        {
            AppId = appId, ManagedServiceId = svc.Id, Alias = "ORDERS", AttachOrder = 1
        });
        await db.SaveChangesAsync();

        var resolver = new AttachedServiceConnectionResolver(db, protector);
        (await resolver.ResolveAsync(appId, "orders", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_alias_that_names_no_attachment_on_this_app_resolves_to_null_rather_than_throwing()
    {
        using var db = NewDb();
        var protector = new PassthroughProtector();
        var resolver = new AttachedServiceConnectionResolver(db, protector);

        // C2's own contract: this is exactly the case a value reference to a detached/renamed
        // attachment must be able to detect and turn into a named, actionable failure — never a crash.
        (await resolver.ResolveAsync(Guid.NewGuid(), "NO_SUCH_ALIAS", default)).Should().BeNull();
    }

    [Fact]
    public async Task An_alias_belonging_to_a_different_app_is_not_returned()
    {
        using var db = NewDb();
        var protector = new PassthroughProtector();
        var svc = new ManagedService
        {
            WorkspaceId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), ServerId = Guid.NewGuid(),
            Name = "orders", Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
            ContainerName = "harbora-svc-orders", InternalPort = 5432, Username = "harbora",
            EncryptedPassword = protector.Protect("resolver-secret-03"),
            DatabaseName = "orders", VolumeName = "harbora-svc-orders-data", Status = ServiceStatus.Running
        };
        db.ManagedServices.Add(svc);
        db.AppManagedServices.Add(new AppManagedService
        {
            AppId = Guid.NewGuid(), ManagedServiceId = svc.Id, Alias = "ORDERS", AttachOrder = 1
        });
        await db.SaveChangesAsync();

        var resolver = new AttachedServiceConnectionResolver(db, protector);
        (await resolver.ResolveAsync(Guid.NewGuid(), "ORDERS", default)).Should().BeNull(
            "the alias exists, but not on the app that was asked about");
    }
}
