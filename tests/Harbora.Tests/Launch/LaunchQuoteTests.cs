using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Launch;
using Harbora.Shared.Inspection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Harbora.Tests.Launch.LaunchFixtures;

namespace Harbora.Tests.Launch;

/// <summary>
/// The one place in this flow a bill can surprise somebody. Every figure here is read through the
/// function that charges it, and the first test is the one that says so: it creates the resources and
/// looks at the wallet.
/// </summary>
public sealed class LaunchQuoteTests
{
    private static async Task<(LaunchHarness Harness, Guid Id, LaunchChoices Choices)> PricedAsync(long balance = 100_000, SourceInspection? inspection = null)
    {
        var h = new LaunchHarness();
        h.Billing.Enabled = true;
        using (var db = h.Store())
        {
            db.Wallets.Add(new Wallet { WorkspaceId = h.WorkspaceId, BalanceMinor = balance, Currency = "IRR" });
            db.SaveChanges();
        }

        var id = await h.ReadyGitAsync(inspection ?? NodeWithPostgres());
        return (h, id, await h.DefaultsAsync(id));
    }

    private static long Balance(LaunchHarness h) =>
        h.Store().Wallets.IgnoreQueryFilters().Single(w => w.WorkspaceId == h.WorkspaceId).BalanceMinor;

    [Fact]
    public async Task The_quote_is_exactly_what_leaves_the_wallet_when_it_is_confirmed()
    {
        var (h, id, choices) = await PricedAsync();
        using var _ = h;

        var quote = await h.Request().QuoteAsync(id, choices, default);

        quote.Refusal.Should().BeNull();
        quote.RefusalIsAboutMoney.Should().BeFalse();
        quote.Exempt.Should().BeFalse();
        quote.FirstHourMinor.Should().Be(250, "one database and one app at 125");
        quote.BalanceMinor.Should().Be(100_000);
        quote.Currency.Should().Be("IRR");
        quote.Lines.Should().HaveCount(2);
        quote.Lines.Should().OnlyContain(l => l.HourlyMinor == 125);
        quote.Lines.Select(l => l.Name).Should().Contain(n => n.Contains("shop-postgresql")).And.Contain(n => n.Contains("shop"));
        quote.Lines.Should().OnlyContain(l => !string.IsNullOrWhiteSpace(l.NameFa));

        var before = Balance(h);
        (await h.Request().ConfirmAsync(id, choices, null, default)).Ok.Should().BeTrue();

        (before - Balance(h)).Should().Be(quote.FirstHourMinor);
        h.Store().BillingLedger.IgnoreQueryFilters().Sum(l => l.AmountMinor).Should().Be(-quote.FirstHourMinor);
    }

    [Fact]
    public async Task A_price_set_on_the_server_is_the_one_quoted_because_it_is_the_one_charged()
    {
        var (h, id, choices) = await PricedAsync();
        using var _ = h;
        using (var db = h.Store())
        {
            db.ServerInstanceOffers.Add(new ServerInstanceOffer
            {
                ServerId = h.ServerB.Id, InstanceSizeKey = LaunchHarness.SizeKey, RunningRatePerHourMinor = 700
            });
            db.SaveChanges();
        }
        var onB = choices with { ServerId = h.ServerB.Id };

        var quote = await h.Request().QuoteAsync(id, onB, default);

        quote.FirstHourMinor.Should().Be(1400);
        quote.Lines.Should().OnlyContain(l => l.HourlyMinor == 700);

        var before = Balance(h);
        (await h.Request().ConfirmAsync(id, onB, null, default)).Ok.Should().BeTrue();
        (before - Balance(h)).Should().Be(1400);
    }

    [Fact]
    public async Task With_no_server_chosen_the_price_is_the_one_on_the_host_the_scheduler_will_really_pick()
    {
        var (h, id, choices) = await PricedAsync();
        using var _ = h;
        using (var db = h.Store())
        {
            db.ServerInstanceOffers.Add(new ServerInstanceOffer
            {
                ServerId = h.ServerB.Id, InstanceSizeKey = LaunchHarness.SizeKey, RunningRatePerHourMinor = 300
            });
            db.SaveChanges();
        }
        h.Scheduler.PlaceOn(h.ServerB.Id);

        var quote = await h.Request().QuoteAsync(id, choices, default);
        quote.FirstHourMinor.Should().Be(600);

        var before = Balance(h);
        (await h.Request().ConfirmAsync(id, choices, null, default)).Ok.Should().BeTrue();
        (before - Balance(h)).Should().Be(quote.FirstHourMinor);
    }

    [Fact]
    public async Task Billing_switched_off_is_exempt_and_charges_nothing()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        var choices = await h.DefaultsAsync(id);

        var quote = await h.Request().QuoteAsync(id, choices, default);

