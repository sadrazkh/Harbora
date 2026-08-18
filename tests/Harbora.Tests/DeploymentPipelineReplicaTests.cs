using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>App.DesiredReplicas</c> has been stored, cloned and displayed since the initial schema, and
/// nothing under <c>Harbora.Infrastructure.Deployments</c> or <c>.Docker</c> ever read it: a customer
/// configuring 3 replicas got exactly one container, same as one. These tests are the proof the engine
/// now actually reads the column — <see cref="DeploymentPipelineCutoverTests"/> covers the
/// single-container path this one specialises, and every one of those tests still passes unmodified,
/// which is itself part of the guarantee: nothing changes for an app that never touched replicas.
/// </summary>
public class DeploymentPipelineReplicaTests
{
    // ---- the headline claim ----

    [Fact]
    public async Task Three_replicas_means_three_containers_actually_run()
    {
        using var h = new PipelineHarness().WithReplicas(3);
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.LiveContainerNames.Should().BeEquivalentTo(
        [
            h.ReplicaContainerFor(1, 1),
            h.ReplicaContainerFor(1, 2),
            h.ReplicaContainerFor(1, 3)
        ], "a three-replica app must have three containers running, not one");
    }

    [Fact]
    public async Task Replica_one_keeps_the_exact_name_a_single_container_app_has_always_used()
    {
        // The naming scheme already in place is workspace-qualified
        // (harbora-{workspace}-{slug}-{number}); replicas extend it rather than replacing it, so an
        // app that has never touched replicas is completely unaffected.
        using var h = new PipelineHarness().WithReplicas(3);
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        h.Docker.LiveContainerNames.Should().Contain(h.ContainerFor(1),
            "replica 1 must answer to the exact name ContainerName() has always produced");
    }

    [Fact]
    public async Task An_app_with_no_replicas_configured_still_gets_exactly_one_container()
    {
        // DesiredReplicas defaults to 1 — the overwhelming majority of apps today. Zero behaviour
        // change for that case is the whole safety argument for this feature.
        using var h = new PipelineHarness();
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        h.Docker.LiveContainerNames.Should().Equal(h.ContainerFor(1));
    }

    // ---- Traefik routes across every replica ----

    [Fact]
    public async Task Traefik_gets_a_server_for_every_replica_not_just_the_first()
    {
        using var h = new PipelineHarness().WithReplicas(3).WithDomain();
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        var route = h.Db.Routes.IgnoreQueryFilters().AsNoTracking().Single();
        var upstreams = RouteUpstreams.All(route);

        upstreams.Should().HaveCount(3, "the loadBalancer must carry a server per replica");
        upstreams.Select(u => u.Host).Should().BeEquivalentTo(
        [
            h.ReplicaContainerFor(1, 1), h.ReplicaContainerFor(1, 2), h.ReplicaContainerFor(1, 3)
        ]);
    }

    [Fact]
    public async Task A_single_replica_route_carries_no_extra_upstreams()
    {
        // The exact single-target shape RoutesController, AdminerService and the designer already
        // read — proof that a one-replica deploy renders identically to before this feature existed.
        using var h = new PipelineHarness().WithDomain();
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        var route = h.Db.Routes.IgnoreQueryFilters().AsNoTracking().Single();
        route.ExtraUpstreamsJson.Should().BeNullOrEmpty();
        route.LoadBalancerHealthCheckPath.Should().BeNull();
    }

    [Fact]
    public async Task Traefik_actively_polls_every_replica_when_the_app_has_a_health_path()
    {
        // This is what makes "a replica that dies stops receiving traffic" true without the panel
        // running a polling loop of its own — Traefik's own loadBalancer healthCheck does it.
        using var h = new PipelineHarness().WithReplicas(3).WithDomain().WithHealthPath("/healthz");
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        var route = h.Db.Routes.IgnoreQueryFilters().AsNoTracking().Single();
        route.LoadBalancerHealthCheckPath.Should().Be("/healthz");
    }

    // ---- health: all-or-nothing, exactly like a single container already promised ----

    [Fact]
    public async Task Every_replica_is_health_checked_before_any_traffic_moves()
    {
        using var h = new PipelineHarness().WithReplicas(3).WithDomain().WithHealthPath("/healthz");
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        h.Http.RequestedUrls.Should().Contain($"http://{h.ReplicaContainerFor(1, 1)}:{h.App.ContainerPort}/healthz");
        h.Http.RequestedUrls.Should().Contain($"http://{h.ReplicaContainerFor(1, 2)}:{h.App.ContainerPort}/healthz");
        h.Http.RequestedUrls.Should().Contain($"http://{h.ReplicaContainerFor(1, 3)}:{h.App.ContainerPort}/healthz");
    }

