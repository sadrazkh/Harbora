using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Nodes;
using Harbora.NodeAgent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// The control plane's half of one node's channel: negotiate, resume, then handle frames until the
/// node goes away.
///
/// <para>
/// Database access goes through a short scope per write rather than one context for the session.
/// A channel lives for days; a DbContext held that long accumulates a change tracker nobody clears
/// and holds a pooled connection nobody else can use.
/// </para>
/// </summary>
public sealed class NodeChannelSession(
    IServiceScopeFactory scopeFactory,
    NodeChannelRegistry registry,
    IOptions<NodeAgentControlPlaneOptions> options,
    TimeProvider clock,
    ILoggerFactory loggerFactory,
    ILogger<NodeChannelSession> log)
{
    /// <summary>
    /// How long the node has to send its hello. Short: a socket that connects and says nothing is
    /// worse than one that fails to connect, because it looks like a healthy session.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly NodeAgentControlPlaneOptions _options = options.Value;

    /// <summary>Run one session to completion. Returns when the node disconnects or is refused.</summary>
    public async Task RunAsync(WebSocket socket, X509Certificate2 clientCertificate, CancellationToken ct)
    {
        var node = await AuthenticateAsync(clientCertificate, ct);

        if (node is null)
        {
            await CloseWithAsync(socket, WebSocketCloseStatus.PolicyViolation,
                "This certificate is not a currently valid node credential.");
            return;
        }

        var hello = await ReadHelloAsync(socket, node.NodeId, ct);

        if (hello is null)
        {
            await CloseWithAsync(socket, WebSocketCloseStatus.ProtocolError, "Expected a hello frame.");
            return;
        }

        if (!hello.SupportedProtocolVersions.Contains(NodeContract.ProtocolVersion))
        {
            // Refused rather than downgraded. Half a protocol is worse than none, and a node that is
            // told which versions the panel speaks can be updated to one of them.
            log.LogWarning(
                "Node {NodeId} offered protocol v{Offered} and this panel speaks v{Ours}; refusing the session.",
                node.NodeId, string.Join(",", hello.SupportedProtocolVersions), NodeContract.ProtocolVersion);

            await CloseWithAsync(socket, WebSocketCloseStatus.ProtocolError,
                $"This panel speaks protocol v{NodeContract.ProtocolVersion}.");
            return;
        }

        var resumed = hello.ResumeToken is { Length: > 0 } &&
                      string.Equals(hello.ResumeToken, node.ResumeToken, StringComparison.Ordinal);

        var resumeToken = resumed ? node.ResumeToken! : NewResumeToken();
        var grantedScopes = Deserialize<List<string>>(node.GrantedScopesJson) ?? NodeScopes.Default.ToList();

        var connection = new NodeConnection(
            node.NodeId, node.Id, socket, resumeToken, grantedScopes, clock, loggerFactory.CreateLogger<NodeConnection>());

        if (resumed) connection.RecordReceived(node.LastReceivedSequence);

        await using var registration = await registry.RegisterAsync(connection);

        await MarkConnectedAsync(node.NodeId, hello, resumeToken, resumed, ct);

        await connection.SendFrameAsync(ControlFrame.Create(ControlFrames.HelloAck, new ControlHelloAck
        {
            ProtocolVersion = NodeContract.ProtocolVersion,
            ResumeToken = resumeToken,
            ServerTime = clock.GetUtcNow(),
            // Only what is durably stored. A node trims its outbox on this number, and it cannot get
            // those frames back if we claimed more than we wrote.
            LastReceivedSequence = resumed ? node.LastReceivedSequence : 0,
            MinimumAgentVersion = _options.MinimumAgentVersion,
            HeartbeatIntervalSeconds = _options.HeartbeatIntervalSeconds,
            GrantedScopes = grantedScopes,
            ResumeRejected = !resumed,
        }), ct);

        log.LogInformation(
            "Node {NodeId} ({Name}) session open: agent {Agent}, resume {Resume}.",
            node.NodeId, node.Name, hello.AgentVersion, resumed ? "accepted" : "rejected");

        try
        {
            await PumpAsync(connection, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is WebSocketException or InvalidDataException or IOException)
        {
            log.LogWarning(e, "Node {NodeId} session ended abnormally.", node.NodeId);
        }
        finally
        {
            await MarkDisconnectedAsync(node.NodeId, connection.LastReceivedSequence, CancellationToken.None);
            await connection.CloseAsync("session ended");
        }
    }

    // --- authentication ---

    /// <summary>
    /// A certificate gets a session only when it chains to our CA <em>and</em> is the one on record
    /// for a node that is not revoked. The chain alone would let any node's certificate open any
    /// node's session.
    /// </summary>
    private async Task<Node?> AuthenticateAsync(X509Certificate2 certificate, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var authority = scope.ServiceProvider.GetRequiredService<NodeCertificateAuthority>();

        if (!await authority.ValidatesAsync(certificate, ct))
        {
            log.LogWarning("Refused a node channel: the certificate does not chain to this panel's node CA.");
            return null;
        }

        // IgnoreQueryFilters: this request has no session, so a filtered read would find nothing and
        // every node on the platform would be refused with a confusing message.
        var node = await db.Nodes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.CertificateThumbprint == certificate.Thumbprint, ct);

        if (node is null)
        {
            log.LogWarning(
                "Refused a node channel: certificate {Thumbprint} chains to the CA but is not the current credential of any node. " +
                "This is what a superseded certificate looks like after a rotation.",
                certificate.Thumbprint);
            return null;
        }

        if (node.IsRevoked)
        {
            log.LogWarning("Refused a node channel: node {NodeId} was revoked{Reason}.",
                node.NodeId, node.RevokedReason is null ? "" : $" ({node.RevokedReason})");
            return null;
        }

        return node;
    }

    private async Task<NodeHello?> ReadHelloAsync(WebSocket socket, string nodeId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);

        var probe = new NodeConnection(nodeId, Guid.Empty, socket, string.Empty, [], clock,
            loggerFactory.CreateLogger<NodeConnection>());

        try
        {
            while (true)
            {
                var frame = await probe.ReceiveAsync(timeout.Token);
                if (frame is null) return null;

                if (frame.Type is NodeFrames.Hello or NodeFrames.Resume)
                    return frame.PayloadAs<NodeHello>();

                // A frame before the handshake has no negotiated version to be read under, so it is
                // not merely early — it is unreadable.
                log.LogWarning("Node {NodeId} sent a {Type} frame before its hello; ignoring it.", nodeId, frame.Type);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Node {NodeId} did not send a hello within {Timeout}.", nodeId, HandshakeTimeout);
            return null;
        }
    }

    // --- the loop ---

    private async Task PumpAsync(NodeConnection connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await connection.ReceiveAsync(ct);
            if (frame is null) return;

            if (frame.V != NodeContract.ProtocolVersion)
            {
                log.LogWarning(
                    "Dropping a {Type} frame from node {NodeId} sent with protocol v{Version}.",
                    frame.Type, connection.NodeId, frame.V);
                continue;
            }

            var durable = await HandleAsync(connection, frame, ct);

            // Acknowledged only after the frame is durable, and only for frames that carry a
            // sequence. Acking a frame we merely received would let the node discard something we
            // then lose.
            if (durable && frame.Sequence > 0)
            {
                connection.RecordReceived(frame.Sequence);
                await connection.AcknowledgeAsync(connection.LastReceivedSequence, ct);
            }
        }
    }

    /// <summary>Returns whether the frame reached durable storage (or needed no storage).</summary>
    private async Task<bool> HandleAsync(NodeConnection connection, ControlFrame frame, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case NodeFrames.Heartbeat when frame.PayloadAs<NodeHeartbeat>() is { } heartbeat:
                await ApplyHeartbeatAsync(connection.NodeId, heartbeat, ct);
                return true;

            case NodeFrames.Inventory when frame.PayloadAs<NodeInventory>() is { } inventory:
                await ApplyInventoryAsync(connection.NodeId, inventory, ct);
                return true;

            case NodeFrames.CommandAck when frame.PayloadAs<CommandAck>() is { } ack:
                connection.CompleteAck(ack);
                await RecordAckAsync(ack, ct);
                return true;

            case NodeFrames.CommandResult when frame.PayloadAs<CommandResult>() is { } result:
                // Persisted first, then handed to whoever is waiting: a caller that returns to the
                // browser before the row is written would show an outcome the next page load denies.
                await RecordResultAsync(connection.NodeId, result, ct);
                connection.CompleteCommand(result);
                return true;

            case NodeFrames.Event when frame.PayloadAs<NodeEvent>() is { } nodeEvent:
                await RecordEventAsync(connection, nodeEvent, ct);
                return true;

            case NodeFrames.LogChunk when frame.PayloadAs<LogChunk>() is { } chunk:
                // Deliberately not stored. Log output belongs to whoever asked for it; keeping every
                // line of every stream would make the node's chattiest feature its most expensive.
                await connection.DispatchLogAsync(chunk);
                return false;

            case NodeFrames.Pong:
                return false;

            default:
                log.LogDebug("Ignoring an unhandled {Type} frame from node {NodeId}.", frame.Type, connection.NodeId);
                return false;
        }
    }

    // --- persistence ---

    private async Task MarkConnectedAsync(string nodeId, NodeHello hello, string resumeToken, bool resumed, CancellationToken ct)
    {
        await UpdateNodeAsync(nodeId, node =>
        {
            node.Status = NodeStatus.Online;
            node.AgentVersion = hello.AgentVersion;
            node.LastConnectedAt = clock.GetUtcNow();
            node.ResumeToken = resumeToken;

            if (!resumed)
            {
                node.LastReceivedSequence = 0;
                node.LastSentSequence = 0;
            }

            NodeEnrollmentService.ApplyInventory(node, hello.Inventory, hello.Capabilities);
        }, ct);

        await SyncSchedulingTargetAsync(nodeId, ct);
    }

    private async Task MarkDisconnectedAsync(string nodeId, long lastReceivedSequence, CancellationToken ct)
    {
        await UpdateNodeAsync(nodeId, node =>
        {
            // Only if this session is still the current one: a node that reconnected while this loop
            // was unwinding is online, and marking it offline here would flap it for no reason.
            if (registry.IsConnected(nodeId)) return;

            node.Status = node.IsRevoked ? NodeStatus.Revoked : NodeStatus.Offline;
            node.DisconnectedAt = clock.GetUtcNow();
            node.LastReceivedSequence = Math.Max(node.LastReceivedSequence, lastReceivedSequence);
        }, ct);

        // The scheduler reads Server.Status, so a node that went away has to be marked there too —
        // otherwise the next placement picks a machine nobody can reach.
        await SyncSchedulingTargetAsync(nodeId, ct);
    }

    private async Task ApplyHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat, CancellationToken ct)
    {
        await UpdateNodeAsync(nodeId, node =>
        {
            node.LastHeartbeatAt = heartbeat.At;
            node.Health = heartbeat.Health.ToString().ToLowerInvariant();
            node.AgentVersion = heartbeat.AgentVersion;
            node.Draining = heartbeat.Draining;
            node.FreeMemoryBytes = heartbeat.FreeMemoryBytes;
            node.FreeDiskBytes = heartbeat.FreeDiskBytes;
            node.Load1 = heartbeat.Load1;
            node.RunningWorkloads = heartbeat.RunningWorkloads;
            node.ActiveDatabaseGrants = heartbeat.ActiveDatabaseGrants;
            node.ActiveTunnels = heartbeat.ActiveTunnels;
            node.CertificateNotAfter = heartbeat.CertificateExpiresAt ?? node.CertificateNotAfter;

            node.Status = node.IsRevoked ? NodeStatus.Revoked
                : heartbeat.Draining ? NodeStatus.Draining
                : NodeStatus.Online;
        }, ct);

        await SyncSchedulingTargetAsync(nodeId, ct);
    }

    private async Task ApplyInventoryAsync(string nodeId, NodeInventory inventory, CancellationToken ct)
    {
        await UpdateNodeAsync(nodeId, node =>
        {
            node.InventoryJson = JsonSerializer.Serialize(inventory, NodeContract.Json);
            NodeEnrollmentService.ApplyInventory(node, inventory, Deserialize<NodeCapabilities>(node.CapabilitiesJson)
                ?? new NodeCapabilities
                {
                    AgentVersion = node.AgentVersion,
                    SupportedProtocolVersions = [NodeContract.ProtocolVersion],
                    SupportedCommands = [],
                });
        }, ct);
    }

    private async Task RecordAckAsync(CommandAck ack, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var record = await db.NodeCommands.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CommandId == ack.CommandId, ct);

        if (record is null) return;

        record.AcknowledgedAt = ack.AcceptedAt;
        record.IdempotentReplay = ack.Deduplicated;

        if (ack.Rejected is { } rejection)
        {
            record.Status = NodeCommandStatus.Rejected;
            record.ErrorCode = rejection.Code.ToString();
            record.ErrorMessage = rejection.Message;
            record.CompletedAt = clock.GetUtcNow();
        }
        else if (record.Status is NodeCommandStatus.Queued or NodeCommandStatus.Sent)
        {
            record.Status = NodeCommandStatus.Acknowledged;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task RecordResultAsync(string nodeId, CommandResult result, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var record = await db.NodeCommands.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CommandId == result.CommandId, ct);

        if (record is null)
        {
            log.LogWarning(
                "Node {NodeId} reported a result for command {CommandId}, which this panel has no record of. " +
                "That is what a result replayed after a database restore looks like.",
                nodeId, result.CommandId);
            return;
        }

        record.Status = result.Status switch
        {
            CommandStatus.Succeeded => NodeCommandStatus.Succeeded,
            CommandStatus.Failed => NodeCommandStatus.Failed,
            CommandStatus.Cancelled => NodeCommandStatus.Cancelled,
            CommandStatus.TimedOut => NodeCommandStatus.TimedOut,
            _ => NodeCommandStatus.Rejected,
        };

        record.CompletedAt = result.CompletedAt;
        record.IdempotentReplay = result.IdempotentReplay;
        record.ResultJson = result.Result?.GetRawText();
        record.ErrorCode = result.Error?.Code.ToString();
        record.ErrorMessage = result.Error?.Message;

        await db.SaveChangesAsync(ct);
    }

    private async Task RecordEventAsync(NodeConnection connection, NodeEvent nodeEvent, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        db.NodeEvents.Add(new NodeEventRecord
        {
            NodeRowId = connection.NodeRowId,
            NodeId = connection.NodeId,
            Kind = nodeEvent.Kind,
            Message = nodeEvent.Message,
            WorkloadId = nodeEvent.WorkloadId,
            DataJson = nodeEvent.Data is null ? null : JsonSerializer.Serialize(nodeEvent.Data, NodeContract.Json),
            At = nodeEvent.At,
        });

        await db.SaveChangesAsync(ct);

        // Surfaced in the panel's log too: a rolled-back deploy on a node is something an operator
        // looking at the panel's own journal should not have to go to the node to discover.
        log.LogInformation("Node {NodeId} event {Kind}: {Message}", connection.NodeId, nodeEvent.Kind, nodeEvent.Message);
    }

    private async Task UpdateNodeAsync(string nodeId, Action<Node> mutate, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (node is null) return;

        mutate(node);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Keep the node's scheduling target in step with what it just reported.
    ///
    /// <para>
    /// Runs after the node row is written, in its own scope, and never throws into the session: a
    /// projection that fails to update is a scheduler working from slightly stale capacity, which
    /// is survivable. Dropping the channel over it would not be.
    /// </para>
    /// </summary>
    private async Task SyncSchedulingTargetAsync(string nodeId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NodeServerLink>().SyncAsync(nodeId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not refresh the scheduling target for node {NodeId}.", nodeId);
        }
    }

    private static async Task CloseWithAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseAsync(status, reason, timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
    }

    private static string NewResumeToken() => "sess_" + RandomNumberGenerator.GetHexString(24, lowercase: true);

    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, NodeContract.Json); }
        catch (JsonException) { return null; }
    }
}
