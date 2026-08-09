using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Billing;

public class LedgerShapeTests
{
    [Fact]
    public void The_idempotency_index_treats_two_missing_resource_ids_as_the_same_row()
    {
        // The plan-minimum line carries a null ResourceId — there is no resource behind it. Postgres
        // counts two NULLs as different values by default, so without NULLS NOT DISTINCT a retried
        // tick writes that line twice straight through a unique index, which is the one thing the
        // index exists to stop.
        //
        // This reads the model, so it catches the configuration being dropped. It does NOT prove the
        // database refuses the second row: only real Postgres can say that, and the fact that does it
        // — two plan-minimum lines for one hour colliding — is owed by the Postgres lane in Task 10.
        using var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("ledger-shape-" + Guid.NewGuid()).Options);

        var index = db.Model.FindEntityType(typeof(BillingLedgerEntry))!
            .GetIndexes()
            .Single(i => i.IsUnique);

        // Read in two steps on purpose. `FindAnnotation(...)?.Value.Should()` would evaluate to null
        // and assert NOTHING the moment somebody deleted the configuration — a test that goes green
        // by losing its own subject.
        var nullsDistinct = index.FindAnnotation("Npgsql:NullsDistinct");

        nullsDistinct.Should().NotBeNull("without this annotation the index is Postgres's default, " +
            "which lets two plan-minimum lines share an hour");
        nullsDistinct!.Value.Should().Be(false,
            "a null ResourceId is the plan-minimum line, and two of them in one hour is a double charge");
    }

    [Fact]
    public void A_charge_is_negative_and_a_credit_is_positive()
    {
        // The sign lives in AmountMinor, not in the Kind, so SUM(AmountMinor) is the balance and
        // nothing has to know which kinds subtract.
        var charge = new BillingLedgerEntry { Kind = LedgerKind.Charge, AmountMinor = -500 };
        var credit = new BillingLedgerEntry { Kind = LedgerKind.Credit, AmountMinor = 500 };

        (charge.AmountMinor + credit.AmountMinor).Should().Be(0);
    }

    [Fact]
    public void A_ledger_line_keeps_the_resource_name_it_was_written_with()
    {
        // Copied, never joined. An app deleted next month must still be readable on this month's
        // bill, and a join to a deleted row gives a blank line where a name should be.
        var line = new BillingLedgerEntry
        {
            ResourceType = BilledResourceType.App,
            ResourceId = Guid.CreateVersion7(),
            ResourceName = "shop-api",
        };

        line.ResourceName.Should().Be("shop-api");
    }

    [Fact]
    public void Money_is_a_whole_number_of_minor_units()
    {
        // Guards the one decision that cannot be walked back later: a bill assembled from a
        // floating type drifts by fractions that compound over thousands of hourly lines.
        typeof(BillingLedgerEntry).GetProperty(nameof(BillingLedgerEntry.AmountMinor))!
            .PropertyType.Should().Be<long>();
        typeof(Wallet).GetProperty(nameof(Wallet.BalanceMinor))!
            .PropertyType.Should().Be<long>();
    }
}
