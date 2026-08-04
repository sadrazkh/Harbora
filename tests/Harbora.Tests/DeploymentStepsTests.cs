using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The staged progress bar.
///
/// It was drawn once from the status at page load and nothing ever moved it — the logs streamed,
/// the deployment ran to completion, and the five boxes sat still. A bar that never moves is worse
/// than no bar: it says the deployment is stuck.
///
/// The mapping is a rule here because it is needed twice — by Razor on load and by the browser as
/// the deployment runs — and two copies drift the first time a status is added.
/// </summary>
public class DeploymentStepsTests
{
    [Theory]
    [InlineData(DeploymentStatus.Queued, 0)]
    [InlineData(DeploymentStatus.Building, 1)]
    [InlineData(DeploymentStatus.Pushing, 1)]
    [InlineData(DeploymentStatus.Deploying, 2)]
    [InlineData(DeploymentStatus.HealthChecking, 3)]
    [InlineData(DeploymentStatus.Succeeded, 4)]
    public void A_running_status_sits_on_a_step(DeploymentStatus status, int expected)
    {
        DeploymentSteps.IndexOf(status).Should().Be(expected);
    }

    [Fact]
    public void Pushing_shares_a_step_with_building()
    {
        // To somebody watching, pushing an image is the same wait as building it. A sixth box would
        // move the bar backwards the moment the status changed.
        DeploymentSteps.IndexOf(DeploymentStatus.Pushing)
            .Should().Be(DeploymentSteps.IndexOf(DeploymentStatus.Building));
    }

    [Theory]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Cancelled)]
    public void A_stop_is_not_a_position_on_the_bar(DeploymentStatus status)
    {
        // Failure is not "step 6". Giving it an index would march the bar forward on the way to
        // reporting that nothing worked.
        DeploymentSteps.IndexOf(status).Should().BeNull();
    }

    [Fact]
    public void The_bar_never_moves_backwards_as_a_deployment_runs()
    {
        // The order the pipeline actually walks. Any status that sits earlier than the one before
        // it would make the bar retreat mid-deploy, which reads as something going wrong.
        DeploymentStatus[] order =
        [
            DeploymentStatus.Queued, DeploymentStatus.Building, DeploymentStatus.Pushing,
            DeploymentStatus.Deploying, DeploymentStatus.HealthChecking, DeploymentStatus.Succeeded
        ];

        var indexes = order.Select(s => DeploymentSteps.IndexOf(s)!.Value).ToList();

        indexes.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Every_step_before_the_current_one_is_done_and_every_step_after_is_pending()
    {
        var states = Enumerable.Range(0, DeploymentSteps.Count)
            .Select(i => DeploymentSteps.StateOf(i, DeploymentStatus.Deploying))
            .ToList();

        states.Should().Equal(
            StepState.Done, StepState.Done, StepState.Active, StepState.Pending, StepState.Pending);
    }

    [Fact]
    public void Exactly_one_step_is_active_while_a_deployment_runs()
    {
        // Two animated steps say two things are happening; none says nothing is.
        foreach (var status in new[]
                 {
                     DeploymentStatus.Queued, DeploymentStatus.Building, DeploymentStatus.Pushing,
                     DeploymentStatus.Deploying, DeploymentStatus.HealthChecking
                 })
        {
            Enumerable.Range(0, DeploymentSteps.Count)
                .Count(i => DeploymentSteps.StateOf(i, status) == StepState.Active)
                .Should().Be(1, $"{status}");
        }
    }

    [Fact]
    public void Nothing_is_still_animating_once_a_deployment_has_ended()
    {
        // The complaint that started this: a bar that keeps pulsing after the deployment finished
        // is a page that looks like it is still working.
        foreach (var status in new[]
                 {
                     DeploymentStatus.Succeeded, DeploymentStatus.Failed,
                     DeploymentStatus.Cancelled, DeploymentStatus.RolledBack
                 })
        {
            Enumerable.Range(0, DeploymentSteps.Count)
                .Should().NotContain(i => DeploymentSteps.StateOf(i, status) == StepState.Active, $"{status}");
        }
    }

    [Fact]
    public void Success_completes_every_step()
    {
        Enumerable.Range(0, DeploymentSteps.Count)
            .Select(i => DeploymentSteps.StateOf(i, DeploymentStatus.Succeeded))
            .Should().OnlyContain(s => s == StepState.Done);
    }

    [Fact]
    public void A_rollback_shows_how_far_it_got_and_that_it_did_not_ship()
    {
        // Marking everything done would claim the release went live; marking nothing done would
        // lose that it built, deployed and was health-checked before coming back.
        var states = Enumerable.Range(0, DeploymentSteps.Count)
            .Select(i => DeploymentSteps.StateOf(i, DeploymentStatus.RolledBack))
            .ToList();

        states[^1].Should().Be(StepState.Failed);
        states[..^1].Should().OnlyContain(s => s == StepState.Done);
    }

    [Fact]
    public void A_failure_marks_the_bar_without_completing_anything()
    {
        var states = Enumerable.Range(0, DeploymentSteps.Count)
            .Select(i => DeploymentSteps.StateOf(i, DeploymentStatus.Failed))
            .ToList();

        states[0].Should().Be(StepState.Failed);
        states.Should().NotContain(StepState.Done);
    }

    [Fact]
    public void Every_status_the_pipeline_can_reach_is_either_a_step_or_terminal()
    {
        // A status that is neither leaves the bar frozen with no explanation — which is the failure
        // this whole file exists because of.
        foreach (var status in Enum.GetValues<DeploymentStatus>())
        {
            var placed = DeploymentSteps.IndexOf(status) is not null || DeploymentSteps.IsTerminal(status);
            placed.Should().BeTrue($"{status} must be drawable");
        }
    }

    [Fact]
    public void The_map_handed_to_the_browser_matches_the_rule()
    {
        // The browser reads this instead of carrying its own copy. If they can disagree, they will.
        foreach (var status in Enum.GetValues<DeploymentStatus>())
        {
            if (DeploymentSteps.IndexOf(status) is { } expected)
                DeploymentSteps.Map[status.ToString()].Should().Be(expected);
            else
                DeploymentSteps.Map.Should().NotContainKey(status.ToString());
        }

        DeploymentSteps.TerminalNames.Should().BeEquivalentTo(
            Enum.GetValues<DeploymentStatus>().Where(DeploymentSteps.IsTerminal).Select(s => s.ToString()));
    }

    [Fact]
    public void No_step_index_falls_outside_the_bar()
    {
        foreach (var status in Enum.GetValues<DeploymentStatus>())
            if (DeploymentSteps.IndexOf(status) is { } index)
                index.Should().BeInRange(0, DeploymentSteps.Count - 1, $"{status}");
    }
}
