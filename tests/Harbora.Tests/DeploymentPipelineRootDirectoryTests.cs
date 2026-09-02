using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 1.2 (2026-09 market-gaps round two): deploying an app from a sub-directory of a repository.
///
/// <para>
/// <c>App.BuildContextPath</c> has been in the schema since the initial migration, but
/// <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/> read it with
/// <c>TrimStart('.', '/', '\\')</c> and fell back to the checkout's own root whenever the resolved
/// path did not exist on disk. Both are exactly this codebase's defining defect class: the trim
/// turned <c>"../other"</c> into <c>"other"</c> and quietly built a different directory than the one
/// that was asked for, and the fallback turned a typo'd root directory into a build that ran from the
/// repository root and reported success. These tests prove the fixed behaviour: the fake engine sees
/// the exact sub-directory as the build context, and a root directory that is not actually there fails
/// the deployment by name instead of silently building from somewhere else.
/// </para>
/// </summary>
public class DeploymentPipelineRootDirectoryTests
{
    [Fact]
    public async Task A_root_directory_makes_the_build_context_the_sub_directory_not_the_checkout_root()
    {
        using var h = new PipelineHarness()
            .WithGitSource()
            .WithRootDirectory("services/api")
            .WithDockerfileAt("services/api");
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.BuildRequests.Should().ContainSingle().Which.ContextPath.Should().Be(
            Path.Combine(h.WorkDir, "services", "api"),
            "the build context must be the sub-directory the root directory names, not the checkout root");
    }

    [Fact]
    public async Task No_root_directory_still_builds_from_the_checkout_root_exactly_as_before()
    {
        using var h = new PipelineHarness().WithGitSource().WithDockerfile();
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.BuildRequests.Should().ContainSingle().Which.ContextPath.Should().Be(h.WorkDir);
    }

    [Fact]
    public async Task A_root_directory_that_does_not_exist_in_the_checkout_fails_the_deploy_by_name()
    {
        using var h = new PipelineHarness()
            .WithGitSource()
            .WithRootDirectory("services/does-not-exist")
            .WithDockerfile();   // exists at the checkout root — proves the pipeline never falls back to it
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("services/does-not-exist",
            "the failure must name the root directory that was missing, not just say the build failed");
        h.Docker.BuildRequests.Should().BeEmpty(
            "the old bug built the checkout root instead of failing — no build may run at all here");
    }

    [Fact]
    public async Task A_root_directory_that_traverses_outside_the_checkout_is_refused_by_name_not_silently_rebased()
    {
        using var h = new PipelineHarness()
            .WithGitSource()
            .WithRootDirectory("../elsewhere")
            .WithDockerfile();
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("..",
            "the refusal must name what was wrong (a '..' segment), the way AppRootDirectory.Explain does");
        h.Docker.BuildRequests.Should().BeEmpty(
            "TrimStart('.', '/', '\\') used to turn this into \"elsewhere\" and build it anyway — that must " +
            "no longer happen");
    }
}
