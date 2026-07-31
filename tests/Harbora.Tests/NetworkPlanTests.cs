using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Networking;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Moving a running platform from one network per tenant to one per environment.
///
/// The danger is not the new network — it is the moment in between. A service that has redeployed
/// lives on its environment's network; one that has not is still only on the workspace network. If
/// the redeployed service moved outright it would stop being reachable by the ones that had not
/// caught up, and nothing would say why: the hostname still appears in the configuration, it simply
/// answers nowhere.
/// </summary>
public class NetworkPlanTests
{
    [Fact]
    public void During_the_move_a_container_joins_both_networks()
    {
        var networks = NetworkPlan.For("harbora-env-shop-prod-abc123", "harbora-ws-acme", keepWorkspaceNetwork: true);

        networks.Should().BeEquivalentTo(["harbora-env-shop-prod-abc123", "harbora-ws-acme"],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void Once_nothing_needs_the_old_network_only_the_environment_remains()
    {
        var networks = NetworkPlan.For("harbora-env-shop-prod-abc123", "harbora-ws-acme", keepWorkspaceNetwork: false);

        networks.Should().BeEquivalentTo(["harbora-env-shop-prod-abc123"]);
    }

    [Fact]
    public void A_service_with_no_environment_stays_where_it_is()
    {
        // An app created before projects existed and never reassigned. Inventing a boundary it was
        // never placed inside would cut it off from everything it talks to.
        NetworkPlan.For(null, "harbora-ws-acme", keepWorkspaceNetwork: true)
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

    [Fact]
    public async Task A_deploy_also_keeps_it_on_the_workspace_network_for_now()
    {
        // The guarantee that makes this change safe to ship to a running platform.
        using var h = new PipelineHarness();

        await h.RunAsync(h.QueueDeployment(number: 1));

        h.Docker.ConnectedNetworks(h.ContainerFor(1))
            .Should().Contain(n => n.StartsWith("harbora-ws-"),
                "a service that has not redeployed must still be able to reach this one");
    }
}
