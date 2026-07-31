using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a deployment does differently for each kind of service.
///
/// Everything Harbora deployed until now was implicitly a web service: it published a port, was probed
/// over HTTP, and had traffic routed to it. A worker answers no HTTP, forever — waiting for a reply and
/// then failing the deploy would be a bug wearing a health check's clothes.
/// </summary>
public class ServicePlanTests
{
    [Fact]
    public void Only_web_facing_kinds_get_public_traffic()
    {
        ServicePlan.HasPublicTraffic(ServiceKind.Web).Should().BeTrue();
        ServicePlan.HasPublicTraffic(ServiceKind.Static).Should().BeTrue();

        ServicePlan.HasPublicTraffic(ServiceKind.Private).Should().BeFalse("that is what 'private' means");
        ServicePlan.HasPublicTraffic(ServiceKind.Worker).Should().BeFalse();
        ServicePlan.HasPublicTraffic(ServiceKind.Cron).Should().BeFalse();
        ServicePlan.HasPublicTraffic(ServiceKind.ReleaseTask).Should().BeFalse();
    }

    [Fact]
    public void A_worker_is_never_probed_over_http()
    {
        // The whole point. A queue consumer that settles into its loop answers nothing, and that is
        // success, not a failed deploy.
        ServicePlan.HasHttpHealthCheck(ServiceKind.Worker).Should().BeFalse();
        ServicePlan.HasHttpHealthCheck(ServiceKind.Cron).Should().BeFalse();
    }

    [Fact]
    public void A_private_service_is_still_probed()
    {
        // It serves HTTP to its siblings, so "is it answering?" is exactly the right question — it
        // simply has no public route.
        ServicePlan.HasHttpHealthCheck(ServiceKind.Private).Should().BeTrue();
        ServicePlan.HasPublicTraffic(ServiceKind.Private).Should().BeFalse();
    }

    [Fact]
    public void Everything_but_a_release_task_is_reachable_inside_the_project()
    {
        // A private service exists to be called by its siblings; a worker often exposes metrics to
        // them. A release task runs once and is gone before anything could call it.
        ServicePlan.JoinsInternalNetwork(ServiceKind.Private).Should().BeTrue();
        ServicePlan.JoinsInternalNetwork(ServiceKind.Worker).Should().BeTrue();
        ServicePlan.JoinsInternalNetwork(ServiceKind.ReleaseTask).Should().BeFalse();
    }

    [Fact]
    public void Only_a_kind_that_can_serve_may_have_domains()
    {
        // Offering a domain for a worker would accept something that cannot work.
        ServicePlan.CanHaveDomains(ServiceKind.Web).Should().BeTrue();
        ServicePlan.CanHaveDomains(ServiceKind.Worker).Should().BeFalse();
        ServicePlan.CanHaveDomains(ServiceKind.Private).Should().BeFalse();
    }

    [Fact]
    public void A_worker_gets_no_hostname_by_either_route()
    {
        // Both routes matter. An early version of this guard suppressed only the typed domain, and a
        // worker still ended up with "{slug}.{root}" — plus a certificate for an address that would
        // never answer. Mutation testing found it; reading the code had not.
        ServicePlan.HostFor(ServiceKind.Worker, "worker.example.com", "worker", "apps.example.com").Should().BeNull();
        ServicePlan.HostFor(ServiceKind.Worker, null, "worker", "apps.example.com").Should().BeNull();
        ServicePlan.HostFor(ServiceKind.Private, null, "api", "apps.example.com").Should().BeNull();
    }

    [Fact]
    public void A_web_service_keeps_both_routes_to_a_hostname()
    {
        ServicePlan.HostFor(ServiceKind.Web, "Shop.Example.COM", "shop", "apps.example.com")
            .Should().Be("shop.example.com", "a typed domain wins, lowercased");

        ServicePlan.HostFor(ServiceKind.Web, null, "shop", "apps.example.com")
            .Should().Be("shop.apps.example.com", "otherwise it is derived from the platform root");

        ServicePlan.HostFor(ServiceKind.Web, null, "shop", null)
            .Should().BeNull("with no root domain configured there is nothing to derive");
    }

    [Fact]
    public void One_shot_kinds_are_not_expected_to_keep_running()
    {
        ServicePlan.IsLongRunning(ServiceKind.Web).Should().BeTrue();
        ServicePlan.IsLongRunning(ServiceKind.Worker).Should().BeTrue();
        ServicePlan.IsLongRunning(ServiceKind.Cron).Should().BeFalse();
        ServicePlan.IsLongRunning(ServiceKind.ReleaseTask).Should().BeFalse();
    }

    [Fact]
    public void Every_kind_explains_itself()
    {
        // The form asks someone to choose; a list of bare words is not a choice.
        foreach (var kind in Enum.GetValues<ServiceKind>())
            ServicePlan.Describe(kind).Should().NotBeNullOrWhiteSpace($"{kind} needs a description");
    }

    // ---- through the real pipeline ----

    [Fact]
    public async Task A_worker_deploys_without_being_probed_or_routed()
    {
        // End to end: the same pipeline that deploys a web service must not wait for HTTP from a
        // worker, and must not put it behind the proxy.
        using var h = new PipelineHarness().WithDomain().WithHealthPath("/healthz");
        h.App.Kind = ServiceKind.Worker;
        h.Db.SaveChanges();
        h.Http.Status = System.Net.HttpStatusCode.NotFound;   // nothing is listening, as expected

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Http.Attempts.Should().Be(0, "a worker has no HTTP to probe");
        h.Proxy.ApplyCount.Should().Be(0, "nothing should be routed to a worker");
    }

    [Fact]
    public async Task A_web_service_is_still_probed_and_routed()
    {
        // The guard on the change above: the behaviour that already worked must be untouched.
        using var h = new PipelineHarness().WithDomain().WithHealthPath("/healthz");

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Http.Attempts.Should().BeGreaterThan(0);
        h.Proxy.ApplyCount.Should().Be(1);
    }

    [Fact]
    public async Task A_private_service_is_probed_but_not_routed()
    {
        using var h = new PipelineHarness().WithDomain().WithHealthPath("/healthz");
        h.App.Kind = ServiceKind.Private;
        h.Db.SaveChanges();

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Http.Attempts.Should().BeGreaterThan(0, "it serves HTTP to its siblings");
        h.Proxy.ApplyCount.Should().Be(0, "but nothing public points at it");
    }

    [Fact]
    public async Task A_worker_whose_container_dies_still_fails_the_deploy()
    {
        // Skipping the HTTP probe must not mean skipping the health gate. A crash-looping worker is
        // still a broken deploy.
        using var h = new PipelineHarness();
        h.App.Kind = ServiceKind.Worker;
        h.Db.SaveChanges();
        h.Docker.StartedContainerState = "restarting";
        h.Docker.StartedContainerStatus = "Restarting (1) 2 seconds ago";
        h.Docker.ContainerLogs = "connection refused";

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("crashing");
    }
}
