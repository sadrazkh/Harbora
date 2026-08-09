using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

public class BillingRatesTests
{
    private static InstanceSize Size(long running, long stopped) => new()
    {
        Key = "small",
        RunningRatePerHourMinor = running,
        StoppedRatePerHourMinor = stopped,
    };

    [Fact]
    public void A_running_workload_is_charged_its_running_rate()
    {
        BillingRates.ForWorkload(Size(1000, 100), BilledRunState.Running).Should().Be(1000);
    }

    [Fact]
    public void A_stopped_workload_is_charged_the_reserved_rate_not_the_running_one()
    {
        // The customer stopped it but did not delete it, so the slot, the image and the volume are
        // still theirs. Charging the running rate would bill for CPU nobody is using; charging zero
        // would let a workspace park a hundred gigabytes for free.
        BillingRates.ForWorkload(Size(1000, 100), BilledRunState.Stopped).Should().Be(100);
    }

    [Fact]
    public void A_line_with_no_run_state_of_its_own_is_charged_nothing_by_the_workload_rate()
    {
        // Volumes and the plan-minimum line carry NotApplicable: they are priced by their own rules,
        // not by a size. Reaching this arm with a priced size must still yield zero, or a volume
        // would silently collect the running rate of whatever size was passed alongside it.
        BillingRates.ForWorkload(Size(1000, 100), BilledRunState.NotApplicable).Should().Be(0);
    }

    [Fact]
    public void A_size_with_no_rates_costs_nothing_rather_than_throwing()
    {
        // Sizes existed before this module did. An unpriced one must read as free until somebody
        // prices it, because the alternative is a tick that dies on one row and bills nobody.
        BillingRates.ForWorkload(new InstanceSize { Key = "legacy" }, BilledRunState.Running)
            .Should().Be(0);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(1073741824L, 1L)]       // exactly 1 GiB
    [InlineData(1073741825L, 2L)]       // one byte over rounds up
    [InlineData(5368709120L, 5L)]       // 5 GiB
    [InlineData(-1L, 0L)]               // a nonsense reading is free, not one whole gibibyte
    public void Disk_is_charged_by_the_gibibyte_rounded_up(long bytes, long expectedGib)
    {
        BillingRates.GibibytesCeiling(bytes).Should().Be(expectedGib);
    }

    [Fact]
    public void A_volume_costs_its_rounded_up_gibibytes_times_the_rate()
    {
        // 3 GiB + 1 byte at 250/GiB-hour = 4 × 250.
        BillingRates.ForVolume(3L * 1024 * 1024 * 1024 + 1, ratePerGbHourMinor: 250).Should().Be(1000);
    }

    [Fact]
    public void Rounding_up_an_absurd_volume_gives_the_true_figure_rather_than_a_wrapped_one()
    {
        // long.MaxValue bytes is not a real disk, but the arithmetic that mishandles it is a real
        // hazard. This project compiles unchecked, so the obvious `bytes + BytesPerGibibyte - 1`
        // does not throw on it — it wraps to a large negative and turns an hour's charge into a
        // credit. Asserting only "does not throw" would go green for that broken version too, so
        // the exact answer is asserted: 2^63 - 1 bytes is 2^33 gibibytes once rounded up.
        BillingRates.GibibytesCeiling(long.MaxValue).Should().Be(8589934592L);
    }
}
