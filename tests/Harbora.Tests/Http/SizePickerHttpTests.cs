using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the size chooser actually renders.
///
/// <para>
/// Asserted on the <c>data-picker-*</c> attributes rather than on the words. The copy is built from
/// interpolated C# strings and Razor's encoder writes non-ASCII out as numeric character references,
/// so the Persian this panel renders by default is not a string that appears in the response — and
/// pinning the English would pin the test to a language the fixture does not use.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class SizePickerHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>
    /// A tier and a server, with the tier priced only on that server.
    /// </summary>
    private (Guid ServerId, string Key) SeedPricedTier(string name, long? globalRate, long? serverRate)
    {
        var server = new Server { Name = name, Hostname = name, IsLocal = false };
        var key = name + "-tier";

        Panel.Seed(db =>
        {
            db.Servers.Add(server);
            db.InstanceSizes.Add(new InstanceSize
            {
                Key = key,
                Name = name + " tier",
                NameFa = name,
                Family = "memory",
                CpuCores = 2,
                MemoryBytes = 4L * 1024 * 1024 * 1024,
                DiskBytes = 40L * 1024 * 1024 * 1024,
                RunningRatePerHourMinor = globalRate,
                StoppedRatePerHourMinor = globalRate
            });
            if (serverRate is not null)
                db.ServerInstanceOffers.Add(new ServerInstanceOffer
                {
                    ServerId = server.Id,
                    InstanceSizeKey = key,
                    RunningRatePerHourMinor = serverRate
                });
        });

        return (server.Id, key);
    }

    private async Task<string> CreateFormAsync(string who, string ip)
    {
        Panel.GivenUser(fixture.WorkspaceId, who, SystemRole.Owner);
        var client = await Panel.SignedInAs(ip, who);
        return await (await client.GetAsync("/apps/create")).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task The_create_form_offers_tiers_as_cards_rather_than_a_dropdown()
    {
        // The ask this whole change answers: the size was a <select> folded inside Advanced, so the
        // fact that decides the bill was among the hardest things on the page to find, and it showed
        // no price at all.
        var (_, key) = SeedPricedTier("cards", globalRate: 200, serverRate: null);

        var html = await CreateFormAsync("picker-cards@example.com", "203.0.113.240");

        html.Should().Contain($"data-picker-tier=\"{key}\"", "the tier must render as a card of its own");
        html.Should().Contain("name=\"InstanceSizeKey\"",
            "and still post the field the create action binds");
        html.Should().NotContain("<select name=\"InstanceSizeKey\"",
            "the dropdown it replaced must be gone, not merely hidden beside it");
    }

    [Fact]
    public async Task A_tier_card_shows_the_hour_and_the_month_it_implies()
    {
        // 200 minor units an hour is 2.00, and 200 × 730 is 146,000 — 1,460.00. The monthly figure is
        // the half a customer plans around and the half the page did not have.
        SeedPricedTier("monthly", globalRate: 200, serverRate: null);

        var html = await CreateFormAsync("picker-monthly@example.com", "203.0.113.241");

        html.Should().Contain("2.00", "the exact hourly rate");
        html.Should().Contain("1,460.00", "and the month it implies at 730 hours");
    }

    [Fact]
    public async Task A_tier_priced_only_on_one_server_reads_at_that_servers_price()
    {
        // The feature, rendered. The tier is unpriced globally and priced at 5.00 on this host, so the
        // card must show the host's figure — not "not priced", and not a blank.
        SeedPricedTier("override", globalRate: null, serverRate: 500);

        var html = await CreateFormAsync("picker-override@example.com", "203.0.113.242");

        html.Should().Contain("5.00", "the server's own rate decides what this card says");
        html.Should().Contain("3,650.00", "and the month that rate implies");
    }

    [Fact]
    public async Task A_card_that_cannot_be_chosen_says_which_reason_applies()
    {
        // Refused cards are drawn rather than hidden, and each carries its reason. Hiding an unpriced
        // tier takes capacity off the chooser that the operator cannot then see they are failing to
        // sell; hiding a refused one tells the reader nothing about how to get it.
        //
        // The state travels as an attribute so this holds in either language — see the class remarks.
        var (_, key) = SeedPricedTier("refused", globalRate: 200, serverRate: null);

        Panel.Seed(db =>
        {
            // Withdrawn on every server there is, so no card for it can be chosen anywhere.
            foreach (var server in db.Servers.ToList())
                db.ServerInstanceOffers.Add(new ServerInstanceOffer
                {
                    ServerId = server.Id,
                    InstanceSizeKey = key,
                    IsOffered = false
                });
        });

        var html = await CreateFormAsync("picker-refused@example.com", "203.0.113.243");

        html.Should().Contain($"data-picker-tier=\"{key}\"",
            "a withdrawn tier is still drawn — an operator has to be able to see they are not selling it");
        html.Should().Contain("data-picker-tier-state=\"NotOfferedHere\"",
            "and it says which of the five reasons applies rather than merely being greyed out");
    }

    [Fact]
    public async Task Every_tier_card_declares_the_family_its_tab_filters_on()
    {
        // The tab strip and the cards are filtered against the same value. A card with no family would
        // belong to no tab and disappear the moment the script ran — a priced tier, gone.
        var (_, key) = SeedPricedTier("family", globalRate: 200, serverRate: null);

        var html = await CreateFormAsync("picker-family@example.com", "203.0.113.244");

        html.Should().Contain($"data-picker-tier=\"{key}\"");
        html.Should().Contain("data-picker-tier-family=\"memory\"",
            "the family is normalised and carried onto the card the tab strip hides and shows");
    }
}