        quote.Exempt.Should().BeTrue();
        quote.FirstHourMinor.Should().Be(0);
        quote.Refusal.Should().BeNull();
    }

    [Fact]
    public async Task The_providers_own_workspace_is_exempt_exactly_as_the_charge_decides()
    {
        var (h, id, choices) = await PricedAsync(balance: 0);
        using var _ = h;
        using (var db = h.Store())
        {
            db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == h.WorkspaceId).IsDefault = true;
            db.SaveChanges();
        }

        var quote = await h.Request().QuoteAsync(id, choices, default);

        quote.Exempt.Should().BeTrue();
        quote.FirstHourMinor.Should().Be(0);
        quote.Refusal.Should().BeNull("an empty wallet does not stop the operator's own repair workload");

        (await h.Request().ConfirmAsync(id, choices, null, default)).Ok.Should().BeTrue();
        h.Store().BillingLedger.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task Too_small_a_balance_is_a_refusal_in_both_languages_and_confirming_changes_nothing()
    {
        var (h, id, choices) = await PricedAsync(balance: 200);
        using var _ = h;

        var quote = await h.Request().QuoteAsync(id, choices, default);

        quote.Refusal.Should().NotBeNull();
        quote.RefusalIsAboutMoney.Should().BeTrue("this is the one more credit would fix");
        quote.Refusal!.Message.Should().Contain("Top up");
        quote.Refusal.MessageFa.Should().Contain("شارژ");
        quote.FirstHourMinor.Should().Be(250, "the figure it could not pay is still the figure");
        quote.BalanceMinor.Should().Be(200);

        var refused = await h.Request().ConfirmAsync(id, choices, null, default);
        refused.Ok.Should().BeFalse("each resource alone could be paid for, and the launch as a whole cannot");
        h.Store().ManagedServices.IgnoreQueryFilters().Should().BeEmpty();
        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        Balance(h).Should().Be(200);
    }

    [Fact]
    public async Task An_unpriced_size_is_a_refusal_naming_what_has_no_price()
    {
        var (h, id, choices) = await PricedAsync();
        using var _ = h;
        using (var db = h.Store())
        {
            db.InstanceSizes.Add(new InstanceSize { Key = "unpriced", Name = "Unpriced", RunningRatePerHourMinor = null });
            db.SaveChanges();
        }

        var quote = await h.Request().QuoteAsync(id, choices with { InstanceSizeKey = "unpriced" }, default);

        quote.Refusal!.Message.Should().Contain("no running hourly price");
        quote.RefusalIsAboutMoney.Should().BeFalse("no amount of credit prices a tier");
        quote.Refusal.MessageFa.Should().Contain("قیمت");
        quote.Lines.Should().OnlyContain(l => l.HourlyMinor == null);
    }

    [Fact]
    public async Task A_suspended_workspace_is_a_refusal()
    {
        var (h, id, choices) = await PricedAsync();
        using var _ = h;
        using (var db = h.Store())
        {
            db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == h.WorkspaceId).IsSuspended = true;
            db.SaveChanges();
        }

        var quote = await h.Request().QuoteAsync(id, choices, default);

        quote.Refusal!.Message.Should().Contain("suspended");
        quote.RefusalIsAboutMoney.Should().BeFalse();
        quote.Refusal.MessageFa.Should().Contain("معلق");
    }

    [Fact]
    public async Task With_no_wallet_the_balance_is_unknown_rather_than_zero_and_it_is_refused()
    {
        using var h = new LaunchHarness();
        h.Billing.Enabled = true;
        var id = await h.ReadyGitAsync(Bare());

        var quote = await h.Request().QuoteAsync(id, await h.DefaultsAsync(id), default);

        quote.BalanceMinor.Should().BeNull();
        quote.Refusal.Should().NotBeNull();
        quote.RefusalIsAboutMoney.Should().BeTrue("a wallet is what it is short of");
    }

    [Fact]
    public async Task A_spend_limit_the_charge_would_hit_is_a_refusal_here_too()
    {
        var (h, id, choices) = await PricedAsync();
        using var _ = h;
        using (var db = h.Store())
        {
            db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == h.WorkspaceId).MonthlySpendLimitMinor = 100;
            db.SaveChanges();
        }

        var quote = await h.Request().QuoteAsync(id, choices, default);

        quote.Refusal!.Message.Should().Contain("spend limit");
        quote.RefusalIsAboutMoney.Should().BeFalse("its owner set that limit, and a top-up does not lift it");
    }

    [Fact]
    public async Task Extracting_the_rate_changed_nothing_about_what_is_charged_or_refused()
    {
        // The charge's own rules, asked straight, with the quote beside each.
        var (h, _, _) = await PricedAsync();
        using var _ = h;
        await using var db = h.Store();
        var billing = new ResourceCreationBilling(db, h.Clock, Microsoft.Extensions.Options.Options.Create(h.Billing));

        CreatedBillableResource Resource(string? size, Guid? server) =>
            new(BilledResourceType.App, Guid.CreateVersion7(), "x", size, server);

        (await billing.QuoteAsync(h.WorkspaceId, [Resource(LaunchHarness.SizeKey, null)], default)).FirstHourMinor.Should().Be(125);
        (await billing.QuoteAsync(h.WorkspaceId, [Resource(null, null)], default)).Refusal.Should().NotBeNull();
        (await billing.QuoteAsync(h.WorkspaceId, [Resource("nope", null)], default)).Refusal.Should().NotBeNull();
        (await billing.QuoteAsync(h.WorkspaceId, [], default)).Exempt.Should().BeTrue();

        (await billing.QuoteAsync(h.WorkspaceId,
                [new(BilledResourceType.MailDomain, Guid.CreateVersion7(), "example.com", null, null, 80)], default))
            .FirstHourMinor.Should().Be(80);
    }
}
