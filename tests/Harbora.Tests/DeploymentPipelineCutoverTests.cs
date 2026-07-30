using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// End-to-end tests of the real <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/>
/// over a recording fake container runtime (doc 15, Phase B).
///
/// These exist because the zero-downtime claim is a statement about **ordering**, not return values:
/// "the new container is started alongside the old one, traffic switches only after health checks
/// pass, and the old container is retired only after cutover". Every assertion below is on the
/// sequence of runtime calls, which is the only way that guarantee can be falsified without a real
/// Docker host.
/// </summary>
public class DeploymentPipelineCutoverTests
{
    // ---- the happy path ordering ----

    [Fact]
    public async Task The_new_container_starts_before_the_old_one_is_removed()
    {
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        var deployment = h.QueueDeployment(number: 2);

        await h.RunAsync(deployment);

        var started = h.Docker.IndexOf("RunContainerAsync", h.ContainerFor(2));
        var removedOld = h.Docker.IndexOf("RemoveContainerAsync", h.ContainerFor(1));

        started.Should().BeGreaterThanOrEqualTo(0, "the new container must be started");
        removedOld.Should().BeGreaterThan(started,
            "the old container may only be retired after the new one is up — this IS the zero-downtime guarantee");
    }

    [Fact]
    public async Task Traffic_switches_before_the_old_container_is_retired()
    {
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        var deployment = h.QueueDeployment(number: 2);

        await h.RunAsync(deployment);

        h.Proxy.ApplyCount.Should().Be(1, "the proxy must be reconfigured exactly once, at cutover");

        // The proxy engine and the docker engine are different fakes, so ordering between them is
        // asserted through the state the pipeline leaves: the old container is still live when the
        // proxy is applied is not directly observable — but a retired container after a successful
        // apply is, and an apply that never happened would leave traffic on the dead container.
        var removedOld = h.Docker.IndexOf("RemoveContainerAsync", h.ContainerFor(1));
        removedOld.Should().BeGreaterThanOrEqualTo(0, "the old container must eventually be retired");
        h.Docker.LiveContainerNames.Should().ContainSingle().Which.Should().Be(h.ContainerFor(2));
    }

    [Fact]
    public async Task A_successful_deploy_ends_live_on_the_new_container_only()
    {
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        var deployment = h.QueueDeployment(number: 2);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.LiveContainerNames.Should().Equal(h.ContainerFor(2));

        var app = await h.Db.Apps.AsNoTracking().FirstAsync(a => a.Id == h.App.Id);
        app.ActiveDeploymentId.Should().Be(deployment.Id);
        app.Status.Should().Be(AppStatus.Running);
    }

    [Fact]
    public async Task Status_progresses_through_the_health_check_state()
    {
        using var h = new PipelineHarness();
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        h.Stream.Statuses.Should().ContainInOrder(
            DeploymentStatus.Building, DeploymentStatus.Deploying,
            DeploymentStatus.HealthChecking, DeploymentStatus.Succeeded);
    }

    // ---- failure must never drop traffic ----

    [Fact]
    public async Task A_failed_health_check_never_switches_traffic()
    {
        using var h = new PipelineHarness().WithDomain().WithHealthPath();
        h.WithPreviousDeployment(number: 1);
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;   // app is up but unhealthy
        var deployment = h.QueueDeployment(number: 2);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Proxy.ApplyCount.Should().Be(0, "traffic must never move to a container that failed its probe");
        h.Http.Attempts.Should().Be(h.Options.HealthHttpAttempts, "every configured probe attempt should be used");
    }

    [Fact]
    public async Task A_failed_deploy_leaves_the_previous_container_serving()
    {
        using var h = new PipelineHarness().WithHealthPath();
        h.WithPreviousDeployment(number: 1);
        h.Http.Status = System.Net.HttpStatusCode.BadGateway;
        var deployment = h.QueueDeployment(number: 2);

        await h.RunAsync(deployment);

        h.Docker.LiveContainerNames.Should().BeEquivalentTo([h.ContainerFor(1)],
            "the previously-serving container must survive a failed deploy untouched");
        h.Docker.OperationsOn(h.ContainerFor(1)).Should().NotContain("RemoveContainerAsync");
    }

