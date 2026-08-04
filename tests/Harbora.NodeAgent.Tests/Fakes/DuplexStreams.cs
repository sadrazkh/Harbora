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
