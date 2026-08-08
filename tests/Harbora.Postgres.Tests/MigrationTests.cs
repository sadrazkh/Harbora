using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// That the migrations apply at all, and that what they leave behind is what they say.
///
/// <para>
/// <c>MigrationConsistencyTests</c> in the fast suite already proves the model and the snapshot
/// agree. What it cannot prove is that the statements run: it never opens a connection. Sixty-odd
/// migrations had never been executed against a PostgreSQL by anything in this repository, and the
/// four newest carry hand-written SQL.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class MigrationTests(PostgresLane lane)
{
    [PostgresFact]
    public async Task Every_migration_applies_to_an_empty_database()
    {
        var connectionString = await lane.HeadSchemaAsync();
        await using var db = PostgresLane.Open(connectionString);

        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await db.Database.GetAppliedMigrationsAsync()).Should().BeEquivalentTo(db.Database.GetMigrations());
    }

    [PostgresFact]
    public async Task The_upgrade_under_test_is_the_four_migrations_this_branch_added()
    {
        // A tripwire on the constant the upgrade lane migrates to. Renaming a migration, or adding
        // one before the boundary, would otherwise quietly turn "upgraded from the previous release"
        // into something else, and every assertion built on it would go on passing.
        await using var db = PostgresLane.Open(await lane.HeadSchemaAsync());

        var all = db.Database.GetMigrations().ToList();
        var boundary = all.IndexOf(UpgradeFromPreviousRelease.PreviousRelease);

        boundary.Should().BeGreaterThanOrEqualTo(0,
            $"{UpgradeFromPreviousRelease.PreviousRelease} is the last migration on master and the schema " +
            "this branch upgrades from");

        all.Skip(boundary + 1).Take(UpgradeFromPreviousRelease.Applied.Length)
            .Should().Equal(UpgradeFromPreviousRelease.Applied);
    }

    [PostgresFact]
    public async Task An_install_at_the_previous_release_can_be_carried_across()
    {
        // Building the upgrade IS the assertion: UpgradeFromPreviousRelease seeds the rows a real
        // install could be carrying and then migrates. A hand-written statement that throws — or an
        // index build that meets a row the statement above it should have settled — fails here.
        var upgraded = await lane.UpgradedAsync();

        await using var db = PostgresLane.Open(upgraded.ConnectionString);
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task The_two_columns_the_queue_grew_are_nullable_and_empty_on_a_fresh_install()
    {
        var connectionString = await lane.HeadSchemaAsync();

        (await ColumnAsync(connectionString, "Jobs", "NextAttemptAt"))
            .Should().Be(new ColumnShape("timestamp with time zone", Nullable: true));

        (await ColumnAsync(connectionString, "Jobs", "ExclusiveWith"))
            .Should().Be(new ColumnShape("uuid", Nullable: true));

        (await ColumnAsync(connectionString, "BackupSnapshots", "StagingPath"))
            .Should().Be(new ColumnShape("character varying", Nullable: true));
    }

    [PostgresFact]
    public async Task The_active_backup_index_is_unique_over_the_target_and_filtered_to_the_live_states()
    {
        var definition = await IndexDefinitionAsync(
            await lane.HeadSchemaAsync(), "IX_BackupSnapshots_ActiveTarget");

        definition.Should().StartWith("CREATE UNIQUE INDEX");
        definition.Should().Contain("""("WorkspaceId", "TargetType", "TargetRef")""");
        // Postgres rewrites IN (0, 1, 2) as = ANY (ARRAY[...]); what matters is that a filter
        // survived at all, because without one a target could be backed up exactly once, ever.
        definition.Should().Contain("WHERE").And.Contain("Status");
    }

    [PostgresFact]
    public async Task The_active_restore_index_is_unique_over_the_destination_alone()
    {
        var definition = await IndexDefinitionAsync(
            await lane.HeadSchemaAsync(), "IX_RestoreJobs_ActiveDestination");

        definition.Should().StartWith("CREATE UNIQUE INDEX");
        definition.Should().Contain("""("Destination")""");
        definition.Should().NotContain("WorkspaceId",
            "a destination names one thing on the machine, so two tenants racing for it is the case " +
            "a workspace-scoped index would wave through");
        definition.Should().Contain("WHERE").And.Contain("Status");
    }

    [PostgresFact]
    public async Task The_chart_index_puts_the_ranged_column_last()
    {
        var definition = await IndexDefinitionAsync(
            await lane.HeadSchemaAsync(), "IX_MetricRollups_ServerId_Name_ResourceRef_Period_PeriodStart");

        definition.Should().NotStartWith("CREATE UNIQUE",
            "several rollups share every one of these columns");
        definition.Should().Contain("""("ServerId", "Name", "ResourceRef", "Period", "PeriodStart")""");
    }

    [PostgresFact]
    public async Task The_chart_query_is_answered_from_the_index_without_a_sort()
    {
        // What this settles, and what it does not. It proves the index is applicable to the exact
        // query MonitoringController runs and that the ORDER BY costs nothing extra — so an index
        // that stopped covering one of the five columns, or lost PeriodStart, shows up here as a
        // Sort node. It does not prove the order is the *cheapest* one; that is a cost question, and
        // the textual pin above is what holds the order itself.
        var connectionString = await lane.FreshlyMigratedAsync("chart");
        var server = Guid.NewGuid();

        await using var connection = await PostgresLane.ConnectAsync(connectionString);

        await using (var fill = new NpgsqlCommand(
            """
            INSERT INTO "MetricRollups"
                ("Id", "ServerId", "Name", "ResourceRef", "Period", "PeriodStart",
                 "Average", "Minimum", "Maximum", "SampleCount", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), @server, n.name, r.ref, p.period,
                   now() - (g || ' minutes')::interval,
                   0, 0, 0, 0, now(), now()
            FROM generate_series(1, 400) AS g,
                 (VALUES ('cpu'), ('memory'), ('disk')) AS n(name),
                 (VALUES ('app-a'), ('app-b')) AS r(ref),
                 (VALUES (0), (1)) AS p(period)
            """, connection))
        {
            fill.Parameters.AddWithValue("server", server);
            await fill.ExecuteNonQueryAsync();
        }

        await using (var analyse = new NpgsqlCommand("ANALYZE \"MetricRollups\"", connection))
            await analyse.ExecuteNonQueryAsync();

        // Both are cost penalties rather than prohibitions, and the table is filled first so the
        // planner would reach for the index on its own merits anyway. They are here so the plan this
        // asserts on is the same plan on every runner, whatever its cost settings happen to be.
        await using (var off = new NpgsqlCommand(
            "SET enable_seqscan = off; SET enable_bitmapscan = off", connection))
            await off.ExecuteNonQueryAsync();

        var plan = await ExplainAsync(connection,
            $"""
             SELECT "PeriodStart", "Average", "Minimum", "Maximum" FROM "MetricRollups"
             WHERE "ServerId" = '{server}' AND "Name" = 'cpu' AND "ResourceRef" = 'app-a'
               AND "Period" = 0 AND "PeriodStart" >= now() - interval '2 hours'
             ORDER BY "PeriodStart"
             """);

        plan.Should().Contain("IX_MetricRollups_ServerId_Name_ResourceRef_Period_PeriodStart");
        plan.Should().NotContain("Sort", "PeriodStart is the last column, so the index already returns the order");
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, string query)
    {
        await using var command = new NpgsqlCommand("EXPLAIN " + query, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var lines = new List<string>();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join("\n", lines);
    }

    private static async Task<string> IndexDefinitionAsync(string connectionString, string index)
    {
        var definition = await PostgresLane.ScalarAsync<string>(connectionString,
            $"SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = '{index}'");

        definition.Should().NotBeNull($"the migration creates an index called {index}");
        return definition!;
    }

    private static async Task<ColumnShape> ColumnAsync(string connectionString, string table, string column)
    {
        await using var connection = await PostgresLane.ConnectAsync(connectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT data_type, is_nullable FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = @column
            """, connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"\"{table}\".\"{column}\" should exist");

        return new ColumnShape(reader.GetString(0), reader.GetString(1) == "YES");
    }

    private sealed record ColumnShape(string DataType, bool Nullable);
}