    [Fact]
    public async Task One_unhealthy_replica_fails_the_whole_deploy_and_keeps_the_old_release_serving()
    {
        using var h = new PipelineHarness().WithReplicas(3).WithDomain().WithHealthPath("/healthz");
        h.WithPreviousDeployment(number: 1);
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;
        var deployment = h.QueueDeployment(number: 2);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Proxy.ApplyCount.Should().Be(0, "traffic must never move while any replica is unhealthy");
        h.Docker.LiveContainerNames.Should().BeEquivalentTo([h.ContainerFor(1)],
            "the previous release must still be the only thing serving after a failed replicated deploy");
    }

    [Fact]
    public async Task A_failed_replicated_deploy_removes_every_one_of_its_own_containers()
    {
        using var h = new PipelineHarness().WithReplicas(3).WithHealthPath("/healthz");
        h.WithPreviousDeployment(number: 1);
        h.Http.Status = System.Net.HttpStatusCode.BadGateway;
        var deployment = h.QueueDeployment(number: 2);

        await h.RunAsync(deployment);

        var removed = h.Docker.Calls
            .Where(c => c.Operation == "RemoveContainerAsync").Select(c => c.Target).ToList();
        removed.Should().BeEquivalentTo(
        [
            h.ReplicaContainerFor(2, 1), h.ReplicaContainerFor(2, 2), h.ReplicaContainerFor(2, 3)
        ], "a failed deploy must clean up every replica it started, and touch nothing that was already serving");
    }

    // ---- cutover keeps N new, retires every old ----

    [Fact]
    public async Task Cutover_from_one_replica_to_three_ends_with_exactly_the_three_new_containers_live()
    {
        using var h = new PipelineHarness().WithReplicas(3);
        h.WithPreviousDeployment(number: 1); // the app was running with one replica before
        var deployment = h.QueueDeployment(number: 2);

        await h.RunAsync(deployment);

        h.Docker.LiveContainerNames.Should().BeEquivalentTo(
        [
            h.ReplicaContainerFor(2, 1), h.ReplicaContainerFor(2, 2), h.ReplicaContainerFor(2, 3)
        ], "the old single container must be retired once every new replica is healthy and live");
    }

    [Fact]
    public async Task Scaling_down_on_the_next_deploy_leaves_only_the_new_lower_replica_count_live()
    {
        using var h = new PipelineHarness().WithReplicas(3);
        var first = h.QueueDeployment(number: 1);
        await h.RunAsync(first);
        h.Docker.LiveContainerNames.Should().HaveCount(3);

        h.WithReplicas(1);
        var second = h.QueueDeployment(number: 2);
        await h.RunAsync(second);

        h.Docker.LiveContainerNames.Should().BeEquivalentTo([h.ContainerFor(2)],
            "scaling down must remove the extra containers, not just stop adding to them");
    }

    // ---- remote nodes: one port per replica ----

    [Fact]
    public async Task On_a_remote_node_every_replica_publishes_its_own_distinct_host_port()
    {
        using var h = new PipelineHarness(localServer: false).WithReplicas(3);
        var deployment = h.QueueDeployment(number: 1);

        await h.RunAsync(deployment);

        var ports = h.Docker.RunRequests.Select(r => r.PublishToHostPort).ToList();
        ports.Should().HaveCount(3);
        ports.Should().OnlyContain(p => p.HasValue);
        ports.Should().OnlyHaveUniqueItems("three replicas answering on one shared port would be indistinguishable");
    }

    [Fact]
    public async Task A_failed_replicated_deploy_on_a_remote_node_frees_every_port_it_reserved()
    {
        using var h = new PipelineHarness(localServer: false).WithReplicas(3);
        h.Docker.StartedContainerState = "exited";
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Db.HostPortAllocations.Should().BeEmpty("a failed deploy must not strand any replica's port");
    }

    [Fact]
    public async Task Scaling_down_on_a_remote_node_frees_the_ports_the_extra_replicas_held()
    {
        using var h = new PipelineHarness(localServer: false).WithReplicas(3);
        await h.RunAsync(h.QueueDeployment(number: 1));
        h.Db.HostPortAllocations.Should().HaveCount(3);

        h.WithReplicas(1);
        await h.RunAsync(h.QueueDeployment(number: 2));

        h.Db.HostPortAllocations.Should().ContainSingle(
            "only the one replica still running should still hold a port");
    }

    // ---- rollback and redeploy stay meaningful ----

    [Fact]
    public async Task Rolling_back_releases_at_the_apps_current_replica_count_not_the_targets()
    {
        // The rollback target ran with one replica; the app has since been scaled to three. A
        // rollback re-releases the OLD image (ADR-006) but must still start at the CURRENT count —
        // replicas are a property of the app, not frozen into a deployment row.
        using var h = new PipelineHarness();
        var target = h.WithPreviousDeployment(number: 1, image: "harbora/blog:build-1");
        h.WithReplicas(3);
        var rollback = h.QueueDeployment(number: 2, rollbackTo: target.Id);

        var result = await h.RunAsync(rollback);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.LiveContainerNames.Should().HaveCount(3,
            "a rollback is still a deployment of the app as it is configured now");
    }
}
