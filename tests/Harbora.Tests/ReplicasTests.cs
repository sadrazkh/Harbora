using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 3.2 (round-2 market-gaps plan): PostgreSQL read replicas. Layered the same way <see cref="PitrTests"/>
/// (the sibling machinery this reuses) proves point-in-time recovery: <see cref="ReplicationSupport"/>
/// refuses the wrong engine before anything is built; <see cref="ReadReplicaPlan"/> proves every other
/// refusal a create can hit; <see cref="ReadReplicaSeedPlan"/>/<see cref="ReplicaPromotionPlan"/> prove
/// the command shapes; <see cref="ReplicationLagQuery"/> proves the lag query and its parser;
/// <see cref="ReplicationLagPresenter"/> proves the single most important requirement of this task —
/// a replica whose lag cannot be measured says so, never a fabricated zero.
/// </summary>
public class ReplicationSupportTests
{
    [Theory]
    [InlineData(ManagedServiceType.PostgreSql, true)]
    [InlineData(ManagedServiceType.MySql, false)]
    [InlineData(ManagedServiceType.MariaDb, false)]
    [InlineData(ManagedServiceType.Redis, false)]
    [InlineData(ManagedServiceType.MongoDb, false)]
    [InlineData(ManagedServiceType.RabbitMq, false)]
    [InlineData(ManagedServiceType.Nats, false)]
    [InlineData(ManagedServiceType.Meilisearch, false)]
    public void Only_postgresql_has_a_replication_story(ManagedServiceType type, bool expected) =>
        ReplicationSupport.Supports(type).Should().Be(expected);

    [Fact]
    public void The_unsupported_reason_names_the_engine() =>
        ReplicationSupport.UnsupportedReason(ManagedServiceType.Redis).Should().Contain("Redis");

    [Fact]
    public void MySql_is_told_binlog_replication_is_a_separate_follow_on() =>
        ReplicationSupport.UnsupportedReason(ManagedServiceType.MySql).Should().Contain("binlog",
            "MySQL replication is a named follow-on item, not something this refusal should imply is coming for free");
}

public class ReadReplicaPlanTests
{
    private static ManagedService Primary(
        ManagedServiceType type = ManagedServiceType.PostgreSql,
        ServiceStatus status = ServiceStatus.Running,
        string version = "16-alpine",
        Guid? primaryManagedServiceId = null) =>
        new()
        {
            Id = Guid.NewGuid(), Name = "orders-primary", Type = type, Status = status, Version = version,
            ServerId = Guid.NewGuid(), PrimaryManagedServiceId = primaryManagedServiceId
        };

    [Fact]
    public void A_non_postgresql_primary_is_refused_by_name()
    {
        var primary = Primary(ManagedServiceType.MySql);
        ReadReplicaPlan.WhyRefused(primary, primary.ServerId, primary.Version)
            .Should().Contain("MySql");
    }

    [Fact]
    public void Replicating_a_replica_is_refused()
    {
        var primary = Primary(primaryManagedServiceId: Guid.NewGuid());
        ReadReplicaPlan.WhyRefused(primary, primary.ServerId, primary.Version)
            .Should().Contain("itself a read replica");
    }

    [Fact]
    public void A_stopped_primary_is_refused()
    {
        var primary = Primary(status: ServiceStatus.Stopped);
        ReadReplicaPlan.WhyRefused(primary, primary.ServerId, primary.Version)
            .Should().Contain("not running");
    }

    [Fact]
    public void A_different_server_is_refused_because_cross_server_networking_is_not_built()
    {
        var primary = Primary();
        ReadReplicaPlan.WhyRefused(primary, Guid.NewGuid(), primary.Version)
            .Should().Contain("same server");
    }

    [Fact]
    public void A_mismatched_version_is_refused()
    {
        var primary = Primary(version: "16-alpine");
        ReadReplicaPlan.WhyRefused(primary, primary.ServerId, "15-alpine")
            .Should().Contain("exact same PostgreSQL version");
    }

    [Fact]
    public void A_valid_replica_of_a_running_primary_on_the_same_server_and_version_is_allowed()
    {
        var primary = Primary();
        ReadReplicaPlan.WhyRefused(primary, primary.ServerId, primary.Version).Should().BeNull();
    }
}

public class ReadReplicaSeedPlanTests
{
    [Fact]
    public void The_seed_command_writes_recovery_config_automatically_via_dash_R()
    {
        var command = ReadReplicaSeedPlan.SeedCommand("harbora-svc-orders", 5432, "harbora", "/var/lib/postgresql/data");

        command.Should().Contain("pg_basebackup")
            .And.Contain("-R", "the -R flag is what writes standby.signal + primary_conninfo — the whole point")
            .And.Contain("harbora-svc-orders")
            .And.Contain("harbora");
        string.Join(' ', command).Should().Contain("-D /var/lib/postgresql/data");
    }

    [Fact]
    public void The_password_never_appears_in_argv()
    {
        var command = ReadReplicaSeedPlan.SeedCommand("host", 5432, "harbora", "/data");
        command.Should().NotContain("s3cret-password");

        var env = ReadReplicaSeedPlan.Environment("s3cret-password");
        env["PGPASSWORD"].Should().Be("s3cret-password");
    }
}

public class ReplicaPromotionPlanTests
{
    [Fact]
    public void The_promote_command_calls_pg_promote()
    {
        var command = ReplicaPromotionPlan.Command("harbora-svc-standby", 5432, "harbora", "orders");
        string.Join(' ', command).Should().Contain("pg_promote()");
    }
}

