using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whether an app gets a name its neighbours can call it by, decided without Docker or a database.
///
/// The mechanism this feeds already existed and was already used — for compose services only. An
/// ordinary app was reachable solely as harbora-{slug}-{number}, and the deployment number in that
/// name changes every time it ships.
/// </summary>
public class PrivateAddressTests
{
    private static readonly string[] NothingTaken = [];

    [Fact]
    public void An_ordinary_app_is_reachable_by_its_slug()
    {
        var decision = PrivateAddress.Decide(ServiceKind.Web, "shop", NothingTaken);

        decision.Outcome.Should().Be(PrivateAddressOutcome.Registered);
        decision.Alias.Should().Be("shop");
    }

    [Fact]
    public void A_worker_gets_one_too_because_its_siblings_may_scrape_it()
    {
        PrivateAddress.Decide(ServiceKind.Worker, "mailer", NothingTaken)
            .Outcome.Should().Be(PrivateAddressOutcome.Registered,
                "ServicePlan.JoinsInternalNetwork is true for a worker — a metrics port its siblings " +
                "read is the case that rule was written for");
    }

    [Fact]
    public void A_release_task_gets_none_because_it_runs_once_and_exits()
    {
        var decision = PrivateAddress.Decide(ServiceKind.ReleaseTask, "migrate", NothingTaken);

        decision.Alias.Should().BeNull();
        decision.Outcome.Should().Be(PrivateAddressOutcome.KindDoesNotJoin);
    }

    [Fact]
    public void A_name_another_container_already_answers_to_is_not_registered()
    {
        var decision = PrivateAddress.Decide(ServiceKind.Web, "db", ["db", "cache"]);

        decision.Alias.Should().BeNull(
            "docker balances between every container holding an alias, so registering this one sends " +
            "some calls to a stranger — an app reaching the wrong database is worse than no shortcut");
        decision.Outcome.Should().Be(PrivateAddressOutcome.Ambiguous);
    }

    [Fact]
    public void The_comparison_ignores_case_because_dns_does()
    {
        PrivateAddress.Decide(ServiceKind.Web, "DB", ["db"])
            .Outcome.Should().Be(PrivateAddressOutcome.Ambiguous);
    }

    [Fact]
    public void An_app_with_no_slug_gets_nothing_rather_than_an_empty_alias()
    {
        PrivateAddress.Decide(ServiceKind.Web, "  ", NothingTaken)
            .Outcome.Should().Be(PrivateAddressOutcome.NoSlug);
    }

    [Fact]
    public void The_url_carries_the_apps_own_port_not_a_guess()
    {
        PrivateAddress.Url("shop", 8080).Should().Be("http://shop:8080");
    }
}
