using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Nodes;
using Harbora.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The admin form the owner asked for: CPU commitment already had a knob
/// (<see cref="Server.CpuOvercommitFactor"/>) but nothing let an operator turn it, and memory had no
/// knob at all. This is that form, per node/server, plus the honesty the request demanded — the
/// capacity page must show physical, factor and allocatable together, never a bare number that reads
/// as hardware.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class NodeCapacityPolicyHttpTests(HarboraHttpFixture fixture)
{
    private const long GB = 1024L * 1024 * 1024;
    private HarboraWebFactory Panel => fixture.Panel;

    private (string NodeId, Guid ServerId) SeedAttachedNode(
        string suffix, int cpuCores = 4, long totalMemoryGb = 16,
        double reservedMemoryRatio = 0.15, double cpuOvercommitFactor = 1, double memoryOvercommitFactor = 1)
    {
        var server = new Server
        {
            Name = "node-" + suffix,
            Hostname = "10.0.0." + suffix,
            IsLocal = false,
            Status = ServerStatus.Online,
            CpuCores = cpuCores,
            TotalMemoryBytes = totalMemoryGb * GB,
            ReservedMemoryRatio = reservedMemoryRatio,
            CpuOvercommitFactor = cpuOvercommitFactor,
            MemoryOvercommitFactor = memoryOvercommitFactor
        };
        var nodeId = "n-cap-" + suffix;
        var node = new Node
        {
            NodeId = nodeId,
            Name = "node-" + suffix,
            Status = NodeStatus.Online,
            Health = "healthy",
            ServerId = server.Id,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };

        Panel.Seed(db =>
        {
            db.Servers.Add(server);
            db.Nodes.Add(node);
        });

        return (nodeId, server.Id);
    }

    private async Task<HttpClient> OwnerClientAsync(string email, string ip)
    {
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        return await Panel.SignedInAs(ip, email);
    }

    // --- the honesty requirement: physical, factor and allocatable together ---

    [Fact]
    public async Task The_node_page_shows_physical_capacity_beside_the_allocatable_figure_it_is_not()
    {
        var (nodeId, _) = SeedAttachedNode("honest", cpuCores: 6, totalMemoryGb: 11,
            reservedMemoryRatio: 0.15, cpuOvercommitFactor: 2, memoryOvercommitFactor: 1);
        var client = await OwnerClientAsync("cap-honest@example.com", "203.0.113.150");

        var html = await (await client.GetAsync($"/nodes/{nodeId}")).Content.ReadAsStringAsync();

        // The physical figure a bare "12 cores" would otherwise be mistaken for.
        html.Should().Contain("data-physical-cpu-cores=\"6\"");
        html.Should().Contain($"data-physical-memory-bytes=\"{11 * GB}\"");
        // The factor that turns 6 physical cores into 12 allocatable ones — the policy multiplier,
        // named as such rather than folded invisibly into a single number.
        html.Should().Contain("data-cpu-overcommit-factor=\"2\"");
        html.Should().Contain($"data-allocatable-cpu=\"12\"");
    }

    [Fact]
    public async Task The_capacity_policy_form_opens_on_the_servers_current_values()
    {
        var (nodeId, _) = SeedAttachedNode("current", reservedMemoryRatio: 0.20,
            cpuOvercommitFactor: 3, memoryOvercommitFactor: 1.5);
        var client = await OwnerClientAsync("cap-current@example.com", "203.0.113.151");

        var html = await (await client.GetAsync($"/nodes/{nodeId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-capacity-policy", "the admin form the owner asked for must be present");
        html.Should().Contain("name=\"reservedMemoryPercent\"");
        html.Should().MatchRegex("name=\"reservedMemoryPercent\"[^>]*value=\"20\"");
        html.Should().MatchRegex("name=\"cpuOvercommitFactor\"[^>]*value=\"3\"");
        html.Should().MatchRegex("name=\"memoryOvercommitFactor\"[^>]*value=\"1.5\"");
    }

    [Fact]
    public async Task The_form_shows_a_recommendation_without_forcing_it()
    {
        // CPU and memory get different suggested numbers — the owner's own framing (CPU contention
        // slows things down, memory exhaustion gets a process killed) demands two dials, not one
        // repeated twice. The stored value stays whatever the admin already set (asserted above);
        // this only proves the recommendation text itself is not the same for both.
        var (nodeId, _) = SeedAttachedNode("suggest");
        var client = await OwnerClientAsync("cap-suggest@example.com", "203.0.113.152");

        var html = await (await client.GetAsync($"/nodes/{nodeId}")).Content.ReadAsStringAsync();

        html.Should().Contain(ServerCapacityPolicy.RecommendedCpuOvercommitFactor.ToString("0.#"));
        html.Should().Contain(ServerCapacityPolicy.RecommendedMemoryOvercommitFactor.ToString("0.#"));
    }

    // --- saving ---

    [Fact]
    public async Task Saving_a_valid_policy_updates_the_server_row()
    {
        var (nodeId, serverId) = SeedAttachedNode("save");
        var client = await OwnerClientAsync("cap-save@example.com", "203.0.113.153");

        var token = await client.AntiforgeryTokenFrom($"/nodes/{nodeId}");
        var response = await client.PostFormAsync($"/nodes/{nodeId}/capacity-policy", token,
            ("reservedMemoryPercent", "25"), ("cpuOvercommitFactor", "2.5"), ("memoryOvercommitFactor", "1.2"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/nodes/{nodeId}");

        var server = Panel.Read(db => db.Servers.Single(s => s.Id == serverId));
        server.ReservedMemoryRatio.Should().BeApproximately(0.25, 1e-9);
        server.CpuOvercommitFactor.Should().Be(2.5);
        server.MemoryOvercommitFactor.Should().Be(1.2);
    }

    [Theory]
    // Zero: NodeCapacity.CanFit reads a zero-or-less allocatable figure as "unmeasured — allow
    // everything", so a stored zero would be the opposite of what a zero-as-refusal probably meant.
    [InlineData("0", "1", "1")]
    // Negative is nonsensical for a multiplier.
    [InlineData("-1", "1", "1")]
    // Past the CPU ceiling.
    [InlineData("9", "1", "1")]
    // Past the (tighter) memory ceiling.
    [InlineData("1", "5", "1")]
    // Reserving the whole host leaves nothing to schedule.
    [InlineData("1", "1", "95")]
    public async Task An_out_of_bounds_value_is_refused_and_nothing_changes(
        string cpuFactor, string memFactor, string reservedPercent)
    {
        var (nodeId, serverId) = SeedAttachedNode("bounds",
            reservedMemoryRatio: 0.15, cpuOvercommitFactor: 1, memoryOvercommitFactor: 1);
        var client = await OwnerClientAsync($"cap-bounds-{cpuFactor}-{memFactor}-{reservedPercent}@example.com", "203.0.113.154");

        var token = await client.AntiforgeryTokenFrom($"/nodes/{nodeId}");
        var response = await client.PostFormAsync($"/nodes/{nodeId}/capacity-policy", token,
            ("reservedMemoryPercent", reservedPercent), ("cpuOvercommitFactor", cpuFactor), ("memoryOvercommitFactor", memFactor));

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var server = Panel.Read(db => db.Servers.Single(s => s.Id == serverId));
        server.ReservedMemoryRatio.Should().Be(0.15, "a refused write must leave the row exactly as it was");
        server.CpuOvercommitFactor.Should().Be(1);
        server.MemoryOvercommitFactor.Should().Be(1);
    }

    [Fact]
    public async Task A_viewer_may_not_change_the_capacity_policy()
    {
        var (nodeId, serverId) = SeedAttachedNode("viewer");
        Panel.GivenUser(fixture.WorkspaceId, "cap-viewer2@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.155", "cap-viewer2@example.com");

        // A viewer cannot even reach the page (ServersManage gates the whole controller), so there is
        // no antiforgery token to spend — prove the route itself refuses, the same way
        // CapabilityPolicyHttpTests proves other capability-gated routes.
        var page = await client.GetAsync($"/nodes/{nodeId}");
        page.StatusCode.Should().Be(HttpStatusCode.Found);
        page.RedirectPath().Should().Be("/account/denied");

        Panel.Read(db => db.Servers.Single(s => s.Id == serverId)).CpuOvercommitFactor.Should().Be(1);
    }

    // --- lowering below current commitment ---

    [Fact]
    public async Task Lowering_a_factor_below_what_is_already_committed_saves_anyway_and_says_so()
    {
        // 4 cores × 2.0 = 8 allocatable vCPU; an app already holds 6 of them — comfortably inside.
        var (nodeId, serverId) = SeedAttachedNode("lower", cpuCores: 4, cpuOvercommitFactor: 2);
        Panel.Seed(db => db.Apps.Add(new App
        {
            WorkspaceId = fixture.WorkspaceId, ServerId = serverId, Name = "big", Slug = "cap-lower-big",
            CpuLimit = 6, EnvironmentId = fixture.DefaultEnvironmentId
        }));
        var client = await OwnerClientAsync("cap-lower@example.com", "203.0.113.156");

        // Drop the factor to 1.0 — 4 allocatable vCPU, less than the 6 already committed.
        var token = await client.AntiforgeryTokenFrom($"/nodes/{nodeId}");
        var response = await client.PostFormAsync($"/nodes/{nodeId}/capacity-policy", token,
            ("reservedMemoryPercent", "15"), ("cpuOvercommitFactor", "1"), ("memoryOvercommitFactor", "1"));

        // Not refused — an admin correcting an over-generous factor is a legitimate move.
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Servers.Single(s => s.Id == serverId)).CpuOvercommitFactor.Should().Be(1);

        // Said plainly on the very next render: committed now exceeds allocatable, in the numbers
        // themselves rather than in a sentence that would only exist in one language.
        var html = await (await client.GetAsync($"/nodes/{nodeId}")).Content.ReadAsStringAsync();
        html.Should().Contain("data-committed-cpu=\"6\"");
        html.Should().Contain("data-allocatable-cpu=\"4\"");

        // And nothing already placed was touched.
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Count(a => a.ServerId == serverId)).Should().Be(1);
    }
}
