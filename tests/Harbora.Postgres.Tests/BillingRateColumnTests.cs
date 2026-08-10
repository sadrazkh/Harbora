using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// That an upgraded install arrives with its prices <b>unset</b> rather than free.
///
/// <para>
/// The fast suite proves the C# distinction: <c>BillingRates</c> hands back null for a rate nobody
/// has answered and zero for a rate somebody answered with a zero. What it cannot reach is where
/// the nulls come from, because that is a property of the DDL and nothing in the model, the
/// snapshot or the InMemory provider can see DDL. <c>PayAsYouGoBilling</c> adds the four rate
/// columns <c>bigint</c> and nullable with no default, so a plan that predates billing crosses the
/// upgrade holding nothing.
/// </para>
///
/// <para>
/// It used to rest on two hand-written statements. The migration that first added these columns had
/// to add them <c>NOT NULL DEFAULT 0</c> — the only way to add a required column to a table that
/// already has rows — and the next one dropped the constraint, dropped the default and rewrote
/// every zero back to null. Squashing billing's seven migrations into one deleted that round trip
/// rather than undoing it. The assertions below did not change, because what they claim about the
/// rows never depended on how the rows came to be that way, and they are what would notice if a
/// later migration made one of these columns required again.
/// </para>
///
/// <para>
/// The failure it guards is quiet in the way this project's worst ones are: every plan and every
/// built-in tier priced at zero, every hourly tick reporting success, and nobody billed.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class BillingRateColumnTests(PostgresLane lane)
{
    [PostgresFact]
    public async Task An_install_carried_across_arrives_with_no_price_rather_than_a_price_of_zero()
    {
        var upgraded = await lane.UpgradedAsync();
        await using var db = PostgresLane.Open(upgraded.ConnectionString);

        var plan = await db.Plans.AsNoTracking()
            .SingleAsync(p => p.Id == UpgradeFromPreviousRelease.Seeded.PlanCarriedAcross);

        // Both of them, not a sample: each column is added by a statement of its own, so one
        // property declared required leaves exactly that column reading as a deliberate zero for
        // ever while its neighbour goes on being right.
        plan.BaseRatePerHourMinor.Should().BeNull("nobody has priced this plan's floor");
        plan.DiskGbHourMinor.Should().BeNull("nobody has priced a gibibyte-hour of volume");

        var size = await db.InstanceSizes.AsNoTracking()
            .SingleAsync(s => s.Id == UpgradeFromPreviousRelease.Seeded.SizeCarriedAcross);

        size.RunningRatePerHourMinor.Should().BeNull("nobody has priced an hour of this tier running");
        size.StoppedRatePerHourMinor.Should().BeNull("nobody has priced an hour of it stopped");
    }

    /// <summary>
    /// The four, spelled as the migrations spell them. A fact rather than a theory with four cases,
    /// and the reason has changed: <see cref="PostgresTheoryAttribute"/> now exists, so a theory here
    /// would be gated properly rather than going red on every machine without a daemon. It stays a
    /// fact because these four are one claim — an upgraded install carries no prices — asked of one
    /// shared read-only schema. Four theory cases would build nothing extra and report one mistake
    /// as four.
    ///
    /// <para>
    /// Four, where an earlier draft of this branch created seven. <c>OverageCpuCoreHourMinor</c> and
    /// its two neighbours were added and dropped again inside the branch, and the squash means no
    /// migration creates them at all. Nothing ever read them: the excess past a cap is charged at
    /// the ordinary meter, so they were a surcharge that looked settable and collected nothing.
    /// <see cref="An_overage_surcharge_column_is_gone_rather_than_left_unread"/> is the other side
    /// of this list — it fails if one of them comes back.
    /// </para>
    /// </summary>
    private static readonly (string Table, string Column)[] RateColumns =
    [
        ("Plans", "BaseRatePerHourMinor"),
        ("Plans", "DiskGbHourMinor"),
        ("InstanceSizes", "RunningRatePerHourMinor"),
        ("InstanceSizes", "StoppedRatePerHourMinor"),
    ];

    [PostgresFact]
    public async Task A_rate_column_keeps_no_default_so_a_row_written_without_one_is_unpriced()
    {
        // Once the half of the migration that was easy to miss: `DROP NOT NULL` does not drop a
        // column default, so a `DEFAULT 0` survived into the head schema until a hand-written
        // `ALTER … DROP DEFAULT` took it away. Generated in one pass the column never has one, and
        // this stays a fact rather than a footnote for the reason that made the omission possible in
        // the first place — the model records no default either way, so EF cannot see the
        // difference and will never report it. EF names every mapped column on insert, so its own
        // writes are unaffected; a restore, a repair by hand or a bulk load that omits the column is
        // what must get an unpriced row rather than a priced one.
        var connectionString = await lane.HeadSchemaAsync();

        foreach (var (table, column) in RateColumns)
        {
            var found = await PostgresLane.ScalarAsync<string>(connectionString,
                $"""
                 SELECT COALESCE(column_default, 'no default') FROM information_schema.columns
                 WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'
                 """);

            // Null here means the column is not in the catalogue at all, which the COALESCE is
            // there to tell apart from a column that is present and carries no default.
            found.Should().NotBeNull($"the migrations create \"{table}\".\"{column}\"");
            found.Should().Be("no default", $"\"{table}\".\"{column}\" must not fill itself in");
        }
    }

    /// <summary>
    /// That the three overage surcharge columns really left the database, and not only the model.
    ///
    /// <para>
    /// They were added, made nullable and dropped again inside one unmerged branch, and squashing
    /// that branch's seven migrations into one means nothing creates them any more. A model
    /// property deleted without the matching <c>DropColumn</c> leaves a column behind that nothing
    /// names — invisible to EF, invisible to <c>MigrationConsistencyTests</c> once the snapshot
    /// agrees with the model, and still sitting there as a priced-looking figure for the next
    /// person who reads the schema by hand.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task An_overage_surcharge_column_is_gone_rather_than_left_unread()
    {
        var connectionString = await lane.HeadSchemaAsync();

        foreach (var column in new[]
                 {
                     "OverageCpuCoreHourMinor", "OverageMemoryGbHourMinor", "OverageDiskGbHourMinor"
                 })
        {
            var found = await PostgresLane.ScalarAsync<string>(connectionString,
                $"""
                 SELECT column_name FROM information_schema.columns
                 WHERE table_schema = 'public' AND table_name = 'Plans' AND column_name = '{column}'
                 """);

            found.Should().BeNull(
                $"\"Plans\".\"{column}\" is a surcharge nothing collects; bringing it back means "
                + "wiring the tick first, not adding a box to the plan form");
        }
    }
}
