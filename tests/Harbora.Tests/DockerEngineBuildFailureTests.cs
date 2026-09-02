using Docker.DotNet.Models;
using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Docker's build API answers 200 OK and lets its JSON message stream end normally even when the
/// build itself failed — a Dockerfile <c>RUN</c> step returning non-zero, for example. The failure
/// arrives as a message *inside* that stream (<c>JSONMessage.Error</c> / <c>ErrorMessage</c>), never
/// as an HTTP error and never as an exception Docker.DotNet throws on its own, so nothing notices
/// unless <see cref="DockerEngine"/> itself does.
///
/// <para>
/// Before this, <see cref="DockerEngine.BuildImageFromTarAsync"/> returned the requested image tag
/// regardless of what the stream said. A real deploy hit exactly this: the log's last line was
/// "Step 6/23 : RUN npm run build" and then nothing, the pipeline moved on believing the image
/// existed, and the failure surfaced two steps later as a misleading
/// <c>Docker API responded with status code=NotFound ... No such image</c> from the run step instead
/// of the Dockerfile line that actually broke.
/// </para>
///
/// <para>
/// Constructing a real <see cref="DockerEngine"/> needs a Docker daemon behind <c>IDockerClient</c>,
/// which this suite does not have (see <see cref="DockerEngineInspectMappingTests"/>); <c>JSONMessage</c>
/// and <c>JSONError</c> are plain settable POCOs, though, so the failure-detection decision — pulled
/// out as three internal statics — is reachable and tested directly here, with no daemon involved.
/// </para>
/// </summary>
public class DockerEngineBuildFailureTests
{
    [Fact]
    public void A_message_with_no_error_at_all_does_not_describe_a_failure()
    {
        var message = new JSONMessage { Stream = "Step 3/10 : COPY . .\n" };

        DockerEngine.DescribesBuildFailure(message).Should().BeFalse();
    }

    [Fact]
    public void A_message_carrying_ErrorMessage_describes_a_failure()
    {
        var message = new JSONMessage
        {
            ErrorMessage = "The command '/bin/sh -c npm run build' returned a non-zero code: 1"
        };

        DockerEngine.DescribesBuildFailure(message).Should().BeTrue();
    }

    [Fact]
    public void A_message_carrying_only_the_structured_Error_field_also_describes_a_failure()
    {
        // Docker.DotNet exposes the same failure two ways depending on daemon version; both have to
        // be caught, or a daemon that only fills in one of them would "build successfully".
        var message = new JSONMessage { Error = new JSONError { Message = "non-zero code: 1" } };

        DockerEngine.DescribesBuildFailure(message).Should().BeTrue();
    }

    [Fact]
    public void Blank_error_fields_do_not_count_as_a_failure()
    {
        var message = new JSONMessage { ErrorMessage = "   ", Error = new JSONError { Message = "" } };

        DockerEngine.DescribesBuildFailure(message).Should().BeFalse();
    }

    [Theory]
    [InlineData("Step 6/23 : RUN npm run build")]
    [InlineData("  Step 6/23 : RUN npm run build  ")]
    public void A_dockerfile_step_line_is_recognised_as_the_current_step(string line)
    {
        DockerEngine.IsStepLine(line).Should().BeTrue();
    }

    [Fact]
    public void An_ordinary_output_line_is_not_mistaken_for_a_step_line()
    {
        DockerEngine.IsStepLine("added 214 packages in 3s").Should().BeFalse();
    }

    [Fact]
    public void The_failure_message_names_the_failing_step_and_quotes_the_daemons_own_detail()
    {
        // The exact regression: the deployment log's last line was "Step 6/23 : RUN npm run build"
        // and then nothing — this is what should reach the deployment's ErrorMessage instead of the
        // "No such image" the run step reports two steps later.
        var failure = new JSONMessage
        {
            ErrorMessage = "The command '/bin/sh -c npm run build' returned a non-zero code: 1"
        };

        var message = DockerEngine.BuildFailureMessage(
            "harbora/driveunion:build-2", "Step 6/23 : RUN npm run build", failure);

        message.Should().Contain("Step 6/23 : RUN npm run build");
        message.Should().Contain("harbora/driveunion:build-2");
        message.Should().Contain("returned a non-zero code: 1");
    }

    [Fact]
    public void A_failure_with_no_step_seen_yet_still_names_the_image_and_the_daemons_detail()
    {
        // A build can fail before any "Step N/M" line is ever printed (an invalid Dockerfile, a bad
        // build arg) — the message must still say something useful rather than a null step.
        var failure = new JSONMessage { ErrorMessage = "dockerfile parse error on line 1" };

        var message = DockerEngine.BuildFailureMessage("harbora/app:build-1", null, failure);

        message.Should().Contain("harbora/app:build-1");
        message.Should().Contain("dockerfile parse error on line 1");
        message.Should().NotContain("Step");
    }

    // ---- BuildParameters (round-two 1.1: build cache between deploys) ----
    //
    // What this engine actually asks Docker.DotNet for — the seam DeploymentPipeline's own
    // BuildCache decision reaches once it crosses into the real (non-fake) engine. Same reasoning as
    // the tests above: a real DockerEngine needs a daemon this suite does not have, but
    // ImageBuildParameters is a plain settable POCO, so this is reachable with none.

    [Fact]
    public void A_named_cache_source_reaches_the_classic_builders_CacheFrom_parameter()
    {
        var parameters = DockerEngine.BuildParameters(
            "Dockerfile", "harbora/blog:build-2", new Dictionary<string, string>(),
            cacheFrom: ["harbora/blog:build-1"], noCache: false);

        parameters.CacheFrom.Should().Equal("harbora/blog:build-1");
        parameters.NoCache.Should().BeFalse();
    }

    [Fact]
    public void No_cache_source_leaves_CacheFrom_null_rather_than_an_empty_list()
    {
        var parameters = DockerEngine.BuildParameters(
            "Dockerfile", "harbora/blog:build-1", new Dictionary<string, string>(),
            cacheFrom: null, noCache: false);

        parameters.CacheFrom.Should().BeNull();
    }

    [Fact]
    public void Forcing_a_rebuild_sets_NoCache_regardless_of_any_cache_source()
    {
        var parameters = DockerEngine.BuildParameters(
            "Dockerfile", "harbora/blog:build-2", new Dictionary<string, string>(),
            cacheFrom: null, noCache: true);

        parameters.NoCache.Should().BeTrue();
    }
}
