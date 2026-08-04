using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Nodes;
using Harbora.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// Makes a v1 node visible to the scheduler by giving it a <see cref="Server"/> row.
///
/// <para>
/// The platform schedules onto Servers: <c>NodeCapacityService</c> reads that table, the scheduler
/// reads capacity, and an app carries a <c>ServerId</c>. A Node with no Server row is enrolled,
/// connected and commandable — and invisible to every one of those. Rather than teach the scheduler
/// about a second kind of target, a node projects itself into the model that already exists.
/// </para>
///
/// <para>
/// The Server row is a projection, not a second source of truth: capacity and status are rewritten
/// from the node's own reports on every heartbeat, and the fields that would let the old inbound
/// agent be reached (<c>AgentEndpoint</c>, <c>AgentTokenHash</c>) are deliberately left null. That
/// null is what <see cref="NodeAwareServerEngineFactory"/> keys on, and it is why that factory must
/// never treat "no endpoint" as "the local machine".
/// </para>
/// </summary>
public sealed class NodeServerLink(
    HarboraDbContext db,
    IOptions<NodeAgentControlPlaneOptions> options,
    ILogger<NodeServerLink> log)
{
    private readonly NodeAgentControlPlaneOptions _options = options.Value;

    /// <summary>
    /// Create or refresh the node's scheduling target, and return its id.
    ///
    /// <para>
    /// Returns null when the operator has turned auto-registration off and the node has never been
    /// linked by hand — a node enrolled purely to publish a database should not silently become a
    /// deploy target.
    /// </para>
    /// </summary>
    public async Task<Guid?> SyncAsync(string nodeId, CancellationToken ct)
    {
        // IgnoreQueryFilters throughout: this runs from the channel session, which has no session
        // of its own. A filtered read would find nothing and quietly create a second Server row on
        // every heartbeat.
        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (node is null) return null;

        var server = node.ServerId is { } serverId
            ? await db.Servers.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == serverId, ct)
            : null;

        if (server is null)
        {
            if (!_options.AutoRegisterAsServer) return null;

            server = new Server
            {
                IsLocal = false,
                // Null on purpose. This is what tells the engine factory the node speaks the v1
                // contract rather than the old inbound HTTP agent.
                AgentEndpoint = null,
                AgentTokenHash = null,
                // Set once, at creation. A plan restricts placement by pool, and an operator who
                // retags the pool afterwards should not have it undone by the next heartbeat.
                Pool = node.Region ?? string.Empty,
            };

            db.Servers.Add(server);
            node.ServerId = server.Id;

            log.LogInformation(
                "Node {NodeId} ({Name}) is now a scheduling target (server {ServerId}).",
                node.NodeId, node.Name, server.Id);
        }

        Project(node, server);

        await db.SaveChangesAsync(ct);
        return server.Id;
    }

    /// <summary>
    /// Attach a node to the scheduler by hand, for an install that turned auto-registration off.
    /// </summary>
    public async Task<Guid?> AttachAsync(string nodeId, CancellationToken ct)
    {
        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (node is null) return null;

        if (node.ServerId is { } existing) return existing;

        var server = new Server
        {
            IsLocal = false,
            AgentEndpoint = null,
            AgentTokenHash = null,
            Pool = node.Region ?? string.Empty,
        };
        db.Servers.Add(server);
        node.ServerId = server.Id;

        Project(node, server);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Node {NodeId} attached to the scheduler as server {ServerId}.", nodeId, server.Id);
        return server.Id;
    }

    /// <summary>
    /// Stop scheduling onto a node.
    ///
    /// <para>
    /// Refused while anything is placed on it. Removing the row under a running app would leave the
    /// app pointing at a server that does not exist — the panel would show it as deployed and have
    /// no way to reach, stop or delete it.
    /// </para>
    /// </summary>
    public async Task<DetachResult> DetachAsync(string nodeId, CancellationToken ct)
    {
        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (node?.ServerId is not { } serverId) return new DetachResult(false, "This node is not a scheduling target.");

        // Platform-wide, not workspace-scoped: the node may hold another tenant's app, and detaching
        // must be blocked by any of them rather than by the ones this admin can see.
        var apps = await db.Apps.IgnoreQueryFilters().CountAsync(a => a.ServerId == serverId, ct);
        var services = await db.ManagedServices.IgnoreQueryFilters().CountAsync(s => s.ServerId == serverId, ct);

        if (apps + services > 0)
            return new DetachResult(false,
                $"Move or delete this node's {apps} app(s) and {services} service(s) first.");

        var server = await db.Servers.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == serverId, ct);
        if (server is not null) db.Servers.Remove(server);

        node.ServerId = null;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Node {NodeId} is no longer a scheduling target.", nodeId);
        return new DetachResult(true, null);
    }

    /// <summary>Whatever the node last reported, written onto its Server row.</summary>
    private static void Project(Node node, Server server)
    {
        server.Name = node.Name;
        server.CpuCores = node.CpuCores;
        server.TotalMemoryBytes = node.TotalMemoryBytes;
        server.TotalDiskBytes = node.TotalDiskBytes;
        server.DockerVersion = node.ContainerRuntimeVersion;
        server.LastHeartbeatAt = node.LastHeartbeatAt;

        // A draining node reports Degraded rather than Offline: the scheduler skips anything that is
        // not Online, and Offline would also make the panel show it as unreachable, which it is not.
        server.Status = node.IsRevoked ? ServerStatus.Offline
            : node.Draining ? ServerStatus.Degraded
            : node.Status switch
            {
                NodeStatus.Online when node.Health == "healthy" => ServerStatus.Online,
                NodeStatus.Online => ServerStatus.Degraded,
                NodeStatus.Draining => ServerStatus.Degraded,
                NodeStatus.Offline => ServerStatus.Offline,
                _ => ServerStatus.Unknown,
            };

        ProjectHostname(node, server);
    }

    /// <summary>
    /// The address the panel's proxy sends traffic to for apps on this node.
    ///
    /// <para>
    /// An outbound-only node still has to be reachable <em>inbound</em> on its published ports for
    /// HTTP ingress to work, because there is no shared overlay between the panel and the node and
    /// the v1 contract has no reverse HTTP tunnel yet. So this is the node's own reported address,
    /// which is right for a routable fleet and wrong for a node behind NAT — where the deploy will
    /// succeed and the route will time out. That case is documented rather than guessed at.
    /// </para>
    ///
    /// <para>
    /// An operator who points a DNS name at the node keeps it. The test is what the stored value
    /// looks like rather than whether the node still reports it: a raw address is this method's own
    /// output and gets refreshed — otherwise a node that changed address would keep routing traffic
    /// to the old one forever — while a name is something only a person types, so it is left alone.
    /// </para>
    /// </summary>
    private static void ProjectHostname(Node node, Server server)
    {
        var addresses = Deserialize(node.IpAddressesJson);

        var derived = addresses
                          .Select(a => a.Trim())
                          .Where(a => a.Length > 0 && IPAddress.TryParse(a, out _))
                          .Select(a => (Address: a, Parsed: IPAddress.Parse(a)))
                          .Where(a => !IPAddress.IsLoopback(a.Parsed))
                          // A globally routable v4 address first: it is the one a panel on another
                          // network can actually open a socket to.
                          .OrderByDescending(a => a.Parsed.AddressFamily == AddressFamily.InterNetwork && IsGlobal(a.Parsed))
                          .ThenByDescending(a => a.Parsed.AddressFamily == AddressFamily.InterNetwork)
                          .Select(a => a.Address)
                          .FirstOrDefault()
                      ?? node.Name;

        var current = server.Hostname;

        var isStillNodeDerived =
            string.IsNullOrWhiteSpace(current) ||
            current == "localhost" ||
            current == node.Name ||
            IPAddress.TryParse(current, out _);

        if (isStillNodeDerived) server.Hostname = derived;
    }

    /// <summary>Not RFC1918, not link-local, not carrier-grade NAT.</summary>
    private static bool IsGlobal(IPAddress address)
    {
        var octets = address.GetAddressBytes();

        return octets switch
        {
            [10, ..] => false,
            [172, >= 16 and <= 31, ..] => false,
            [192, 168, ..] => false,
            [169, 254, ..] => false,
            [100, >= 64 and <= 127, ..] => false,
            [127, ..] => false,
            _ => true,
        };
    }

    private static List<string> Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}

public sealed record DetachResult(bool Ok, string? Reason);
