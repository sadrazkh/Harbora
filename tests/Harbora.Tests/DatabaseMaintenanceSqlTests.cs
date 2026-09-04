using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The VACUUM/VACUUM FULL/ANALYZE/REINDEX/OPTIMIZE TABLE statements (2.3, round-2 market-gaps plan) —
/// pure logic, no engine involved. <see cref="DatabaseGrantExecutorHostTests"/> and
/// <see cref="DatabaseMaintenanceServiceTests"/> prove what actually reaches the fake engine; this
/// proves the statements themselves are built correctly and that every engine without a maintenance
/// story is refused by name rather than handed a statement it cannot honour.
/// </summary>
public class DatabaseMaintenanceSqlTests
{
    [Theory]
    [InlineData(ManagedServiceType.PostgreSql, true)]
    [InlineData(ManagedServiceType.MySql, true)]
    [InlineData(ManagedServiceType.MariaDb, true)]
    [InlineData(ManagedServiceType.Redis, false)]
    [InlineData(ManagedServiceType.MongoDb, false)]
    [InlineData(ManagedServiceType.RabbitMq, false)]
    [InlineData(ManagedServiceType.Nats, false)]
    [InlineData(ManagedServiceType.Meilisearch, false)]
    public void Only_the_three_relational_engines_have_a_maintenance_story(ManagedServiceType type, bool expected) =>
        DatabaseMaintenanceSql.Supports(type).Should().Be(expected);

    [Fact]
    public void An_engine_with_no_maintenance_story_is_refused_by_name()
    {
        var reason = DatabaseMaintenanceSql.UnsupportedReason(ManagedServiceType.Redis);
        reason.Should().Contain("Redis");
    }

    [Theory]
    [InlineData(DatabaseMaintenanceOperation.Vacuum)]
    [InlineData(DatabaseMaintenanceOperation.VacuumFull)]
    [InlineData(DatabaseMaintenanceOperation.Analyze)]
    [InlineData(DatabaseMaintenanceOperation.Reindex)]
    public void PostgreSql_offers_vacuum_analyze_and_reindex_but_not_optimize(DatabaseMaintenanceOperation op) =>
        DatabaseMaintenanceSql.SupportsOperation(ManagedServiceType.PostgreSql, op).Should().BeTrue();

    [Fact]
    public void PostgreSql_does_not_offer_optimize_table() =>
        DatabaseMaintenanceSql.SupportsOperation(ManagedServiceType.PostgreSql, DatabaseMaintenanceOperation.Optimize)
            .Should().BeFalse();

    [Theory]
    [InlineData(ManagedServiceType.MySql)]
    [InlineData(ManagedServiceType.MariaDb)]
    public void MySql_and_MariaDb_offer_only_optimize_table(ManagedServiceType type)
    {
        DatabaseMaintenanceSql.SupportsOperation(type, DatabaseMaintenanceOperation.Optimize).Should().BeTrue();
        DatabaseMaintenanceSql.SupportsOperation(type, DatabaseMaintenanceOperation.Vacuum).Should().BeFalse();
        DatabaseMaintenanceSql.SupportsOperation(type, DatabaseMaintenanceOperation.VacuumFull).Should().BeFalse();
        DatabaseMaintenanceSql.SupportsOperation(type, DatabaseMaintenanceOperation.Analyze).Should().BeFalse();
        DatabaseMaintenanceSql.SupportsOperation(type, DatabaseMaintenanceOperation.Reindex).Should().BeFalse();
    }

    [Fact]
    public void An_operation_this_engine_does_not_offer_is_refused_by_name()
    {
        var reason = DatabaseMaintenanceSql.UnsupportedOperationReason(
            ManagedServiceType.MySql, DatabaseMaintenanceOperation.VacuumFull);
        reason.Should().Contain("VACUUM FULL").And.Contain("MySql");
    }

    // -------------------------------------------------------------------------------------------
    // The variants are described honestly — the requirement's own words.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Plain_vacuum_is_described_as_online()
    {
        DatabaseMaintenanceSql.IsOnline(DatabaseMaintenanceOperation.Vacuum).Should().BeTrue();
        DatabaseMaintenanceSql.Describe(DatabaseMaintenanceOperation.Vacuum).Should().Contain("Online");
    }

    [Fact]
    public void Vacuum_full_is_described_as_locking_and_needing_disk_headroom()
    {
        DatabaseMaintenanceSql.IsOnline(DatabaseMaintenanceOperation.VacuumFull).Should().BeFalse();
        var description = DatabaseMaintenanceSql.Describe(DatabaseMaintenanceOperation.VacuumFull);
        description.Should().Contain("ACCESS EXCLUSIVE");
        description.Should().Contain("free disk space");
    }

    [Fact]
    public void Optimize_table_is_described_as_locking_the_table()
    {
        DatabaseMaintenanceSql.IsOnline(DatabaseMaintenanceOperation.Optimize).Should().BeFalse();
        DatabaseMaintenanceSql.Describe(DatabaseMaintenanceOperation.Optimize).Should().Contain("locked");
    }

    // -------------------------------------------------------------------------------------------
    // The statements themselves.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Vacuum_full_on_postgres_issues_exactly_that_statement()
    {
        var command = DatabaseMaintenanceSql.Build(
            ManagedServiceType.PostgreSql, DatabaseMaintenanceOperation.VacuumFull,
            "harbora-svc-shop", 5432, "harbora", "orders");

        command.Should().NotBeNull();
        command!.Command.Should().Contain("VACUUM FULL;");
        command.Command.Should().Contain("-d");
        command.Command.Should().Contain("orders");
    }

    [Fact]
    public void Plain_vacuum_on_postgres_issues_exactly_that_statement()
    {
        var command = DatabaseMaintenanceSql.Build(
            ManagedServiceType.PostgreSql, DatabaseMaintenanceOperation.Vacuum,
            "harbora-svc-shop", 5432, "harbora", "orders");

        command!.Command.Should().Contain("VACUUM;");
        command.Command.Should().NotContain("VACUUM FULL;");
    }

    [Fact]
    public void Reindex_on_postgres_names_the_database_in_the_statement()
    {
        var command = DatabaseMaintenanceSql.Build(
            ManagedServiceType.PostgreSql, DatabaseMaintenanceOperation.Reindex,
            "harbora-svc-shop", 5432, "harbora", "orders");

        command!.Command.Should().Contain("REINDEX DATABASE \"orders\";");
    }

    [Fact]
    public void Optimize_on_mariadb_runs_mariadb_check_against_the_named_database()
    {
        var command = DatabaseMaintenanceSql.Build(
            ManagedServiceType.MariaDb, DatabaseMaintenanceOperation.Optimize,
            "harbora-svc-shop", 3306, "harbora", "orders");

        command.Should().NotBeNull();
        command!.Command.Should().Contain("mariadb-check");
        command.Command.Should().Contain("--optimize");
        command.Command.Should().Contain("orders");
    }

    [Fact]
    public void A_database_name_that_fails_the_safety_check_builds_no_command()
    {
        var command = DatabaseMaintenanceSql.Build(
            ManagedServiceType.PostgreSql, DatabaseMaintenanceOperation.Vacuum,
            "harbora-svc-shop", 5432, "harbora", "orders'; DROP TABLE users; --");

        command.Should().BeNull();
    }

    [Fact]
    public void An_engine_with_no_maintenance_story_builds_no_command()
    {
        var command = DatabaseMaintenanceSql.Build(
            ManagedServiceType.Redis, DatabaseMaintenanceOperation.Vacuum,
            "harbora-svc-cache", 6379, "harbora", "0");

        command.Should().BeNull();
    }
}