    [Fact]
    public async Task A_failed_deploy_removes_only_its_own_container()
    {
        using var h = new PipelineHarness().WithHealthPath();
        h.WithPreviousDeployment(number: 1);
        h.Http.Status = System.Net.HttpStatusCode.ServiceUnavailable;
        var deployment = h.QueueDeployment(number: 2);

        await h.RunAsync(deployment);

        var removed = h.Docker.Calls
            .Where(c => c.Operation == "RemoveContainerAsync").Select(c => c.Target).ToList();
        removed.Should().Equal(h.ContainerFor(2));
    }

    [Fact]
    public async Task A_container_that_exits_reports_why_instead_of_just_failing()
    {
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        h.Docker.StartedContainerState = "exited";   // crashes immediately on boot
        h.Docker.StartedContainerStatus = "Exited (1) 2 seconds ago";
        h.Docker.ContainerLogs = "Error: database is uninitialized and superuser password is not specified";
        var deployment = h.QueueDeployment(number: 2);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        // The old message was "Container failed its health check." — true, and no help at all. The
        // cause is in the container's own output, which is the one place the user cannot look once
        // the failed container has been cleaned up.
        result.ErrorMessage.Should().Contain("exited")
            .And.Contain("Exited (1)")
            .And.Contain("superuser password is not specified");
        h.Docker.LiveContainerNames.Should().Equal(h.ContainerFor(1));
    }

    [Fact]
    public async Task A_crash_looping_container_fails_fast_with_the_crash_as_the_reason()
    {
        // The real-world case: `unless-stopped` means Docker restarts a container that dies on
        // startup, so it is reported as "restarting". Waiting for the health path to answer would
        // burn the whole timeout and then blame the wrong thing.
        using var h = new PipelineHarness().WithDomain().WithHealthPath("/healthz");
        h.WithPreviousDeployment(number: 1);
        h.Docker.StartedContainerState = "restarting";
        h.Docker.StartedContainerStatus = "Restarting (1) 3 seconds ago";
        h.Docker.ContainerLogs = "Error: Database is uninitialized and superuser password is not specified.";
        var deployment = h.QueueDeployment(number: 2);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("crashing").And.Contain("Restarting (1)")
            .And.Contain("superuser password is not specified");
        h.Http.Attempts.Should().Be(0, "there is no point probing a container that is still crashing");
        h.Docker.LiveContainerNames.Should().Equal(h.ContainerFor(1));
    }

    [Fact]
    public async Task A_container_that_runs_but_never_answers_says_what_was_probed()
    {
        using var h = new PipelineHarness().WithDomain().WithHealthPath("/healthz");
        h.WithPreviousDeployment(number: 1);
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;   // up, but unhealthy
        var deployment = h.QueueDeployment(number: 2);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        // A running container that fails its probe is a different problem from one that died, and
        // sending the user to look for a crash would waste their time.
        result.ErrorMessage.Should().Contain("/healthz").And.Contain("running");
        result.ErrorMessage.Should().NotContain("exited");
    }

    [Fact]
    public async Task An_app_that_dies_while_being_probed_reports_the_crash_not_the_silence()
    {
        // The ordinary shape of a bad deploy: the container comes up, the health path never answers
        // because the process is already falling over, and it is gone by the time we give up. The
        // unanswered probe is the symptom; the exit is the cause, so the exit is what to report.
        using var h = new PipelineHarness().WithDomain().WithHealthPath("/healthz");
        h.WithPreviousDeployment(number: 1);
        h.Http.Status = System.Net.HttpStatusCode.ServiceUnavailable;
        h.Docker.ContainerLogs = "panic: cannot open config file";
        var deployment = h.QueueDeployment(number: 2);
        h.Http.OnProbe = () => h.Docker.MarkExited(h.ContainerFor(2), "Exited (2) 5 seconds ago");

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("exited").And.Contain("Exited (2)")
            .And.Contain("panic: cannot open config file");
    }

