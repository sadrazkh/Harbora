using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The CPU percentage, computed once for both sides.
///
/// The control plane computes it for containers on its own host and the agent computes it for
/// containers on a node. Two copies of this arithmetic means the same container reads differently
/// depending on where it happens to be running, and the difference gets blamed on the node.
///
/// The counters are totals since the container started, so the interesting cases are all about the
/// moments when there is no usable interval to divide by.
/// </summary>
public class ContainerCpuTests
{
    [Fact]
    public void Half_of_one_core_is_fifty_percent()
    {
        ContainerCpu.Percent(cpuDelta: 500, systemDelta: 1000, onlineCpus: 1).Should().Be(50);
    }

    [Fact]
    public void The_scale_is_per_core_so_a_saturated_host_reads_above_a_hundred()
    {
        // 100 is one core saturated, not the whole machine. A container using all four cores of a
        // four-core host reads 400, and flattening that to 100 would hide the difference between
        // "busy" and "using everything there is".
        ContainerCpu.Percent(1000, 1000, 4).Should().Be(400);
    }

    [Fact]
    public void The_first_sample_after_a_start_is_unknown_rather_than_idle()
    {
        // There is no previous sample, so the system delta is zero. Reporting 0% here says a
        // container is idle at exactly the moment somebody is watching it come up.
        ContainerCpu.Percent(500, 0, 1).Should().BeNull();
    }

    [Fact]
    public void The_first_sample_of_an_idle_container_is_unknown_rather_than_not_a_number()
    {
        // Both counters at zero — an idle container's very first sample, which is the common case
        // on a container that has just started. Without the zero-interval guard this is 0/0, and
        // the reset check below does not catch it because zero is not greater than zero. The result
        // is NaN, which System.Text.Json refuses to write at all: the reading does not come out
        // wrong, the whole response fails.
        ContainerCpu.Percent(0, 0, 1).Should().BeNull();
    }

    [Fact]
    public void A_reset_counter_is_not_a_reading()
    {
        // Both counters cover every core, so more container time than host time cannot have
        // happened. It means the counters wrapped or the container was replaced between samples,
        // and the arithmetic would produce a spike nobody can explain.
        ContainerCpu.Percent(cpuDelta: 5000, systemDelta: 1000, onlineCpus: 1).Should().BeNull();
    }

    [Fact]
    public void Using_every_core_there_is_sits_on_the_right_side_of_that_guard()
    {
        // The boundary: a container saturating the whole host has cpuDelta == systemDelta, which is
        // legitimate and must survive the reset check rather than being discarded as impossible.
        ContainerCpu.Percent(1000, 1000, 1).Should().Be(100);
        ContainerCpu.Percent(1001, 1000, 1).Should().BeNull();
    }

    [Fact]
    public void An_unreported_core_count_is_taken_as_one_rather_than_zero()
    {
        // Zero cores would multiply every reading to nothing, so every container on a runtime that
        // does not report the count would read as idle.
        ContainerCpu.Percent(500, 1000, 0).Should().Be(50);
    }

    [Fact]
    public void An_idle_container_reads_as_idle()
    {
        // Zero usage over a real interval is a measurement, and distinct from the null above.
        ContainerCpu.Percent(0, 1000, 1).Should().Be(0);
    }

    [Fact]
    public void The_reading_is_rounded_to_something_a_person_reads()
    {
        ContainerCpu.Percent(3333, 10000, 1).Should().Be(33.33);
    }
}
