using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Transport;

namespace Harbora.NodeAgent.Tunnels;

public readonly record struct TunnelFrame(uint StreamId, TunnelFrameType Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Reads and writes tunnel frames on a stream.
///
/// <para>
/// Header is <c>streamId(4) | type(1) | length(4)</c>, big-endian, followed by the payload. Fixed
/// and tiny on purpose: the gateway is a different codebase, possibly a different language, and a
/// framing that needs a parser is a framing that will be parsed differently on one of the two ends.
/// </para>
/// </summary>
public sealed class TunnelFramer(Stream stream)
{
    public const int HeaderBytes = TunnelFraming.HeaderBytes;
    public const int MaxPayloadBytes = TunnelFraming.MaxPayloadBytes;

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task WriteAsync(TunnelFrame frame, CancellationToken ct)
    {
        if (frame.Payload.Length > MaxPayloadBytes)
            throw new InvalidOperationException($"Tunnel payload of {frame.Payload.Length} bytes exceeds the {MaxPayloadBytes} limit.");

        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, frame.StreamId);
        header[4] = (byte)frame.Type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), frame.Payload.Length);

        await _writeGate.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(header, ct);
            if (frame.Payload.Length > 0) await stream.WriteAsync(frame.Payload, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>The next frame, or null once the peer closed cleanly.</summary>
    public async Task<TunnelFrame?> ReadAsync(CancellationToken ct)
    {
        var header = new byte[HeaderBytes];

        if (!await ReadExactlyAsync(header, ct)) return null;

        var streamId = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = (TunnelFrameType)header[4];
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5));

        if (length is < 0 or > MaxPayloadBytes)
            throw new InvalidDataException($"Tunnel frame declares a {length}-byte payload, which is out of range.");

        if (length == 0) return new TunnelFrame(streamId, type, ReadOnlyMemory<byte>.Empty);

        var payload = new byte[length];
        if (!await ReadExactlyAsync(payload, ct))
            throw new EndOfStreamException("The tunnel closed midway through a frame payload.");

        return new TunnelFrame(streamId, type, payload);
    }

    /// <summary>Send the registration line and read the gateway's answer.</summary>
    public async Task<TunnelRegistrationResponse> RegisterAsync(TunnelRegistration registration, CancellationToken ct)
    {
        var line = Encoding.UTF8.GetBytes(NodeContract.Serialize(registration) + "\n");

        await stream.WriteAsync(line, ct);
        await stream.FlushAsync(ct);

        var buffer = new List<byte>(512);
        var single = new byte[1];

        while (buffer.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(single, ct);
            if (read == 0) throw new EndOfStreamException("The gateway closed before answering the registration.");
            if (single[0] == (byte)'\n') break;
            buffer.Add(single[0]);
        }

        var json = Encoding.UTF8.GetString(buffer.ToArray());

        return NodeContract.Deserialize<TunnelRegistrationResponse>(json)
               ?? throw new InvalidDataException("The gateway sent an unreadable registration response.");
    }

    private async Task<bool> ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer[read..], ct);

            // Zero bytes on the very first read is a clean close; part-way through it is a truncation.
            if (chunk == 0) return read != 0 ? throw new EndOfStreamException("The tunnel closed mid-frame.") : false;

            read += chunk;
        }

        return true;
    }
}

/// <summary>Opens the outbound connection to the Harbora TCP gateway.</summary>
public interface ITunnelConnectionFactory
{
    Task<Stream> ConnectAsync(Uri gateway, NodeIdentity identity, CancellationToken ct);
}

/// <summary>Opens a connection to a container on this node.</summary>
public interface ILocalDialer
{
    Task<Stream> DialAsync(string host, int port, CancellationToken ct);
}

/// <summary>Mutually-authenticated TLS to the gateway, using the node's own certificate.</summary>
public sealed class TlsTunnelConnectionFactory(ControlPlaneTls tls) : ITunnelConnectionFactory
{
    public async Task<Stream> ConnectAsync(Uri gateway, NodeIdentity identity, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };

        await client.ConnectAsync(gateway.Host, gateway.Port, ct);

        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(tls.Build(identity, gateway.Host), ct);

        return ssl;
    }
}

public sealed class TcpLocalDialer : ILocalDialer
{
    public async Task<Stream> DialAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, ct);
        return client.GetStream();
    }
}
