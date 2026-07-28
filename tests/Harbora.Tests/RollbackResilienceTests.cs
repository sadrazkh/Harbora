using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Phase C: pruning runs end-to-end through the real pipeline, and a rollback whose artifact is
/// gone fails up front with an explanation instead of part-way through a deploy.
/// </summary>
public class RollbackResilienceTests
{
    // ---- pruning through the real pipeline ----

    [Fact]
    public async Task A_successful_deploy_prunes_images_outside_the_retention_window()
    {
        using var h = new PipelineHarness().WithGitSource().WithDockerfile();
        h.Options.ImageRetentionCount = 2;
        h.Docker.SeedImage("harbora/blog:build-1", "harbora/blog:build-2", "harbora/blog:build-3");
        h.SeedSucceededDeployment(1, "harbora/blog:build-1");
        h.SeedSucceededDeployment(2, "harbora/blog:build-2");
        h.WithPreviousDeployment(number: 3, image: "harbora/blog:build-3");

        await h.RunAsync(h.QueueDeployment(number: 4));

        h.Docker.StoredImageTags.Should().BeEquivalentTo(
            ["harbora/blog:build-3", "harbora/blog:build-4"],
            "the newest two rollback targets survive, everything older is reclaimed");
    }

    [Fact]
    public async Task Pruning_never_removes_the_image_that_is_serving_traffic()
    {
        using var h = new PipelineHarness().WithGitSource().WithDockerfile();
        h.Options.ImageRetentionCount = 1;
        h.Docker.SeedImage("harbora/blog:build-1");
        h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");

        await h.RunAsync(h.QueueDeployment(number: 2));

        h.Docker.StoredImageTags.Should().Contain("harbora/blog:build-2");
    }

    [Fact]
    public async Task Retention_can_be_disabled()
    {
        using var h = new PipelineHarness().WithGitSource().WithDockerfile();
        h.Options.ImageRetentionCount = 0;
        h.Docker.SeedImage("harbora/blog:build-1", "harbora/blog:build-2");
        h.SeedSucceededDeployment(1, "harbora/blog:build-1");
        h.WithPreviousDeployment(number: 2, image: "harbora/blog:build-2");

        await h.RunAsync(h.QueueDeployment(number: 3));

        h.Docker.CountOf("RemoveImageAsync").Should().Be(0);
        h.Docker.StoredImageTags.Should().HaveCount(3);
    }

    [Fact]
    public async Task Pruning_happens_only_after_the_deployment_has_succeeded()
    {
        using var h = new PipelineHarness().WithGitSource().WithDockerfile();
        h.Options.ImageRetentionCount = 1;
        h.Docker.SeedImage("harbora/blog:build-1");
        h.SeedSucceededDeployment(1, "harbora/blog:build-1");
        h.WithPreviousDeployment(number: 2, image: "harbora/blog:build-2");

        await h.RunAsync(h.QueueDeployment(number: 3));

        // Housekeeping must not race the cutover: the old container is retired before any image goes.
        var retired = h.Docker.IndexOf("RemoveContainerAsync", h.ContainerFor(2));
        var pruned = h.Docker.IndexOf("RemoveImageAsync");
        pruned.Should().BeGreaterThan(retired);
    }

    [Fact]
    public async Task A_failed_deploy_prunes_nothing()
    {
        using var h = new PipelineHarness().WithHealthPath();
        h.Options.ImageRetentionCount = 1;
        h.Docker.SeedImage("harbora/blog:build-1", "harbora/blog:build-2");
        h.SeedSucceededDeployment(1, "harbora/blog:build-1");
        h.WithPreviousDeployment(number: 2, image: "harbora/blog:build-2");
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;

        await h.RunAsync(h.QueueDeployment(number: 3));

        h.Docker.CountOf("RemoveImageAsync").Should().Be(0,
            "a failed deploy has not proven anything works — it must not also destroy rollback targets");
    }

    [Fact]
    public async Task An_image_that_cannot_be_deleted_does_not_fail_the_deploy()
    {
        using var h = new PipelineHarness().WithGitSource().WithDockerfile();
        h.Options.ImageRetentionCount = 1;
        h.Docker.SeedImage("harbora/blog:build-1");
        h.SeedSucceededDeployment(1, "harbora/blog:build-1");
        h.Docker.UndeletableImages.Add("harbora/blog:build-1");
        h.WithPreviousDeployment(number: 2, image: "harbora/blog:build-2");

        var result = await h.RunAsync(h.QueueDeployment(number: 3));

        result.Status.Should().Be(DeploymentStatus.Succeeded,
            "cleanup is housekeeping; it must never turn a live, working deployment into a failure");
    }

    // ---- rollback with a missing artifact ----

