using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using Harbora.NodeAgent.Identity;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Transport;

/// <summary>
/// A bidirectional stream of text messages. Abstracted away from WebSockets so the channel's
/// resume, ack and backoff behaviour can be tested against an in-memory pair rather than against a
/// real socket and a real control plane.
/// </summary>
public interface IMessageTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    Task SendAsync(string message, CancellationToken ct);

    /// <summary>The next message, or null once the peer has closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    Task CloseAsync(string reason, CancellationToken ct);
}

public interface IMessageTransportFactory
{
    Task<IMessageTransport> ConnectAsync(Uri uri, NodeIdentity identity, CancellationToken ct);
}

/// <summary>Dials the control plane's channel over WSS, presenting the node certificate.</summary>
public sealed class WebSocketTransportFactory(ControlPlaneTls tls, ILogger<WebSocketTransportFactory> log)
    : IMessageTransportFactory
{
    public async Task<IMessageTransport> ConnectAsync(Uri uri, NodeIdentity identity, CancellationToken ct)
    {
        var socket = new ClientWebSocket();

        socket.Options.RemoteCertificateValidationCallback =
            tls.Build(identity, uri.Host).RemoteCertificateValidationCallback;
        socket.Options.ClientCertificates.Add(identity.Certificate);

        // Keeps a NAT or a load balancer from silently dropping an idle channel. The agent's own
        // heartbeat would eventually notice, but a ping is cheaper than a reconnect.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        log.LogDebug("Dialling control channel at {Uri}.", uri);
        await socket.ConnectAsync(uri, ct);

        return new WebSocketTransport(socket);
    }
}

/// <summary>Text framing over a <see cref="ClientWebSocket"/>, reassembling fragmented messages.</summary>
public sealed class WebSocketTransport(WebSocket socket) : IMessageTransport
{
    private const int BufferSize = 16 * 1024;

    /// <summary>
    /// A deploy spec with a large compose file is the biggest thing that legitimately arrives.
    /// Beyond this the peer is either broken or hostile, and buffering it would be the attack.
    /// </summary>
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task SendAsync(string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);

        // WebSocket forbids concurrent sends; the heartbeat timer and a command result routinely
        // want to write at the same moment.
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

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var accumulated = new MemoryStream();

        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);

                if (result.MessageType == WebSocketMessageType.Close) return null;

                accumulated.Write(buffer, 0, result.Count);

                if (accumulated.Length > MaxMessageBytes)
                    throw new InvalidDataException(
                        $"Control-plane message exceeded {MaxMessageBytes} bytes and was dropped.");

                if (result.EndOfMessage) break;
            }

            return Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);
        }
        catch (WebSocketException)
        {
            // An abruptly dropped connection is the normal case, not an exceptional one: the
            // reconnect loop is the handler, and it only needs to know the stream ended.
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await accumulated.DisposeAsync();
        }
    }

    public async Task CloseAsync(string reason, CancellationToken ct)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, ct);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing a connection that is already gone is the expected path out of a reconnect.
        }
    }

    public ValueTask DisposeAsync()
    {
        _sendGate.Dispose();
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