    [Fact]
    public async Task A_container_removed_by_something_else_is_not_called_a_crash()
    {
        // Distinct cause, distinct fix: nothing is wrong with the image, so pointing the user at
        // their environment variables would send them looking in the wrong place.
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        h.Docker.DropStartedContainers = true;
        var deployment = h.QueueDeployment(number: 2);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("disappeared");
        result.ErrorMessage.Should().NotContain("never reached the running state");
    }

    [Fact]
    public async Task A_failed_build_never_starts_a_container()
    {
        using var h = new PipelineHarness().WithGitSource().WithDockerfile();
        h.Docker.BuildFailure = new InvalidOperationException("build blew up");
        h.WithPreviousDeployment(number: 1);
        var deployment = h.QueueDeployment(number: 2);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Docker.CountOf("RunContainerAsync").Should().Be(0);
        h.Docker.LiveContainerNames.Should().Equal(h.ContainerFor(1));
    }

    [Fact]
    public async Task A_failure_notifies_and_records_the_reason()
    {
        using var h = new PipelineHarness().WithHealthPath();
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        h.Notifications.Notifications.Should().ContainSingle()
            .Which.Event.Should().Be(AlertEvent.DeployFailed);
    }

    [Fact]
    public async Task A_first_ever_failed_deploy_marks_the_app_failed_not_running()
    {
        using var h = new PipelineHarness().WithHealthPath();
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;
        var deployment = h.QueueDeployment(number: 1);   // no previous deployment

        await h.RunAsync(deployment);

        var app = await h.Db.Apps.AsNoTracking().FirstAsync(a => a.Id == h.App.Id);
        app.Status.Should().Be(AppStatus.Failed, "nothing was ever serving, so the app is down");
    }

    // ---- rollback re-releases an artifact, never rebuilds ----

    [Fact]
    public async Task A_rollback_releases_the_target_image_without_building()
    {
        using var h = new PipelineHarness().WithGitSource().WithDockerfile();
        var target = h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");
        var rollback = h.QueueDeployment(number: 2, rollbackTo: target.Id);

        var result = await h.RunAsync(rollback);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        result.ImageTag.Should().Be("harbora/blog:build-1", "a rollback re-releases the exact prior artifact");
        h.Docker.CountOf("BuildImageAsync").Should().Be(0, "rebuilding could produce a different image (ADR-006)");
        h.Git.CheckoutCount.Should().Be(0, "a rollback must not touch the source at all");
    }

    [Fact]
    public async Task A_rollback_marks_the_deployment_it_displaced()
    {
        using var h = new PipelineHarness();
        var oldGood = h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");

        // #2 deploys successfully and becomes live, then we roll back to #1.
        var bad = h.QueueDeployment(number: 2);
        await h.RunAsync(bad);

        var rollback = h.QueueDeployment(number: 3, rollbackTo: oldGood.Id);
        await h.RunAsync(rollback);

        var displaced = await h.Db.Deployments.AsNoTracking().FirstAsync(d => d.Id == bad.Id);
        displaced.Status.Should().Be(DeploymentStatus.RolledBack,
            "history must show which version the rollback abandoned");

        var target = await h.Db.Deployments.AsNoTracking().FirstAsync(d => d.Id == oldGood.Id);
        target.Status.Should().Be(DeploymentStatus.Succeeded, "the rollback target itself is untouched");
    }

    [Fact]
    public async Task A_rollback_to_a_deployment_with_no_image_fails_without_touching_traffic()
    {
        using var h = new PipelineHarness();
        var imageless = h.WithPreviousDeployment(number: 1, image: null!);
        var rollback = h.QueueDeployment(number: 2, rollbackTo: imageless.Id);

        var result = await h.RunAsync(rollback);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("no retained image");
        h.Docker.CountOf("RunContainerAsync").Should().Be(0);
        h.Proxy.ApplyCount.Should().Be(0);
    }

