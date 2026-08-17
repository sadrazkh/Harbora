using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Networking;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// One network per workload.
///
/// <para>
/// This used to also cover the moment in between one network per tenant and one per environment: a
/// service that had redeployed lived on its environment's network, one that had not was still only on
/// the workspace network, and a container joined both until nothing needed the old one any more. P3
/// (2026-08-17 app-environment-management design) finished that move — <c>A_deploy_also_keeps_it_on_the_workspace_network_for_now</c>,
/// below, is the test that used to hold the dual attach's own guarantee, and it inverts here rather
/// than being deleted, because the fact that it was always meant to invert is the receipt that this
/// was the plan all along.
/// </para>
/// </summary>
public class NetworkPlanTests
{
    [Fact]
    public void An_environment_network_is_used_alone_once_the_workload_has_one()
    {
        var networks = NetworkPlan.For("harbora-env-shop-prod-abc123", "harbora-ws-acme");

        networks.Should().BeEquivalentTo(["harbora-env-shop-prod-abc123"]);
    }

    [Fact]
    public void A_service_with_no_environment_stays_where_it_is()
    {
        // An app created before projects existed and never reassigned. Inventing a boundary it was
        // never placed inside would cut it off from everything it talks to.
        NetworkPlan.For(null, "harbora-ws-acme")
            .Should().BeEquivalentTo(["harbora-ws-acme"]);
    }

    [Fact]
    public void The_narrower_network_is_the_one_it_is_addressed_on()
    {
        // A container on both answers on either. Pointing the proxy at the environment's network
        // keeps the transition from quietly becoming permanent.
        NetworkPlan.Primary("harbora-env-shop-prod-abc123", "harbora-ws-acme")
            .Should().Be("harbora-env-shop-prod-abc123");

        NetworkPlan.Primary(null, "harbora-ws-acme").Should().Be("harbora-ws-acme");
    }

    // ---- through the real pipeline ----

    [Fact]
    public async Task A_deploy_puts_the_container_on_its_environments_network()
    {
        using var h = new PipelineHarness();

        await h.RunAsync(h.QueueDeployment(number: 1));

        var request = h.Docker.RunRequests.Should().ContainSingle().Subject;
        EnvironmentNetwork.IsEnvironmentNetwork(request.NetworkName)
            .Should().BeTrue($"the container was placed on '{request.NetworkName}'");
    }

    /// <summary>
    /// The inversion of the guarantee this test used to hold. Its own name is the receipt: dropping
    /// the workspace network was always the plan, and P3 (2026-08-17 app-environment-management
    /// design) is the sub-project that got to carry it out, once backup, restore, restore rehearsal
    /// and rotation no longer needed the workspace network to reach a database on it.
    /// </summary>
    [Fact]
    public async Task A_deploy_no_longer_joins_the_workspace_network()
    {
        using var h = new PipelineHarness();

        await h.RunAsync(h.QueueDeployment(number: 1));

        h.Docker.ConnectedNetworks(h.ContainerFor(1))
            .Should().NotContain(n => n.StartsWith("harbora-ws-"),
                "the dual attach is gone — a placed workload joins its environment network alone");
    }

    /// <summary>
    /// The risk the spec calls out by name: the proxy and the panel are joined to every network in
    /// <c>DeploymentPipeline</c>'s list, never removed from one, so halving that list is a benefit —
    /// unless something disconnects them from a network they still route traffic on. Nothing in
    /// <c>IDockerEngine</c> can: there is no disconnect method for a deploy to call. This pins that as
    /// a behaviour rather than an absence in an interface: a network the proxy joined for an app that
    /// has not redeployed since before P3 is still there after a later deploy that no longer asks for
    /// the workspace network at all.
    /// </summary>
    [Fact]
    public async Task A_later_deploy_does_not_drop_the_proxy_from_a_network_an_older_app_still_needs()
    {
        using var h = new PipelineHarness();

        // Stands in for what an app deployed before P3 shipped left behind: the proxy already a
        // member of the workspace network, on which that app is still the only place it is reachable.
        await h.Docker.ConnectNetworkAsync(h.Options.ProxyContainerName, "harbora-ws-acme", default);
        await h.Docker.ConnectNetworkAsync(h.Options.PanelContainerName, "harbora-ws-acme", default);

        await h.RunAsync(h.QueueDeployment(number: 1));

        h.Docker.ConnectedNetworks(h.Options.ProxyContainerName).Should().Contain("harbora-ws-acme",
            "the old app is still only reachable there, and nothing here may disconnect the proxy from it");
        h.Docker.ConnectedNetworks(h.Options.PanelContainerName).Should().Contain("harbora-ws-acme");

        // And the new deploy's own network is added, not swapped in — the proxy ends up on both.
        h.Docker.ConnectedNetworks(h.Options.ProxyContainerName)
            .Should().Contain(n => EnvironmentNetwork.IsEnvironmentNetwork(n));
    }
}
