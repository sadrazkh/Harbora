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
