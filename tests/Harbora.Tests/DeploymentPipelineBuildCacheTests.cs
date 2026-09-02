using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Round-two 1.1: build cache between deploys. Every deploy used to rebuild every layer from
/// scratch even when nothing an app depends on had changed, which on a single self-hosted node is
/// the difference between a 40-second deploy and a 4-minute one.
///
/// <para>
/// Proved at the same seam <see cref="BuildArgsTests"/> already uses —
/// <see cref="Fakes.FakeDockerEngine.BuildRequests"/>, what the pipeline actually handed the
/// engine — plus <see cref="Fakes.PipelineHarness.Stream"/> for the log line the requirement is
/// explicit about: a deploy that is fast for an unexplained reason is a mystery, not a feature, so
/// the log has to say when the cache was used and when it was not, and why not, every time.
/// </para>
/// </summary>
public class DeploymentPipelineBuildCacheTests
{
    [Fact]
    public async Task The_first_build_of_an_app_names_no_cache_source_and_says_so()
    {
        using var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository)
            .WithGitSource().WithDockerfile();

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var build = h.Docker.BuildRequests.Should().ContainSingle().Subject;
        build.CacheFrom.Should().BeNullOrEmpty("there is no previous build for this app to reuse layers from");
        build.NoCache.Should().BeFalse();

        h.Stream.Lines.Should().Contain(l =>
            l.Contains("Build cache") && l.Contains("no previous image to cache from"));
    }

    [Fact]
    public async Task A_previous_successful_build_of_the_same_app_is_named_as_the_cache_source()
    {
        using var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository)
            .WithGitSource().WithDockerfile();
        h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");

        var deployment = h.QueueDeployment(number: 2);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var build = h.Docker.BuildRequests.Should().ContainSingle().Subject;
        build.CacheFrom.Should().BeEquivalentTo(["harbora/blog:build-1"]);
        build.NoCache.Should().BeFalse();

        h.Stream.Lines.Should().Contain(l =>
            l.Contains("Build cache") && l.Contains("reusing layers from harbora/blog:build-1"));
    }

    /// <summary>
    /// The retention race the brief calls out by name: image retention (either the pipeline's own
    /// <c>PruneOldImagesAsync</c> after a LATER deploy, or <c>DiskCleanupService</c>'s periodic
    /// sweep) only ever protects the newest N ROLLBACK-ELIGIBLE tags — not necessarily the newest
    /// BUILD tag <see cref="Harbora.Infrastructure.Deployments.DeploymentPlanning.PreviousBuildImage"/>
    /// picks. The image this deploy wants to reuse layers from can already be gone by the time the
    /// build actually starts, and that must fall back to a cold build, never fail the deploy.
    /// </summary>
    [Fact]
    public async Task A_cache_candidate_removed_by_retention_before_the_build_starts_falls_back_to_cold_without_failing()
    {
        using var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository)
            .WithGitSource().WithDockerfile();
        h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");
        // Simulates image retention reclaiming it between "this is the candidate" and "the build
        // actually starts" — the exact race BuildCache's ImageExistsAsync check exists to catch.
        h.Docker.ForgetImage("harbora/blog:build-1");

        var deployment = h.QueueDeployment(number: 2);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded,
            "a cache source that raced retention out from under the build must never fail the deploy");
        var build = h.Docker.BuildRequests.Should().ContainSingle().Subject;
        build.CacheFrom.Should().BeNullOrEmpty();

        h.Stream.Lines.Should().Contain(l =>
            l.Contains("Build cache") && l.Contains("harbora/blog:build-1") && l.Contains("no longer on this node"));
    }

    [Fact]
    public async Task Forcing_a_rebuild_ignores_a_perfectly_usable_previous_image()
    {
        using var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository)
            .WithGitSource().WithDockerfile();
        h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");

        var deployment = h.QueueDeployment(number: 2, forceRebuild: true);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var build = h.Docker.BuildRequests.Should().ContainSingle().Subject;
        build.CacheFrom.Should().BeNullOrEmpty("a forced rebuild must not reuse anything, even a perfectly good image");
        build.NoCache.Should().BeTrue("a forced rebuild must bypass the engine's own layer cache too, not just cache-from");

        h.Stream.Lines.Should().Contain(l =>
            l.Contains("Build cache") && l.Contains("cold build requested"));
    }

    /// <summary>
    /// A Compose stack's per-service builds don't (yet) get a cache source of their own — see
    /// <c>DeploymentPipeline.StartComposeStackAsync</c>'s own comment for why an app-level candidate
    /// would be naming a stranger's layers as often as not — but "no cache" from the deploy UI must
    /// still mean no cache anywhere this deployment builds, not just on the single-container path.
    /// </summary>
    [Fact]
    public async Task Forcing_a_rebuild_also_bypasses_the_cache_for_every_compose_service_build()
    {
        using var h = new Fakes.PipelineHarness()
            .WithComposeFile("""
                services:
                  web:
                    build: .
                    ports:
                      - "8080:8080"
                """)
            .WithDockerfile();

        var deployment = h.QueueDeployment(forceRebuild: true);
        await h.RunAsync(deployment);

        var build = h.Docker.BuildRequests.Should().ContainSingle().Subject;
        build.CacheFrom.Should().BeNullOrEmpty();
        build.NoCache.Should().BeTrue();
    }
}
