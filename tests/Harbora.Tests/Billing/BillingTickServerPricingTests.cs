using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// What the hourly pass does once a price can belong to a server rather than only to a tier.
///
/// <para>
/// Separate from <see cref="BillingTickTests"/> and not folded into it: those tests seed apps with no
/// server at all, which is the case that has to go on billing at the global rate, and every one of
/// them proves that already by continuing to pass. These are about the new column, and they need a
/// fixture with real servers in it.
/// </para>
/// </summary>
public class BillingTickServerPricingTests
{
    private static readonly DateTimeOffset Hour = BillingTickTests.Hour;

    /// <summary>
    /// One workspace, one tier priced globally, and as many servers as the caller names — each with
    /// one running app on it.
    ///
    /// <para>
    /// One tier across all of them on purpose. The whole subject is that the same tier costs
    /// different money in different places, and a fixture that gave each server its own tier could
    /// not tell that apart from the ordinary per-tier pricing that already worked.
    /// </para>
    /// </summary>
    private static (Guid WorkspaceId, Dictionary<string, Guid> Servers) SeedFleet(
        HarboraDbContext db, long? globalRunning, long? globalStopped, params string[] serverNames)
    {
        // A floor of zero rather than null: zero is an answer ("no minimum") and null would make
        // every one of these tests report an unpriced plan it is not asking about.
        var plan = new Plan { Name = "fleet-plan", BaseRatePerHourMinor = 0 };
        db.Plans.Add(plan);

        var workspace = new Workspace { Name = "fleet", Slug = "fleet", PlanId = plan.Id };
        db.Workspaces.Add(workspace);

        db.InstanceSizes.Add(new InstanceSize
        {
            Key = "small",
            Name = "Small",
            RunningRatePerHourMinor = globalRunning,
            StoppedRatePerHourMinor = globalStopped,
        });

        var servers = new Dictionary<string, Guid>();
        foreach (var name in serverNames)
        {
            var server = new Server { Name = name, Hostname = name, IsLocal = false };
            db.Servers.Add(server);
            servers[name] = server.Id;

            db.Apps.Add(new App
            {
                WorkspaceId = workspace.Id,
                Name = $"api-on-{name}",
                Slug = $"api-on-{name}",
                Status = AppStatus.Running,
                InstanceSizeKey = "small",
                ServerId = server.Id,
            });
        }

        db.Wallets.Add(new Wallet { WorkspaceId = workspace.Id });
        return (workspace.Id, servers);
    }

    private static void Price(
        HarboraDbContext db, Guid serverId, long? running = null, long? stopped = null,
        bool offered = true) =>
        db.ServerInstanceOffers.Add(new ServerInstanceOffer
        {
            ServerId = serverId,
            InstanceSizeKey = "small",
            RunningRatePerHourMinor = running,
            StoppedRatePerHourMinor = stopped,
            IsOffered = offered,
        });

