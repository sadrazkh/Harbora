using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Harbora.Tests.Launch.LaunchFixtures;

namespace Harbora.Tests.Launch;

/// <summary>
/// A launch's default size has to be what the size chooser opens on, and the chooser lives in the web
/// project where the launch cannot call it. So the rules are asked twice, and here the two are held
/// together over every reason a tier can be passed over. If <c>SizePickerService</c> gains a rule, one of
/// these should fail.
/// </summary>
public sealed class LaunchSizeDefaultParityTests
{
    private static async Task AgreesAsync(LaunchHarness h, string? expected)
    {
        var id = await h.ReadyGitAsync(Bare());
        var launch = (await h.DefaultsAsync(id)).InstanceSizeKey;
        var chooser = await h.SizePicker().OpensOnAsync(h.WorkspaceId);

        launch.Should().Be(chooser, "the launch and the chooser must open on one tier");
        launch.Should().Be(expected);
    }

    private static void Seed(LaunchHarness h, Action<Harbora.Data.HarboraDbContext> change)
    {
        using var db = h.Store();
        change(db);
        db.SaveChanges();
    }

    private static InstanceSize Tier(string key, int order, long? rate = 500) =>
        new() { Key = key, Name = key, SortOrder = order, CpuCores = 1, MemoryBytes = 1L << 30, RunningRatePerHourMinor = rate };

    [Fact]
    public async Task The_first_tier_by_sort_order()
    {
        using var h = new LaunchHarness();
        Seed(h, db => db.InstanceSizes.Add(Tier("earlier", -1)));

        await AgreesAsync(h, "earlier");
    }

    [Fact]
    public async Task Skipping_a_tier_the_plan_does_not_include()
    {
        using var h = new LaunchHarness();
        Seed(h, db =>
        {
            db.InstanceSizes.Add(Tier("earlier", -1));
            db.Plans.IgnoreQueryFilters().Single().AllowedSizeKeys = LaunchHarness.SizeKey;
        });

        await AgreesAsync(h, LaunchHarness.SizeKey);
    }

    [Fact]
    public async Task Skipping_a_tier_withdrawn_from_the_host_it_would_land_on()
    {
        using var h = new LaunchHarness();
        Seed(h, db =>
        {
            db.InstanceSizes.Add(Tier("earlier", -1));
            db.ServerInstanceOffers.Add(new ServerInstanceOffer { ServerId = h.ServerA.Id, InstanceSizeKey = "earlier", IsOffered = false });
        });

        await AgreesAsync(h, LaunchHarness.SizeKey);
    }

    [Fact]
    public async Task Skipping_a_tier_nobody_priced_when_billing_is_on()
    {
        using var h = new LaunchHarness();
        h.Billing.Enabled = true;
        Seed(h, db =>
        {
            db.InstanceSizes.Add(Tier("unpriced", -1, rate: null));
            db.Wallets.Add(new Harbora.Domain.Billing.Wallet { WorkspaceId = h.WorkspaceId, BalanceMinor = 1000 });
        });

        await AgreesAsync(h, LaunchHarness.SizeKey);
    }

    [Fact]
    public async Task Keeping_an_unpriced_tier_when_billing_is_off_because_a_price_is_no_gate_then()
    {
        using var h = new LaunchHarness();
        Seed(h, db => db.InstanceSizes.Add(Tier("unpriced", -1, rate: null)));

        await AgreesAsync(h, "unpriced");
    }

    [Fact]
    public async Task Keeping_an_unpriced_tier_in_the_providers_own_workspace_because_nothing_charges_it()
    {
        using var h = new LaunchHarness();
        h.Billing.Enabled = true;
        Seed(h, db =>
        {
            db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == h.WorkspaceId).IsDefault = true;
            db.InstanceSizes.Add(Tier("unpriced", -1, rate: null));
        });

        await AgreesAsync(h, "unpriced");
    }

    [Fact]
    public async Task A_fresh_install_can_be_launched_on_with_billing_on_and_nothing_priced()
    {
        // The launch proof's first real launches: every one of them, on a clean machine, on the defaults,
        // ended "InstanceSizeKey: Choose a size." with "Nothing is charged to this workspace" just above it.
        // Billing ships on, the seeded tiers ship unpriced, and the first workspace is the provider's own.
        using var h = new LaunchHarness();
        h.Billing.Enabled = true;
        Seed(h, db =>
        {
            db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == h.WorkspaceId).IsDefault = true;
            foreach (var tier in db.InstanceSizes) tier.RunningRatePerHourMinor = null;
        });

        var id = await h.ReadyGitAsync(Bare());
        var defaults = await h.DefaultsAsync(id);

        defaults.InstanceSizeKey.Should().NotBeNullOrWhiteSpace();
        (await h.Request().ValidateConfirmAsync(id, defaults, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_customers_workspace_on_that_same_install_still_needs_a_priced_tier()
    {
        using var h = new LaunchHarness();
        h.Billing.Enabled = true;
        Seed(h, db =>
        {
            foreach (var tier in db.InstanceSizes) tier.RunningRatePerHourMinor = null;
        });

        await AgreesAsync(h, null);
    }

    [Fact]
    public async Task Skipping_a_disabled_tier()
    {
        using var h = new LaunchHarness();
        Seed(h, db =>
        {
            var tier = Tier("earlier", -1);
            tier.IsEnabled = false;
            db.InstanceSizes.Add(tier);
        });

        await AgreesAsync(h, LaunchHarness.SizeKey);
    }

    [Fact]
    public async Task With_no_tier_at_all_neither_opens_on_anything()
    {
        using var h = new LaunchHarness();
        Seed(h, db => db.InstanceSizes.RemoveRange(db.InstanceSizes));

        await AgreesAsync(h, null);
    }

    [Fact]
    public async Task Inside_a_plan_pinned_to_a_pool_the_host_outside_it_is_not_consulted()
    {
        using var h = new LaunchHarness();
        Seed(h, db =>
        {
            db.Plans.IgnoreQueryFilters().Single().NodePool = "eu";
            db.Servers.IgnoreQueryFilters().Single(s => s.Id == h.ServerB.Id).Pool = "eu";
            db.InstanceSizes.Add(Tier("earlier", -1));
            // Withdrawn on the host the pool rules out, offered on the one it allows.
            db.ServerInstanceOffers.Add(new ServerInstanceOffer { ServerId = h.ServerA.Id, InstanceSizeKey = "earlier", IsOffered = false });
        });

        await AgreesAsync(h, "earlier");
    }
}
