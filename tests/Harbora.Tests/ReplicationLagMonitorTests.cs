using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Backups;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 3.2 (round-2 market-gaps plan): <see cref="ReplicationLagMonitor"/> is the single writer of
/// <see cref="ReplicationLagStatus"/>, and therefore the class this task's single most important
/// requirement actually rests on — a replica whose lag cannot be measured must say so, never a
/// fabricated zero. Registered the same way <c>WalArchiveShipperTests</c> proves its own sibling
/// shipper: dependencies are resolved through <see cref="IServiceScopeFactory"/> exactly as they are
/// in production, so every one of them here is a singleton instance the test still holds.
/// </summary>
public class ReplicationLagMonitorTests
{
    private static ReplicationLagMonitor NewMonitor(BackupHarness h)
    {
        var services = new ServiceCollection();
        services.AddSingleton(h.Db);
        services.AddSingleton<IServerEngineFactory>(h.Engines);
        services.AddSingleton<ISecretProtector>(new Harbora.Tests.Fakes.PassthroughProtector());
        services.AddSingleton<ISystemClock>(h.Clock);
        services.AddSingleton(Options.Create(new Harbora.Infrastructure.Deployments.HarboraRuntimeOptions()));
        var provider = services.BuildServiceProvider();

        return new ReplicationLagMonitor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReplicationLagMonitor>.Instance);
    }

    private static async Task<ManagedService> SeedReplicaAsync(BackupHarness h, ManagedService primary, string name)
    {
        var environmentId = primary.EnvironmentId;
        var replica = await h.SeedDatabaseAsync(primary.ServerId, name, environmentId);
        replica.PrimaryManagedServiceId = primary.Id;
        replica.Status = ServiceStatus.Running;
        await h.Db.SaveChangesAsync();
        return replica;
    }

    [Fact]
    public async Task A_successful_query_records_a_known_lag()
    {
        using var h = new BackupHarness();
        var primary = await h.SeedDatabaseAsync(Guid.NewGuid(), "orders");
        var replica = await SeedReplicaAsync(h, primary, "orders-standby");

        var replayedAt = new DateTimeOffset(2026, 9, 4, 10, 15, 23, TimeSpan.Zero);
        h.Clock.UtcNow = replayedAt.AddSeconds(5);
        h.Docker.OneOffOutput.Add("2026-09-04 10:15:23+00\n");

        await NewMonitor(h).CheckDueReplicasAsync(default);

        var status = await h.Db.ReplicationLagStatuses.SingleAsync(s => s.ManagedServiceId == replica.Id);
        status.LastSuccessAt.Should().Be(h.Clock.UtcNow);
        status.LagSeconds.Should().BeApproximately(5, 0.5);
        status.ConsecutiveFailures.Should().Be(0);

        var view = ReplicationLagPresenter.Compute(status, h.Clock.UtcNow);
        view.Status.Should().Be(ReplicaLagStatus.Known);
    }

    [Fact]
    public async Task A_failing_query_leaves_lag_unknown_and_never_records_a_zero()
    {
        using var h = new BackupHarness();
        var primary = await h.SeedDatabaseAsync(Guid.NewGuid(), "orders");
        var replica = await SeedReplicaAsync(h, primary, "orders-standby");
        h.Docker.OneOffExitCode = 1;

        await NewMonitor(h).CheckDueReplicasAsync(default);

        var status = await h.Db.ReplicationLagStatuses.SingleAsync(s => s.ManagedServiceId == replica.Id);
        status.LastSuccessAt.Should().BeNull();
        status.LagSeconds.Should().BeNull();
        status.ConsecutiveFailures.Should().Be(1);
        status.LastError.Should().NotBeNullOrWhiteSpace();

        var view = ReplicationLagPresenter.Compute(status, h.Clock.UtcNow);
        view.Status.Should().Be(ReplicaLagStatus.Unknown);
        view.Lag.Should().BeNull("a failed query must never be presented as a lag of zero");
    }

    [Fact]
    public async Task An_empty_answer_records_a_success_with_no_figure_never_a_zero()
    {
        using var h = new BackupHarness();
        var primary = await h.SeedDatabaseAsync(Guid.NewGuid(), "orders");
        var replica = await SeedReplicaAsync(h, primary, "orders-standby");
        h.Docker.OneOffOutput.Add("\n"); // pg_last_xact_replay_timestamp() answered SQL NULL

        await NewMonitor(h).CheckDueReplicasAsync(default);

        var status = await h.Db.ReplicationLagStatuses.SingleAsync(s => s.ManagedServiceId == replica.Id);
        status.LastSuccessAt.Should().NotBeNull("the query itself succeeded — PostgreSQL just had nothing to say yet");
        status.LagSeconds.Should().BeNull();

        var view = ReplicationLagPresenter.Compute(status, h.Clock.UtcNow);
        view.Status.Should().Be(ReplicaLagStatus.Unknown);
        view.Lag.Should().BeNull();
    }

    [Fact]
    public async Task A_stopped_replica_is_never_queried_at_all()
    {
        using var h = new BackupHarness();
        var primary = await h.SeedDatabaseAsync(Guid.NewGuid(), "orders");
        var replica = await SeedReplicaAsync(h, primary, "orders-standby");
        replica.Status = ServiceStatus.Stopped;
        await h.Db.SaveChangesAsync();

        await NewMonitor(h).CheckDueReplicasAsync(default);

        h.Docker.Calls.Should().BeEmpty("a stopped replica has no live connection to query");
        (await h.Db.ReplicationLagStatuses.AnyAsync(s => s.ManagedServiceId == replica.Id)).Should().BeFalse(
            "no row at all is what makes ReplicationLagPresenter say NeverMeasured rather than Unknown");
    }

    [Fact]
    public async Task An_ordinary_instance_with_no_replicas_is_never_touched()
    {
        using var h = new BackupHarness();
        await h.SeedDatabaseAsync(Guid.NewGuid(), "orders"); // no PrimaryManagedServiceId — an ordinary instance

        await NewMonitor(h).CheckDueReplicasAsync(default);

        h.Docker.Calls.Should().BeEmpty();
    }
}
