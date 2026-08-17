namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Which network a container joins for its workload.
///
/// <para>
/// This used to also decide whether a container kept a second, temporary membership on the shared
/// workspace network while the platform moved from one-network-per-tenant to one-per-environment: a
/// service that had redeployed lived on its environment's network, one that had not was still only on
/// the workspace network, and dropping the second membership outright would have made the redeployed
/// one unreachable by anything that had not caught up yet. P3 (2026-08-17
/// app-environment-management design) finished that move — <c>BackupEngine</c>'s dump and restore,
/// its restore rehearsal, the backup module's stager, and <c>ManagedServiceEngine</c>'s rotation all
/// reach a database on its own environment network now, rather than needing the workspace network to
/// still be attached — so the dual attach this class used to grant on request is gone. A workload
/// with an environment gets that network alone; one without (still legal until P2 makes the column
/// required) keeps the workspace network it has always had.
/// </para>
/// </summary>
public static class NetworkPlan
{
    /// <summary>
    /// The network to attach, as a single-element list: the environment's once it has one, otherwise
    /// the workspace's. Still a list, and not a bare string, because callers historically iterate it
    /// to `EnsureNetworkAsync` every name a workload might need — one element today, but the shape a
    /// caller should keep writing against rather than assuming.
    /// </summary>
    public static IReadOnlyList<string> For(string? environmentNetwork, string workspaceNetwork)
    {
        if (string.IsNullOrWhiteSpace(environmentNetwork))
            return [workspaceNetwork];

        return [environmentNetwork];
    }

    /// <summary>
    /// The network a container is addressed on. Always the environment's once it has one: a service
    /// on both networks answers on either, and picking the narrower one keeps the transition from
    /// quietly becoming permanent.
    /// </summary>
    public static string Primary(string? environmentNetwork, string workspaceNetwork) =>
        string.IsNullOrWhiteSpace(environmentNetwork) ? workspaceNetwork : environmentNetwork;
}
