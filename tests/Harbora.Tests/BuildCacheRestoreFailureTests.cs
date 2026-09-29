using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="BuildCache.IsCacheRestoreFailure(Exception?)"/> — the single decision behind "repeat this
/// build without its cache source". It keys on text because text is all the daemon gives: the same
/// error reached the panel as a stream's error message (DriveUnion #44) and as an ordinary progress
/// line (Loomi #9, #12), and over a remote agent the exception type does not survive HTTP at all.
/// </summary>
public class BuildCacheRestoreFailureTests
{
    /// <summary>The daemon's own words, exactly as the live server recorded them.</summary>
    private const string DaemonText =
        "failed to restore cached image from \"sha256:1d3c5e7a\" to sha256:9f8a7b21: " +
        "failed to create cache image: layer does not exist";

    [Fact]
    public void The_daemons_text_on_its_own_is_recognised()
    {
        BuildCache.IsCacheRestoreFailure(DaemonText).Should().BeTrue();
    }

    [Fact]
    public void The_daemons_text_inside_a_build_failure_message_is_recognised()
    {
        // DriveUnion #44's shape: the daemon sent it as the stream's error message, and
        // DockerEngine.BuildFailureMessage put the image and the step in front of it.
        var ex = new DockerBuildException(
            $"Build of harbora/driveunion:build-44 failed at Step 19/23 : COPY . .: {DaemonText}");

        BuildCache.IsCacheRestoreFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void The_daemons_text_inside_the_no_image_backstops_message_is_recognised()
    {
        // Loomi #12's shape: the text was an ordinary progress line, so the only thing that reports
        // the failure is the check that the image exists — and it quotes the last lines the daemon said.
        var ex = new DockerBuildException(
            "Build of harbora/loomi:build-12 ended without an error, but the image does not exist. " +
            "The last step reported was Step 19/23 : COPY . .. " +
            $"Last output: Step 19/23 : COPY . . | {DaemonText}");

        BuildCache.IsCacheRestoreFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void An_error_that_crossed_HTTP_from_a_remote_agent_is_recognised_by_its_message_not_its_type()
    {
        // RemoteDockerEngine's caller sees an HttpRequestException, never the agent's own
        // DockerBuildException — the type is gone and only the words remain.
        var ex = new HttpRequestException($"The agent's build failed. {DaemonText}");

        BuildCache.IsCacheRestoreFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void The_text_is_found_when_it_is_only_on_an_inner_exception()
    {
        var ex = new InvalidOperationException(
            "The build did not complete, see inner exception for details.",
            new HttpRequestException("agent said: " + DaemonText));

        BuildCache.IsCacheRestoreFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void The_text_is_found_inside_an_aggregate()
    {
        var ex = new AggregateException(
            new InvalidOperationException("unrelated"), new DockerBuildException(DaemonText));

        BuildCache.IsCacheRestoreFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void The_text_survives_being_flattened_by_FailureText()
    {
        // What a deployment's stored error looks like after the pipeline has walked the chain.
        var ex = new InvalidOperationException(
            "Build failed",
            new DockerBuildException($"Build of harbora/blog:build-3 failed at Step 4/9 : RUN true: {DaemonText}"));

        var described = FailureText.Describe(ex);

        described.Should().Contain("→", "this is the joined form FailureText produces");
        BuildCache.IsCacheRestoreFailure(described).Should().BeTrue();
    }

    [Fact]
    public void The_text_is_recognised_whatever_its_case()
    {
        BuildCache.IsCacheRestoreFailure(DaemonText.ToUpperInvariant()).Should().BeTrue();
    }

    [Theory]
    [InlineData("The command '/bin/sh -c npm ci' returned a non-zero code: 1")]
    [InlineData("Build of harbora/blog:build-2 failed at Step 6/23 : RUN npm ci: npm ERR! network timeout")]
    [InlineData("Docker API responded with status code=NotFound, response={\"message\":\"No such image: harbora/blog:build-2\"}")]
    [InlineData("failed to create cache image: layer does not exist")]
    [InlineData("npm cache clean --force failed to restore permissions")]
    [InlineData("the daemon could not restore the volume")]
    [InlineData("")]
    public void Any_other_failure_is_not_a_cache_restore_failure(string text)
    {
        BuildCache.IsCacheRestoreFailure(text).Should().BeFalse();
        BuildCache.IsCacheRestoreFailure(new DockerBuildException(text)).Should().BeFalse();
    }

    [Fact]
    public void Nothing_at_all_is_not_a_cache_restore_failure()
    {
        BuildCache.IsCacheRestoreFailure((Exception?)null).Should().BeFalse();
        BuildCache.IsCacheRestoreFailure((string?)null).Should().BeFalse();
    }
}
