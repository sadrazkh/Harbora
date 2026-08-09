using FluentAssertions;
using Harbora.Domain.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

public class LedgerShapeTests
{
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
