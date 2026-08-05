using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// How much of a machine is already spoken for.
///
/// The scheduler refuses to place anything that does not fit, which only works if "committed" means
/// everything on the node. It summed applications and nothing else, so a PostgreSQL given 512 MB
/// was invisible: the node reported that memory as free, the scheduler placed applications into it,
/// and the host was overcommitted by exactly the size of every database on it.
///
/// The same omission was fixed in QuotaService for a workspace's plan. This is the other half of
/// it, for the machine — found by watching a real server refuse a 256 MB application while two
/// databases it could not see held half a gigabyte.
/// </summary>
public class NodeCapacityTests
{
    private const long MB = 1024 * 1024;

    private static HarboraDbContext Db() =>
        new(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("capacity-" + Guid.NewGuid()).Options);

    private static Server Machine() => new()
    {
        Id = Guid.NewGuid(),
        Name = "local",
        IsLocal = true,
        Status = ServerStatus.Online,
        TotalMemoryBytes = 1000 * MB,
        // No headroom, so the arithmetic under test is the committed side rather than the reserve.
        ReservedMemoryRatio = 0,
        CpuCores = 4
    };

    [Fact]
    public async Task A_database_counts_against_the_machine_it_runs_on()
    {
        var server = Machine();
        await using var db = Db();
        db.Servers.Add(server);
        db.ManagedServices.Add(new Harbora.Domain.Services.ManagedService
        {
            WorkspaceId = Guid.NewGuid(), ServerId = server.Id, Name = "orders",
            MemoryLimitBytes = 512 * MB, CpuLimit = 1
        });
        await db.SaveChangesAsync();

        var node = await new NodeCapacityService(db).GetAsync(server.Id, CancellationToken.None);

        node!.CommittedMemoryBytes.Should().Be(512 * MB);
        node.CommittedCpu.Should().Be(1);
    }

    [Fact]
    public async Task Applications_and_databases_are_added_together()
    {
        var server = Machine();
        await using var db = Db();
        db.Servers.Add(server);
        db.Apps.Add(new Harbora.Domain.Apps.App
        {
            WorkspaceId = Guid.NewGuid(), ServerId = server.Id, Name = "web", Slug = "web",
            MemoryLimitBytes = 256 * MB, CpuLimit = 0.5
        });
        db.ManagedServices.Add(new Harbora.Domain.Services.ManagedService
        {
            WorkspaceId = Guid.NewGuid(), ServerId = server.Id, Name = "db",
            MemoryLimitBytes = 512 * MB, CpuLimit = 1
        });
        await db.SaveChangesAsync();

        var node = await new NodeCapacityService(db).GetAsync(server.Id, CancellationToken.None);

        node!.CommittedMemoryBytes.Should().Be(768 * MB);
        node.CommittedCpu.Should().Be(1.5);
    }

    [Fact]
    public async Task What_a_database_holds_is_not_offered_to_something_else()
    {
        // The consequence, stated as the scheduler sees it. Without the database counted, this node
        // would report 1000 MB free and accept a 600 MB application on top of a 512 MB database.
        var server = Machine();
        await using var db = Db();
        db.Servers.Add(server);
        db.ManagedServices.Add(new Harbora.Domain.Services.ManagedService
        {
            WorkspaceId = Guid.NewGuid(), ServerId = server.Id, Name = "db",
            MemoryLimitBytes = 512 * MB
        });
        await db.SaveChangesAsync();

        var placement = await new SchedulerService(new NodeCapacityService(db))
            .PlaceAsync(600 * MB, 0, null, CancellationToken.None);

        placement.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Another_tenants_load_is_counted_too()
    {
        // Placement is about the machine, not about the person doing the placing. Counting only the
        // caller's own resources would report a node as nearly empty while somebody else filled it.
        var server = Machine();
        await using var db = Db();
        db.Servers.Add(server);
        db.Apps.Add(new Harbora.Domain.Apps.App
        {
            WorkspaceId = Guid.NewGuid(), ServerId = server.Id, Name = "theirs", Slug = "theirs",
            MemoryLimitBytes = 900 * MB
        });
        await db.SaveChangesAsync();

        var node = await new NodeCapacityService(db).GetAsync(server.Id, CancellationToken.None);

        node!.CommittedMemoryBytes.Should().Be(900 * MB);
    }

    [Fact]
    public async Task An_empty_machine_has_all_of_itself_free()
    {
        var server = Machine();
        await using var db = Db();
        db.Servers.Add(server);
        await db.SaveChangesAsync();

        var node = await new NodeCapacityService(db).GetAsync(server.Id, CancellationToken.None);

        node!.CommittedMemoryBytes.Should().Be(0);
        node.FreeMemoryBytes.Should().Be(1000 * MB);
    }
}
