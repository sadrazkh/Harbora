using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Harbora.NodeAgent.Contracts;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// One node's ingress tunnel, from the panel's side: every request to every app on that node,
/// multiplexed onto the single connection the node dialled out.
///
/// <para>
/// Same framing as a database tunnel, and for the same reason — a connection per request would mean
/// a TLS handshake per page load. The difference is that the target is not fixed: an <c>open</c>
/// carries the node host port it wants, because one tunnel serves every app on the node. The node
/// checks that port against the ones it published, so naming one here is a request, not an
/// instruction.
/// </para>
/// </summary>
public sealed class IngressChannel(string nodeId, Stream nodeStream, ILogger log)
    : INodeIngressChannel, IAsyncDisposable
{
    private readonly ConcurrentDictionary<uint, TcpClient> _clients = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    private uint _nextStreamId;

    public string NodeId { get; } = nodeId;

    public int ActiveStreams => _clients.Count;

    /// <summary>Read frames from the node until it stops or the panel does.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);

        try
        {
            await PumpFromNodeAsync(linked.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or EndOfStreamException)
        {
            log.LogInformation("Ingress tunnel for node {NodeId} ended: {Reason}", NodeId, e.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await _stopping.CancelAsync();
        }
    }

    /// <inheritdoc />
    public async Task ServeAsync(int nodeHostPort, TcpClient client, CancellationToken ct)
    {
        var streamId = Interlocked.Increment(ref _nextStreamId);
        _clients[streamId] = client;

        try
        {
            // The target rides on the open, which is the whole difference from a database tunnel.
            await SendAsync(streamId, TunnelFrameType.Open, TunnelFraming.EncodeTarget(nodeHostPort), ct);

            await PumpFromClientAsync(streamId, client, ct);
        }
        finally
        {
            CloseClient(streamId);
            await SendAsync(streamId, TunnelFrameType.Close, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        }
    }

    private async Task PumpFromClientAsync(uint streamId, TcpClient client, CancellationToken ct)
    {
        var buffer = new byte[TunnelFraming.MaxPayloadBytes];

        try
        {
            var stream = client.GetStream();

            while (!ct.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;

                await SendAsync(streamId, TunnelFrameType.Data, buffer.AsMemory(0, read), ct);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task PumpFromNodeAsync(CancellationToken ct)
    {
        var header = new byte[TunnelFraming.HeaderBytes];

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactlyAsync(nodeStream, header, ct)) return;

            var streamId = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = (TunnelFrameType)header[4];
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5));

            if (length is < 0 or > TunnelFraming.MaxPayloadBytes)
                throw new InvalidDataException($"Ingress frame declares a {length}-byte payload.");

            var payload = length == 0 ? [] : new byte[length];
            if (length > 0 && !await ReadExactlyAsync(nodeStream, payload, ct))
                throw new EndOfStreamException("The node closed midway through a frame payload.");

            switch (type)
            {
                case TunnelFrameType.Data when _clients.TryGetValue(streamId, out var client):
                    try
                    {
                        await client.GetStream().WriteAsync(payload, ct);
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException)
                    {
                        CloseClient(streamId);
                    }
                    break;

                case TunnelFrameType.Close:
                    // The node refusing a port arrives here too, which is what makes a request for
                    // an app that is no longer deployed fail fast instead of hanging.
                    CloseClient(streamId);
                    break;

                case TunnelFrameType.Ping:
                    await SendAsync(0, TunnelFrameType.Ping, ReadOnlyMemory<byte>.Empty, ct);
                    break;

                case TunnelFrameType.Open:
                    // Nodes do not open streams. One that tried would be reaching into the panel
                    // rather than out of it.
                    log.LogWarning("Node {NodeId} tried to open an ingress stream; ignoring.", NodeId);
                    break;
            }
        }
    }

    private async Task SendAsync(uint streamId, TunnelFrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new byte[TunnelFraming.HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, streamId);
        header[4] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), payload.Length);

        try
        {
            await _writeGate.WaitAsync(ct);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            await nodeStream.WriteAsync(header, ct);
            if (payload.Length > 0) await nodeStream.WriteAsync(payload, ct);
            await nodeStream.FlushAsync(ct);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The tunnel is gone. Cancelling here is what ends every stream on it at once, rather
            // than leaving each request to discover it separately by timing out.
            await _stopping.CancelAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void CloseClient(uint streamId)
    {
        if (_clients.TryRemove(streamId, out var client)) client.Dispose();
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer[read..], ct);
            if (chunk == 0) return read != 0 ? throw new EndOfStreamException() : false;
            read += chunk;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        foreach (var streamId in _clients.Keys.ToList()) CloseClient(streamId);

        _writeGate.Dispose();
        _stopping.Dispose();

        await Task.CompletedTask;
    }
}
