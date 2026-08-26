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
    public async Task An_attachment_pointed_at_a_logical_database_resolves_to_that_databases_own_login()
    {
        // D1 (2026-08-25 shared-databases plan): the actual gap this closes. Two apps on the same
        // instance used to be handed the same admin login and the same database no matter what —
        // this proves an attachment that names a logical database gets THAT database's own name and
        // login instead, not the instance's admin credentials.
        using var db = NewDb();
        var protector = new PassthroughProtector();
        var appId = Guid.NewGuid();
        var svc = new ManagedService
        {
            WorkspaceId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), ServerId = Guid.NewGuid(),
            Name = "shared-pg", Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
            ContainerName = "harbora-svc-shared-pg", InternalPort = 5432, Username = "harbora",
            EncryptedPassword = protector.Protect("instance-admin-secret"),
            DatabaseName = "harbora_admin", VolumeName = "harbora-svc-shared-pg-data", Status = ServiceStatus.Running
        };
        var logical = new ManagedServiceDatabase
        {
            ManagedServiceId = svc.Id, Name = "orders_app", Username = "orders_app_user",
            EncryptedPassword = protector.Protect("orders-app-own-secret")
        };
        db.ManagedServices.Add(svc);
        db.ManagedServiceDatabases.Add(logical);
        db.AppManagedServices.Add(new AppManagedService
        {
            AppId = appId, ManagedServiceId = svc.Id, ManagedServiceDatabaseId = logical.Id,
            Alias = "ORDERS", AttachOrder = 1
        });
        await db.SaveChangesAsync();

        var resolver = new AttachedServiceConnectionResolver(db, protector);
        var connectionString = await resolver.ResolveAsync(appId, "ORDERS", default);

        connectionString.Should().Be("postgresql://orders_app_user:orders-app-own-secret@harbora-svc-shared-pg:5432/orders_app",
            "the attachment must use the logical database's own login and name, never the instance's admin credentials");
    }

    [Fact]
    public async Task Two_apps_on_two_different_logical_databases_of_one_instance_get_isolated_credentials()
    {
        using var db = NewDb();
        var protector = new PassthroughProtector();
        var appOne = Guid.NewGuid();
        var appTwo = Guid.NewGuid();
        var svc = new ManagedService
        {
            WorkspaceId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), ServerId = Guid.NewGuid(),
            Name = "shared-pg-2", Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
            ContainerName = "harbora-svc-shared-pg-2", InternalPort = 5432, Username = "harbora",
            EncryptedPassword = protector.Protect("instance-admin-secret-2"),
            DatabaseName = "harbora_admin_2", VolumeName = "harbora-svc-shared-pg-2-data", Status = ServiceStatus.Running
        };
        var ordersDb = new ManagedServiceDatabase
        {
            ManagedServiceId = svc.Id, Name = "orders", Username = "orders_user",
            EncryptedPassword = protector.Protect("orders-secret")
        };
        var billingDb = new ManagedServiceDatabase
        {
            ManagedServiceId = svc.Id, Name = "billing", Username = "billing_user",
            EncryptedPassword = protector.Protect("billing-secret")
        };
        db.ManagedServices.Add(svc);
        db.ManagedServiceDatabases.AddRange(ordersDb, billingDb);
        db.AppManagedServices.Add(new AppManagedService
        { AppId = appOne, ManagedServiceId = svc.Id, ManagedServiceDatabaseId = ordersDb.Id, Alias = "DB", AttachOrder = 1 });
        db.AppManagedServices.Add(new AppManagedService
        { AppId = appTwo, ManagedServiceId = svc.Id, ManagedServiceDatabaseId = billingDb.Id, Alias = "DB", AttachOrder = 1 });
        await db.SaveChangesAsync();

        var resolver = new AttachedServiceConnectionResolver(db, protector);

        (await resolver.ResolveAsync(appOne, "DB", default)).Should().Contain("orders_user").And.Contain("/orders");
        (await resolver.ResolveAsync(appTwo, "DB", default)).Should().Contain("billing_user").And.Contain("/billing");
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
