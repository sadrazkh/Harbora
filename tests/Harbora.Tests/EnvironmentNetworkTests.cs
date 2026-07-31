using FluentAssertions;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The boundary a private network draws.
///
/// Every service a tenant owned used to share one network, so staging could reach production's
/// database by name — the isolation stopped at the tenant and went no further. An environment is
/// what people mean when they say "production", so that is where the boundary belongs.
/// </summary>
public class EnvironmentNetworkTests
{
    private static readonly Guid Prod = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Staging = Guid.Parse("99999999-8888-7777-6666-555555555555");

    [Fact]
    public void Two_environments_of_the_same_project_do_not_share_a_network()
    {
        // The whole point: staging must not be able to reach production's database by name.
        EnvironmentNetwork.For("shop", "production", Prod)
            .Should().NotBe(EnvironmentNetwork.For("shop", "staging", Staging));
    }

    [Fact]
    public void Two_projects_whose_names_reduce_to_the_same_slug_stay_apart()
    {
        // "My App" and "my-app" clean to the same text. Without the id in the name they would share
        // a network — the exact isolation failure this replaces, reintroduced by a naming collision.
        var a = EnvironmentNetwork.For("my-app", "production", Prod);
        var b = EnvironmentNetwork.For("my app", "production", Staging);

        a.Should().NotBe(b);
    }

    [Fact]
    public void The_same_environment_always_gets_the_same_name()
    {
        // A name that drifts between deploys would leave containers stranded on an old network.
        EnvironmentNetwork.For("shop", "production", Prod)
            .Should().Be(EnvironmentNetwork.For("shop", "production", Prod));
    }

    [Fact]
    public void A_name_never_exceeds_what_docker_accepts()
    {
        // Over the limit Docker refuses the network, and it happens mid-deploy rather than when the
        // project was named.
        var name = EnvironmentNetwork.For(new string('a', 200), new string('b', 200), Prod);

        name.Length.Should().BeLessThanOrEqualTo(EnvironmentNetwork.MaxLength);
        name.Should().EndWith(Prod.ToString("N")[..8], "the id is what makes it unique, so it is never trimmed");
    }

    [Theory]
    [InlineData("Shop Front", "Pre-Production")]
    [InlineData("shop/../etc", "prod;rm -rf")]
    [InlineData("", "")]
    public void A_name_carries_nothing_that_needs_escaping(string project, string environment)
    {
        // These travel through shell commands and label filters.
        var name = EnvironmentNetwork.For(project, environment, Prod);

        name.Should().MatchRegex("^[a-z0-9-]+$");
    }

    [Fact]
    public void An_environment_network_is_recognisable_as_one()
    {
        // Needed to tell our own networks from the workspace network a container is still attached to
        // during the transition.
        EnvironmentNetwork.IsEnvironmentNetwork(EnvironmentNetwork.For("shop", "production", Prod))
            .Should().BeTrue();

        EnvironmentNetwork.IsEnvironmentNetwork("harbora-ws-acme").Should().BeFalse();
        EnvironmentNetwork.IsEnvironmentNetwork(null).Should().BeFalse();
    }
}
