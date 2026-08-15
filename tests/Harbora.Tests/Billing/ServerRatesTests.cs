using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

public class ServerRatesTests
{
    private static InstanceSize Size(long? running, long? stopped) => new()
    {
        Key = "small",
        RunningRatePerHourMinor = running,
        StoppedRatePerHourMinor = stopped,
    };

    private static ServerInstanceOffer Offer(
        long? running = null, long? stopped = null, bool offered = true) => new()
    {
        InstanceSizeKey = "small",
        RunningRatePerHourMinor = running,
        StoppedRatePerHourMinor = stopped,
        IsOffered = offered,
    };

    [Fact]
    public void A_server_that_has_priced_a_tier_itself_charges_its_own_rate()
    {
        ServerRates.ForWorkload(Size(1000, 100), Offer(running: 1500), BilledRunState.Running)
            .Should().Be(1500);
    }

    [Fact]
    public void A_server_that_has_priced_the_stopped_state_charges_its_own_reserved_rate()
    {
        ServerRates.ForWorkload(Size(1000, 100), Offer(stopped: 250), BilledRunState.Stopped)
            .Should().Be(250);
    }

    [Fact]
    public void A_tier_with_no_row_for_this_server_is_charged_the_global_rate()
    {
        // The ordinary case on every install until somebody opens the pricing matrix. It is what
        // makes switching this feature on change nobody's bill.
        ServerRates.ForWorkload(Size(1000, 100), null, BilledRunState.Running).Should().Be(1000);
        ServerRates.ForWorkload(Size(1000, 100), null, BilledRunState.Stopped).Should().Be(100);
    }

    [Fact]
    public void A_row_that_prices_neither_state_is_charged_the_global_rate()
    {
        // An absent row and a row of blanks mean the same thing: offered, at the global price. The
        // provider may have created the row only to withdraw the tier, or to price it later.
        ServerRates.ForWorkload(Size(1000, 100), Offer(), BilledRunState.Running).Should().Be(1000);
        ServerRates.ForWorkload(Size(1000, 100), Offer(), BilledRunState.Stopped).Should().Be(100);
    }

    [Fact]
    public void A_state_left_blank_on_the_server_inherits_its_own_global_column_not_the_other_state()
    {
        // The rule that is easy to get wrong and expensive when it is. This server charges 1500 an
        // hour running and says nothing about stopped — so a stopped workload costs the GLOBAL
        // stopped rate of 100, never the 1500 the server set for running.
        //
        // Falling back across states would charge a stopped workload fifteen times its price on
        // exactly the servers where somebody was careful enough to price one state and not the other.
        var size = Size(running: 1000, stopped: 100);
        var offer = Offer(running: 1500);

        ServerRates.ForWorkload(size, offer, BilledRunState.Running).Should().Be(1500);
        ServerRates.ForWorkload(size, offer, BilledRunState.Stopped).Should().Be(100);
    }

    [Fact]
    public void A_tier_withdrawn_from_a_server_is_still_charged_for_what_is_already_on_it()
    {
        // Withdrawal stops new placement; it is not a repricing. Reading it as unpriced would stop
        // billing everything already running on the tier — silently, and in the platform's favour —
        // which is the exact shape of failure the nullable rate columns exist to prevent.
        //
        // This is the same separation Plan.IsEnabled already makes: a withdrawn plan changes nothing
        // for the tenants on it.
        ServerRates.ForWorkload(Size(1000, 100), Offer(running: 1500, offered: false), BilledRunState.Running)
            .Should().Be(1500);
    }

    [Fact]
    public void A_tier_nobody_has_priced_at_either_level_reads_as_unset_rather_than_free()
    {
        // Null all the way down stays null. An unpriced tier on a server is not a free tier on that
        // server, and the pass reports it rather than hosting whatever is on it for nothing.
        ServerRates.ForWorkload(Size(null, null), Offer(), BilledRunState.Running).Should().BeNull();
        ServerRates.ForWorkload(Size(null, null), null, BilledRunState.Running).Should().BeNull();
    }

    [Fact]
    public void A_server_may_price_a_tier_the_global_list_never_did()
    {
        // The override is not a discount on an existing figure — it is an answer in its own right,
        // so a tier left unpriced globally becomes chargeable on the server that priced it.
        ServerRates.ForWorkload(Size(null, null), Offer(running: 900, stopped: 90), BilledRunState.Running)
            .Should().Be(900);
    }

    [Fact]
    public void A_server_may_give_away_a_tier_the_global_list_charges_for()
    {
        // Zero is an answer here too. A provider running a free tier on one box must not have it
        // read as "no override" and quietly charged at the global rate.
        ServerRates.ForWorkload(Size(1000, 100), Offer(running: 0, stopped: 0), BilledRunState.Running)
            .Should().Be(0);
    }

    [Fact]
    public void A_line_with_no_run_state_of_its_own_is_charged_nothing_however_the_server_is_priced()
    {
        // Volumes and the plan-minimum line carry NotApplicable and are priced by their own rules.
        // A server override must not leak into them: this arm consults no rate column at all, so it
        // is a real zero rather than an unset, exactly as BillingRates already answers it.
        ServerRates.ForWorkload(Size(1000, 100), Offer(running: 1500), BilledRunState.NotApplicable)
            .Should().Be(0);
    }

    [Fact]
    public void A_server_with_no_row_offers_every_tier_it_has_capacity_for()
    {
        // No row is not a refusal. A provider who has never opened the matrix offers everything,
        // which is what the platform did before this table existed.
        ServerRates.OffersNewWork(null).Should().BeTrue();
        ServerRates.OffersNewWork(Offer()).Should().BeTrue();
        ServerRates.OffersNewWork(Offer(offered: false)).Should().BeFalse();
    }
}
