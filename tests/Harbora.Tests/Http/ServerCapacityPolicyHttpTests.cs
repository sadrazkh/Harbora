using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The capacity policy for a server that is <b>not</b> a node.
///
/// <para>
/// The 2026-08-27 overcommit work put the only editor for <see cref="Server.CpuOvercommitFactor"/>
/// and <see cref="Server.MemoryOvercommitFactor"/> on the node detail page, behind
/// <c>sched.IsAttached</c> — which is <c>ServerId is not null</c> on a <c>Node</c> row. But the
/// <b>Local</b> server is seeded directly by <c>DbSeeder</c> and a <c>Node</c> row is only ever
/// created by <c>NodeEnrollmentService</c> when an agent enrols. So on a single-server install — the
/// common case, and the one where every app actually runs — the panel offered no way to reach these
/// values at all, while telling the operator they were an administrator's decision. A deploy refused
/// with 409 for want of vCPU could not be unblocked from the panel.
/// </para>
///
/// <para>
/// These tests are about that gap: the Servers page must carry the same policy form, with the same
/// bounds, for every server including the local one.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ServerCapacityPolicyHttpTests(HarboraHttpFixture fixture)
{
    private const long GB = 1024L * 1024 * 1024;
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>A server with no <c>Node</c> row at all — exactly what <c>DbSeeder</c> creates.</summary>
    private Guid SeedServerWithNoNode(
        string suffix, bool isLocal = true, int cpuCores = 6, long totalMemoryGb = 10,
        double reservedMemoryRatio = 0.15, double cpuOvercommitFactor = 1, double memoryOvercommitFactor = 1)
    {
        var server = new Server
        {
            Name = "srv-" + suffix,
            Hostname = isLocal ? "localhost" : "10.1.0." + suffix,
            IsLocal = isLocal,
            Status = ServerStatus.Online,
            CpuCores = cpuCores,
            TotalMemoryBytes = totalMemoryGb * GB,
            ReservedMemoryRatio = reservedMemoryRatio,
            CpuOvercommitFactor = cpuOvercommitFactor,
            MemoryOvercommitFactor = memoryOvercommitFactor
        };

        Panel.Seed(db => db.Servers.Add(server));
        return server.Id;
    }

    private async Task<HttpClient> OwnerClientAsync(string email, string ip)
    {
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        return await Panel.SignedInAs(ip, email);
    }

    private Server Read(Guid id) =>
        Panel.Read(db => db.Servers.IgnoreQueryFilters().Single(s => s.Id == id));

    [Fact]
    public async Task A_server_with_no_node_still_gets_a_capacity_policy_form()
    {
        var id = SeedServerWithNoNode("form", cpuOvercommitFactor: 2, memoryOvercommitFactor: 1.5,
            reservedMemoryRatio: 0.20);
        var client = await OwnerClientAsync("srv-cap-form@example.com", "198.51.100.53");

        var html = await (await client.GetAsync("/servers")).Content.ReadAsStringAsync();

        html.Should().Contain($"data-capacity-policy=\"{id}\"",
            "the local server is where everything runs on a single-server install, and it had no editor at all");
        html.Should().MatchRegex($"id=\"cpuOvercommitFactor-{id}\"[^>]*value=\"2\"");
        html.Should().MatchRegex($"id=\"memoryOvercommitFactor-{id}\"[^>]*value=\"1.5\"");
        html.Should().MatchRegex($"id=\"reservedMemoryPercent-{id}\"[^>]*value=\"20\"");
    }

    [Fact]
    public async Task The_form_states_each_ceiling_so_the_two_are_not_read_as_interchangeable()
    {
        var id = SeedServerWithNoNode("ceilings");
        var client = await OwnerClientAsync("srv-cap-ceil@example.com", "198.51.100.54");

        var html = await (await client.GetAsync("/servers")).Content.ReadAsStringAsync();

        // CPU tolerates 8x and memory only 4x, because memory overcommit fails by OOM-kill while CPU
        // contention only queues. A form that offered one ceiling for both would be lying about that.
        html.Should().MatchRegex($"id=\"cpuOvercommitFactor-{id}\"[^>]*max=\"8\"");
        html.Should().MatchRegex($"id=\"memoryOvercommitFactor-{id}\"[^>]*max=\"4\"");
    }

    [Fact]
    public async Task Saving_valid_factors_stores_them_and_changes_what_the_scheduler_may_hand_out()
    {
        var id = SeedServerWithNoNode("save", cpuCores: 6, totalMemoryGb: 10);
        var client = await OwnerClientAsync("srv-cap-save@example.com", "198.51.100.55");
        var token = await client.AntiforgeryTokenFrom("/servers");

        var response = await client.PostFormAsync($"/servers/{id}/capacity-policy", token,
            ("reservedMemoryPercent", "15"),
            ("cpuOvercommitFactor", "3"),
            ("memoryOvercommitFactor", "2"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var after = Read(id);
        after.CpuOvercommitFactor.Should().Be(3);
        after.MemoryOvercommitFactor.Should().Be(2);
        after.ReservedMemoryRatio.Should().BeApproximately(0.15, 0.0001);
    }

    [Fact]
    public async Task A_cpu_factor_past_the_ceiling_is_refused_and_nothing_is_stored()
    {
        var id = SeedServerWithNoNode("cpumax", cpuOvercommitFactor: 2);
        var client = await OwnerClientAsync("srv-cap-cpumax@example.com", "198.51.100.56");
        var token = await client.AntiforgeryTokenFrom("/servers");

        await client.PostFormAsync($"/servers/{id}/capacity-policy", token,
            ("reservedMemoryPercent", "15"),
            ("cpuOvercommitFactor", "9"),
            ("memoryOvercommitFactor", "1"));

        Read(id).CpuOvercommitFactor.Should().Be(2, "a refused value must not be half-applied");
    }

    [Fact]
    public async Task A_memory_factor_past_its_tighter_ceiling_is_refused_even_though_cpu_would_allow_it()
    {
        var id = SeedServerWithNoNode("memmax", memoryOvercommitFactor: 1);
        var client = await OwnerClientAsync("srv-cap-memmax@example.com", "198.51.100.57");
        var token = await client.AntiforgeryTokenFrom("/servers");

        // 6 is a perfectly legal CPU factor and an illegal memory one. The asymmetry is the point.
        await client.PostFormAsync($"/servers/{id}/capacity-policy", token,
            ("reservedMemoryPercent", "15"),
            ("cpuOvercommitFactor", "6"),
            ("memoryOvercommitFactor", "6"));

        var after = Read(id);
        after.MemoryOvercommitFactor.Should().Be(1);
        after.CpuOvercommitFactor.Should().Be(1,
            "one invalid field refuses the whole form rather than applying the half that passed");
    }

    [Fact]
    public async Task A_decimal_factor_is_parsed_invariantly_even_though_the_panel_renders_Persian()
    {
        var id = SeedServerWithNoNode("culture");
        var client = await OwnerClientAsync("srv-cap-culture@example.com", "198.51.100.58");
        var token = await client.AntiforgeryTokenFrom("/servers");

        // The trap NodesController's own remark documents: the request culture is fa, and binding a
        // double with it turns "2.5" into 0. These fields bind as strings and parse invariantly.
        await client.PostFormAsync($"/servers/{id}/capacity-policy", token,
            ("reservedMemoryPercent", "15"),
            ("cpuOvercommitFactor", "2.5"),
            ("memoryOvercommitFactor", "1"));

        Read(id).CpuOvercommitFactor.Should().Be(2.5);
    }
}
