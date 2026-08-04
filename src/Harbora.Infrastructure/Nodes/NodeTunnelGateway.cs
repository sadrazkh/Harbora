using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Nodes;
using Harbora.NodeAgent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// The public end of a database tunnel.
///
/// <para>
/// Nodes dial in; customers connect to a port here. That direction is the whole point: the customer's
/// server needs no inbound firewall rule, the address they hand to a colleague belongs to Harbora,
/// and revoking access means closing sockets we hold rather than trusting a remote machine to have
/// unbound a port.
/// </para>
///
/// <para>
/// The allowlist and the connection cap are enforced <em>here</em> rather than on the node, because
/// this is the only place the client's real address exists. And they are read from the grant the
/// panel itself issued, never from the registration frame — a node that could widen its own
/// allowlist would make the allowlist decorative.
/// </para>
/// </summary>
public sealed class NodeTunnelGateway(
    IServiceScopeFactory scopeFactory,
    IOptions<NodeAgentControlPlaneOptions> options,
    TimeProvider clock,
    ILogger<NodeTunnelGateway> log) : BackgroundService
{
    private const int HeaderBytes = TunnelFraming.HeaderBytes;
    private const int MaxPayloadBytes = TunnelFraming.MaxPayloadBytes;

    private readonly NodeAgentControlPlaneOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, TunnelEndpoint> _tunnels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, byte> _allocatedPorts = new();

    public int ActiveTunnels => _tunnels.Count;

    public IReadOnlyList<string> ActiveGrantIds => _tunnels.Keys.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.GatewayListenPort <= 0)
        {
            log.LogInformation("The node TCP gateway is disabled (NodeAgent:GatewayListenPort is 0).");
            return;
        }

        X509Certificate2 certificate;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var ca = scope.ServiceProvider.GetRequiredService<NodeCertificateAuthority>();
            certificate = await ca.IssueGatewayCertificateAsync(PublicHost, stoppingToken);
        }
        catch (Exception e)
        {
            // Not fatal to the panel. A gateway that cannot start means database tunnels do not
            // work; refusing to boot over it would take the whole platform down with them.
            log.LogError(e, "The node TCP gateway could not obtain a certificate; database tunnels are unavailable.");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, _options.GatewayListenPort);

        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            log.LogError(e, "The node TCP gateway could not bind port {Port}.", _options.GatewayListenPort);
            return;
        }

        log.LogInformation(
            "Node TCP gateway listening on {Port}; publishing grants on {Start}–{End} as {Host}.",
            _options.GatewayListenPort, _options.GatewayPublicPortStart, _options.GatewayPublicPortEnd, PublicHost);

        stoppingToken.Register(listener.Stop);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient node;
                try
                {
                    node = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    break;
                }

                // Each node connection is handled independently: one node presenting a bad
                // certificate must not delay the next one's handshake.
                _ = Task.Run(() => AcceptNodeAsync(node, certificate, stoppingToken), CancellationToken.None);
            }
        }
        finally
        {
            certificate.Dispose();

            foreach (var tunnel in _tunnels.Values.ToList()) await tunnel.DisposeAsync();
            _tunnels.Clear();
        }
    }

    private string PublicHost =>
        _options.GatewayPublicHost
        ?? _options.TunnelGatewayUrl?.Split(':')[0]
        ?? "localhost";

    // --- node side ---

    private async Task AcceptNodeAsync(TcpClient client, X509Certificate2 serverCertificate, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        SslStream? tls = null;
        TunnelEndpoint? endpoint = null;

        try
        {
            client.NoDelay = true;
            tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                // Validated properly below against the CA and the node row; accepting here only
                // gets us past the handshake so there is a certificate to check.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, ct);

            if (tls.RemoteCertificate is not { } presented)
            {
                log.LogWarning("Gateway refused {Remote}: no client certificate.", remote);
                return;
            }

            using var certificate = X509CertificateLoader.LoadCertificate(presented.Export(X509ContentType.Cert));

            var registration = await ReadRegistrationAsync(tls, ct);
            if (registration is null)
            {
                log.LogWarning("Gateway refused {Remote}: no readable registration.", remote);
                return;
            }

            var grant = await AuthoriseAsync(certificate, registration, ct);

            if (grant is null)
            {
                await WriteLineAsync(tls, NodeContract.Serialize(new TunnelRegistrationResponse
                {
                    Accepted = false,
                    Error = NodeError.From(NodeErrorCode.TunnelRejected,
                        "No active database access grant matches this registration."),
                }), ct);

                log.LogWarning(
                    "Gateway refused a tunnel for grant {GrantId} from {Remote}: no matching active grant.",
                    registration.GrantId, remote);
                return;
            }

            if (!TryAllocatePort(out var publicPort))
            {
                await WriteLineAsync(tls, NodeContract.Serialize(new TunnelRegistrationResponse
                {
                    Accepted = false,
                    Error = NodeError.From(NodeErrorCode.TunnelUnavailable,
                        "The gateway has no free public port.", retryable: true),
                }), ct);
                return;
            }

            endpoint = new TunnelEndpoint(registration.GrantId, grant, tls, publicPort, clock, log);

            if (_tunnels.TryRemove(registration.GrantId, out var previous))
            {
                // A node reconnecting after a blip: the new socket is the live one.
                log.LogInformation("Grant {GrantId} re-registered; closing the previous tunnel.", registration.GrantId);
                await previous.DisposeAsync();
            }

            _tunnels[registration.GrantId] = endpoint;

            await WriteLineAsync(tls, NodeContract.Serialize(new TunnelRegistrationResponse
            {
                Accepted = true,
                PublicEndpoint = $"{PublicHost}:{publicPort}",
                PublicPort = publicPort,
                ExpiresAt = grant.ExpiresAt,
            }), ct);

            log.LogInformation(
                "Grant {GrantId} published at {Host}:{Port} for node {NodeId} (allowlist: {Allowlist}).",
                registration.GrantId, PublicHost, publicPort, grant.NodeId,
                grant.IpAllowlist.Count == 0 ? "any address" : string.Join(", ", grant.IpAllowlist));

            await endpoint.RunAsync(ct);
        }
        catch (Exception e) when (e is IOException or AuthenticationException or SocketException or InvalidDataException)
        {
            log.LogWarning(e, "A gateway node connection from {Remote} ended abnormally.", remote);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (endpoint is not null)
            {
                _tunnels.TryRemove(endpoint.GrantId, out _);
                _allocatedPorts.TryRemove(endpoint.PublicPort, out _);
                await endpoint.DisposeAsync();
            }

            tls?.Dispose();
            client.Dispose();
        }
    }

    /// <summary>
    /// Decide whether this node may publish this grant, using the grant the panel issued rather
    /// than the one the node describes.
    /// </summary>
    private async Task<AuthorisedGrant?> AuthoriseAsync(
        X509Certificate2 certificate, TunnelRegistration registration, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var ca = scope.ServiceProvider.GetRequiredService<NodeCertificateAuthority>();

        if (!await ca.ValidatesAsync(certificate, ct)) return null;

        // IgnoreQueryFilters throughout: the gateway is a background listener with no session, and
        // a filtered read would find nothing and refuse every tunnel while reporting no error.
        var node = await db.Nodes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.CertificateThumbprint == certificate.Thumbprint, ct);

        if (node is null || node.IsRevoked) return null;

        // The grant must be one this panel actually issued to this node.
        var issued = await db.NodeCommands.IgnoreQueryFilters()
            .Where(c => c.NodeId == node.NodeId &&
                        c.Command == NodeCommands.CreateDatabaseAccessGrant &&
                        c.Status == NodeCommandStatus.Succeeded)
            .OrderByDescending(c => c.IssuedAt)
            .ToListAsync(ct);

        var match = issued.FirstOrDefault(c => SpecOf(c)?.GrantId == registration.GrantId);
        if (match is null) return null;

        var spec = SpecOf(match)!;

        // A later revocation for the same grant id wins, whatever the node believes.
        var revoked = await db.NodeCommands.IgnoreQueryFilters()
            .Where(c => c.NodeId == node.NodeId &&
                        c.Command == NodeCommands.RevokeDatabaseAccessGrant &&
                        c.IssuedAt > match.IssuedAt)
            .ToListAsync(ct);

        if (revoked.Any(c => RevokeOf(c)?.GrantId == registration.GrantId)) return null;

        var expiresAt = spec.Mode == DatabaseAccessMode.Temporary && spec.TtlSeconds is { } ttl
            ? match.IssuedAt.AddSeconds(ttl)
            : (DateTimeOffset?)null;

        if (expiresAt is { } deadline && clock.GetUtcNow() >= deadline) return null;

        return new AuthorisedGrant(
            node.NodeId,
            spec.TenantId,
            // From the issued spec, not from the registration frame.
            spec.IpAllowlist,
            spec.MaxConnections,
            spec.MaxConnectionsPerMinute,
            expiresAt);
    }

    /// <summary>
    /// Whether a client address is covered by an allowlist entry — a plain address or a CIDR block.
    ///
    /// <para>
    /// Public because it is the rule an allowlist means, and a rule that decides who reaches a
    /// customer's database should be exercised directly rather than inferred from a socket test.
    /// </para>
    /// </summary>
    public static bool AddressMatches(string entry, IPAddress address)
    {
        var parts = entry.Split('/');

        if (!IPAddress.TryParse(parts[0], out var network)) return false;
        if (parts.Length == 1) return network.Equals(address);
        if (parts.Length > 2) return false;
        if (!int.TryParse(parts[1], out var prefix)) return false;
        if (network.AddressFamily != address.AddressFamily) return false;

        var bits = network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefix < 0 || prefix > bits) return false;

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();

        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
            if (networkBytes[i] != addressBytes[i]) return false;

        if (remainingBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }

    private bool TryAllocatePort(out int port)
    {
        for (var candidate = _options.GatewayPublicPortStart; candidate <= _options.GatewayPublicPortEnd; candidate++)
            if (_allocatedPorts.TryAdd(candidate, 0))
            {
                port = candidate;
                return true;
            }

        port = 0;
        return false;
    }

    private static async Task<TunnelRegistration?> ReadRegistrationAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(512);
        var single = new byte[1];

        while (buffer.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(single, ct);
            if (read == 0) return null;
            if (single[0] == (byte)'\n') break;
            buffer.Add(single[0]);
        }

        try
        {
            return NodeContract.Deserialize<TunnelRegistration>(Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await stream.FlushAsync(ct);
    }

    private static DatabaseAccessGrantSpec? SpecOf(NodeCommandRecord record) =>
        TryDeserialize<DatabaseAccessGrantSpec>(record.PayloadJson);

    private static RevokeDatabaseAccessRequest? RevokeOf(NodeCommandRecord record) =>
        TryDeserialize<RevokeDatabaseAccessRequest>(record.PayloadJson);

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, NodeContract.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The grant's terms, as the panel issued them.</summary>
    private sealed record AuthorisedGrant(
        string NodeId,
        string TenantId,
        IReadOnlyList<string> IpAllowlist,
        int MaxConnections,
        int MaxConnectionsPerMinute,
        DateTimeOffset? ExpiresAt);

    /// <summary>
    /// One published grant: a public listener, and the multiplexed connection back to the node.
    /// </summary>
    private sealed class TunnelEndpoint(
        string grantId,
        AuthorisedGrant grant,
        Stream nodeStream,
        int publicPort,
        TimeProvider clock,
        ILogger log) : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<uint, TcpClient> _clients = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly ConcurrentQueue<DateTimeOffset> _recentConnections = new();
        private readonly CancellationTokenSource _stopping = new();

        private TcpListener? _listener;
        private uint _nextStreamId;

        public string GrantId { get; } = grantId;
        public int PublicPort { get; } = publicPort;

        public async Task RunAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);

            _listener = new TcpListener(IPAddress.Any, PublicPort);
            _listener.Start();

            var accepting = AcceptClientsAsync(linked.Token);
            var pumping = PumpFromNodeAsync(linked.Token);

            // Either side ending ends the tunnel: a node that disconnected cannot serve the clients
            // still attached, and clients holding a dead tunnel would wait for a timeout instead of
            // a refusal.
            await Task.WhenAny(accepting, pumping);
            await _stopping.CancelAsync();
        }

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(ct);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    return;
                }

                if (!Admit(client))
                {
                    client.Dispose();
                    continue;
                }

                var streamId = Interlocked.Increment(ref _nextStreamId);
                _clients[streamId] = client;

                await SendAsync(streamId, TunnelFrameType.Open, ReadOnlyMemory<byte>.Empty, ct);
                _ = Task.Run(() => PumpFromClientAsync(streamId, client, ct), CancellationToken.None);
            }
        }

        /// <summary>
        /// The allowlist, the connection cap and the rate limit, applied where the client's real
        /// address is visible.
        /// </summary>
        private bool Admit(TcpClient client)
        {
            var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;

            if (address is null) return false;

            if (grant.IpAllowlist.Count > 0 && !grant.IpAllowlist.Any(entry => AddressMatches(entry, address)))
            {
                log.LogWarning("Grant {GrantId} refused a connection from {Address}: not on the allowlist.", GrantId, address);
                return false;
            }

            if (_clients.Count >= grant.MaxConnections)
            {
                log.LogWarning(
                    "Grant {GrantId} refused a connection from {Address}: {Count} of {Max} connections already open.",
                    GrantId, address, _clients.Count, grant.MaxConnections);
                return false;
            }

            var now = clock.GetUtcNow();
            while (_recentConnections.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(1))
                _recentConnections.TryDequeue(out _);

            if (_recentConnections.Count >= grant.MaxConnectionsPerMinute)
            {
                log.LogWarning("Grant {GrantId} refused a connection from {Address}: rate limit.", GrantId, address);
                return false;
            }

            _recentConnections.Enqueue(now);

            if (grant.ExpiresAt is { } expiry && now >= expiry)
            {
                log.LogWarning("Grant {GrantId} refused a connection: it expired at {Expiry:u}.", GrantId, expiry);
                return false;
            }

            return true;
        }

        private async Task PumpFromClientAsync(uint streamId, TcpClient client, CancellationToken ct)
        {
            var buffer = new byte[MaxPayloadBytes];

            try
            {
                var stream = client.GetStream();

                while (!ct.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) break;

                    await SendAsync(streamId, TunnelFrameType.Data, buffer.AsMemory(0, read), ct);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                CloseClient(streamId);
                await SendAsync(streamId, TunnelFrameType.Close, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
            }
        }

        private async Task PumpFromNodeAsync(CancellationToken ct)
        {
            var header = new byte[HeaderBytes];

            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactlyAsync(nodeStream, header, ct)) return;

                var streamId = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = (TunnelFrameType)header[4];
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5));

                if (length is < 0 or > MaxPayloadBytes)
                    throw new InvalidDataException($"Tunnel frame declares a {length}-byte payload.");

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
                        CloseClient(streamId);
                        break;

                    case TunnelFrameType.Ping:
                        await SendAsync(0, TunnelFrameType.Ping, ReadOnlyMemory<byte>.Empty, ct);
                        break;
                }
            }
        }

        private async Task SendAsync(uint streamId, TunnelFrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var header = new byte[HeaderBytes];
            BinaryPrimitives.WriteUInt32BigEndian(header, streamId);
            header[4] = (byte)type;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), payload.Length);

            await _writeGate.WaitAsync(ct);
            try
            {
                await nodeStream.WriteAsync(header, ct);
                if (payload.Length > 0) await nodeStream.WriteAsync(payload, ct);
                await nodeStream.FlushAsync(ct);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
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

            try { _listener?.Stop(); } catch (SocketException) { }

            foreach (var streamId in _clients.Keys.ToList()) CloseClient(streamId);

            _writeGate.Dispose();
            _stopping.Dispose();
        }
    }
}
