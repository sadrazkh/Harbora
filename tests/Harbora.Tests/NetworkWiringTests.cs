using FluentAssertions;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Changing wiring from the diagram.
///
/// Both edits look harmless on a canvas. Attaching across environments writes a hostname that will
/// never resolve; moving a service to another environment severs every connection it had. Each
/// failure arrives at the next deploy and looks like something else entirely, which is why the rule
/// lives here rather than in a controller.
/// </summary>
public class NetworkWiringTests
{
    private static readonly Guid Production = Guid.CreateVersion7();
    private static readonly Guid Staging = Guid.CreateVersion7();

    [Fact]
    public void A_database_in_the_same_environment_can_be_attached()
    {
        NetworkWiring.CanAttach(Production, Production).Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_database_in_another_environment_is_refused_not_warned()
    {
        // There is no configuration in which this works: the name is only resolvable on the other
        // network. Attaching anyway produces a service that starts and cannot reach its data.
        var verdict = NetworkWiring.CanAttach(Production, Staging);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("different environments");
    }

    [Fact]
    public void Moving_somewhere_it_already_is_is_refused()
    {
        NetworkWiring.CanMove(Production, Production, [], []).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Moving_a_service_away_from_its_database_warns_by_name()
    {
        // The warning this whole rule exists for.
        var verdict = NetworkWiring.CanMove(Production, Staging, ["shop-db"], []);

        verdict.Allowed.Should().BeTrue();
        verdict.Warnings.Should().Contain(w => w.Contains("shop-db"));
    }

    [Fact]
    public void Moving_a_service_others_depend_on_warns_about_them()
    {
        var verdict = NetworkWiring.CanMove(Production, Staging, [], ["api", "worker"]);

        verdict.Warnings.Should().Contain(w => w.Contains("api") && w.Contains("worker"));
    }

    [Fact]
    public void A_redeploy_is_always_mentioned()
    {
        // Even a move with nothing attached does not reach the running container on its own. A
        // silent no-op is how "I moved it and nothing happened" becomes a support conversation.
        var verdict = NetworkWiring.CanMove(Production, Staging, [], []);

        verdict.Warnings.Should().Contain(w => w.Contains("redeployed"));
    }

    [Fact]
    public void A_long_list_of_casualties_is_summarised_rather_than_recited()
    {
        // Forty names is a warning nobody finishes reading, and the count is the part that decides.
        var many = Enumerable.Range(1, 40).Select(i => $"db{i}").ToList();

        var verdict = NetworkWiring.CanMove(Production, Staging, many, []);

        var warning = verdict.Warnings.Single(w => w.Contains("db1"));
        warning.Should().Contain("37 more");
        warning.Should().NotContain("db40");
    }
}
