namespace Harbora.Infrastructure.Networking;

/// <summary>One box in the architecture diagram.</summary>
/// <param name="Tier">What kind of thing it is — decides colour and icon, not position.</param>
/// <param name="Status">A semantic tone: ok, warn, error, idle.</param>
/// <param name="Detail">A second line, or null when there is nothing true to say.</param>
public sealed record GraphNode(string Id, string Label, string Tier, string Icon, string Status, string? Detail);

/// <summary>A connection, drawn from the thing that depends on the thing it depends on.</summary>
public sealed record GraphEdge(string FromId, string ToId);

/// <summary>A node with a place on the grid.</summary>
public sealed record PlacedNode(string Id, string Label, string Tier, string Icon, string Status, string? Detail, int Row, int Column);

/// <summary>
/// Turns an architecture into a picture.
///
/// Depth-first placement, with two guards that exist because their absence does not produce a bad
/// diagram — it produces a page that never finishes rendering:
///
/// <list type="bullet">
/// <item>a visited set, because two services naming each other by hostname is ordinary and a naive
/// walk on that recurses for ever;</item>
/// <item>a row cap, because a forty-deep chain is a diagram nobody can read and a scrollbar to
/// nowhere.</item>
/// </list>
///
/// Deterministic on purpose: a diagram that reshuffles on refresh cannot be discussed, because
/// "the box on the left" stops meaning anything.
/// </summary>
public static class GraphLayout
{
    /// <summary>Below this the diagram is readable; past it, rows are shared rather than added.</summary>
    public const int MaxRows = 8;

    public static IReadOnlyList<PlacedNode> Arrange(
        IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var known = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        // Edges naming something that is gone are dropped, not trusted. Connections are derived
        // from environment variables, which happily outlive the service they point at.
        var real = edges
            .Where(e => known.Contains(e.FromId) && known.Contains(e.ToId) && e.FromId != e.ToId)
            .ToList();

        // Keyed by the thing depended upon, not the thing depending: a node sits one row below
        // whatever needs it, so traffic reads downwards — domains and web services at the top,
        // the databases they rest on at the bottom, which is the direction people draw it.
        var dependents = real
            .GroupBy(e => e.ToId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.FromId).ToList(), StringComparer.Ordinal);

        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
            rows[node.Id] = Depth(node.Id, dependents, rows, []);

        // Ordered by row, then by the input order, so the same architecture always draws the same.
        var order = nodes.Select((n, i) => (n.Id, Index: i))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);

        var placed = new List<PlacedNode>();
        foreach (var group in nodes.GroupBy(n => rows[n.Id]).OrderBy(g => g.Key))
        {
            var column = 0;
            foreach (var node in group.OrderBy(n => order[n.Id]))
            {
                placed.Add(new PlacedNode(
                    node.Id, node.Label, node.Tier, node.Icon, node.Status, node.Detail,
                    group.Key, column++));
            }
        }

        return placed;
    }

    /// <summary>
    /// How far down a node sits: one row below the deepest thing that depends on it. A node nobody
    /// depends on is an entry point and sits at the top.
    ///
    /// <paramref name="visiting"/> is the cycle guard. When a node is reached again while still
    /// being computed, its depth is genuinely undefined, so the walk stops and reports zero rather
    /// than looking for the bottom of something that has none.
    /// </summary>
    private static int Depth(
        string id,
        IReadOnlyDictionary<string, List<string>> dependents,
        Dictionary<string, int> memo,
        HashSet<string> visiting)
    {
        if (memo.TryGetValue(id, out var known)) return known;
        if (!visiting.Add(id)) return 0;

        var depth = 0;
        if (dependents.TryGetValue(id, out var above))
        {
            foreach (var dependent in above)
                depth = Math.Max(depth, Depth(dependent, dependents, memo, visiting) + 1);
        }

        visiting.Remove(id);

        // Past the cap rows are shared rather than added: an unreadable diagram is not more honest
        // than a compressed one.
        depth = Math.Min(depth, MaxRows);

        memo[id] = depth;
        return depth;
    }
}
