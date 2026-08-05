namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// Turning Docker's cumulative CPU counters into the percentage people read.
///
/// It lives in the contract rather than in either side because both compute it: the control plane
/// for containers on its own host, and the agent for containers on a node. Two copies of this
/// arithmetic means the same container reads differently depending on where it happens to be
/// running, and the difference would be blamed on the node rather than on the formula.
///
/// The counters are totals since the container started, so a reading needs two samples. Docker
/// supplies the previous one alongside the current one; what matters here is what to do when it
/// does not.
/// </summary>
public static class ContainerCpu
{
    /// <summary>
    /// The share of the host's CPU this container used between two samples, as a percentage where
    /// 100 is one core saturated.
    /// </summary>
    /// <param name="cpuDelta">Container CPU nanoseconds between the samples.</param>
    /// <param name="systemDelta">Host CPU nanoseconds between the same two samples.</param>
    /// <param name="onlineCpus">How many cores the reading covers. Zero is treated as one.</param>
    /// <returns>
    /// Null when there is no interval to divide by. The first sample after a container starts has
    /// no predecessor, so <paramref name="systemDelta"/> is zero — and returning 0% there reports a
    /// busy container as idle at exactly the moment somebody is watching it come up.
    /// </returns>
    public static double? Percent(ulong cpuDelta, ulong systemDelta, ulong onlineCpus)
    {
        if (systemDelta == 0) return null;

        // Both counters cover every core, so a container cannot have used more CPU time than the
        // host did in the same interval. More means the counters wrapped or the container was
        // replaced between samples, and the arithmetic would produce a spike nobody can explain.
        if (cpuDelta > systemDelta) return null;

        var cores = onlineCpus == 0 ? 1UL : onlineCpus;

        return Math.Round((double)cpuDelta / systemDelta * cores * 100.0, 2);
    }
}
