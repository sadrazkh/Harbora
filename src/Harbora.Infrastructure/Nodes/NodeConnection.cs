using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Harbora.NodeAgent.Contracts;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// One live session with one node.
///
/// <para>
/// Held in a singleton registry so any request can send a command to a node without knowing which
/// request is currently pumping its socket. That makes concurrency the interesting part: a
/// WebSocket forbids overlapping sends, and a deploy from the UI, a heartbeat ack and a log stream
/// all want to write at once.
/// </para>
/// </summary>
public sealed class NodeConnection(
    string nodeId,
    Guid nodeRowId,
    WebSocket socket,
    string resumeToken,
    IReadOnlyList<string> grantedScopes,
    TimeProvider clock,
    ILogger logger) : IAsyncDisposable
{
    private const int ReceiveBufferSize = 16 * 1024;

    /// <summary>A deploy spec with a large compose file is the biggest legitimate frame.</summary>
    private const int MaxFrameBytes = 8 * 1024 * 1024;

    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pending =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandAck>> _acks =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Func<LogChunk, Task>> _logSinks =
        new(StringComparer.Ordinal);

    private long _sequence;

    public string NodeId { get; } = nodeId;
    public Guid NodeRowId { get; } = nodeRowId;
    public string ResumeToken { get; } = resumeToken;
    public IReadOnlyList<string> GrantedScopes { get; } = grantedScopes;
    public DateTimeOffset ConnectedAt { get; } = clock.GetUtcNow();

    /// <summary>Highest sequence from this node that has been durably handled.</summary>
    public long LastReceivedSequence { get; private set; }

    public bool IsOpen => socket.State == WebSocketState.Open;

    public int InFlightCommands => _pending.Count;

    public void RecordReceived(long sequence)
    {
        // Monotonic: an out-of-order frame must not rewind the position, or the next reconnect would
        // ask the node to replay everything after it.
        if (sequence > LastReceivedSequence) LastReceivedSequence = sequence;
    }

    // --- sending ---

    public async Task SendFrameAsync(ControlFrame frame, CancellationToken ct)
    {
        if (!IsOpen) throw new NodeNotConnectedException(NodeId);

        var bytes = Encoding.UTF8.GetBytes(NodeContract.Serialize(frame));

        await _sendGate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task SendAsync<T>(string type, T payload, string? correlationId, CancellationToken ct) =>
        SendFrameAsync(ControlFrame.Create(type, payload, Interlocked.Increment(ref _sequence), correlationId), ct);

    /// <summary>
    /// Tell the node how far we have durably got. Only ever claims what has been written, because a
    /// node that trims its outbox on this promise cannot get those frames back.
    /// </summary>
    public Task AcknowledgeAsync(long sequence, CancellationToken ct) =>
        SendFrameAsync(ControlFrame.Create(ControlFrames.Ack, new { sequence }), ct);

    // --- command correlation ---

    /// <summary>
    /// Send a command and wait for its terminal result.
    ///
    /// <para>
    /// Registered before the send, not after: the node can answer faster than this method can reach
    /// its next line, and a result that arrives before anyone is listening for it is a command that
    /// appears to have timed out while actually succeeding.
    /// </para>
    /// </summary>
    public async Task<CommandResult> SendCommandAsync(CommandEnvelope envelope, TimeSpan timeout, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(envelope.CommandId, completion))
            throw new InvalidOperationException($"Command {envelope.CommandId} is already in flight on this connection.");

        try
        {
            await SendAsync(ControlFrames.Command, envelope, envelope.CorrelationId, ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // A little past the node's own bound, so a node that times out internally gets to say so
            // — its answer is more useful than ours.
            deadline.CancelAfter(timeout + TimeSpan.FromSeconds(30));

            await using (deadline.Token.Register(() => completion.TrySetCanceled(deadline.Token)))
                return await completion.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Node {NodeId} did not answer command {CommandId} ({Command}) within {Timeout}.",
                NodeId, envelope.CommandId, envelope.Command, timeout);

            return new CommandResult
            {
                CommandId = envelope.CommandId,
                Status = CommandStatus.TimedOut,
                Error = NodeError.From(NodeErrorCode.Timeout, "The node did not answer in time.", retryable: true),
                StartedAt = clock.GetUtcNow(),
                CompletedAt = clock.GetUtcNow(),
            };
        }
        finally
        {
            _pending.TryRemove(envelope.CommandId, out _);
            _acks.TryRemove(envelope.CommandId, out _);
            _logSinks.TryRemove(envelope.CommandId, out _);
        }
    }

    /// <summary>Wait for the ack of an already-sent command, when the caller wants admission rather than completion.</summary>
    public Task<CommandAck> WaitForAckAsync(string commandId, TimeSpan timeout, CancellationToken ct)
    {
        var completion = _acks.GetOrAdd(commandId,
            _ => new TaskCompletionSource<CommandAck>(TaskCreationOptions.RunContinuationsAsynchronously));

        return completion.Task.WaitAsync(timeout, ct);
    }

    public void CompleteAck(CommandAck ack)
    {
        _acks.GetOrAdd(ack.CommandId,
            _ => new TaskCompletionSource<CommandAck>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(ack);

        // A rejected command produces no result frame, so its ack is the terminal answer.
        if (ack.Rejected is { } rejection && _pending.TryGetValue(ack.CommandId, out var pending))
            pending.TrySetResult(new CommandResult
            {
                CommandId = ack.CommandId,
                Status = CommandStatus.Rejected,
                Error = rejection,
                StartedAt = clock.GetUtcNow(),
                CompletedAt = clock.GetUtcNow(),
            });
    }

    public void CompleteCommand(CommandResult result)
    {
        if (_pending.TryGetValue(result.CommandId, out var pending))
        {
            pending.TrySetResult(result);
            return;
        }

        // Nobody is waiting: the panel restarted, or the caller gave up. Not an error — the result
        // is still persisted by the session loop — but worth seeing when reconstructing a timeline.
        logger.LogDebug(
            "Node {NodeId} answered command {CommandId} with no caller waiting for it.", NodeId, result.CommandId);
    }

    // --- log streaming ---

    public IDisposable SubscribeLogs(string commandId, Func<LogChunk, Task> sink)
    {
        _logSinks[commandId] = sink;
        return new Subscription(() => _logSinks.TryRemove(commandId, out _));
    }

    public async Task DispatchLogAsync(LogChunk chunk)
    {
        if (!_logSinks.TryGetValue(chunk.CommandId, out var sink)) return;

        try
        {
            await sink(chunk);
        }
        catch (Exception e)
        {
            // A broken consumer must not take down the session that feeds it.
            logger.LogDebug(e, "A log subscriber for command {CommandId} threw; dropping it.", chunk.CommandId);
            _logSinks.TryRemove(chunk.CommandId, out _);
        }
    }

    // --- receiving ---

    /// <summary>The next frame, or null once the node has closed.</summary>
    public async Task<ControlFrame?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var accumulated = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct);
            }
            catch (WebSocketException)
            {
                // A dropped connection is the ordinary way a session ends, not an exceptional one.
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close) return null;

            accumulated.Write(buffer, 0, result.Count);

            if (accumulated.Length > MaxFrameBytes)
                throw new InvalidDataException($"Node {NodeId} sent a frame above {MaxFrameBytes} bytes.");

            if (result.EndOfMessage) break;
        }

        var json = Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);

        try
        {
            return NodeContract.Deserialize<ControlFrame>(json);
        }
        catch (System.Text.Json.JsonException e)
        {
            logger.LogWarning(e, "Discarding an unparseable frame from node {NodeId} ({Length} bytes).", NodeId, json.Length);
            return ControlFrame.Create("unparseable", new { });
        }
    }

    public async Task CloseAsync(string reason)
    {
        // Every waiter learns now rather than at its own timeout: a caller blocked on a command
        // whose node has gone is a request that would otherwise hang for minutes.
        foreach (var pending in _pending.Values)
            pending.TrySetResult(new CommandResult
            {
                CommandId = string.Empty,
                Status = CommandStatus.Failed,
                Error = NodeError.From(NodeErrorCode.NodeNotReady, $"The node disconnected: {reason}", retryable: true),
                StartedAt = clock.GetUtcNow(),
                CompletedAt = clock.GetUtcNow(),
            });

        _pending.Clear();
        _logSinks.Clear();

        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("closing");
        _sendGate.Dispose();
        socket.Dispose();
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public sealed class NodeNotConnectedException(string nodeId)
    : Exception($"Node {nodeId} is not currently connected to this panel.")
{
    public string NodeId { get; } = nodeId;
}

/// <summary>
/// Which nodes are connected to <em>this</em> panel instance, and how to reach them.
///
/// <para>
/// In-memory and per-instance on purpose. A node holds one WebSocket to one instance, so a command
/// can only be delivered by the instance holding that socket — a shared registry would list nodes
/// this process cannot actually talk to. Behind more than one panel replica, a command has to be
/// routed to the right instance; see docs/node-agent/merge-notes.md.
/// </para>
/// </summary>
public sealed class NodeChannelRegistry(ILogger<NodeChannelRegistry> log)
{
    private readonly ConcurrentDictionary<string, NodeConnection> _connections = new(StringComparer.Ordinal);
    private readonly ILogger<NodeChannelRegistry> _log = log;

    public int Count => _connections.Count;

    public IReadOnlyList<string> ConnectedNodeIds => _connections.Keys.ToList();

    public bool IsConnected(string nodeId) => _connections.TryGetValue(nodeId, out var c) && c.IsOpen;

    public NodeConnection? Get(string nodeId) =>
        _connections.TryGetValue(nodeId, out var connection) && connection.IsOpen ? connection : null;

    /// <summary>
    /// Take ownership of a node's session, displacing any earlier one.
    ///
    /// <para>
    /// The new connection wins because the old one is, by construction, a socket the node has
    /// already given up on — a half-dead TCP connection can stay "open" locally for a long time, and
    /// preferring it would mean sending commands into a void.
    /// </para>
    /// </summary>
    public async Task<IAsyncDisposable> RegisterAsync(NodeConnection connection)
    {
        if (_connections.TryRemove(connection.NodeId, out var previous))
        {
            _log.LogInformation("Node {NodeId} reconnected; closing the previous session.", connection.NodeId);
            await previous.CloseAsync("superseded by a newer connection");
        }

        _connections[connection.NodeId] = connection;
        _log.LogInformation("Node {NodeId} connected ({Count} node(s) on this instance).", connection.NodeId, _connections.Count);

        return new Registration(this, connection);
    }

    private void Unregister(NodeConnection connection)
    {
        // Compare before removing: a later session may already have replaced this one, and removing
        // by key alone would evict the live connection when an old loop finally unwinds.
        if (_connections.TryGetValue(connection.NodeId, out var current) && ReferenceEquals(current, connection))
            _connections.TryRemove(connection.NodeId, out _);
    }

    private sealed class Registration(NodeChannelRegistry registry, NodeConnection connection) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            registry.Unregister(connection);
            registry._log.LogInformation("Node {NodeId} disconnected.", connection.NodeId);
            return ValueTask.CompletedTask;
        }
    }
}