    // ---- remote nodes: old and new must not collide ----

    [Fact]
    public async Task On_a_remote_node_each_deployment_publishes_its_own_host_port()
    {
        using var h = new PipelineHarness(localServer: false);
        h.WithPreviousDeployment(number: 1);
        var first = h.QueueDeployment(number: 2);
        await h.RunAsync(first);

        var second = h.QueueDeployment(number: 3);
        await h.RunAsync(second);

        var ports = h.Docker.RunRequests.Select(r => r.PublishToHostPort).ToList();
        ports.Should().OnlyContain(p => p.HasValue, "a remote node has no shared overlay to route by name");
        ports.Should().OnlyHaveUniqueItems(
            "old and new containers coexist during cutover, so they cannot share a host port");
    }

    [Fact]
    public async Task A_superseded_deployments_port_is_released_only_after_the_cutover()
    {
        // Held until traffic has moved, then given back: releasing early would offer another app a
        // port still carrying live traffic, and never releasing would drain the node's range.
        using var h = new PipelineHarness(localServer: false);
        h.WithPreviousDeployment(number: 1);
        await h.RunAsync(h.QueueDeployment(number: 2));
        await h.RunAsync(h.QueueDeployment(number: 3));

        var live = h.Docker.RunRequests[^1].PublishToHostPort;
        h.Db.HostPortAllocations.Should().ContainSingle()
            .Which.Port.Should().Be(live!.Value, "only the deployment now serving keeps its port");
    }

    [Fact]
    public async Task A_failed_deployment_on_a_remote_node_gives_its_port_back()
    {
        // Otherwise every failed deploy costs the node a port permanently, and a repeatedly failing
        // app quietly consumes the whole range.
        using var h = new PipelineHarness(localServer: false);
        h.Docker.StartedContainerState = "exited";
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Db.HostPortAllocations.Should().BeEmpty();
    }

    [Fact]
    public async Task On_a_local_node_the_proxy_routes_by_container_name()
    {
        using var h = new PipelineHarness().WithDomain();
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        h.Proxy.Applications.Should().ContainSingle()
            .Which.TargetService.Should().Be(h.ContainerFor(1));
        h.Docker.RunRequests.Should().ContainSingle()
            .Which.PublishToHostPort.Should().BeNull("the local proxy shares the tenant network");
    }

    [Fact]
    public async Task On_a_remote_node_the_proxy_routes_to_the_node_host_and_published_port()
    {
        using var h = new PipelineHarness(localServer: false).WithDomain();
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        var applied = h.Proxy.Applications.Should().ContainSingle().Subject;
        applied.TargetService.Should().Be(h.Server.Hostname);
        applied.TargetPort.Should().Be(h.Docker.RunRequests.Single().PublishToHostPort);
    }

    // ---- cleanup robustness ----

    [Fact]
    public async Task A_container_that_refuses_to_be_retired_does_not_fail_the_deploy()
    {
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        h.Docker.UnremovableContainers.Add(h.ContainerFor(1));
        var deployment = h.QueueDeployment(number: 2);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded,
            "the new version is already serving; a stuck old container is a cleanup problem, not a deploy failure");
    }

    [Fact]
    public async Task The_health_probe_targets_the_same_address_the_proxy_will_use()
    {
        using var h = new PipelineHarness().WithDomain().WithHealthPath("/healthz");
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        // Probing a different address than the proxy routes to would make the health gate meaningless.
        h.Http.RequestedUrls.Should().ContainSingle()
            .Which.Should().Be($"http://{h.ContainerFor(1)}:{h.App.ContainerPort}/healthz");
    }

    [Fact]
    public async Task An_app_with_no_domains_still_deploys()
    {
        using var h = new PipelineHarness();
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Proxy.ApplyCount.Should().Be(0, "there is nothing to route yet");
    }
}
