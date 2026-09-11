using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P2 (round-3 start-command plan): <c>App.StartCommand</c> exists precisely so a Worker built
/// without a Dockerfile has a way to correct a wrong buildpack guess. These are the proof the
/// pipeline actually reads it — <see cref="DeploymentPipelineReplicaTests"/> is the template this
/// follows: the row means nothing until the engine is shown reading it, and "nothing set" must stay
/// byte-for-byte the deploy every existing app without this column already gets.
/// </summary>
public class DeploymentPipelineStartCommandTests
{
    [Fact]
    public async Task An_app_with_a_start_command_set_deploys_with_it_shaped_as_a_shell_invocation()
    {
        using var h = new PipelineHarness().WithStartCommand("python bot.py");
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var request = h.Docker.RunRequests.Should().ContainSingle().Subject;
        // Shell form, not exec form: a person typed this string, and splitting it into argv ourselves
        // would mean reinventing shell quoting badly for the first value with a space or a pipe in it.
        request.Command.Should().Equal("sh", "-c", "python bot.py");
    }

    [Fact]
    public async Task An_app_with_no_start_command_deploys_with_a_null_command_exactly_as_today()
    {
        // No WithStartCommand call at all — the harness's own default, and therefore the shape every
        // pre-existing test in this suite already deploys under.
        using var h = new PipelineHarness();
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var request = h.Docker.RunRequests.Should().ContainSingle().Subject;
        request.Command.Should().BeNull("an app that never touched this column must deploy exactly as it always has");
    }

    [Fact]
    public async Task Whitespace_only_start_command_is_stripped_before_it_ever_reaches_the_container_spec()
    {
        // A whitespace-only value would otherwise become `sh -c " "` — a command of one space rather
        // than the unset value the operator actually meant. StartCommandFor's own IsNullOrWhiteSpace
        // check is what this proves, end to end through the pipeline rather than in isolation.
        using var h = new PipelineHarness().WithStartCommand("   ");
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        var request = h.Docker.RunRequests.Should().ContainSingle().Subject;
        request.Command.Should().BeNull();
    }
}
