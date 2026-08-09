using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

public class BillingRatesTests
{
    private static InstanceSize Size(long? running, long? stopped) => new()
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
        //
        // Zero here, not unset: this arm never consults a rate column, so there is no unanswered
        // question to report. The size's own prices are irrelevant to a line that is not a workload.
        BillingRates.ForWorkload(Size(1000, 100), BilledRunState.NotApplicable).Should().Be(0);
    }

    [Fact]
    public void A_size_nobody_has_priced_reads_as_unset_rather_than_free()
    {
        // The whole point of the nullable column. An operator who adds a size and forgets to price
        // it must not get silent free hosting for every workload on it, for ever, with each hourly
        // tick reporting success. Unset is a question nobody answered; the caller has to see that.
        BillingRates.ForWorkload(new InstanceSize { Key = "legacy" }, BilledRunState.Running)
            .Should().BeNull();
    }

    [Fact]
    public void A_size_priced_at_zero_reads_as_free_rather_than_unset()
    {
        // The other half of the distinction, and the reason null was needed at all. Somebody typed
        // a zero here on purpose — a free tier is a legitimate thing to sell — and that answer must
        // survive as an answer, not be mistaken for the absence of one.
        BillingRates.ForWorkload(Size(0, 0), BilledRunState.Running).Should().Be(0);
        BillingRates.ForWorkload(Size(0, 0), BilledRunState.Stopped).Should().Be(0);
    }

    [Fact]
    public void A_size_priced_for_running_but_not_for_stopped_is_unset_only_where_it_is_unset()
    {
        // The realistic half-finished size: somebody priced the obvious column and left the other.
        // Resolving each state from its own column keeps the gap visible instead of letting the
        // priced side vouch for the unpriced one.
        BillingRates.ForWorkload(Size(1000, null), BilledRunState.Running).Should().Be(1000);
        BillingRates.ForWorkload(Size(1000, null), BilledRunState.Stopped).Should().BeNull();
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
    public void A_volume_whose_gibibyte_hour_has_no_price_reads_as_unset()
    {
        // A plan with no disk price has not said that disk is free; it has said nothing about disk.
        // Multiplying an unanswered rate by a real size and calling the product zero is how a
        // hundred gibibytes end up hosted for nothing.
        BillingRates.ForVolume(5L * 1024 * 1024 * 1024, ratePerGbHourMinor: null).Should().BeNull();
    }

    [Fact]
    public void An_empty_volume_on_an_unpriced_plan_is_still_unset_rather_than_zero()
    {
        // Nothing times an unknown price is arithmetically zero and factually unknown. Reporting
        // zero here would hide the unpriced plan behind whichever of its volumes happened to be
        // empty this hour, and the gap would surface only once one of them filled up.
        BillingRates.ForVolume(0, ratePerGbHourMinor: null).Should().BeNull();
    }

    [Fact]
    public void A_volume_priced_at_zero_costs_nothing()
    {
        BillingRates.ForVolume(5L * 1024 * 1024 * 1024, ratePerGbHourMinor: 0).Should().Be(0);
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

    [Fact]
    public void Every_rate_column_can_hold_the_absence_of_an_answer()
    {
        // Nullable is load-bearing here, not incidental. The cheapest way to silence the compiler
        // when this reaches a caller that wants a plain long is to drop the `?` — and that puts the
        // ambiguity straight back, with nothing to notice it. `long?` also pins money as an integer
        // count of minor units: a rate that became decimal? would satisfy "nullable" and still be
        // wrong, because repeated addition of a floating type bends a bill over time.
        var rateColumns = new (Type Owner, string Name)[]
        {
            (typeof(Plan), nameof(Plan.BaseRatePerHourMinor)),
            (typeof(Plan), nameof(Plan.OverageCpuCoreHourMinor)),
            (typeof(Plan), nameof(Plan.OverageMemoryGbHourMinor)),
            (typeof(Plan), nameof(Plan.OverageDiskGbHourMinor)),
            (typeof(Plan), nameof(Plan.DiskGbHourMinor)),
            (typeof(InstanceSize), nameof(InstanceSize.RunningRatePerHourMinor)),
            (typeof(InstanceSize), nameof(InstanceSize.StoppedRatePerHourMinor)),
        };

        foreach (var (owner, name) in rateColumns)
        {
            owner.GetProperty(name)!.PropertyType.Should().Be<long?>(
                $"{owner.Name}.{name} must be able to say that nobody has priced it");
        }
    }
}
