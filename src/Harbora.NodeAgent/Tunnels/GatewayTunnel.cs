using System.Collections.Concurrent;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Tunnels;

/// <summary>Where a tunnel forwards to, on this node.</summary>
public sealed record TunnelTarget(string Host, int Port);

/// <summary>
/// One outbound tunnel: node → Harbora TCP gateway, carrying every client session for a grant.
///
/// <para>
/// The direction is the design. A database published this way costs the customer no inbound
/// firewall rule, the public address belongs to Harbora rather than to them, and revoking access is
/// closing a socket the node owns — not hoping a port somewhere got unbound. The alternative, an
/// engine bound to 0.0.0.0 with a fixed password, is what this exists to avoid.
/// </para>
/// </summary>
public sealed class GatewayTunnel(
    ITunnelConnectionFactory connections,
    ILocalDialer dialer,
    TimeProvider clock,
    ILogger<GatewayTunnel> log) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<uint, Stream> _streams = new();
    private readonly Lock _stateGate = new();

    private Stream? _connection;
    private TunnelFramer? _framer;
    private TunnelState _state = null!;

    public TunnelState State
    {
        get { lock (_stateGate) return _state; }
    }

    /// <summary>
    /// Connect, register, and forward until cancelled or dropped. Returns when the tunnel ends;
    /// the supervisor decides whether that warrants another attempt.
    /// </summary>
    public async Task RunAsync(
        Uri gateway, NodeIdentity identity, TunnelRegistration registration, TunnelTarget target, CancellationToken ct)
    {
        SetState(new TunnelState
        {
            TunnelId = registration.TunnelId,
            GrantId = registration.GrantId,
            Status = TunnelStatus.Connecting,
            LocalTarget = $"{target.Host}:{target.Port}",
        });

        try
        {
            _connection = await connections.ConnectAsync(gateway, identity, ct);
            _framer = new TunnelFramer(_connection);

            var response = await _framer.RegisterAsync(registration, ct);

            if (!response.Accepted)
            {
                var error = response.Error ?? NodeError.From(NodeErrorCode.TunnelRejected, "The gateway refused the tunnel.");
                SetState(State with { Status = TunnelStatus.Failed, LastError = error });
                log.LogWarning("Gateway refused tunnel {TunnelId}: {Message}", registration.TunnelId, error.Message);
                return;
            }

            SetState(State with
            {
                Status = TunnelStatus.Connected,
                PublicEndpoint = response.PublicEndpoint,
                ConnectedAt = clock.GetUtcNow(),
            });

            log.LogInformation(
                "Tunnel {TunnelId} for grant {GrantId} is published at {Endpoint}.",
                registration.TunnelId, registration.GrantId, response.PublicEndpoint);

            await PumpAsync(target, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetState(State with { Status = TunnelStatus.Closed });
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or System.Net.Sockets.SocketException)
        {
            SetState(State with
            {
                Status = TunnelStatus.Failed,
                LastError = NodeError.From(NodeErrorCode.TunnelUnavailable, e.Message, retryable: true),
            });

            log.LogWarning(e, "Tunnel {TunnelId} dropped.", registration.TunnelId);
        }
        finally
        {
            await CloseAllStreamsAsync();
            if (_connection is not null) await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private async Task PumpAsync(TunnelTarget target, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await _framer!.ReadAsync(ct);
            if (frame is null) return;

            switch (frame.Value.Type)
            {
                case TunnelFrameType.Open:
                    await OpenStreamAsync(frame.Value.StreamId, target, ct);
                    break;

                case TunnelFrameType.Data:
                    await ForwardToLocalAsync(frame.Value, ct);
                    break;

                case TunnelFrameType.Close:
                    await CloseStreamAsync(frame.Value.StreamId);
                    break;

                case TunnelFrameType.Ping:
                    await _framer.WriteAsync(new TunnelFrame(0, TunnelFrameType.Ping, ReadOnlyMemory<byte>.Empty), ct);
                    break;
            }
        }
    }

    private async Task OpenStreamAsync(uint streamId, TunnelTarget target, CancellationToken ct)
    {
        try
        {
            var local = await dialer.DialAsync(target.Host, target.Port, ct);
            _streams[streamId] = local;

            SetState(State with
            {
                ActiveConnections = _streams.Count,
                LastActivityAt = clock.GetUtcNow(),
            });

            // One pump per stream, reading from the container and writing frames back.
            _ = Task.Run(() => PumpLocalToGatewayAsync(streamId, local, ct), ct);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
        {
            // The database is not accepting connections. Telling the gateway lets it fail the
            // client's connect immediately instead of leaving it hanging.
            log.LogWarning(e, "Could not reach {Host}:{Port} for tunnel stream {StreamId}.", target.Host, target.Port, streamId);
            await SafeWriteAsync(new TunnelFrame(streamId, TunnelFrameType.Close, ReadOnlyMemory<byte>.Empty), ct);
        }
    }

    private async Task PumpLocalToGatewayAsync(uint streamId, Stream local, CancellationToken ct)
    {
        var buffer = new byte[TunnelFramer.MaxPayloadBytes];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await local.ReadAsync(buffer, ct);
                if (read == 0) break;

                await _framer!.WriteAsync(
                    new TunnelFrame(streamId, TunnelFrameType.Data, buffer.AsMemory(0, read)), ct);

                lock (_stateGate) _state = _state with { BytesOut = _state.BytesOut + read, LastActivityAt = clock.GetUtcNow() };
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The container went away or the tunnel is closing; either way this stream is over.
        }
        finally
        {
            await CloseStreamAsync(streamId);
            await SafeWriteAsync(new TunnelFrame(streamId, TunnelFrameType.Close, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        }
    }

    private async Task ForwardToLocalAsync(TunnelFrame frame, CancellationToken ct)
    {
        if (!_streams.TryGetValue(frame.StreamId, out var local)) return;

        try
        {
            await local.WriteAsync(frame.Payload, ct);
            await local.FlushAsync(ct);

            lock (_stateGate)
                _state = _state with { BytesIn = _state.BytesIn + frame.Payload.Length, LastActivityAt = clock.GetUtcNow() };
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            await CloseStreamAsync(frame.StreamId);
        }
    }

    private async Task CloseStreamAsync(uint streamId)
    {
        if (!_streams.TryRemove(streamId, out var local)) return;

        try
        {
            await local.DisposeAsync();
        }
        catch (IOException)
        {
        }

        SetState(State with { ActiveConnections = _streams.Count });
    }

    private async Task CloseAllStreamsAsync()
    {
        foreach (var streamId in _streams.Keys.ToList())
            await CloseStreamAsync(streamId);
    }

    private async Task SafeWriteAsync(TunnelFrame frame, CancellationToken ct)
    {
        if (_framer is null) return;

        try
        {
            await _framer.WriteAsync(frame, ct);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private void SetState(TunnelState state)
    {
        lock (_stateGate) _state = state;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAllStreamsAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
