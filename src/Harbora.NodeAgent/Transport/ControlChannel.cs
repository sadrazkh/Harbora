using System.Runtime.CompilerServices;
using System.Text.Json;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Transport;

/// <summary>The control plane and this agent could not agree on a protocol version.</summary>
public sealed class ProtocolNegotiationException(string message) : Exception(message);

/// <summary>
/// One session on the persistent outbound channel: negotiate, resume, send, receive.
///
/// <para>
/// The class owns exactly one connection at a time and knows nothing about reconnecting — that
/// belongs to the worker, which is also the only thing that knows whether reconnecting is still
/// wanted. Keeping the two apart is what lets the session's resume logic be tested without a timer.
/// </para>
/// </summary>
public sealed class ControlChannel(
    IOptions<NodeAgentOptions> options,
    IMessageTransportFactory transports,
    ChannelOutbox outbox,
    JsonFileStore<NodeState> stateStore,
    InventoryCollector inventory,
    TimeProvider clock,
    ILogger<ControlChannel> log)
{
    /// <summary>
    /// How long the control plane has to answer a hello. Short: a control plane that accepts the
    /// TCP connection and then says nothing is a worse outcome than a refused one, because the node
    /// sits there believing it is connected.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly NodeAgentOptions _options = options.Value;
    private IMessageTransport? _transport;

    /// <summary>The negotiated session terms, or null while disconnected.</summary>
    public ControlHelloAck? Session { get; private set; }

    public bool IsConnected => _transport is { IsOpen: true } && Session is not null;

    /// <summary>Connect, negotiate and resume. Throws only when reconnecting could not help.</summary>
    public async Task<ControlHelloAck> OpenAsync(NodeIdentity identity, CancellationToken ct)
    {
        var state = stateStore.Load() ?? new NodeState();
        var uri = ChannelUri(state.ControlPlaneUrl ?? _options.ControlPlaneUrl);

        _transport = await transports.ConnectAsync(uri, identity, ct);
        Session = null;

        var hello = new NodeHello
        {
            NodeId = state.NodeId ?? throw new InvalidOperationException("Cannot open a channel before enrollment."),
            AgentVersion = AgentVersion.Current,
            SupportedProtocolVersions = NodeContract.SupportedProtocolVersions,
            ResumeToken = state.ResumeToken,
            LastReceivedSequence = state.LastReceivedSequence,
            Inventory = await inventory.CollectAsync(ct),
            Capabilities = inventory.Capabilities(),
        };

        // The hello itself is not sequenced and not queued: it is the frame that establishes what
        // sequencing means for this session.
        var frameType = state.ResumeToken is null ? NodeFrames.Hello : NodeFrames.Resume;
        await _transport.SendAsync(NodeContract.Serialize(ControlFrame.Create(frameType, hello)), ct);

        var ack = await AwaitHelloAckAsync(ct);

        if (!NodeContract.SupportedProtocolVersions.Contains(ack.ProtocolVersion))
            throw new ProtocolNegotiationException(
                $"The control plane selected protocol v{ack.ProtocolVersion}; this agent speaks {string.Join(", ", NodeContract.SupportedProtocolVersions)}.");

        Session = ack;

        stateStore.Update(s => (s ?? new NodeState()) with
        {
            NegotiatedProtocolVersion = ack.ProtocolVersion,
            ResumeToken = ack.ResumeToken,
            GrantedScopes = ack.GrantedScopes is { Count: > 0 } g ? g : (s?.GrantedScopes ?? []),
            MinimumAgentVersion = ack.MinimumAgentVersion ?? s?.MinimumAgentVersion,
            HeartbeatIntervalSeconds = ack.HeartbeatIntervalSeconds > 0 ? ack.HeartbeatIntervalSeconds : (s?.HeartbeatIntervalSeconds ?? 30),
            LastConnectedAt = clock.GetUtcNow(),
        });

        await SynchroniseAsync(ack, ct);

        log.LogInformation(
            "Control channel open (protocol v{Version}, heartbeat {Heartbeat}s, resume {Resume}).",
            ack.ProtocolVersion, ack.HeartbeatIntervalSeconds, ack.ResumeRejected ? "rejected" : "accepted");

        return ack;
    }

    /// <summary>
    /// Queue a frame and try to send it. A send that fails is not lost: it is already in the
    /// durable outbox and will go out on the next connection.
    /// </summary>
    public async Task SendAsync<T>(string type, T payload, string? correlationId, CancellationToken ct)
    {
        var json = string.Empty;
        var sequence = outbox.Append(seq =>
        {
            json = NodeContract.Serialize(ControlFrame.Create(type, payload, seq, correlationId));
            return json;
        });

        await TrySendAsync(json, sequence, ct);
    }

    /// <summary>
    /// Send without queueing. For frames whose value expires immediately — a heartbeat that failed
    /// to send is worthless a second later, and replaying a queue of them after a reconnect would
    /// tell the panel about a liveness that is now historical.
    /// </summary>
    /// <returns>
    /// True when the frame actually went to the transport. False is not an error — there is no
    /// connection, or one was closing — but a caller that can work the frame out again and resend it
    /// must not treat false as delivered.
    /// </returns>
    public async Task<bool> SendEphemeralAsync<T>(string type, T payload, CancellationToken ct)
    {
        // IsConnected, not merely an open transport: OpenAsync attaches the transport before the
        // hello-ack comes back, and a frame sent in that window reaches the control plane before the
        // handshake that tells it which protocol version the frame is in. A caller that treats "sent"
        // as "told" would move on from it.
        if (!IsConnected || _transport is not { } transport) return false;

        try
        {
            await transport.SendAsync(NodeContract.Serialize(ControlFrame.Create(type, payload)), ct);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
        {
            log.LogDebug(e, "Dropping an ephemeral {Type} frame; the channel is closing.", type);
            return false;
        }
    }

    /// <summary>
    /// Frames from the control plane, with the housekeeping ones handled here rather than passed
    /// on: acks trim the outbox, pings are answered, and neither is something a command loop
    /// should have to know about.
    /// </summary>
    public async IAsyncEnumerable<ControlFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_transport is not { } transport) yield break;

        while (!ct.IsCancellationRequested)
        {
            var raw = await transport.ReceiveAsync(ct);
            if (raw is null)
            {
                log.LogInformation("Control channel closed by the control plane.");
                yield break;
            }

            var frame = TryParse(raw);
            if (frame is null) continue;

            if (Session is { } session && frame.V != session.ProtocolVersion)
            {
                // Dropped rather than guessed at. A frame from a version we did not negotiate may
                // mean something different in fields that share a name.
                log.LogWarning(
                    "Dropping a {Type} frame sent with protocol v{FrameVersion}; this session negotiated v{SessionVersion}.",
                    frame.Type, frame.V, session.ProtocolVersion);
                continue;
            }

            if (frame.Sequence > 0) RecordReceived(frame.Sequence);

            switch (frame.Type)
            {
                case ControlFrames.Ack:
                    HandleAck(frame);
                    continue;

                case ControlFrames.Ping:
                    await SendEphemeralAsync(NodeFrames.Pong, new { at = clock.GetUtcNow() }, ct);
                    continue;

                default:
                    yield return frame;
                    continue;
            }
        }
    }

    public async Task CloseAsync(string reason)
    {
        Session = null;

        if (_transport is not { } transport) return;
        _transport = null;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await transport.CloseAsync(reason, timeout.Token);
        await transport.DisposeAsync();
    }

    /// <summary>Turn the control-plane base URL into the channel's websocket URI.</summary>
    internal static Uri ChannelUri(string controlPlaneUrl)
    {
        var baseUri = new Uri(controlPlaneUrl.TrimEnd('/') + "/");
        var scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";

        return new UriBuilder(new Uri(baseUri, NodeContract.ChannelPath)) { Scheme = scheme }.Uri;
    }

    private async Task<ControlHelloAck> AwaitHelloAckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);

        while (true)
        {
            string? raw;
            try
            {
                raw = await _transport!.ReceiveAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The control plane did not answer the hello within {HandshakeTimeout.TotalSeconds:0}s.");
            }

            if (raw is null)
                throw new IOException("The control plane closed the channel during the handshake.");

            var frame = TryParse(raw);
            if (frame is null) continue;

            if (frame.Type != ControlFrames.HelloAck)
            {
                // A command before the handshake completes has no negotiated version to be read
                // under, so it is not merely early — it is unreadable.
                log.LogWarning("Ignoring a {Type} frame received before the handshake completed.", frame.Type);
                continue;
            }

            return frame.PayloadAs<ControlHelloAck>()
                   ?? throw new IOException("The control plane sent a hello-ack with no payload.");
        }
    }

    /// <summary>Replay what the control plane has not seen, or start clean when it rejected the resume.</summary>
    private async Task SynchroniseAsync(ControlHelloAck ack, CancellationToken ct)
    {
        if (ack.ResumeRejected)
        {
            var discarded = outbox.Pending().Count;
            if (discarded > 0)
                log.LogWarning(
                    "The control plane rejected the resume; discarding {Count} unacknowledged frame(s) it has no session for.",
                    discarded);

            outbox.Reset();

            stateStore.Update(s => (s ?? new NodeState()) with { LastReceivedSequence = 0 });

            await SendAsync(NodeFrames.Inventory, await inventory.CollectAsync(ct), correlationId: null, ct);
            return;
        }

        outbox.AcknowledgeThrough(ack.LastReceivedSequence);

        var pending = outbox.Pending();
        if (pending.Count == 0) return;

        log.LogInformation("Replaying {Count} unacknowledged frame(s) after reconnect.", pending.Count);

        foreach (var entry in pending)
            await TrySendAsync(entry.Json, entry.Sequence, ct);
    }

    private async Task TrySendAsync(string json, long sequence, CancellationToken ct)
    {
        if (_transport is not { IsOpen: true } transport)
        {
            log.LogDebug("Channel is down; frame {Sequence} stays queued.", sequence);
            return;
        }

        try
        {
            await transport.SendAsync(json, ct);
            stateStore.Update(s => (s ?? new NodeState()) with { LastSentSequence = Math.Max(s?.LastSentSequence ?? 0, sequence) });
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
        {
            log.LogWarning(e, "Frame {Sequence} could not be sent; it stays queued for the next connection.", sequence);
        }
    }

    private void HandleAck(ControlFrame frame)
    {
        var acked = frame.PayloadAs<SequenceAck>();
        if (acked is null) return;

        outbox.AcknowledgeThrough(acked.Sequence);
    }

    private void RecordReceived(long sequence) =>
        stateStore.Update(s => (s ?? new NodeState()) with
        {
            // Monotonic: an out-of-order frame must not rewind the resume position and cause the
            // control plane to replay everything after it on the next connect.
            LastReceivedSequence = Math.Max(s?.LastReceivedSequence ?? 0, sequence),
        });

    private ControlFrame? TryParse(string raw)
    {
        try
        {
            return NodeContract.Deserialize<ControlFrame>(raw);
        }
        catch (JsonException e)
        {
            log.LogWarning(e, "Discarding an unparseable frame from the control plane ({Length} bytes).", raw.Length);
            return null;
        }
    }

    private sealed record SequenceAck(long Sequence);
}
