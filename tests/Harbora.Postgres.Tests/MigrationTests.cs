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
/// migrations had never been executed against a PostgreSQL by anything in this repository, and
/// three of the newest carry hand-written SQL.
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
    public async Task The_upgrade_starts_where_master_ends_and_the_next_four_are_unchanged()
    {
        // A tripwire on the constant the upgrade lane migrates to. Renaming a migration, or adding
        // one before the boundary, would otherwise quietly turn "upgraded from the previous release"
        // into something else, and every assertion built on it would go on passing.
        //
        // Four rather than all eleven these branches now carry, for the reason on Applied: this
        // pins the boundary, and An_install_at_the_previous_release_can_be_carried_across covers
        // everything past it by running it.
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

        // The length is part of the column, not decoration. StagingPath holds a filesystem path the
        // node writes into, and a migration that declared it character varying with no bound — or
        // with a smaller one — would still be "character varying" here while quietly changing what
        // the panel can store.
        (await ColumnAsync(connectionString, "BackupSnapshots", "StagingPath"))
            .Should().Be(new ColumnShape("character varying", Nullable: true, MaxLength: 1024));
    }

    /// <summary>
    /// Why an index with no filter at all would be wrong here, said once for the two backup indexes
    /// below: without the <c>WHERE</c>, finished rows are covered too and a target could be backed
    /// up — or a directory restored into — exactly once, ever.
    /// </summary>
    private const string AnUnfilteredIndexWouldCoverFinishedRows =
        "an index with no filter at all covers finished rows too, and a target would be backed " +
        "up exactly once, ever";

    [PostgresFact]
    public async Task The_active_backup_index_is_unique_over_the_target_and_filtered_to_the_live_states()
    {
        var definition = await IndexCatalogue.DefinitionAsync(
            await lane.HeadSchemaAsync(), "IX_BackupSnapshots_ActiveTarget");

        definition.Should().StartWith("CREATE UNIQUE INDEX");
        definition.Should().Contain("""("WorkspaceId", "TargetType", "TargetRef")""");

        // Pending, Preparing, Running — the three live states, spelled as the migration spells them
        // because a migration that has shipped goes on meaning the numbers it was written with.
        IndexCatalogue.FilteredValues(definition, AnUnfilteredIndexWouldCoverFinishedRows)
            .Should().BeEquivalentTo(new[] { 0, 1, 2 },
                "a filter over any other set covers the wrong rows: too few and a second live backup of " +
                "one target walks straight past the index, too many and a target can be backed up once " +
                "and never again. Postgres printed the index as {0}", definition);
    }

    [PostgresFact]
    public async Task The_active_restore_index_is_unique_over_the_destination_alone()
    {
        var definition = await IndexCatalogue.DefinitionAsync(
            await lane.HeadSchemaAsync(), "IX_RestoreJobs_ActiveDestination");

        definition.Should().StartWith("CREATE UNIQUE INDEX");
        definition.Should().Contain("""("Destination")""");
        definition.Should().NotContain("WorkspaceId",
            "a destination names one thing on the machine, so two tenants racing for it is the case " +
            "a workspace-scoped index would wave through");

        // Pending and Running. A restore has no Preparing, so this list is shorter than the backups'
        // by one — and Completed is out, or a directory could be restored into exactly once, ever.
        IndexCatalogue.FilteredValues(definition, AnUnfilteredIndexWouldCoverFinishedRows)
            .Should().BeEquivalentTo(new[] { 0, 1 },
                "the index has to cover every state a restore can still be holding the directory in, and " +
                "no state in which it has let go of it. Postgres printed the index as {0}", definition);
    }

    [PostgresFact]
    public async Task The_chart_index_puts_the_ranged_column_last()
    {
        var definition = await IndexCatalogue.DefinitionAsync(
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

    private static async Task<ColumnShape> ColumnAsync(string connectionString, string table, string column)
    {
        await using var connection = await PostgresLane.ConnectAsync(connectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT data_type, is_nullable, character_maximum_length FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = @column
            """, connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"\"{table}\".\"{column}\" should exist");

        return new ColumnShape(
            reader.GetString(0),
            reader.GetString(1) == "YES",
            await reader.IsDBNullAsync(2) ? null : reader.GetInt32(2));
    }

    /// <summary>
    /// <paramref name="MaxLength"/> is null for every type that has no length — a uuid, a timestamp —
    /// which is what <c>information_schema</c> reports for them, so the default says "not that kind
    /// of column" rather than "unchecked".
    /// </summary>
    private sealed record ColumnShape(string DataType, bool Nullable, int? MaxLength = null);
}
