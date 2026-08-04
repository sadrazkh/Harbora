namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// The complete set of operations a control plane may ask a node to perform — and, by omission,
/// everything it may not. There is deliberately no "run this shell command" member: the node
/// exposes verbs, not a terminal, so a compromised or coerced control plane still cannot turn a
/// customer's server into an arbitrary-execution endpoint.
/// </summary>
public static class NodeCommands
{
    public const string DeployWorkload = "DeployWorkload";
    public const string UpdateWorkload = "UpdateWorkload";
    public const string StopWorkload = "StopWorkload";
    public const string StartWorkload = "StartWorkload";
    public const string RestartWorkload = "RestartWorkload";
    public const string DeleteWorkload = "DeleteWorkload";
    public const string GetWorkloadStatus = "GetWorkloadStatus";
    public const string ListWorkloads = "ListWorkloads";
    public const string StreamLogs = "StreamLogs";

    public const string CreateNetwork = "CreateNetwork";
    public const string DeleteNetwork = "DeleteNetwork";

    public const string CreateVolume = "CreateVolume";
    public const string SnapshotVolume = "SnapshotVolume";
    public const string RestoreVolume = "RestoreVolume";

    public const string CreateDatabaseAccessGrant = "CreateDatabaseAccessGrant";
    public const string RevokeDatabaseAccessGrant = "RevokeDatabaseAccessGrant";
    public const string RotateDatabaseAccessCredential = "RotateDatabaseAccessCredential";

    public const string RegisterHttpRoute = "RegisterHttpRoute";
    public const string RegisterTcpRoute = "RegisterTcpRoute";
    public const string RemoveRoute = "RemoveRoute";
    public const string ConfigureIngress = "ConfigureIngress";

    public const string DrainNode = "DrainNode";
    public const string UpdateAgent = "UpdateAgent";
}

/// <summary>
/// Coarse permission buckets. A node is enrolled with a set of these; a command whose required
/// scope is outside that set is refused before its payload is even parsed, which keeps an
/// unauthorised command from reaching any code that could act on its contents.
/// </summary>
public static class NodeScopes
{
    public const string WorkloadsRead = "workloads:read";
    public const string WorkloadsWrite = "workloads:write";
    public const string NetworksWrite = "networks:write";
    public const string VolumesWrite = "volumes:write";
    public const string DatabaseAccessWrite = "database-access:write";
    public const string RoutesWrite = "routes:write";
    public const string NodeAdmin = "node:admin";

    /// <summary>What a node is granted when the control plane sends no explicit scope list.</summary>
    public static readonly IReadOnlyList<string> Default =
    [
        WorkloadsRead, WorkloadsWrite, NetworksWrite, VolumesWrite,
        DatabaseAccessWrite, RoutesWrite, NodeAdmin,
    ];
}

/// <summary>Static facts about a command: which scope it needs and how long it may reasonably take.</summary>
public sealed record NodeCommandDescriptor(string Name, string RequiredScope, int DefaultTimeoutSeconds, bool Mutating);

/// <summary>
/// The allowlist itself. <see cref="TryGet"/> failing is the single gate every inbound command
/// passes through, so adding a verb means adding a row here — there is no dynamic dispatch path
/// that could reach a handler this table does not name.
/// </summary>
public static class NodeCommandCatalog
{
    private static readonly Dictionary<string, NodeCommandDescriptor> Map =
        new(StringComparer.Ordinal)
        {
            [NodeCommands.DeployWorkload] = new(NodeCommands.DeployWorkload, NodeScopes.WorkloadsWrite, 1800, true),
            [NodeCommands.UpdateWorkload] = new(NodeCommands.UpdateWorkload, NodeScopes.WorkloadsWrite, 1800, true),
            [NodeCommands.StopWorkload] = new(NodeCommands.StopWorkload, NodeScopes.WorkloadsWrite, 300, true),
            [NodeCommands.StartWorkload] = new(NodeCommands.StartWorkload, NodeScopes.WorkloadsWrite, 300, true),
            [NodeCommands.RestartWorkload] = new(NodeCommands.RestartWorkload, NodeScopes.WorkloadsWrite, 300, true),
            [NodeCommands.DeleteWorkload] = new(NodeCommands.DeleteWorkload, NodeScopes.WorkloadsWrite, 600, true),
            [NodeCommands.GetWorkloadStatus] = new(NodeCommands.GetWorkloadStatus, NodeScopes.WorkloadsRead, 60, false),
            [NodeCommands.ListWorkloads] = new(NodeCommands.ListWorkloads, NodeScopes.WorkloadsRead, 60, false),
            [NodeCommands.StreamLogs] = new(NodeCommands.StreamLogs, NodeScopes.WorkloadsRead, 3600, false),

            [NodeCommands.CreateNetwork] = new(NodeCommands.CreateNetwork, NodeScopes.NetworksWrite, 120, true),
            [NodeCommands.DeleteNetwork] = new(NodeCommands.DeleteNetwork, NodeScopes.NetworksWrite, 120, true),

            [NodeCommands.CreateVolume] = new(NodeCommands.CreateVolume, NodeScopes.VolumesWrite, 120, true),
            [NodeCommands.SnapshotVolume] = new(NodeCommands.SnapshotVolume, NodeScopes.VolumesWrite, 3600, true),
            [NodeCommands.RestoreVolume] = new(NodeCommands.RestoreVolume, NodeScopes.VolumesWrite, 3600, true),

            [NodeCommands.CreateDatabaseAccessGrant] = new(NodeCommands.CreateDatabaseAccessGrant, NodeScopes.DatabaseAccessWrite, 300, true),
            [NodeCommands.RevokeDatabaseAccessGrant] = new(NodeCommands.RevokeDatabaseAccessGrant, NodeScopes.DatabaseAccessWrite, 120, true),
            [NodeCommands.RotateDatabaseAccessCredential] = new(NodeCommands.RotateDatabaseAccessCredential, NodeScopes.DatabaseAccessWrite, 300, true),

            [NodeCommands.RegisterHttpRoute] = new(NodeCommands.RegisterHttpRoute, NodeScopes.RoutesWrite, 120, true),
            [NodeCommands.RegisterTcpRoute] = new(NodeCommands.RegisterTcpRoute, NodeScopes.RoutesWrite, 120, true),
            [NodeCommands.RemoveRoute] = new(NodeCommands.RemoveRoute, NodeScopes.RoutesWrite, 120, true),
            // routes:write, not node:admin — it decides how traffic reaches this node's routes, and
            // nothing else. A node enrolled without that scope cannot be made to dial the gateway.
            [NodeCommands.ConfigureIngress] = new(NodeCommands.ConfigureIngress, NodeScopes.RoutesWrite, 120, true),

            [NodeCommands.DrainNode] = new(NodeCommands.DrainNode, NodeScopes.NodeAdmin, 1800, true),
            [NodeCommands.UpdateAgent] = new(NodeCommands.UpdateAgent, NodeScopes.NodeAdmin, 1800, true),
        };

    public static IReadOnlyCollection<string> All => Map.Keys;

    public static bool TryGet(string? command, out NodeCommandDescriptor descriptor)
    {
        descriptor = null!;
        return command is not null && Map.TryGetValue(command, out descriptor!);
    }

    /// <summary>
    /// True while the node is draining and the command would create new work. Read-only commands
    /// and the two node-admin verbs stay available, otherwise a drained node could never be told
    /// to update or to stop draining.
    /// </summary>
    public static bool RejectedWhileDraining(string command) =>
        TryGet(command, out var d) && d.Mutating && d.RequiredScope != NodeScopes.NodeAdmin;
}
