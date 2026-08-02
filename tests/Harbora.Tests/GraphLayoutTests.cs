using FluentAssertions;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Arranging an architecture into a picture.
///
/// The case that decides whether this is safe is a cycle: two services that reference each other by
/// hostname is an ordinary thing to build, and a naive depth walk on it does not draw a bad diagram
/// — it never returns. A page that hangs is worse than a page that is ugly.
/// </summary>
public class GraphLayoutTests
{
    private static GraphNode Node(string id, string tier = "service") =>
        new(id, id, tier, "boxes", "ok", null);

    [Fact]
    public void A_simple_chain_stacks_in_order()
    {
        var nodes = new[] { Node("web"), Node("api"), Node("db") };
        var edges = new[] { new GraphEdge("web", "api"), new GraphEdge("api", "db") };

        var laid = GraphLayout.Arrange(nodes, edges);

        laid.Single(n => n.Id == "web").Row.Should().Be(0);
        laid.Single(n => n.Id == "api").Row.Should().Be(1);
        laid.Single(n => n.Id == "db").Row.Should().Be(2);
    }

    [Fact]
    public void A_cycle_does_not_hang_and_every_node_is_placed()
    {
        // Two services naming each other. Depth is undefined here, so the rule has to stop rather
        // than look for the bottom of something with no bottom.
        var nodes = new[] { Node("a"), Node("b") };
        var edges = new[] { new GraphEdge("a", "b"), new GraphEdge("b", "a") };

        var laid = GraphLayout.Arrange(nodes, edges);

        laid.Should().HaveCount(2);
        laid.Should().OnlyContain(n => n.Row >= 0);
    }

    [Fact]
    public void A_node_pointing_at_itself_is_placed()
    {
        var laid = GraphLayout.Arrange([Node("a")], [new GraphEdge("a", "a")]);

        laid.Should().ContainSingle().Which.Row.Should().Be(0);
    }

    [Fact]
    public void An_unconnected_node_still_gets_a_place()
    {
        // A service nothing talks to is the normal state of a fresh project, not an error.
        var laid = GraphLayout.Arrange([Node("lonely")], []);

        laid.Should().ContainSingle().Which.Row.Should().Be(0);
    }

    [Fact]
    public void Nodes_in_the_same_row_never_share_a_column()
    {
        var nodes = new[] { Node("a"), Node("b"), Node("c") };

        var laid = GraphLayout.Arrange(nodes, []);

        laid.Select(n => (n.Row, n.Column)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Nodes_in_a_row_keep_the_order_they_arrived_in()
    {
        // Asserted against the input order rather than against a second run of the same code:
        // comparing two runs only proves the layout is consistently wrong, which is how a reversed
        // ordering survived this test in its first form.
        var nodes = new[] { Node("web"), Node("api"), Node("admin") };

        var laid = GraphLayout.Arrange(nodes, []);

        laid.Select(n => n.Id).Should().Equal("web", "api", "admin");
        laid.Select(n => n.Column).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void An_edge_to_a_node_that_is_not_there_is_ignored()
    {
        // Connections are derived from environment variables, which can name a service that was
        // deleted. That must not take the whole diagram down.
        var laid = GraphLayout.Arrange([Node("web")], [new GraphEdge("web", "ghost")]);

        laid.Should().ContainSingle().Which.Id.Should().Be("web");
    }

    [Fact]
    public void A_deleted_service_does_not_push_a_real_one_down_the_page()
    {
        // The direction that actually breaks the picture: an environment variable still names a
        // service that is gone, and the vanished dependant drags the database it pointed at into a
        // row below a dependency nobody has.
        var laid = GraphLayout.Arrange([Node("db")], [new GraphEdge("ghost", "db")]);

        laid.Should().ContainSingle().Which.Row.Should().Be(0);
    }

    [Fact]
    public void A_deep_chain_stays_within_a_sane_depth()
    {
        // Guards against the other failure of a depth walk: a long chain that pushes the diagram
        // off the bottom of the page.
        var nodes = Enumerable.Range(0, 40).Select(i => Node($"n{i}")).ToArray();
        var edges = Enumerable.Range(0, 39).Select(i => new GraphEdge($"n{i}", $"n{i + 1}")).ToArray();

        var laid = GraphLayout.Arrange(nodes, edges);

        laid.Should().HaveCount(40);
        laid.Max(n => n.Row).Should().BeLessThanOrEqualTo(GraphLayout.MaxRows);
    }

    [Fact]
    public void Nothing_in_produces_nothing_out()
    {
        GraphLayout.Arrange([], []).Should().BeEmpty();
    }
}
