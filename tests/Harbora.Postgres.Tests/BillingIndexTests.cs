using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The index that makes a retried hourly tick harmless, refusing what it exists to refuse — and
/// allowing what it must not refuse.
///
/// <para>
/// <c>BillingTick</c> reads the hour's existing lines before it writes, and that read is the ordinary
/// answer to a queue message delivered twice. It is not the answer to two passes running at once, and
/// it is not the answer at all under EF InMemory, which has no unique index: every fast-suite test of
/// the retry passes against a provider that would have accepted both rows. Until this class the guard
/// on somebody being charged twice for one hour was an assumption written in a model builder.
/// </para>
///
/// <para>
/// <b>The half that has never been anywhere near a database is <c>AreNullsDistinct(false)</c>.</b>
/// The plan-minimum line carries a null <c>ResourceId</c> — there is no resource behind it — and
/// Postgres's default treats two nulls as distinct, so a retried tick would write that line twice
/// straight through a unique index. Nothing in the model, the snapshot or InMemory can see the
/// difference; <c>MigrationConsistencyTests</c> compares the model to the snapshot and never opens a
/// connection. <see cref="Two_plan_minimum_lines_for_the_same_hour_cannot_both_exist"/> is that
/// setting being asked of the thing that implements it.
/// </para>
///
/// <para>
/// Half the facts here are the other direction, and they are not padding. An index that refuses too
/// much is the worse failure of the two: over-refusing on the hour would charge an app once and never
/// again, over-refusing on the workspace would bill the first tenant of the pass and fail for every
/// other one, and over-refusing across resource types would make a database's disk unbillable
/// whenever the database itself had been billed. All three would look like a working guard.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class BillingIndexTests(PostgresLane lane)
{
    /// <summary>
    /// The idempotency index, spelled as Postgres holds it.
    ///
    /// <para>
    /// Truncated, and not by hand. The name EF derives from the four columns is 64 characters and an
    /// identifier may be 63, so Npgsql cuts it and marks the cut with a tilde — which is why this is
    /// a constant with a comment rather than something a reader is expected to reconstruct. A test
    /// asserting on the untruncated name finds no index, and the two failures it produces are a
    /// null definition here and a constraint name that never matches over in the refusals.
    /// </para>
    /// </summary>
    private const string IdempotencyIndex = "IX_BillingLedger_WorkspaceId_ResourceType_ResourceId_BillingHo~";

    private static readonly Guid WorkspaceOne = new("31111111-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceTwo = new("31111111-0000-0000-0000-000000000002");

    private static readonly Guid AppOne = new("3a000000-0000-0000-0000-000000000001");
    private static readonly Guid DatabaseOne = new("35000000-0000-0000-0000-000000000001");

    /// <summary>An hour that has ended. The value is arbitrary; that every row shares it is not.</summary>
    private static readonly DateTimeOffset Hour = new(2026, 8, 9, 14, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------------------------
    // What it refuses
    // -------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Two_charges_for_the_same_resource_and_hour_cannot_both_exist()
    {
        // Only a real database has a unique index. InMemory accepts both rows, so every fast-suite
        // fact about the retry would pass while production double-charges.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_idempotency"));

        db.BillingLedger.Add(Charge(WorkspaceOne, BilledResourceType.App, AppOne));
        await db.SaveChangesAsync();

        db.BillingLedger.Add(Charge(WorkspaceOne, BilledResourceType.App, AppOne));

        (await Refusal(db)).ConstraintName.Should().Be(IdempotencyIndex);
    }

    [PostgresFact]
    public async Task Two_plan_minimum_lines_for_the_same_hour_cannot_both_exist()
    {
        // The line with no resource behind it, and the whole reason the index is built NULLS NOT
        // DISTINCT. Under Postgres's default this pair is two distinct keys and both rows land: the
        // customer pays their plan's floor twice for one hour, the wallet moves twice, and every
        // other fact in this file goes on passing because none of the others has a null in the key.
        //
        // It is also the one line a Charge-only filter would have let through, which is why the
        // filter names both kinds the tick writes rather than just the charges.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_plan_minimum"));

        db.BillingLedger.Add(PlanMinimum(WorkspaceOne));
        await db.SaveChangesAsync();

        db.BillingLedger.Add(PlanMinimum(WorkspaceOne));

        (await Refusal(db)).ConstraintName.Should().Be(IdempotencyIndex,
            "two nulls are one key here, or a retried tick charges the floor twice");
    }

    // -------------------------------------------------------------------------------------------
    // What it must not refuse
    // -------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Two_credits_in_the_same_hour_are_both_allowed()
    {
        // A person may legitimately top an account up twice in an hour — a failed card retried, a
        // correction on top of a payment — which is why the filter names the two kinds the tick
        // writes instead of covering the table. A credit carries a null ResourceId exactly like the
        // plan-minimum line above, so an index that covered every kind would refuse the second one
        // with NULLS NOT DISTINCT doing the refusing.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_credits"));

        db.BillingLedger.Add(Credit(WorkspaceOne));
        db.BillingLedger.Add(Credit(WorkspaceOne));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    /// <summary>
    /// The other kind outside the filter, and a case for <see cref="PostgresTheoryAttribute"/> that
    /// is a theory rather than a loop because each row must be written into a database of its own —
    /// a refusal on the first case would otherwise be reported against the second.
    /// </summary>
    [PostgresTheory]
    [InlineData((int)LedgerKind.Credit, "credit")]
    [InlineData((int)LedgerKind.Adjustment, "adjustment")]
    public async Task A_kind_the_tick_never_writes_may_repeat_within_the_hour(int kind, string label)
    {
        // An adjustment is how a mistake is corrected in an append-only ledger — nothing is edited
        // or deleted — so two of them against one resource in one hour is an ordinary afternoon for
        // whoever is unpicking a bad pass.
        //
        // Both rows here carry a resource id, which is what makes this a different question from
        // the credit above: that one is allowed because its ResourceId is null AND its kind is
        // outside the filter, and either alone would do it. These collide on every column the index
        // names, so the filter is the only thing standing between them and a refusal.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync($"billing_{label}"));

        db.BillingLedger.Add(Line(WorkspaceOne, (LedgerKind)kind, BilledResourceType.App, AppOne, 500));
        db.BillingLedger.Add(Line(WorkspaceOne, (LedgerKind)kind, BilledResourceType.App, AppOne, -500));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync(
            $"the tick never writes a {label}, so two in one hour are a person's doing");
    }

    [PostgresFact]
    public async Task Each_workspace_pays_its_own_plan_minimum_for_the_hour()
    {
        // Two nulls in one key, in two tenants. NULLS NOT DISTINCT makes the ResourceIds equal, so
        // the only thing keeping these apart is WorkspaceId — and an index that got this wrong
        // would let the first workspace of the pass pay its floor and refuse every other one, which
        // BillingTick records as a failed workspace and steps over. The platform would bill one
        // tenant's minimum an hour, for ever, and report the rest as broken.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_two_tenants"));

        db.BillingLedger.Add(PlanMinimum(WorkspaceOne));
        db.BillingLedger.Add(PlanMinimum(WorkspaceTwo));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [PostgresFact]
    public async Task The_same_app_is_charged_again_for_the_next_hour()
    {
        // The hour, in one fact. Drop BillingHour from the key and an app is billed once and never
        // again — a bill that stops growing looks exactly like a quiet platform, and the wallet
        // would simply stop moving.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_next_hour"));

        db.BillingLedger.Add(Charge(WorkspaceOne, BilledResourceType.App, AppOne));
        await db.SaveChangesAsync();

        db.BillingLedger.Add(Charge(WorkspaceOne, BilledResourceType.App, AppOne, Hour.AddHours(1)));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [PostgresFact]
    public async Task A_database_and_the_disk_under_it_are_two_lines_for_one_hour()
    {
        // Why BilledResourceType.ServiceVolume was appended instead of reusing Volume. A managed
        // database's disk has no row of its own — ManagedService carries its own VolumeName and
        // StorageBytes, and the Volumes table is keyed by AppId — so the disk line is keyed on the
        // service's own id, which is the same id the service's compute line carries. The two are
        // told apart by ResourceType and by nothing else.
        //
        // Reusing Volume would have made the pair collide outright: same workspace, same type, same
        // id, same hour. The database would be billed for its size and hold as much data as it
        // liked for nothing, and the tick would report the hour as already charged.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_service_disk"));

        db.BillingLedger.Add(Charge(WorkspaceOne, BilledResourceType.Service, DatabaseOne));
        db.BillingLedger.Add(Charge(WorkspaceOne, BilledResourceType.ServiceVolume, DatabaseOne));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync(
            "the compute and the disk are one id and two categories");
    }

    // -------------------------------------------------------------------------------------------
    // What the catalogue says it is
    // -------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_idempotency_index_covers_exactly_the_kinds_the_tick_writes()
    {
        var definition = await IndexCatalogue.DefinitionAsync(await lane.HeadSchemaAsync(), IdempotencyIndex);

        definition.Should().StartWith("CREATE UNIQUE INDEX");
        definition.Should().Contain("""("WorkspaceId", "ResourceType", "ResourceId", "BillingHour")""");

        // Charge and PlanMinimumTopUp, spelled as the migration spells them, because a migration
        // that has shipped goes on meaning the numbers it was written with. Credit is 1 and
        // Adjustment is 3, and both are deliberately outside: a person may repeat either.
        IndexCatalogue.FilteredValues(definition,
                "an index with no filter at all covers credits and adjustments too, and a person " +
                "correcting a bad hour would be refused the second line they need")
            .Should().BeEquivalentTo(new[] { (int)LedgerKind.Charge, (int)LedgerKind.PlanMinimumTopUp },
                "too few kinds and a retried tick writes the plan minimum twice; too many and an " +
                "administrator cannot credit an account twice in an hour. Postgres printed the " +
                "index as {0}", definition);
    }

    [PostgresFact]
    public async Task The_idempotency_index_treats_two_missing_resource_ids_as_one_key()
    {
        // The setting behind Two_plan_minimum_lines_for_the_same_hour_cannot_both_exist, asserted
        // where it is stored rather than where it is felt. The behavioural fact is the one that
        // matters, but it can only say "something refused this"; this one says which property did,
        // so a migration regenerated without the Npgsql:NullsDistinct annotation fails on a line
        // naming the annotation.
        var nullsAreEqual = await IndexCatalogue.TreatsMissingValuesAsEqualAsync(
            await lane.HeadSchemaAsync(), IdempotencyIndex);

        nullsAreEqual.Should().BeTrue(
            "the plan-minimum line has a null ResourceId, so under Postgres's default two of them " +
            "for one hour are two distinct keys and both are accepted");
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>The duplicate-key error, unwrapped — and asserted to be one, not some other failure.</summary>
    private static async Task<PostgresException> Refusal(HarboraDbContext db)
    {
        var thrown = await db.Awaiting(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();

        var inner = thrown.Which.InnerException.Should().BeOfType<PostgresException>().Which;
        inner.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation,
            "BillingTick.SaveAsync catches exactly this code, then asks the database whether the " +
            "hour is now paid for rather than assuming which index refused it");
        return inner;
    }

    private static BillingLedgerEntry Charge(
        Guid workspaceId, BilledResourceType type, Guid resourceId, DateTimeOffset? hour = null) =>
        Line(workspaceId, LedgerKind.Charge, type, resourceId, -1_000, hour);

    /// <summary>
    /// The floor line. Its <c>ResourceId</c> is null by design — see <c>BilledResourceType.PlanBase</c>
    /// — and that null is the subject of half this file.
    /// </summary>
    private static BillingLedgerEntry PlanMinimum(Guid workspaceId) =>
        Line(workspaceId, LedgerKind.PlanMinimumTopUp, BilledResourceType.PlanBase, null, -2_500);

    /// <summary>Money in, from a person. Null resource for the same reason a top-up has none.</summary>
    private static BillingLedgerEntry Credit(Guid workspaceId) =>
        Line(workspaceId, LedgerKind.Credit, BilledResourceType.None, null, 50_000);

    private static BillingLedgerEntry Line(
        Guid workspaceId,
        LedgerKind kind,
        BilledResourceType type,
        Guid? resourceId,
        long amountMinor,
        DateTimeOffset? hour = null) =>
        new()
        {
            WorkspaceId = workspaceId,
            BillingHour = hour ?? Hour,
            Kind = kind,
            AmountMinor = amountMinor,
            ResourceType = type,
            ResourceId = resourceId,
            ResourceName = "seeded",
            RunState = BilledRunState.NotApplicable,
            RatePerHourMinor = Math.Abs(amountMinor),
            Hours = 1
        };
}
