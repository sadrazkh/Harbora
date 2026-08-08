using System.IO.Pipelines;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Tunnels;

namespace Harbora.NodeAgent.Tests.Fakes;

/// <summary>A stream that reads from one half and writes to another — one side of a socket.</summary>
public sealed class DuplexStream(Stream readSide, Stream writeSide) : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => readSide.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        readSide.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count) => writeSide.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        writeSide.WriteAsync(buffer, ct);

    public override void Flush() => writeSide.Flush();
    public override Task FlushAsync(CancellationToken ct) => writeSide.FlushAsync(ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        writeSide.Dispose();
        readSide.Dispose();
    }

    /// <summary>Two connected streams: whatever one writes, the other reads.</summary>
    public static (DuplexStream A, DuplexStream B) CreatePair()
    {
        var aToB = new Pipe();
        var bToA = new Pipe();

        return (
            new DuplexStream(bToA.Reader.AsStream(), aToB.Writer.AsStream()),
            new DuplexStream(aToB.Reader.AsStream(), bToA.Writer.AsStream()));
    }
}

/// <summary>Hands the node one end of an in-memory pair; the test drives the other.</summary>
public sealed class FakeTunnelConnectionFactory(Stream nodeSide) : ITunnelConnectionFactory
{
    public List<Uri> Dialled { get; } = [];

    public Task<Stream> ConnectAsync(Uri gateway, NodeIdentity identity, CancellationToken ct)
    {
        Dialled.Add(gateway);
        return Task.FromResult(nodeSide);
    }
}

/// <summary>Stands in for the database container the tunnel forwards to.</summary>
public sealed class FakeLocalDialer : ILocalDialer
{
    private readonly List<DuplexStream> _targets = [];

    public List<(string Host, int Port)> Dialled { get; } = [];
    public bool RefuseConnections { get; set; }

    /// <summary>The far end of the most recent dial — what the "database" sees.</summary>
    public DuplexStream? LastTarget { get; private set; }

    public Task<Stream> DialAsync(string host, int port, CancellationToken ct)
    {
        Dialled.Add((host, port));

        if (RefuseConnections)
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);

        var (node, database) = DuplexStream.CreatePair();
        _targets.Add(database);
        LastTarget = database;

        return Task.FromResult<Stream>(node);
    }
}

/// <summary>
/// A gateway that answers every registration immediately with a published port, so
/// <see cref="TunnelSupervisor.StartAsync"/> observes a connected tunnel.
///
/// <para>
/// The tunnel's own framing and forwarding are covered by the protocol tests; this exists so a test
/// about something above the tunnel can use the real supervisor — with its real start/stop and
/// counting bookkeeping — rather than stub past it.
/// </para>
/// </summary>
public sealed class AcceptingTunnelGateway : ITunnelConnectionFactory
{
    /// <summary>
    /// Keys the gateway saw register, in order. Key rather than grant id: an ingress registration
    /// carries no grant, so the tunnel's own name is what identifies it on both ends.
    /// </summary>
    public List<string> Registered { get; } = [];

    public int PublicPort { get; set; } = 41000;

    public Task<Stream> ConnectAsync(Uri gateway, NodeIdentity identity, CancellationToken ct)
    {
        var (node, remote) = DuplexStream.CreatePair();

        _ = Task.Run(async () =>
        {
            var framer = new TunnelFramer(remote);

            // Read the registration line, then answer it.
            var buffer = new List<byte>();
            var single = new byte[1];

            while (await remote.ReadAsync(single, ct) == 1 && single[0] != (byte)'\n')
                buffer.Add(single[0]);

            var registration = Harbora.NodeAgent.Contracts.NodeContract
                .Deserialize<Harbora.NodeAgent.Contracts.TunnelRegistration>(
                    System.Text.Encoding.UTF8.GetString(buffer.ToArray()))!;

            var response = Harbora.NodeAgent.Contracts.NodeContract.Serialize(
                new Harbora.NodeAgent.Contracts.TunnelRegistrationResponse
                {
                    Accepted = true,
                    PublicEndpoint = $"{gateway.Host}:{PublicPort}",
                    PublicPort = PublicPort,
                }) + "\n";

            await remote.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response), ct);
            await remote.FlushAsync(ct);

            lock (Registered) Registered.Add(registration.Key);
            _ = framer;
        }, ct);

        return Task.FromResult<Stream>(node);
    }
}
