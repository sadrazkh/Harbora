using Docker.DotNet.Models;
using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Docker's build API answers 200 OK and lets its JSON message stream end normally even when the
/// build itself failed — a Dockerfile <c>RUN</c> step returning non-zero, for example. The failure
/// arrives as a message *inside* that stream (<c>JSONMessage.Error</c>), never as an HTTP error and
/// never as an exception Docker.DotNet throws on its own, so nothing notices unless
/// <see cref="DockerEngine"/> itself does.
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
    public void A_message_carrying_a_failure_alongside_ordinary_stream_output_still_describes_a_failure()
    {
        // Docker.DotNet.Enhanced 3.131.1 (Docker 29 support) dropped JSONMessage's free-text
        // ErrorMessage property — Error.Message is now the only place a failure's text lives. This
        // pins that DescribesBuildFailure looks at Error.Message even on a message that ALSO carries
        // ordinary Stream text, since BuildImageFromTarAsync's progress handler reads both off the
        // very same message.
        var message = new JSONMessage
        {
            Stream = "some ordinary build output\n",
            Error = new JSONError { Message = "The command '/bin/sh -c npm run build' returned a non-zero code: 1" }
        };

        DockerEngine.DescribesBuildFailure(message).Should().BeTrue();
    }

    [Fact]
    public void A_message_carrying_only_the_structured_Error_field_also_describes_a_failure()
    {
        var message = new JSONMessage { Error = new JSONError { Message = "non-zero code: 1" } };

        DockerEngine.DescribesBuildFailure(message).Should().BeTrue();
    }

    [Fact]
    public void Blank_error_fields_do_not_count_as_a_failure()
    {
        var message = new JSONMessage { Error = new JSONError { Message = "" } };

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
            Error = new JSONError { Message = "The command '/bin/sh -c npm run build' returned a non-zero code: 1" }
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
        var failure = new JSONMessage { Error = new JSONError { Message = "dockerfile parse error on line 1" } };

        var message = DockerEngine.BuildFailureMessage("harbora/app:build-1", null, failure);

        message.Should().Contain("harbora/app:build-1");
        message.Should().Contain("dockerfile parse error on line 1");
        message.Should().NotContain("Step");
    }

    [Fact]
    public void What_the_failing_step_printed_comes_first_so_the_last_line_of_the_log_is_the_reason()
    {
        const string sentence = "The build finished without producing an index.html, so this is not a site.";
        var shell = "found=\"\"; " + new string('x', 900);
        var failure = new JSONMessage
        {
            Error = new JSONError { Message = $"The command '/bin/sh -c {shell}' returned a non-zero code: 1" }
        };

        var message = DockerEngine.BuildFailureMessage("harbora/site:build-1", $"Step 8/14 : RUN {shell}", failure, sentence);

        message.Should().StartWith(sentence);
        message.Should().Contain("returned a non-zero code: 1");
        message.Should().Contain("Step 8/14");
        message.Should().Contain("harbora/site:build-1");
        message.Length.Should().BeLessThan(500, "the whole command twice would push the reason out of a clipped line");
    }

    [Fact]
    public void A_step_that_printed_nothing_keeps_the_message_it_always_had()
    {
        var failure = new JSONMessage { Error = new JSONError { Message = "The command '/bin/sh -c false' returned a non-zero code: 1" } };

        DockerEngine.BuildFailureMessage("harbora/app:build-1", "Step 2/3 : RUN false", failure)
            .Should().Be("Build of harbora/app:build-1 failed at Step 2/3 : RUN false: The command returned a non-zero code: 1");
    }

    [Fact]
    public void The_daemons_text_about_a_cache_it_could_not_restore_survives_the_wording()
    {
        // BuildCache keys on this text to decide whether to build again without the cache.
        var failure = new JSONMessage { Error = new JSONError { Message = "failed to restore cached image from \"sha256:a\" to sha256:b" } };

        Harbora.Infrastructure.Deployments.BuildCache.IsCacheRestoreFailure(
            DockerEngine.BuildFailureMessage("harbora/app:build-1", "Step 2/3 : COPY . .", failure, "something it printed"))
            .Should().BeTrue();
    }

    [Fact]
    public void A_step_keeps_only_what_it_printed_itself()
    {
        var output = new BuildStepOutput();

        output.Note("Step 5/14 : RUN npm install");
        output.Note(" ---> Running in 1a2b3c");
        output.Note("added 214 packages in 3s\n");
        output.Note("Removing intermediate container 1a2b3c");
        output.Note(" ---> 9f8e7d");
        output.Said.Should().Be("added 214 packages in 3s");

        output.Note("Step 6/14 : RUN npm run build");
        output.Said.Should().BeNull("nothing the last step said is about this one");

        output.Note(" ---> Running in 4d5e6f");
        output.Note("\n> site@1.0.0 build\n> vite build\n\n");
        output.Note("error during build:\n");
        output.Note("Could not resolve entry module \"index.html\".\n");
        output.Said.Should().Be("error during build: | Could not resolve entry module \"index.html\".");
    }

    [Fact]
    public void What_a_step_wrote_to_stderr_comes_through_without_its_colour_codes()
    {
        // As the real daemon sent it: the classic builder wraps stderr, and the closing code can arrive
        // on a line of its own.
        var output = new BuildStepOutput();

        output.Note("Step 5/11 : RUN npm run build");
        output.Note("\u001b[91mproof: this build fails on purpose\n\u001b[0m");

        output.Said.Should().Be("proof: this build fails on purpose");
    }

    [Fact]
    public void Npms_advice_to_upgrade_does_not_push_the_reason_out()
    {
        var output = new BuildStepOutput();

        output.Note("Step 4/11 : RUN npm install");
        output.Note("npm error 404 Not Found - GET https://registry.npmjs.org/no-such-package\n");
        output.Note("\u001b[91mnpm notice\nnpm notice New major version of npm available! 10.8.2 -> 12.2.1\n" +
                    "npm notice Changelog: https://github.com/npm/cli/releases/tag/v12.2.1\n" +
                    "npm notice To update run: npm install -g npm@12.2.1\nnpm notice\n\u001b[0m");

        output.Said.Should().Be("npm error 404 Not Found - GET https://registry.npmjs.org/no-such-package");
    }

    [Fact]
    public void A_step_that_prints_a_lot_is_kept_short_from_its_last_lines()
    {
        var output = new BuildStepOutput();

        output.Note("Step 6/14 : RUN npm run build");
        for (var i = 1; i <= 20; i++) output.Note($"line {i}");
        output.Note(new string('y', 2000));

        output.Said.Should().StartWith("line 17 | line 18 | line 19 | line 20 | yyy");
        output.Said!.Length.Should().BeLessThan(400);
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