    [Fact]
    public async Task A_rollback_whose_image_was_pruned_fails_before_touching_anything()
    {
        using var h = new PipelineHarness();
        var target = h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");
        // The deployment row still records the image, but the artifact is gone from the node.
        h.Docker.ForgetImage("harbora/blog:build-1");
        var rollback = h.QueueDeployment(number: 2, rollbackTo: target.Id);

        var result = await h.RunAsync(rollback);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("no longer on this server");
        h.Docker.CountOf("RunContainerAsync").Should().Be(0, "nothing should start when the artifact is gone");
        h.Proxy.ApplyCount.Should().Be(0);
        h.Docker.LiveContainerNames.Should().BeEquivalentTo([h.ContainerFor(1)], "the live version is untouched");
    }

    [Fact]
    public async Task A_rollback_whose_image_is_present_proceeds()
    {
        using var h = new PipelineHarness();
        var target = h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");

        var result = await h.RunAsync(h.QueueDeployment(number: 2, rollbackTo: target.Id));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        result.ImageTag.Should().Be("harbora/blog:build-1");
    }

    // ---- pre-flight planner (drives the confirmation screen) ----

    private static RollbackPlanner PlannerFor(PipelineHarness h) =>
        new(h.Db, new SingleEngine(h.Docker));

    private sealed class SingleEngine(FakeDockerEngine engine) : IServerEngineFactory
    {
        public IDockerEngine Local => engine;
        public Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct) => Task.FromResult<IDockerEngine>(engine);
    }

    [Fact]
    public async Task The_planner_reports_what_would_be_restored()
    {
        using var h = new PipelineHarness();
        var target = h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");
        target.CommitSha = "abc1234def";
        target.CommitMessage = "fix the checkout bug";
        target.CommitAuthor = "tester";
        await h.Db.SaveChangesAsync();
        h.Docker.SeedImage("harbora/blog:build-1");
        await h.RunAsync(h.QueueDeployment(number: 2));   // #2 becomes live

        var plan = await PlannerFor(h).PrepareAsync(h.App.Id, target.Id, default);

        plan.CanRollback.Should().BeTrue();
        plan.TargetNumber.Should().Be(1);
        plan.ImageTag.Should().Be("harbora/blog:build-1");
        plan.CommitSha.Should().Be("abc1234def");
        plan.CommitMessage.Should().Be("fix the checkout bug");
        plan.CurrentNumber.Should().Be(2, "the user needs to see what they are moving away from");
    }

    [Fact]
    public async Task The_planner_blocks_a_rollback_whose_image_was_pruned()
    {
        using var h = new PipelineHarness();
        var target = h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");
        await h.RunAsync(h.QueueDeployment(number: 2));
        h.Docker.ForgetImage("harbora/blog:build-1");   // reclaimed after #2 went live

        var plan = await PlannerFor(h).PrepareAsync(h.App.Id, target.Id, default);

        plan.CanRollback.Should().BeFalse();
        plan.Reason.Should().Contain("no longer on the server");
        plan.Reason.Should().Contain("from source", "the user needs to know what to do instead");
    }

    [Fact]
    public async Task The_planner_blocks_rolling_back_to_the_live_version()
    {
        using var h = new PipelineHarness();
        var live = h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");
        h.Docker.SeedImage("harbora/blog:build-1");

        var plan = await PlannerFor(h).PrepareAsync(h.App.Id, live.Id, default);

        plan.CanRollback.Should().BeFalse();
        plan.Reason.Should().Contain("already the live version");
    }

    [Fact]
    public async Task The_planner_blocks_a_deployment_that_never_succeeded()
    {
        using var h = new PipelineHarness();
        var failed = h.SeedSucceededDeployment(1, "harbora/blog:build-1");
        failed.Status = DeploymentStatus.Failed;
        await h.Db.SaveChangesAsync();
        h.Docker.SeedImage("harbora/blog:build-1");

        var plan = await PlannerFor(h).PrepareAsync(h.App.Id, failed.Id, default);

        plan.CanRollback.Should().BeFalse();
        plan.Reason.Should().Contain("never succeeded");
    }

    [Fact]
    public async Task The_planner_blocks_a_deployment_with_no_retained_image()
    {
        using var h = new PipelineHarness();
        var imageless = h.SeedSucceededDeployment(1, image: null);

        var plan = await PlannerFor(h).PrepareAsync(h.App.Id, imageless.Id, default);

        plan.CanRollback.Should().BeFalse();
        plan.Reason.Should().Contain("no retained image");
    }

    [Fact]
    public async Task The_planner_refuses_a_deployment_belonging_to_another_app()
    {
        using var h = new PipelineHarness();
        var mine = h.SeedSucceededDeployment(1, "harbora/blog:build-1");

        var plan = await PlannerFor(h).PrepareAsync(Guid.NewGuid(), mine.Id, default);

        plan.CanRollback.Should().BeFalse("cross-app rollback must not be possible");
    }
}
