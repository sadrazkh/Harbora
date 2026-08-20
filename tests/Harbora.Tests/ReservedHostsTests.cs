using FluentAssertions;
using Harbora.Domain.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The platform's own host names are not free real estate. The one that matters is the node
/// channel's: <c>deploy/traefik/dynamic/node-agent.yml</c> puts exactly one router on it with
/// <c>clientAuthType: RequireAndVerifyClientCert</c>, and the panel is configured to believe the
/// <c>X-Forwarded-Tls-Client-Cert</c> header <em>because</em> that router always overwrites it.
///
/// <para>
/// A tenant who binds that host to an app gets a second router on it from
/// <c>TraefikProxyEngine.RenderRouter</c> — <c>tls: certResolver:</c> with no <c>options:</c>, i.e.
/// the default TLS options, which ask for no client certificate. Traefik resolves TLS options per
/// SNI host name, so two routers with different options on one name make it fall back to the
/// default: mTLS silently stops being enforced on the one host where a client-settable header is
/// trusted. Claiming the panel's own host is louder but the same class of mistake.
/// </para>
/// </summary>
public class ReservedHostsTests
{
    private static IReadOnlyList<string> Platform() => ReservedHosts.ForPlatform(
        panelDomain: "panel.acme.test",
        nodeChannelUrl: "https://nodes.panel.acme.test",
        objectStorageUrl: "https://s3.acme.test");

    [Fact]
    public void The_node_channels_host_is_reserved()
    {
        // The whole reason this rule exists: a second router here turns mTLS off rather than adding
        // a route, and nothing in the UI would say so.
        ReservedHosts.IsReserved("nodes.panel.acme.test", Platform()).Should().BeTrue();
    }

    [Fact]
    public void The_panels_own_host_and_the_object_storage_host_are_reserved_too()
    {
        ReservedHosts.IsReserved("panel.acme.test", Platform()).Should().BeTrue();
        ReservedHosts.IsReserved("s3.acme.test", Platform()).Should().BeTrue();
    }

    [Theory]
    [InlineData("NODES.Panel.Acme.Test")]
    [InlineData("  nodes.panel.acme.test  ")]
    [InlineData("nodes.panel.acme.test.")]
    public void Case_padding_and_a_trailing_dot_do_not_get_round_it(string typed)
    {
        // A hostname is case-insensitive and the root label is optional; a check that reads the
        // string literally is a check with three published bypasses.
        ReservedHosts.IsReserved(typed, Platform()).Should().BeTrue();
    }

    [Fact]
    public void A_tenants_own_domain_is_not_reserved()
    {
        // The rule has to stay narrow. Reserving a suffix rather than exact names would take
        // every apps.* name away from the tenants the platform exists to serve.
        ReservedHosts.IsReserved("shop.example.com", Platform()).Should().BeFalse();
        ReservedHosts.IsReserved("my-nodes.panel.acme.test", Platform()).Should().BeFalse();
        ReservedHosts.IsReserved("nodes.panel.acme.test.evil.example", Platform()).Should().BeFalse();
    }

    [Fact]
    public void The_node_host_is_reserved_even_when_the_panel_was_never_told_what_it_is()
    {
        // NodeAgent__PublicUrl has a compose default of "", which is exactly the broken install
        // whose node host would otherwise be claimable. The installer derives that host as
        // nodes.$PANEL_DOMAIN, so the rule can derive it too rather than leaving a hole.
        var hosts = ReservedHosts.ForPlatform("panel.acme.test", nodeChannelUrl: "", objectStorageUrl: null);

        ReservedHosts.IsReserved("nodes.panel.acme.test", hosts).Should().BeTrue();
    }

    [Fact]
    public void A_bespoke_node_host_is_reserved_alongside_the_derived_one()
    {
        // NODE_DOMAIN is backfilled with a derived value but an operator may set another, and
        // `harbora set-domain` leaves a bespoke one alone. Both names must be off limits.
        var hosts = ReservedHosts.ForPlatform("panel.acme.test", "https://channel.acme.test:443/", null);

        ReservedHosts.IsReserved("channel.acme.test", hosts).Should().BeTrue();
        ReservedHosts.IsReserved("nodes.panel.acme.test", hosts).Should().BeTrue();
    }

    [Fact]
    public void Nothing_is_reserved_when_the_platform_names_nothing()
    {
        // A panel with no PANEL_DOMAIN is a development run. Refusing every domain there would be a
        // rule that fails closed against its own operator for no gain.
        var hosts = ReservedHosts.ForPlatform(null, null, null);

        hosts.Should().BeEmpty();
        ReservedHosts.IsReserved("anything.example.com", hosts).Should().BeFalse();
    }

