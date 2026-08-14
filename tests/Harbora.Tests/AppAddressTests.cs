using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What hostname an app should be given, decided without a database in the way.
///
/// Before this existed the answer depended on which of four creation paths the app came through:
/// three built it differently and the fourth never built one at all. A guarantee with four
/// implementations is not a guarantee.
/// </summary>
public class AppAddressTests
{
    private static readonly string[] NoReservedHosts = [];

    [Fact]
    public void A_web_app_under_a_root_domain_is_given_slug_dot_root()
    {
        var decision = AppAddress.Decide(ServiceKind.Web, requested: null, slug: "shop",
            rootDomain: "apps.example.com", reservedHosts: NoReservedHosts);

        decision.Outcome.Should().Be(AppAddressOutcome.Assigned);
        decision.Host.Should().Be("shop.apps.example.com");
    }

    [Fact]
    public void A_worker_is_given_nothing_and_says_why()
    {
        var decision = AppAddress.Decide(ServiceKind.Worker, requested: null, slug: "mailer",
            rootDomain: "apps.example.com", reservedHosts: NoReservedHosts);

        decision.Host.Should().BeNull();
        decision.Outcome.Should().Be(AppAddressOutcome.KindTakesNoTraffic,
            "a page that shows an empty slot with no reason is the promise-without-a-feature this project keeps removing");
    }

    [Fact]
    public void With_no_root_domain_configured_the_outcome_names_that_rather_than_looking_like_a_refusal()
    {
        var decision = AppAddress.Decide(ServiceKind.Web, requested: null, slug: "shop",
            rootDomain: null, reservedHosts: NoReservedHosts);

        decision.Host.Should().BeNull();
        decision.Outcome.Should().Be(AppAddressOutcome.NoRootDomain);
    }

    [Fact]
    public void A_platform_host_name_is_refused_rather_than_routed_to_an_app()
    {
        var decision = AppAddress.Decide(ServiceKind.Web, requested: "panel.example.com", slug: "shop",
            rootDomain: "apps.example.com", reservedHosts: ["panel.example.com"]);

        decision.Host.Should().BeNull();
        decision.Outcome.Should().Be(AppAddressOutcome.Reserved);
    }

    [Fact]
    public void A_typed_name_wins_over_the_derived_one()
    {
        var decision = AppAddress.Decide(ServiceKind.Web, requested: "Shop.Example.COM", slug: "shop",
            rootDomain: "apps.example.com", reservedHosts: NoReservedHosts);

        decision.Host.Should().Be("shop.example.com", "hostnames are compared lowercased everywhere else");
    }

    [Fact]
    public void The_discriminator_lands_on_the_leftmost_label_so_the_root_domain_still_matches_the_wildcard()
    {
        AppAddress.Discriminate("shop.apps.example.com", "k3f").Should().Be("shop-k3f.apps.example.com",
            "the certificate is a wildcard for *.apps.example.com — a suffix anywhere else would not be covered by it");
    }

    [Fact]
    public void A_single_label_host_can_still_be_discriminated()
    {
        AppAddress.Discriminate("shop", "k3f").Should().Be("shop-k3f");
    }
}
