using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Exhaustive-ish coverage of the deployment state machine (ADR-004). These lock the legal lifecycle
/// so the pipeline and reconciler can never silently drift into an inconsistent status.
/// </summary>
public class DeploymentStateMachineTests
{
    [Theory]
    [InlineData(DeploymentStatus.Queued, DeploymentStatus.Building)]
    [InlineData(DeploymentStatus.Building, DeploymentStatus.Deploying)]
    [InlineData(DeploymentStatus.Building, DeploymentStatus.Pushing)]
    [InlineData(DeploymentStatus.Pushing, DeploymentStatus.Deploying)]
    [InlineData(DeploymentStatus.Deploying, DeploymentStatus.HealthChecking)]
    [InlineData(DeploymentStatus.HealthChecking, DeploymentStatus.Succeeded)]
    [InlineData(DeploymentStatus.Succeeded, DeploymentStatus.RolledBack)]
    [InlineData(DeploymentStatus.Queued, DeploymentStatus.Cancelled)]
    [InlineData(DeploymentStatus.Deploying, DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.HealthChecking, DeploymentStatus.Failed)]
    public void Allows_legal_transitions(DeploymentStatus from, DeploymentStatus to)
        => DeploymentStateMachine.CanTransition(from, to).Should().BeTrue();

    [Theory]
    [InlineData(DeploymentStatus.Queued, DeploymentStatus.Succeeded)]       // can't skip to success
    [InlineData(DeploymentStatus.Succeeded, DeploymentStatus.Building)]     // terminal-ish, no restart
    [InlineData(DeploymentStatus.Failed, DeploymentStatus.Deploying)]       // failed is terminal
    [InlineData(DeploymentStatus.Cancelled, DeploymentStatus.Building)]     // cancelled is terminal
    [InlineData(DeploymentStatus.RolledBack, DeploymentStatus.Succeeded)]   // rolled back is terminal
    [InlineData(DeploymentStatus.Deploying, DeploymentStatus.Building)]     // no going backwards
    [InlineData(DeploymentStatus.Building, DeploymentStatus.Building)]      // self-transition
    public void Rejects_illegal_transitions(DeploymentStatus from, DeploymentStatus to)
        => DeploymentStateMachine.CanTransition(from, to).Should().BeFalse();

    [Fact]
    public void A_deployment_with_nothing_to_health_check_may_finish_from_deploying()
    {
        // A scheduled job and a release task have no container to probe. Routing them through
        // HealthChecking to keep the happy path uniform would record a check that never ran.
        DeploymentStateMachine.CanTransition(DeploymentStatus.Deploying, DeploymentStatus.Succeeded)
            .Should().BeTrue();

        DeploymentStateMachine.CanTransition(DeploymentStatus.Building, DeploymentStatus.Succeeded)
            .Should().BeFalse("nothing has been released yet at that point");
        DeploymentStateMachine.CanTransition(DeploymentStatus.Queued, DeploymentStatus.Succeeded)
            .Should().BeFalse();
    }

    [Fact]
    public void InFlight_and_Terminal_partition_every_state_except_PendingApproval()
    {
        // 5.2 (2026-09 market-gaps round two): PendingApproval is the one deliberate third state —
        // neither something a restart can strand (InFlight) nor something over (Terminal). See
        // DeploymentStateMachine's own doc on InFlight for why a restart must never touch it.
        foreach (DeploymentStatus s in Enum.GetValues<DeploymentStatus>())
        {
            if (s == DeploymentStatus.PendingApproval) continue;
            DeploymentStateMachine.IsInFlight(s).Should().Be(!DeploymentStateMachine.IsTerminal(s));
        }

        DeploymentStateMachine.IsInFlight(DeploymentStatus.Queued).Should().BeTrue();
        DeploymentStateMachine.IsInFlight(DeploymentStatus.HealthChecking).Should().BeTrue();
        DeploymentStateMachine.IsTerminal(DeploymentStatus.Succeeded).Should().BeTrue();
        DeploymentStateMachine.IsTerminal(DeploymentStatus.Failed).Should().BeTrue();
    }

    [Fact]
    public void PendingApproval_is_neither_in_flight_nor_terminal()
    {
        DeploymentStateMachine.IsInFlight(DeploymentStatus.PendingApproval).Should().BeFalse(
            "nothing is working on it — no Job exists yet — so a restart must not treat it as stranded work to fail");
        DeploymentStateMachine.IsTerminal(DeploymentStatus.PendingApproval).Should().BeFalse(
            "it still has a future: an approval, a rejection or an expiry");
        DeploymentStateMachine.IsUnsettled(DeploymentStatus.PendingApproval).Should().BeTrue(
            "a second deploy request for the same app must still be coalesced/refused while one waits on approval");
    }

    [Theory]
    [InlineData(DeploymentStatus.PendingApproval, DeploymentStatus.Queued)]
    [InlineData(DeploymentStatus.PendingApproval, DeploymentStatus.Cancelled)]
    public void PendingApproval_allows_approval_and_rejection(DeploymentStatus from, DeploymentStatus to)
        => DeploymentStateMachine.CanTransition(from, to).Should().BeTrue();

    [Fact]
    public void PendingApproval_cannot_skip_straight_into_building()
        => DeploymentStateMachine.CanTransition(DeploymentStatus.PendingApproval, DeploymentStatus.Building)
            .Should().BeFalse("an approval must move it to Queued first, the same as every other deployment");

    [Fact]
    public void Transition_stamps_started_and_finished_timestamps()
    {
        var now = DateTimeOffset.UtcNow;
        var d = new Deployment { Number = 1, Status = DeploymentStatus.Queued };

        DeploymentStateMachine.Transition(d, DeploymentStatus.Building, now);
        d.Status.Should().Be(DeploymentStatus.Building);
        d.StartedAt.Should().Be(now);

        DeploymentStateMachine.Transition(d, DeploymentStatus.Deploying, now.AddSeconds(1));
        DeploymentStateMachine.Transition(d, DeploymentStatus.HealthChecking, now.AddSeconds(2));
        DeploymentStateMachine.Transition(d, DeploymentStatus.Succeeded, now.AddSeconds(3));
        d.FinishedAt.Should().Be(now.AddSeconds(3));
    }

    [Fact]
    public void Transition_throws_on_illegal_move()
    {
        var d = new Deployment { Number = 1, Status = DeploymentStatus.Queued };
        var act = () => DeploymentStateMachine.Transition(d, DeploymentStatus.Succeeded, DateTimeOffset.UtcNow);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Illegal*");
    }
}
