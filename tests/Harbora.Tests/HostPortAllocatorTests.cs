using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Host ports on a remote node.
///
/// They used to be <c>20000 + sha256(slug#number) % 10000</c> — deterministic, and blind. Ten thousand
/// slots picked at random collide far sooner than the number suggests: a coin flip at about 119
/// deployments on one node, and every redeploy draws again. <c>app78</c> and <c>app138</c> both land
/// on 22585 at their first deployment.
///
/// The damage was worse than a failed deploy. Routes store host *and* port, so a port belonging to a
/// retired deployment that is later handed to a different app quietly points the first app's traffic
/// at the second app's container.
/// </summary>
public class HostPortAllocatorTests
{
    private static readonly Guid NodeA = Guid.CreateVersion7();
    private static readonly Guid NodeB = Guid.CreateVersion7();

    private static HarboraDbContext NewContext(string name) =>
        new(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(name).Options);

    private static HostPortAllocator AllocatorOn(HarboraDbContext db) =>
        new(db, NullLogger<HostPortAllocator>.Instance);

    // ---- picking a port ----

    [Fact]
    public void The_first_free_port_is_the_start_of_the_range()
        => HostPortRange.NextFree([]).Should().Be(HostPortRange.First);

    [Fact]
    public void A_taken_port_is_skipped()
        => HostPortRange.NextFree([20000, 20001, 20003]).Should().Be(20002);

    [Fact]
    public void An_exhausted_range_returns_nothing_rather_than_a_port_outside_it()
    {
        // Handing back 30000 would publish where nothing routes, and the deploy would "succeed".
        var everything = Enumerable.Range(HostPortRange.First, HostPortRange.Last - HostPortRange.First + 1);

        HostPortRange.NextFree(everything).Should().BeNull();
    }

    // ---- reserving it ----

    [Fact]
    public async Task Two_apps_on_the_same_node_never_share_a_port()
    {
        await using var db = NewContext("ports-two-apps-" + Guid.NewGuid());
        var allocator = AllocatorOn(db);

        var first = await allocator.AllocateAsync(NodeA, Guid.CreateVersion7(), 1, default);
        var second = await allocator.AllocateAsync(NodeA, Guid.CreateVersion7(), 1, default);

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Consecutive_deployments_of_one_app_hold_different_ports()
    {
        // They run side by side during the cutover; one port between them would mean the new
        // container cannot bind, or binds over the one still serving traffic.
        await using var db = NewContext("ports-cutover-" + Guid.NewGuid());
        var allocator = AllocatorOn(db);
        var app = Guid.CreateVersion7();

        var old = await allocator.AllocateAsync(NodeA, app, 1, default);
        var fresh = await allocator.AllocateAsync(NodeA, app, 2, default);

        fresh.Should().NotBe(old);
    }

    [Fact]
    public async Task Asking_twice_for_the_same_deployment_returns_the_same_port()
    {
        // A retried deploy must not consume a second port, or a flapping deploy drains the range.
        await using var db = NewContext("ports-retry-" + Guid.NewGuid());
        var allocator = AllocatorOn(db);
        var app = Guid.CreateVersion7();

        var first = await allocator.AllocateAsync(NodeA, app, 7, default);
        var again = await allocator.AllocateAsync(NodeA, app, 7, default);

        again.Should().Be(first);
        db.HostPortAllocations.Should().ContainSingle();
    }

    [Fact]
    public async Task Nodes_do_not_take_ports_from_each_other()
    {
        // Each node has its own address space; sharing the range would waste it for no reason.
        await using var db = NewContext("ports-nodes-" + Guid.NewGuid());
        var allocator = AllocatorOn(db);

        var onA = await allocator.AllocateAsync(NodeA, Guid.CreateVersion7(), 1, default);
        var onB = await allocator.AllocateAsync(NodeB, Guid.CreateVersion7(), 1, default);

        onB.Should().Be(onA);
    }

    // ---- giving it back ----

    [Fact]
    public async Task A_released_port_is_handed_out_again()
    {
        await using var db = NewContext("ports-reuse-" + Guid.NewGuid());
        var allocator = AllocatorOn(db);
        var gone = Guid.CreateVersion7();

        var released = await allocator.AllocateAsync(NodeA, gone, 1, default);
        await allocator.ReleaseAppAsync(gone, default);
        var reused = await allocator.AllocateAsync(NodeA, Guid.CreateVersion7(), 1, default);

        reused.Should().Be(released, "a range that only shrinks would run out");
    }

    [Fact]
    public async Task A_cutover_keeps_the_live_deployments_port_and_frees_the_rest()
    {
        await using var db = NewContext("ports-release-" + Guid.NewGuid());
        var allocator = AllocatorOn(db);
        var app = Guid.CreateVersion7();
        await allocator.AllocateAsync(NodeA, app, 1, default);
        var live = await allocator.AllocateAsync(NodeA, app, 2, default);

        await allocator.ReleaseAllButAsync(NodeA, app, keepDeploymentNumber: 2, default);

        db.HostPortAllocations.Should().ContainSingle()
            .Which.Port.Should().Be(live, "the port carrying traffic is the one that must survive");
    }

    [Fact]
    public async Task A_failed_deployment_gives_its_port_back()
    {
        // Otherwise a node loses one port per failed deploy until the range is gone.
        await using var db = NewContext("ports-failed-" + Guid.NewGuid());
        var allocator = AllocatorOn(db);
        var app = Guid.CreateVersion7();

        var port = await allocator.AllocateAsync(NodeA, app, 3, default);
        await allocator.ReleaseAsync(NodeA, app, 3, default);

        db.HostPortAllocations.Should().BeEmpty();
        (await allocator.AllocateAsync(NodeA, Guid.CreateVersion7(), 1, default)).Should().Be(port);
    }

    [Fact]
    public async Task Releasing_one_app_leaves_another_apps_reservation_alone()
    {
        await using var db = NewContext("ports-scope-" + Guid.NewGuid());
        var allocator = AllocatorOn(db);
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();
        await allocator.AllocateAsync(NodeA, mine, 1, default);
        var keep = await allocator.AllocateAsync(NodeA, theirs, 1, default);

        await allocator.ReleaseAppAsync(mine, default);

        db.HostPortAllocations.Should().ContainSingle().Which.Port.Should().Be(keep);
    }

    [Fact]
    public async Task An_exhausted_node_says_so_instead_of_publishing_somewhere_unroutable()
    {
        await using var db = NewContext("ports-full-" + Guid.NewGuid());
        for (var port = HostPortRange.First; port <= HostPortRange.Last; port++)
            db.HostPortAllocations.Add(new HostPortAllocation
            { ServerId = NodeA, AppId = Guid.CreateVersion7(), DeploymentNumber = 1, Port = port });
        await db.SaveChangesAsync();

        var allocate = async () => await AllocatorOn(db).AllocateAsync(NodeA, Guid.CreateVersion7(), 1, default);

        (await allocate.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("another node", "the message has to name a way out");
    }
}
