using FluentAssertions;
using Harbora.Domain.Servers;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The bounds an administrator's commitment-ratio choice must fall within, and the recommended
/// starting points shown beside them — never applied automatically, per the owner's instruction that
/// the decision stays theirs.
/// </summary>
public class ServerCapacityPolicyTests
{
    [Theory]
    [InlineData(0, false, "zero reads as unmeasured, not undercommit")]
    [InlineData(-1, false, "negative is nonsensical for a multiplier")]
    [InlineData(0.1, true, "the floor itself is valid")]
    [InlineData(0.5, true, "undercommit is a legitimate choice")]
    [InlineData(1, true, "no overcommit")]
    [InlineData(double.NaN, false, "not a number is not a policy")]
    [InlineData(double.PositiveInfinity, false, "infinity is not a policy")]
    public void A_cpu_overcommit_factor_is_valid_only_within_its_floor_and_ceiling(double factor, bool expected, string because)
    {
        ServerCapacityPolicy.IsValidOvercommitFactor(factor, ServerCapacityPolicy.MaxCpuOvercommitFactor)
            .Should().Be(expected, because);
    }

    [Fact]
    public void The_cpu_ceiling_is_higher_than_memorys_because_the_failure_modes_differ()
    {
        // CPU contention queues work; memory overcommit invites the OOM killer. The two ceilings must
        // not be the same number, or the asymmetry the owner asked for is not actually expressed.
        ServerCapacityPolicy.MaxCpuOvercommitFactor.Should().BeGreaterThan(ServerCapacityPolicy.MaxMemoryOvercommitFactor);
    }

    [Fact]
    public void CPU_is_within_bounds_right_past_memorys_ceiling_while_memory_itself_is_refused()
    {
        var justPastMemoryCeiling = ServerCapacityPolicy.MaxMemoryOvercommitFactor + 0.5;

        ServerCapacityPolicy.IsValidOvercommitFactor(justPastMemoryCeiling, ServerCapacityPolicy.MaxCpuOvercommitFactor)
            .Should().BeTrue("CPU's ceiling is higher");
        ServerCapacityPolicy.IsValidOvercommitFactor(justPastMemoryCeiling, ServerCapacityPolicy.MaxMemoryOvercommitFactor)
            .Should().BeFalse("memory's ceiling is tighter on purpose");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0.15, true)]
    [InlineData(0.9, true)]
    [InlineData(0.95, false)]
    [InlineData(-0.1, false)]
    [InlineData(1, false)]
    public void A_reserved_memory_ratio_cannot_leave_nothing_to_schedule(double ratio, bool expected) =>
        ServerCapacityPolicy.IsValidReservedMemoryRatio(ratio).Should().Be(expected);

    [Fact]
    public void The_recommended_memory_factor_takes_no_risk_while_cpus_does()
    {
        // The asymmetry stated as a fact about the constants, not just as comments: memory's own
        // suggestion is "no overcommit at all", CPU's is real oversubscription.
        ServerCapacityPolicy.RecommendedMemoryOvercommitFactor.Should().Be(1.0);
        ServerCapacityPolicy.RecommendedCpuOvercommitFactor.Should().BeGreaterThan(1.0);
    }
}