public class ReplicationLagQueryTests
{
    [Fact]
    public void The_command_queries_the_replicas_own_replay_timestamp()
    {
        var command = ReplicationLagQuery.Command("harbora-svc-standby", 5432, "harbora", "orders");
        string.Join(' ', command).Should().Contain("pg_last_xact_replay_timestamp()");
    }

    [Fact]
    public void A_real_postgres_timestamp_parses()
    {
        var parsed = ReplicationLagQuery.ParseReplayTimestamp("2026-09-04 10:15:23.123456+00\n");
        parsed.Should().NotBeNull();
        parsed!.Value.Year.Should().Be(2026);
    }

    [Fact]
    public void An_empty_answer_is_null_never_a_guessed_moment()
    {
        // psql -t -A prints a blank line for a real SQL NULL — pg_last_xact_replay_timestamp() before
        // the standby has replayed its first commit-timestamped transaction.
        ReplicationLagQuery.ParseReplayTimestamp("\n").Should().BeNull();
        ReplicationLagQuery.ParseReplayTimestamp("").Should().BeNull();
    }

    [Fact]
    public void Unparseable_output_is_null_never_guessed_at()
    {
        ReplicationLagQuery.ParseReplayTimestamp("ERROR: connection refused").Should().BeNull();
    }
}

public class ReplicationLagPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_row_at_all_is_never_measured_never_zero()
    {
        var view = ReplicationLagPresenter.Compute(null, Now);
        view.Status.Should().Be(ReplicaLagStatus.NeverMeasured);
        view.Lag.Should().BeNull();
    }

    [Fact]
    public void A_row_with_no_attempt_yet_is_never_measured()
    {
        var status = new ReplicationLagStatus();
        ReplicationLagPresenter.Compute(status, Now).Status.Should().Be(ReplicaLagStatus.NeverMeasured);
    }

    [Fact]
    public void A_row_that_has_never_succeeded_is_unknown_never_zero()
    {
        var status = new ReplicationLagStatus
        {
            LastAttemptAt = Now, ConsecutiveFailures = 3, LastError = "connection refused"
        };
        var view = ReplicationLagPresenter.Compute(status, Now);
        view.Status.Should().Be(ReplicaLagStatus.Unknown);
        view.Lag.Should().BeNull();
        view.Message.Should().Contain("connection refused");
    }

    [Fact]
    public void A_failing_current_attempt_is_unknown_even_though_an_old_success_exists()
    {
        var status = new ReplicationLagStatus
        {
            LastAttemptAt = Now, LastSuccessAt = Now.AddMinutes(-2), LagSeconds = 1.5,
            ConsecutiveFailures = 2, LastError = "timeout"
        };
        var view = ReplicationLagPresenter.Compute(status, Now);
        view.Status.Should().Be(ReplicaLagStatus.Unknown,
            "the current run is failing — an old success must not be presented as a live figure");
        view.Lag.Should().BeNull();
    }

    [Fact]
    public void A_stale_success_reads_as_unknown_rather_than_a_live_figure()
    {
        var status = new ReplicationLagStatus
        {
            LastAttemptAt = Now, LastSuccessAt = Now - ReplicationLagPresenter.StaleAfter - TimeSpan.FromMinutes(1),
            LagSeconds = 0.5, ConsecutiveFailures = 0
        };
        var view = ReplicationLagPresenter.Compute(status, Now);
        view.Status.Should().Be(ReplicaLagStatus.Unknown,
            "a reading old enough to have gone stale must not be shown as if it were current");
        view.Lag.Should().BeNull();
        view.MeasuredAt.Should().Be(status.LastSuccessAt, "the last-known-good moment is still worth showing");
    }

    [Fact]
    public void A_successful_query_with_no_replayed_transaction_yet_is_unknown_not_zero()
    {
        var status = new ReplicationLagStatus
        {
            LastAttemptAt = Now, LastSuccessAt = Now, LagSeconds = null, ConsecutiveFailures = 0
        };
        var view = ReplicationLagPresenter.Compute(status, Now);
        view.Status.Should().Be(ReplicaLagStatus.Unknown,
            "pg_last_xact_replay_timestamp() answered NULL — the query worked, PostgreSQL just has nothing to say yet");
        view.Lag.Should().BeNull();
    }

    [Fact]
    public void A_fresh_successful_measurement_is_known_and_carries_the_real_figure()
    {
        var status = new ReplicationLagStatus
        {
            LastAttemptAt = Now, LastSuccessAt = Now.AddSeconds(-30), LagSeconds = 2.5, ConsecutiveFailures = 0
        };
        var view = ReplicationLagPresenter.Compute(status, Now);
        view.Status.Should().Be(ReplicaLagStatus.Known);
        view.Lag.Should().Be(TimeSpan.FromSeconds(2.5));
        view.Message.Should().Contain("behind its primary");
    }

    [Fact]
    public void A_negative_lag_from_clock_skew_is_clamped_to_zero_never_negative()
    {
        var status = new ReplicationLagStatus
        {
            LastAttemptAt = Now, LastSuccessAt = Now, LagSeconds = -0.2, ConsecutiveFailures = 0
        };
        var view = ReplicationLagPresenter.Compute(status, Now);
        view.Status.Should().Be(ReplicaLagStatus.Known);
        view.Lag.Should().Be(TimeSpan.Zero);
    }
}
