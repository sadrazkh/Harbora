using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The build cache's cache-restore step fails intermittently on Docker 29 (the classic builder is
/// deprecated there). Three live deployments died on it, each followed by a retry that passed — and
/// the owner keeps the feature, so its failure has to cost one cheap retry, not a failed deploy.
///
/// <para>
/// Every scenario here is the pipeline talking to <see cref="Fakes.FakeDockerEngine"/>, so what is
/// proven is the pipeline's decision — retry or not, with what — and never that a real daemon then
/// restores or fails to restore anything. The text that decision keys on comes from the live
/// server's own deployment history (DriveUnion #44, Loomi #9 and #12).
/// </para>
/// </summary>
public class DeploymentPipelineBuildRetryTests
{
    /// <summary>The daemon's message as the live server recorded it for DriveUnion #44.</summary>
    private const string CacheRestoreError =
        "failed to restore cached image from \"sha256:1d3c5e\" to sha256:9f8a7b: " +
        "failed to create cache image: layer does not exist";

    private static Fakes.PipelineHarness Harness(bool withPreviousBuild)
    {
        var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository)
            .WithGitSource().WithDockerfile();
        if (withPreviousBuild) h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");
        return h;
    }

    [Fact]
    public async Task A_cache_restore_failure_with_a_cache_in_use_is_retried_once_without_the_cache()
    {
        using var h = Harness(withPreviousBuild: true);
        h.Docker.ScriptBuilds(
            new DockerBuildException($"Build of harbora/blog:build-2 failed at Step 19/23 : COPY . .: {CacheRestoreError}"),
            null);

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.BuildRequests.Should().HaveCount(2);
        h.Docker.BuildRequests[0].CacheFrom.Should().Equal("harbora/blog:build-1");
        h.Docker.BuildRequests[1].CacheFrom.Should().BeNull("the retry exists to build without the cache source");
        h.Docker.BuildRequests[1].ImageTag.Should().Be(h.Docker.BuildRequests[0].ImageTag);

        h.Stream.Lines.Should().ContainSingle(l =>
            l.Contains("could not restore the cached image") && l.Contains("without it"));
    }

    [Fact]
    public async Task The_retry_changes_only_the_cache_source_never_the_rest_of_the_build()
    {
        using var h = Harness(withPreviousBuild: true);
        h.Docker.ScriptBuilds(new DockerBuildException(CacheRestoreError), null);

        await h.RunAsync(h.QueueDeployment(number: 2));

        var (first, second) = (h.Docker.BuildRequests[0], h.Docker.BuildRequests[1]);
        second.Should().BeEquivalentTo(first, o => o.Excluding(r => r.CacheFrom));
    }

    [Fact]
    public async Task A_cache_restore_failure_with_no_cache_in_use_is_not_retried()
    {
        // First build of the app: there is no cache source, so there is nothing a retry could drop.
        using var h = Harness(withPreviousBuild: false);
        h.Docker.BuildFailure = new DockerBuildException(CacheRestoreError);

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Docker.BuildRequests.Should().ContainSingle();
        h.Stream.Lines.Should().NotContain(l => l.Contains("without it"));
    }

    [Fact]
    public async Task A_forced_cold_rebuild_is_not_retried_either()
    {
        using var h = Harness(withPreviousBuild: true);
        h.Docker.BuildFailure = new DockerBuildException(CacheRestoreError);

        var result = await h.RunAsync(h.QueueDeployment(number: 2, forceRebuild: true));

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Docker.BuildRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task An_ordinary_build_failure_is_not_retried_even_when_a_cache_was_in_use()
    {
        // A genuine RUN npm ci error must fail once, not twice at double the wait.
        using var h = Harness(withPreviousBuild: true);
        h.Docker.BuildFailure = new DockerBuildException(
            "Build of harbora/blog:build-2 failed at Step 6/23 : RUN npm ci: " +
            "The command '/bin/sh -c npm ci' returned a non-zero code: 1");

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Docker.BuildRequests.Should().ContainSingle();
        result.ErrorMessage.Should().Contain("npm ci");
        h.Stream.Lines.Should().NotContain(l => l.Contains("without it"));
    }

    [Fact]
    public async Task A_retry_that_also_fails_fails_the_deployment_with_the_retrys_error_and_builds_no_third_time()
    {
        using var h = Harness(withPreviousBuild: true);
        h.Docker.ScriptBuilds(
            new DockerBuildException(CacheRestoreError),
            new DockerBuildException("Build of harbora/blog:build-2 failed at Step 6/23 : RUN npm ci: returned a non-zero code: 1"));

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Docker.BuildRequests.Should().HaveCount(2, "one build, one retry, and no more");
        result.ErrorMessage.Should().Contain("npm ci");
        result.ErrorMessage.Should().NotContain("failed to restore cached image",
            "the deployment reports why the retry failed, not the failure that led to it");
        h.Stream.Lines.Where(l => l.Contains("Deployment failed")).Should().ContainSingle();
    }

    [Fact]
    public async Task A_retry_that_fails_the_same_way_again_is_still_only_one_retry()
    {
        using var h = Harness(withPreviousBuild: true);
        h.Docker.BuildFailure = new DockerBuildException(CacheRestoreError);

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Docker.BuildRequests.Should().HaveCount(2);
        h.Stream.Lines.Should().ContainSingle(l => l.Contains("without it"));
    }

    [Fact]
    public async Task A_cache_restore_failure_that_came_back_from_a_remote_agent_is_retried_too()
    {
        // Over HTTP the exception type does not survive: RemoteDockerEngine's caller sees an
        // HttpRequestException (or whatever the transport raises), so only its text can say what
        // went wrong.
        using var h = Harness(withPreviousBuild: true);
        h.Docker.ScriptBuilds(
            new HttpRequestException($"The agent's build failed: {CacheRestoreError}"),
            null);

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.BuildRequests.Should().HaveCount(2);
        h.Docker.BuildRequests[1].CacheFrom.Should().BeNull();
    }
}
