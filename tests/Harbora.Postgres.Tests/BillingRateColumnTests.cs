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
/// the nulls come from. <c>BillingRates</c> adds the seven rate columns as
/// <c>NOT NULL DEFAULT 0</c>, because that is the only way to add a required column to a table that
/// already has rows; <c>BillingRatesNullable</c> then drops the constraint and has to undo the
/// zeros. Nothing in the model, the snapshot or the InMemory provider can see either statement, so
/// without this class the whole distinction rests on two lines of SQL that no test has run.
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

        // Both of them, not a sample: the UPDATE names each column separately, so a forgotten line
        // leaves exactly one of them reading as a deliberate zero for ever.
        plan.BaseRatePerHourMinor.Should().BeNull("nobody has priced this plan's floor");
        plan.DiskGbHourMinor.Should().BeNull("nobody has priced a gibibyte-hour of volume");

        var size = await db.InstanceSizes.AsNoTracking()
            .SingleAsync(s => s.Id == UpgradeFromPreviousRelease.Seeded.SizeCarriedAcross);

        size.RunningRatePerHourMinor.Should().BeNull("nobody has priced an hour of this tier running");
        size.StoppedRatePerHourMinor.Should().BeNull("nobody has priced an hour of it stopped");
    }

    /// <summary>
    /// The four, spelled as the migrations spell them. A fact rather than a theory with four cases
    /// on purpose: this assembly gates on Docker through <see cref="PostgresFactAttribute"/> and
    /// has no theory equivalent, so an <c>[InlineData]</c> row here would run — and fail — on every
    /// machine without a daemon, which is precisely the red-on-a-laptop that attribute exists to
    /// prevent.
    ///
    /// <para>
    /// Four rather than the seven <c>BillingRates</c> created. <c>BillingOverageRatesRemoved</c>
    /// drops <c>OverageCpuCoreHourMinor</c> and its two neighbours, which nothing ever read: the
    /// excess past a cap is charged at the ordinary meter, so those columns were a surcharge that
    /// looked settable and collected nothing.
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
        // The half of the migration that is easy to miss. `DROP NOT NULL` does not drop a column
        // default, so the `DEFAULT 0` the previous migration created would otherwise survive into
        // the head schema. EF names every mapped column on insert, so its own writes are unaffected
        // — but a restore, a repair by hand or a bulk load that omits the column would get a priced
        // row instead of an unpriced one, and the model records no default so EF will never notice
        // the disagreement.
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
    /// They were added by <c>BillingRates</c>, made nullable by <c>BillingRatesNullable</c> and
    /// dropped by <c>BillingOverageRatesRemoved</c>, all inside one unmerged branch. A model
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
