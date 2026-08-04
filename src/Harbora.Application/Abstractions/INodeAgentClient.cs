namespace Harbora.Application.Abstractions;

/// <summary>What a node can do, as far as the control plane is concerned.</summary>
/// <param name="SupportsTcpTunnel">Whether it can open an outbound tunnel for database access.</param>
/// <param name="SupportsCredentialRotation">Whether it can rotate a managed service's password.</param>
/// <param name="AgentVersion">Null when the node has not reported one.</param>
public sealed record NodeCapabilities(
    bool SupportsTcpTunnel,
    bool SupportsCredentialRotation,
    string? AgentVersion,
    string? Architecture);

/// <summary>A tunnel the node holds open so an outside client can reach a service.</summary>
/// <param name="TunnelId">The node's handle for it — used to take it down again.</param>
/// <param name="GatewayHost">The address a customer connects to. Never the node's own address.</param>
/// <param name="GatewayPort">The reserved port on the gateway.</param>
public sealed record TcpTunnel(string TunnelId, string GatewayHost, int GatewayPort);

/// <summary>The result of asking a node to do something, without leaking how it failed.</summary>
public sealed record NodeResult(bool Ok, string? Error);

/// <summary>
/// Everything the control plane asks of a node.
///
/// Deliberately a narrow, dumb contract. No plan, permission, TTL or billing logic lives behind it —
/// those are control-plane decisions, and a node that could make them would be a node that has to be
/// trusted to make them correctly. The node is told what to do; whether it should happen is settled
/// before the call.
///
/// The real agent lands separately. Until then a fake implements this same contract, which is why
/// the contract is written first: business logic built against a fake and a real agent must not need
/// rewriting when the second one arrives.
/// </summary>
public interface INodeAgentClient
{
    /// <summary>
    /// True when this client only pretends to reach a node.
    ///
    /// A default of false so the real agent needs no change to answer correctly, and so a client
    /// that forgets to answer is treated as real rather than quietly disabling the feature.
    ///
    /// It exists because of what the alternative looks like: a page that issues a username, a
    /// password and a connection string pointing at a gateway nobody opened. Harbora's records show
    /// an active grant; the customer gets a name-resolution error and reports a broken database.
    /// </summary>
    bool IsSimulated => false;

    Task<NodeCapabilities> GetCapabilitiesAsync(Guid serverId, CancellationToken ct);

    Task<NodeResult> DeployWorkloadAsync(Guid serverId, string workloadId, string image, CancellationToken ct);
    Task<NodeResult> UpdateWorkloadAsync(Guid serverId, string workloadId, string image, CancellationToken ct);
    Task<string?> GetWorkloadStatusAsync(Guid serverId, string workloadId, CancellationToken ct);

    /// <summary>
    /// Creates a database login for outside use. The node makes the account; it does not decide who
    /// may have one, how long it lives, or which addresses may use it.
    /// </summary>
    Task<NodeResult> CreateDatabaseGrantAsync(
        Guid serverId, string containerName, string username, string password, CancellationToken ct);

    Task<NodeResult> RevokeDatabaseGrantAsync(
        Guid serverId, string containerName, string username, CancellationToken ct);

    Task<NodeResult> RotateDatabaseCredentialAsync(
        Guid serverId, string containerName, string username, string newPassword, CancellationToken ct);

    /// <summary>
    /// Asks the node to open an outbound tunnel to the gateway.
    ///
    /// Outbound on purpose: the alternative is publishing a database port on the node, which puts a
    /// customer's data one firewall rule away from the internet and tells every connecting client
    /// what the node's address is.
    /// </summary>
    Task<TcpTunnel?> CreateTcpTunnelAsync(
        Guid serverId, string containerName, int containerPort, CancellationToken ct);

    Task<NodeResult> RemoveTcpTunnelAsync(Guid serverId, string tunnelId, CancellationToken ct);
}
