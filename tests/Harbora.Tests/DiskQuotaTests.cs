using FluentAssertions;
using Harbora.Infrastructure.Tenancy;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The disk limit, which used to be sold and never applied.
///
/// `MaxDiskBytes` sat on the plan, appeared on the pricing screen, and was checked nowhere. Making
/// it real also means being honest about what real can mean: a Docker volume has no size of its own,
/// so nothing stops a process writing. What can be done is measure what is there and refuse to hand
/// out more room to a workspace already over — and never pretend an unmeasured volume is empty.
/// </summary>
public class DiskQuotaTests
{
    [Fact]
    public void A_workspace_under_its_limit_may_take_more()
    {
        DiskQuota.Allows(10L * 1024 * 1024 * 1024, new DiskUsage(4L * 1024 * 1024 * 1024, 0))
            .Should().BeTrue();
    }

    [Fact]
    public void A_workspace_over_its_limit_may_not()
    {
        DiskQuota.Allows(10L * 1024 * 1024 * 1024, new DiskUsage(11L * 1024 * 1024 * 1024, 0))
            .Should().BeFalse();
    }

    [Fact]
    public void A_limit_of_zero_means_unlimited_like_every_other_field_on_a_plan()
    {
        // Consistency matters more than the choice: MaxApps, MaxServices and the rest all read 0
        // this way, and one field meaning "nothing allowed" would be found the hard way.
        DiskQuota.Allows(0, new DiskUsage(500L * 1024 * 1024 * 1024, 0)).Should().BeTrue();
    }

    [Fact]
    public void Exactly_at_the_limit_is_full()
    {
        // The limit is what the plan allows, not what it allows plus one more thing.
        DiskQuota.Allows(1000, new DiskUsage(1000, 0)).Should().BeFalse();
        DiskQuota.Allows(1000, new DiskUsage(999, 0)).Should().BeTrue();
    }

    [Fact]
    public void The_refusal_names_both_numbers()
    {
        // "Quota exceeded" tells someone nothing they can act on; the first question is always
        // "how much am I using?".
        var message = DiskQuota.Explain(10L * 1024 * 1024 * 1024, new DiskUsage(11L * 1024 * 1024 * 1024, 0));

        message.Should().Contain("10").And.Contain("GB");
        message.Should().Contain("11");
    }

    [Fact]
    public void An_unmeasured_volume_is_admitted_to_rather_than_counted_as_empty()
    {
        // The weak point of the whole idea, so it is said out loud wherever the figure appears.
        var usage = new DiskUsage(2L * 1024 * 1024 * 1024, UnmeasuredResources: 3);

        DiskQuota.Caveat(usage).Should().Contain("3").And.Contain("never been measured");
        DiskQuota.Explain(1024, usage).Should().Contain("never been measured");
    }

    [Fact]
    public void A_fully_measured_workspace_gets_no_caveat()
    {
        // The guard on the line above: a warning shown always is a warning nobody reads.
        DiskQuota.Caveat(new DiskUsage(1024, 0)).Should().BeNull();
        DiskQuota.Explain(512, new DiskUsage(1024, 0)).Should().NotContain("never been measured");
    }

    [Fact]
    public void An_unmeasured_workspace_is_not_blocked_on_a_figure_nobody_took()
    {
        // Refusing on the basis of a measurement that was never made would make the platform
        // unusable for a reason nobody could see.
        DiskQuota.Allows(1024, new DiskUsage(0, UnmeasuredResources: 10)).Should().BeTrue();
    }
}
