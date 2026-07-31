namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Which networks a container joins while the platform moves from one-network-per-tenant to
/// one-per-environment.
///
/// The danger in this change is not the new network — it is the moment in between. A service that
/// has redeployed lives on its environment's network; one that has not is still only on the
/// workspace network. If the redeployed service moved outright, it would stop being reachable by the
/// ones that had not caught up, and nothing would say why: the hostname still resolves in the panel's
/// own configuration, it just answers nowhere.
///
/// So a container joins both, and the workspace network is only dropped once nothing needs it. That
/// is a decision about a running system, which is why it is a rule with tests rather than a line
/// inside the pipeline.
/// </summary>
public static class NetworkPlan
{
    /// <summary>
    /// The networks to attach, in order. The first is the primary — the one the proxy is pointed at.
    /// </summary>
    public static IReadOnlyList<string> For(string? environmentNetwork, string workspaceNetwork, bool keepWorkspaceNetwork)
    {
        if (string.IsNullOrWhiteSpace(environmentNetwork))
            return [workspaceNetwork];

        return keepWorkspaceNetwork && !string.IsNullOrWhiteSpace(workspaceNetwork)
            ? [environmentNetwork, workspaceNetwork]
            : [environmentNetwork];
    }

    /// <summary>
    /// The network a container is addressed on. Always the environment's once it has one: a service
    /// on both networks answers on either, and picking the narrower one keeps the transition from
    /// quietly becoming permanent.
    /// </summary>
    public static string Primary(string? environmentNetwork, string workspaceNetwork) =>
        string.IsNullOrWhiteSpace(environmentNetwork) ? workspaceNetwork : environmentNetwork;
}
