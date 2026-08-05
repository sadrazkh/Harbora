using FluentAssertions;
using Harbora.Infrastructure.Tenancy;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Storage in a resource tier.
///
/// A size was CPU and memory only, so every picker on the platform offered "1 vCPU / 1 GB" and said
/// nothing at all about disk — which is the figure people actually run out of. The label was also
/// built in four separate places, each with its own string, which is why adding a field to it was
/// four edits and three chances to forget one.
/// </summary>
public class InstanceSizeDiskTests
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;

    // --- how a tier reads ---

    [Fact]
    public void A_tier_with_storage_says_so()
    {
        InstanceSizeLabel.For("Small", 1, 1024 * MB, 20 * GB)
            .Should().Be("Small — 1 vCPU / 1 GB / 20 GB");
    }

    [Fact]
    public void A_tier_without_storage_does_not_claim_unlimited_storage()
    {
        // Every tier was in this state until now. A picker that says "unlimited disk" on all five
        // reads as a promise nobody made.
        InstanceSizeLabel.For("Nano", 0.25, 256 * MB, 0)
            .Should().Be("Nano — 0.25 vCPU / 256 MB");
    }

    [Fact]
    public void A_fractional_core_is_not_rounded_into_a_whole_one()
    {
        // Half a core shown as "1 vCPU" is the tier above it.
        InstanceSizeLabel.For("Micro", 0.5, 512 * MB, 0).Should().Contain("0.5 vCPU");
    }

    [Fact]
    public void Memory_and_disk_are_written_the_same_way()
    {
        // One formatter, because "40 GB" in the picker and "40960 MB" in the refusal read as two
        // different limits to the person comparing them.
        InstanceSizeLabel.For("Medium", 2, 2 * GB, 2 * GB)
            .Should().Be("Medium — 2 vCPU / 2 GB / 2 GB");
    }

    // --- what fits in it ---

    [Fact]
    public void What_is_stored_fits_in_a_bigger_tier()
    {
        InstanceDisk.Fits(20 * GB, new DiskUsage(5 * GB, 0)).Should().BeTrue();
    }

    [Fact]
    public void Exactly_full_still_fits()
    {
        // Different from DiskQuota on purpose: that one asks "may another resource be created", so
        // being at the limit is a refusal. This asks "does what exists fit in this box" — and a
        // tier that cannot hold what it advertises is not a tier.
        InstanceDisk.Fits(20 * GB, new DiskUsage(20 * GB, 0)).Should().BeTrue();
    }

    [Fact]
    public void More_than_the_tier_holds_does_not_fit()
    {
        InstanceDisk.Fits(20 * GB, new DiskUsage(21 * GB, 0)).Should().BeFalse();
    }

    [Fact]
    public void A_tier_with_no_ceiling_holds_anything()
    {
        InstanceDisk.Fits(0, new DiskUsage(long.MaxValue, 0)).Should().BeTrue();
    }

    [Fact]
    public void The_refusal_names_both_figures()
    {
        // "Too small" is not something anybody can act on. The first question is how much is there.
        var reason = InstanceDisk.Explain(20 * GB, new DiskUsage(50 * GB, 0));

        reason.Should().Contain("20 GB").And.Contain("50 GB");
    }

    [Fact]
    public void Something_that_fits_has_nothing_to_explain()
    {
        InstanceDisk.Explain(20 * GB, new DiskUsage(1 * GB, 0)).Should().BeNull();
    }

    [Fact]
    public void An_unmeasured_volume_does_not_refuse_a_resize()
    {
        // Refusing on the strength of a number nobody collected is a guess, and it would block a
        // resize on a brand-new app whose volumes have never been walked.
        InstanceDisk.Fits(20 * GB, new DiskUsage(1 * GB, UnmeasuredResources: 9)).Should().BeTrue();
    }

    [Fact]
    public void An_unmeasured_volume_is_still_declared()
    {
        // It is a reason to distrust the figure, which is a different thing from a reason to refuse.
        InstanceDisk.Caveat(new DiskUsage(1 * GB, 3)).Should().Contain("3");
        InstanceDisk.Caveat(new DiskUsage(1 * GB, 0)).Should().BeNull();
    }

    // --- one way of writing bytes ---

    [Theory]
    // Under a kilobyte, in bytes: 19 rounds to "0 KB", and a real measurement reading as nothing is
    // the same lie as an unmeasured one shown as empty.
    [InlineData(19, "19 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(2 * 1024, "2 KB")]
    [InlineData(256 * 1024 * 1024, "256 MB")]
    [InlineData(1536L * 1024 * 1024, "1.5 GB")]
    public void Bytes_are_scaled_to_a_unit_people_read(long bytes, string expected)
    {
        ByteSize.Format(bytes).Should().Be(expected);
    }

    [Fact]
    public void Nothing_is_unlimited_rather_than_zero()
    {
        ByteSize.Format(0).Should().Be("unlimited");
        ByteSize.Format(0, "∞").Should().Be("∞");
    }
}
