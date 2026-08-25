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

    // --- the owner's ask: commitment ratios are the admin's call, per server ---

    /// <summary>
    /// CPU overcommit already worked this way (<see cref="Server.CpuOvercommitFactor"/> pre-dates
    /// this feature); this proves memory now does too, with its own factor rather than sharing CPU's.
    /// </summary>
    [Fact]
    public async Task A_memory_overcommit_factor_scales_allocatable_memory_the_same_way_cpus_does()
    {
        var server = Machine();
        server.MemoryOvercommitFactor = 2;
        await using var db = Db();
        db.Servers.Add(server);
        await db.SaveChangesAsync();

        var node = await new NodeCapacityService(db).GetAsync(server.Id, CancellationToken.None);

        node!.AllocatableMemoryBytes.Should().Be(2000 * MB, "1000 MB physical × 2.0 overcommit, no headroom reserved here");
    }

    /// <summary>
    /// The owner's explicit ask: "a factor below 1 (undercommit) is legitimate." The old formula's
    /// <c>Math.Max(1, factor)</c> floor made that impossible for CPU — this proves it no longer is,
    /// for either resource.
    /// </summary>
    [Fact]
    public async Task An_undercommit_factor_below_one_is_honoured_for_both_resources()
    {
        var server = Machine();
        server.CpuOvercommitFactor = 0.5;
        server.MemoryOvercommitFactor = 0.5;
        await using var db = Db();
        db.Servers.Add(server);
        await db.SaveChangesAsync();

        var node = await new NodeCapacityService(db).GetAsync(server.Id, CancellationToken.None);

        node!.AllocatableCpu.Should().Be(2, "4 physical cores × 0.5 — deliberately committing less than the machine has");
        node.AllocatableMemoryBytes.Should().Be(500 * MB);
    }

    /// <summary>
    /// A stored zero must never collapse allocatable to zero: <see cref="NodeCapacity.CanFit"/> reads
    /// a zero-or-less allocatable figure as "unmeasured — allow everything", so a factor of zero would
    /// be read as the opposite of a refusal. The write side refuses zero outright
    /// (<see cref="Harbora.Domain.Servers.ServerCapacityPolicy"/>); this is the defensive fallback for
    /// a row that predates that validation.
    /// </summary>
    [Fact]
    public async Task A_zero_or_negative_stored_factor_falls_back_to_no_overcommit_rather_than_zeroing_capacity()
    {
        var server = Machine();
        server.CpuOvercommitFactor = 0;
        server.MemoryOvercommitFactor = -1;
        await using var db = Db();
        db.Servers.Add(server);
        await db.SaveChangesAsync();

        var node = await new NodeCapacityService(db).GetAsync(server.Id, CancellationToken.None);

        node!.AllocatableCpu.Should().Be(4, "falls back to 1.0× (the machine's own cores), not 0");
        node.AllocatableMemoryBytes.Should().Be(1000 * MB);
    }

    // --- honest refusals (SchedulerService) ---

    [Fact]
    public async Task A_refused_placement_names_what_is_committed_and_what_is_allocatable()
    {
        var server = Machine();
        server.CpuOvercommitFactor = 1;
        await using var db = Db();
        db.Servers.Add(server);
        db.Apps.Add(new Harbora.Domain.Apps.App
        {
            WorkspaceId = Guid.NewGuid(), ServerId = server.Id, Name = "full", Slug = "full",
            MemoryLimitBytes = 900 * MB
        });
        await db.SaveChangesAsync();

        var placement = await new SchedulerService(new NodeCapacityService(db))
            .CheckAsync(server.Id, 200 * MB, 0, CancellationToken.None);

        placement.Ok.Should().BeFalse();
        // Not a bare "no capacity": the actual committed/allocatable numbers, in GB, are present —
        // 900 MB of the 1000 MB machine (no headroom reserved in this fixture).
        placement.Reason.Should().Contain("0.9 GB").And.Contain("1.0 GB");
        // ...and it is not silently reporting the physical machine as the reason — it names
        // "allocatable" as a policy figure and points at where that policy is changed.
        placement.Reason.Should().Contain("allocatable").And.Contain("Capacity policy");
    }

    [Fact]
    public async Task PlaceAsync_reports_the_closest_miss_rather_than_a_bare_refusal()
    {
        var server = Machine();
        await using var db = Db();
        db.Servers.Add(server);
        db.Apps.Add(new Harbora.Domain.Apps.App
        {
            WorkspaceId = Guid.NewGuid(), ServerId = server.Id, Name = "full", Slug = "full",
            MemoryLimitBytes = 900 * MB
        });
        await db.SaveChangesAsync();

        var placement = await new SchedulerService(new NodeCapacityService(db))
            .PlaceAsync(200 * MB, 0, null, CancellationToken.None);

        placement.Ok.Should().BeFalse();
        placement.Reason.Should().Contain("Closest").And.Contain("allocatable");
    }
}