    [Fact]
    public async Task Two_servers_at_two_prices_charge_one_workspace_two_different_rates_for_one_tier()
    {
        // The feature, stated as a bill. Both apps are on "small"; one runs on the expensive box.
        // Before this, the tier's own rate decided both and the memory-heavy host was sold at the
        // price of the cheap one.
        await using var db = Harness.SystemContext();
        var (ws, servers) = SeedFleet(db, globalRunning: 500, globalStopped: 50, "cheap", "premium");
        Price(db, servers["premium"], running: 1500);
        await db.SaveChangesAsync();
        Harness.SetBalance(db, ws, Harness.PaidUp);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var rates = await db.BillingLedger
            .Where(l => l.WorkspaceId == ws && l.ResourceType == BilledResourceType.App)
            .Select(l => l.RatePerHourMinor)
            .ToListAsync();

        rates.Should().BeEquivalentTo(new[] { 500L, 1500L });
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 2000);
    }

    [Fact]
    public async Task A_server_nobody_has_priced_charges_what_the_tier_charges()
    {
        // The state every install is in until somebody opens the pricing matrix, and the reason
        // shipping this table changes nobody's bill on the day it arrives.
        await using var db = Harness.SystemContext();
        var (ws, _) = SeedFleet(db, globalRunning: 500, globalStopped: 50, "only");
        await db.SaveChangesAsync();
        Harness.SetBalance(db, ws, Harness.PaidUp);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 500);
    }

    [Fact]
    public async Task A_row_that_prices_neither_state_charges_what_the_tier_charges()
    {
        // A provider may create the row to withdraw a tier, or intending to price it later. A blank
        // row must not read as a blank price — that would be free hosting bought with a checkbox.
        await using var db = Harness.SystemContext();
        var (ws, servers) = SeedFleet(db, globalRunning: 500, globalStopped: 50, "only");
        Price(db, servers["only"]);
        await db.SaveChangesAsync();
        Harness.SetBalance(db, ws, Harness.PaidUp);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 500);
    }

    [Fact]
    public async Task A_tier_withdrawn_from_a_server_is_still_charged_for_the_app_already_on_it()
    {
        // Withdrawal stops new placement; it is not a repricing. Read as an unpriced tier it would
        // stop billing everything already running there — silently, and in the platform's favour.
        await using var db = Harness.SystemContext();
        var (ws, servers) = SeedFleet(db, globalRunning: 500, globalStopped: 50, "retiring");
        Price(db, servers["retiring"], running: 900, offered: false);
        await db.SaveChangesAsync();
        Harness.SetBalance(db, ws, Harness.PaidUp);

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 900);
        result.AccountingComplete.Should().BeTrue();
    }

    [Fact]
    public async Task A_server_that_prices_only_the_running_state_charges_the_global_stopped_rate()
    {
        // The crossing bug, at the level that would actually bill somebody. This server charges 1500
        // running and says nothing about stopped, so a stopped app costs the GLOBAL 50 — not the 1500
        // the server set for a state it was not talking about.
        await using var db = Harness.SystemContext();
        var (ws, servers) = SeedFleet(db, globalRunning: 500, globalStopped: 50, "premium");
        Price(db, servers["premium"], running: 1500);
        await db.SaveChangesAsync();

        // Saved first: a query does not see rows still sitting in the change tracker, so stopping the
        // app before the insert had landed would find nothing and leave it running.
        var app = await db.Apps.SingleAsync(a => a.ServerId == servers["premium"]);
        app.Status = AppStatus.Stopped;
        await db.SaveChangesAsync();
        Harness.SetBalance(db, ws, Harness.PaidUp);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 50);
    }

    [Fact]
    public async Task A_server_may_price_a_tier_the_global_list_never_did()
    {
        // An override is an answer in its own right, not a discount on an existing figure. A tier
        // nobody priced globally becomes chargeable on the server that priced it — otherwise a
        // provider could only ever adjust prices, never introduce one.
        await using var db = Harness.SystemContext();
        var (ws, servers) = SeedFleet(db, globalRunning: null, globalStopped: null, "priced");
        Price(db, servers["priced"], running: 700, stopped: 70);
        await db.SaveChangesAsync();
        Harness.SetBalance(db, ws, Harness.PaidUp);

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 700);
        // And nothing is reported: the tier is unpriced globally, but no hour went unpriced.
        result.AccountingComplete.Should().BeTrue();
    }

    [Fact]
    public async Task A_tier_unpriced_on_one_server_is_reported_once_naming_that_server()
    {
        // Two apps on the same unpriced pair produce ONE line, because the report key carries the
        // server and the tier rather than the resource. Twenty thousand copies of one mistake is how
        // the channel that also carries the real faults stops being read.
        //
        // The server's NAME, not its id: an id sends an operator to the database to work out which
        // box to go and price.
        await using var db = Harness.SystemContext();
        var (ws, servers) = SeedFleet(db, globalRunning: null, globalStopped: null, "forgotten");

        db.Apps.Add(new App
        {
            WorkspaceId = ws,
            Name = "second-api",
            Slug = "second-api",
            Status = AppStatus.Running,
            InstanceSizeKey = "small",
            ServerId = servers["forgotten"],
        });
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.AccountingComplete.Should().BeFalse();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("forgotten").And.Contain("small");
        (await db.BillingLedger.CountAsync(l => l.WorkspaceId == ws)).Should().Be(0);
    }

    [Fact]
    public async Task A_tier_priced_on_one_server_and_forgotten_on_another_reports_only_the_forgotten_one()
    {
        // The reason the server is part of the report key. Collapsed to the tier alone, one line
        // would name the tier and an operator would find it priced on the first host they opened —
        // and conclude the warning was stale rather than that a second host was free.
        await using var db = Harness.SystemContext();
        var (ws, servers) = SeedFleet(db, globalRunning: null, globalStopped: null, "priced", "forgotten");
        Price(db, servers["priced"], running: 800);
        await db.SaveChangesAsync();
        Harness.SetBalance(db, ws, Harness.PaidUp);

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("forgotten").And.NotContain("\"priced\"");

        // The priced app is still charged in full. An unknown withholds the plan's floor, never a
        // charge that was priced — the rule BillingHourPlan's own tests pin down.
        (await db.BillingLedger.CountAsync(l => l.WorkspaceId == ws)).Should().Be(1);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 800);
    }

    [Fact]
    public async Task A_server_may_give_away_a_tier_the_global_list_charges_for()
    {
        // Zero is an answer here too, and a free tier writes no ledger line at all — a row of zero
        // would take the resource's slot in the hour's unique index and make a later correction
        // collide with it.
        await using var db = Harness.SystemContext();
        var (ws, servers) = SeedFleet(db, globalRunning: 500, globalStopped: 50, "free");
        Price(db, servers["free"], running: 0, stopped: 0);
        await db.SaveChangesAsync();
        Harness.SetBalance(db, ws, Harness.PaidUp);

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(Harness.PaidUp);
        (await db.BillingLedger.CountAsync(l => l.WorkspaceId == ws)).Should().Be(0);
        // Free is an answer, so the hour is fully accounted for and the floor is not withheld.
        result.AccountingComplete.Should().BeTrue();
    }
}
