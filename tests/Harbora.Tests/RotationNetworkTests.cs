using FluentAssertions;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which network <see cref="Harbora.Infrastructure.Services.ManagedServiceEngine.RotatePasswordAsync"/>
/// reaches the database on.
///
/// <para>
/// P3 (2026-08-17 app-environment-management design) moves four one-off containers that reach a
/// customer's database off the shared workspace network and onto the database's own environment
/// network — ahead of the workspace network's dual attach going away, because removing the attach
/// before the move leaves each of these one-offs unable to resolve the database's container name at
/// all. Rotation was the one call site that had not already made this move: its neighbour
/// <c>TestConnectionAsync</c> already asks for the environment's network, and this pins rotation to
/// the same rule. Asserted on the request the fake engine actually received, not on whether the
/// rotation reported success — a stub that always returns exit 0 would pass this test on either side
/// of the fix, which is exactly the trap <c>DatabaseAccessLifecycleTests.cs</c> already names.
/// </para>
/// </summary>
public class RotationNetworkTests
{
    [Fact]
    public async Task Rotating_a_password_reaches_the_database_on_its_environments_own_network()
    {
        using var h = new RotationHarness();
        var environmentId = await h.SeedEnvironmentAsync("shop", "prod");
        var database = await h.SeedDatabaseAsync("orders", environmentId);

        await h.Engine().RotatePasswordAsync(database.Id, default);

        var request = h.Docker.OneOffRequests.Should().ContainSingle(
            r => string.Join(' ', r.Command).Contains("ALTER", StringComparison.Ordinal)).Subject;

        EnvironmentNetwork.IsEnvironmentNetwork(request.NetworkMode).Should().BeTrue(
            $"the ALTER USER ran on '{request.NetworkMode}', which is not this database's own " +
            "environment network — the workspace network stops being reachable once the dual attach " +
            "goes, and this is the failure that would surface as instead");
    }

    /// <summary>
    /// An app placed before projects and environments existed — still legal until P2 makes the
    /// column required — must keep rotating on the workspace network it has always had. Losing this
    /// would strand a database nobody has redeployed since the migration that backfilled the column.
    /// </summary>
    [Fact]
    public async Task A_database_with_no_environment_yet_still_rotates_on_the_workspace_network()
    {
        using var h = new RotationHarness();
        var database = await h.SeedDatabaseAsync("orders");

        await h.Engine().RotatePasswordAsync(database.Id, default);

        var request = h.Docker.OneOffRequests.Should().ContainSingle(
            r => string.Join(' ', r.Command).Contains("ALTER", StringComparison.Ordinal)).Subject;

        request.NetworkMode.Should().Be("harbora-ws-acme");
    }
}
