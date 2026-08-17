using FluentAssertions;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Networking;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which network a database dump, a database restore and its restore rehearsal reach the database
/// on — the three call sites in <see cref="Harbora.Infrastructure.Backups.BackupEngine"/> that used
/// to run their one-off container on the shared workspace network directly, rather than asking for
/// the database's own environment network the way its neighbour
/// <c>ManagedServiceEngine.TestConnectionAsync</c> already did.
///
/// <para>
/// This is the safety argument for P3 (2026-08-17 app-environment-management design), stated as a
/// test: the workspace network is only reachable today because of the dual attach every workload
/// still carries, and once that attach goes, a one-off still asking for the workspace network by
/// name gets a connection refused from a container that lives a few seconds — the quietest failure
/// this platform can produce. So each test here pins <b>which network the one-off was asked to run
/// on</b>, not whether the operation reported success — a fake engine that always answers exit 0
/// would pass every one of these on both sides of the fix.
/// </para>
/// </summary>
public class BackupDatabaseNetworkTests
{
    [Fact]
    public async Task A_database_dump_reaches_it_on_its_environments_own_network()
    {
        using var h = new BackupHarness();
        var environmentId = await h.SeedEnvironmentAsync("shop", "prod");
        var database = await h.SeedDatabaseAsync(Guid.NewGuid(), environmentId: environmentId);
        var backup = await h.SeedPendingBackupAsync(BackupType.Database, database.Id.ToString());

        // The staged file never lands — FakeDockerEngine records the request but writes nothing to
        // disk — so this backup ends Failed. That is not what this test is about: the one-off already
        // ran, and its request is what is asserted on.
        await h.Engine().RunAsync(backup.Id, default);

        var request = h.Docker.OneOffRequests.Should().ContainSingle().Subject;
        EnvironmentNetwork.IsEnvironmentNetwork(request.NetworkMode).Should().BeTrue(
            $"the dump ran on '{request.NetworkMode}', not this database's own environment network");
    }

    [Fact]
    public async Task A_database_restore_reaches_it_on_its_environments_own_network()
    {
        using var h = new BackupHarness();
        var environmentId = await h.SeedEnvironmentAsync("shop", "prod");
        var database = await h.SeedDatabaseAsync(Guid.NewGuid(), environmentId: environmentId);
        var backup = await h.SeedCompletedDatabaseDumpAsync(database.Id);

        await h.Engine().RestoreAsync(backup.Id, default);

        // Two one-offs: the safety dump taken before anything is touched, and the restore itself.
        // Both must reach the database on its own network — a safety dump that quietly used the
        // wrong network would fail exactly where the restore needs it least.
        h.Docker.OneOffRequests.Should().HaveCount(2);
        h.Docker.OneOffRequests.Should().OnlyContain(r => EnvironmentNetwork.IsEnvironmentNetwork(r.NetworkMode),
            "every one-off a restore runs must reach the database on its own environment network");
    }

    [Fact]
    public async Task A_restore_rehearsal_reaches_the_database_on_its_environments_own_network()
    {
        using var h = new BackupHarness();
        var environmentId = await h.SeedEnvironmentAsync("shop", "prod");
        var database = await h.SeedDatabaseAsync(Guid.NewGuid(), environmentId: environmentId);
        var backup = await h.SeedCompletedDatabaseDumpAsync(database.Id);

        await h.Engine().VerifyAsync(backup.Id, default);

        // create, restore, count and drop — the rehearsal's own four steps, every one of them a
        // separate one-off container against the same throwaway database.
        h.Docker.OneOffRequests.Should().HaveCount(4);
        h.Docker.OneOffRequests.Should().OnlyContain(r => EnvironmentNetwork.IsEnvironmentNetwork(r.NetworkMode),
            "the rehearsal must restore into its scratch database over the environment network, " +
            "the same one the real restore would use");
    }

}
