using System.Collections.Concurrent;
using Harbora.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// A node client for development, before the real agent exists.
///
/// It implements the contract honestly rather than agreeably: it records what it was asked to do so
/// tests can assert on it, and it refuses the things a real node would refuse. A fake that always
/// succeeds teaches the code above it that failure never happens, and that lesson is unlearned in
/// production.
///
/// It is registered only when no real agent is configured, and it says so on every call — a silent
/// fake in a production deployment would report tunnels that do not exist.
/// </summary>
public sealed class FakeNodeAgentClient(ILogger<FakeNodeAgentClient> logger) : INodeAgentClient
{
    private readonly ConcurrentDictionary<string, TcpTunnel> _tunnels = new();
    private readonly ConcurrentDictionary<string, string> _grants = new();

    /// <summary>Everything asked of it, in order, for tests to assert against.</summary>
    public IReadOnlyCollection<string> Calls => _calls;
    private readonly ConcurrentQueue<string> _calls = new();

    private void Record(string call)
    {
        _calls.Enqueue(call);
        logger.LogWarning("Node agent is not configured; {Call} was simulated and nothing was changed.", call);
    }

    public Task<NodeCapabilities> GetCapabilitiesAsync(Guid serverId, CancellationToken ct)
    {
        Record($"capabilities:{serverId}");
        return Task.FromResult(new NodeCapabilities(true, true, "fake-0.0", "amd64"));
    }

    public Task<NodeResult> DeployWorkloadAsync(Guid serverId, string workloadId, string image, CancellationToken ct)
    {
        Record($"deploy:{workloadId}");
        return Task.FromResult(new NodeResult(true, null));
    }

    public Task<NodeResult> UpdateWorkloadAsync(Guid serverId, string workloadId, string image, CancellationToken ct)
    {
        Record($"update:{workloadId}");
        return Task.FromResult(new NodeResult(true, null));
    }

    public Task<string?> GetWorkloadStatusAsync(Guid serverId, string workloadId, CancellationToken ct)
    {
        Record($"status:{workloadId}");
        return Task.FromResult<string?>("running");
    }

    public Task<NodeResult> CreateDatabaseGrantAsync(
        Guid serverId, string containerName, string username, string password, CancellationToken ct)
    {
        Record($"grant:{containerName}:{username}");

        // A real node cannot create the same login twice, and code that assumes it can would only
        // find out on a retry in production.
        if (!_grants.TryAdd($"{containerName}/{username}", username))
            return Task.FromResult(new NodeResult(false, "That login already exists on the database."));

        return Task.FromResult(new NodeResult(true, null));
    }

    public Task<NodeResult> RevokeDatabaseGrantAsync(
        Guid serverId, string containerName, string username, CancellationToken ct)
    {
        Record($"revoke:{containerName}:{username}");

        // Revoking something that is already gone is success, not an error: the sweeper and a person
        // pressing the button can race, and both should end up with the access closed.
        _grants.TryRemove($"{containerName}/{username}", out _);
        return Task.FromResult(new NodeResult(true, null));
    }

    public Task<NodeResult> RotateDatabaseCredentialAsync(
        Guid serverId, string containerName, string username, string newPassword, CancellationToken ct)
    {
        Record($"rotate:{containerName}:{username}");

        if (!_grants.ContainsKey($"{containerName}/{username}"))
            return Task.FromResult(new NodeResult(false, "No such login to rotate."));

        return Task.FromResult(new NodeResult(true, null));
    }

    public Task<TcpTunnel?> CreateTcpTunnelAsync(
        Guid serverId, string containerName, int containerPort, CancellationToken ct)
    {
        Record($"tunnel:{containerName}:{containerPort}");

        var tunnel = new TcpTunnel(
            TunnelId: Guid.CreateVersion7().ToString("N"),
            // Deliberately not the node's address: the whole design is that a customer connects to
            // the gateway and never learns where their database actually runs.
            GatewayHost: "gateway.invalid",
            GatewayPort: 20000 + Random.Shared.Next(1000));

        _tunnels[tunnel.TunnelId] = tunnel;
        return Task.FromResult<TcpTunnel?>(tunnel);
    }

    public Task<NodeResult> RemoveTcpTunnelAsync(Guid serverId, string tunnelId, CancellationToken ct)
    {
        Record($"tunnel-remove:{tunnelId}");
        _tunnels.TryRemove(tunnelId, out _);
        return Task.FromResult(new NodeResult(true, null));
    }

    /// <summary>Tunnels believed to be open — so a test can prove cleanup actually happened.</summary>
    public int OpenTunnels => _tunnels.Count;

    /// <summary>Logins believed to exist.</summary>
    public int OpenGrants => _grants.Count;
}
