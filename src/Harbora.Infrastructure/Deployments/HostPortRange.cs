namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Picks a host port that is genuinely free on a node.
///
/// The previous scheme was <c>20000 + sha256(slug#number) % 10000</c>: deterministic, but it consulted
/// nothing. Ten thousand slots chosen at random collide sooner than the size suggests — a coin flip by
/// around 119 deployments on one node, and every redeploy draws again, so it is ordinary usage rather
/// than a far-off edge case. (<c>app78</c> and <c>app138</c> both land on 22585 at deployment #1.)
///
/// The consequence was worse than a failed deploy: routes store host *and* port, so a port belonging
/// to a retired deployment that is later handed to a different app points the first app's traffic at
/// the second app's container.
/// </summary>
public static class HostPortRange
{
    public const int First = 20000;
    public const int Last = 29999;

    /// <summary>
    /// The lowest port in range not already taken.
    ///
    /// Lowest-free rather than random: it packs the range densely, so exhaustion is reached only when
    /// the node genuinely holds 10,000 live deployments, and it makes the allocation reproducible in
    /// tests and in an operator's head.
    /// </summary>
    public static int? NextFree(IEnumerable<int> taken)
    {
        var used = new HashSet<int>(taken);
        for (var port = First; port <= Last; port++)
            if (!used.Contains(port)) return port;
        return null;
    }

    public static bool IsInRange(int port) => port is >= First and <= Last;
}