    [Fact]
    public void An_empty_candidate_is_not_reserved()
    {
        // "Is required" is the caller's message for a blank host, and it comes first. This rule
        // must not turn a blank field into "that name belongs to the platform".
        ReservedHosts.IsReserved("", Platform()).Should().BeFalse();
        ReservedHosts.IsReserved(null, Platform()).Should().BeFalse();
    }

    [Fact]
    public void A_url_shaped_configuration_value_is_reduced_to_its_host()
    {
        // Storage:S3:PublicEndpoint and NodeAgent:PublicUrl are URLs; PANEL_DOMAIN is a bare host.
        // The list has to be hosts, or the comparison never matches anything a tenant can type.
        ReservedHosts.ForPlatform("panel.acme.test", "https://nodes.panel.acme.test/", "https://s3.acme.test:9000")
            .Should().BeEquivalentTo(["panel.acme.test", "nodes.panel.acme.test", "s3.acme.test"]);
    }
}

/// <summary>
/// Sub-project 7 (2026-08-20 platform-options plan, "Public status page on a platform subdomain"):
/// every workspace's status page lives at <c>status-{workspace.Slug}</c> under the tenant root
/// domain, minted on demand rather than drawn from a fixed list — <see cref="ReservedHosts.ForPlatform"/>
/// cannot enumerate a name that does not exist yet. This is a second, narrower rule: a leftmost-label
/// <em>prefix</em> check scoped to the root domain apps already live under, so
/// <c>AppAddress.Decide</c> can refuse a tenant app the same name would otherwise be free to claim
/// — the same guard, reused, not a second reserved-host mechanism next to it.
/// </summary>
public class ReservedHostPrefixTests
{
    [Fact]
    public void The_status_page_prefix_is_reserved_under_the_tenant_root_domain()
    {
        ReservedHosts.IsReservedPrefix("status-acme.apps.example.com", "apps.example.com").Should().BeTrue();
    }

    [Fact]
    public void A_tenant_app_whose_name_merely_contains_the_word_status_is_not_caught()
    {
        // "mystatus-acme" does not start with the reserved label — a substring match here would take
        // away names that were never the platform's to reserve.
        ReservedHosts.IsReservedPrefix("mystatus-acme.apps.example.com", "apps.example.com").Should().BeFalse();
    }

    [Fact]
    public void The_bare_word_status_with_no_dash_is_an_ordinary_tenant_name()
    {
        // The pattern is "status-{something}"; "status" alone never collides with a minted page.
        ReservedHosts.IsReservedPrefix("status.apps.example.com", "apps.example.com").Should().BeFalse();
    }

    [Fact]
    public void The_root_domain_itself_is_not_caught()
    {
        ReservedHosts.IsReservedPrefix("apps.example.com", "apps.example.com").Should().BeFalse();
    }

    [Fact]
    public void A_label_deeper_than_the_leftmost_one_is_not_caught()
    {
        // Reserving a leftmost label, never a suffix — the same narrowness ReservedHosts.ForPlatform
        // insists on for its own exact names.
        ReservedHosts.IsReservedPrefix("a.status-acme.apps.example.com", "apps.example.com").Should().BeFalse();
    }

    [Theory]
    [InlineData("STATUS-Acme.Apps.Example.Com")]
    [InlineData("  status-acme.apps.example.com  ")]
    [InlineData("status-acme.apps.example.com.")]
    public void Case_padding_and_a_trailing_dot_do_not_get_round_it(string typed)
    {
        ReservedHosts.IsReservedPrefix(typed, "apps.example.com").Should().BeTrue();
    }

    [Fact]
    public void A_status_prefixed_host_on_a_different_domain_is_not_this_rules_business()
    {
        // A customer's own custom domain (sub-project 8) is a different check entirely — this rule
        // only guards the names AppAddress hands out under the platform's own root domain.
        ReservedHosts.IsReservedPrefix("status-acme.customer-owned-domain.com", "apps.example.com").Should().BeFalse();
    }

    [Fact]
    public void No_root_domain_configured_reserves_nothing()
    {
        ReservedHosts.IsReservedPrefix("status-acme.apps.example.com", null).Should().BeFalse();
    }

    [Fact]
    public void An_empty_candidate_is_not_reserved()
    {
        ReservedHosts.IsReservedPrefix("", "apps.example.com").Should().BeFalse();
        ReservedHosts.IsReservedPrefix(null, "apps.example.com").Should().BeFalse();
    }
}
