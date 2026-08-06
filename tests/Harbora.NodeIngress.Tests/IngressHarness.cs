using System.Net;
using System.Net.Sockets;
using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Nodes;
using Harbora.Infrastructure.Nodes;
using Harbora.NodeAgent;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Transport;
using Harbora.NodeAgent.Tunnels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Harbora.NodeIngress.Tests;

/// <summary>
/// A running panel and a running node, connected the way they connect in production.
///
/// <para>
/// Nothing here is a double. The gateway is <see cref="NodeTunnelGateway"/> on a real port; the node
/// dials it over real mutually-authenticated TLS with a certificate the panel's own CA signed; the
/// frames go over a real socket; the node dials the app with the real <see cref="TcpLocalDialer"/>;
/// and the app is a real HTTP server. The only thing standing in for production is the app itself,
/// which is a test server rather than a customer's container — and a container is reached through
/// exactly the same loopback socket.
/// </para>
///
/// <para>
/// That matters because almost every way this feature can break is a seam: a certificate the other
/// side will not accept, a frame the other side parses differently, a port the node refuses, a
/// stream nobody closes. None of those survive a test written against interfaces.
/// </para>
/// </summary>
public sealed class IngressHarness : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-ingress-e2e", Guid.NewGuid().ToString("n"));
    private readonly List<IAsyncDisposable> _nodes = [];
    private readonly List<IDisposable> _disposables = [];
    private readonly CancellationTokenSource _stopping = new();

    private NodeTunnelGateway? _gateway;
    private ServiceProvider? _services;

    public HarboraDbContext Db { get; private set; } = null!;
    public NodeIngressRegistry Ingress { get; private set; } = null!;
    public int GatewayPort { get; private set; }

    /// <summary>
    /// Start the panel half: a CA, an ingress registry and the real gateway listening on a free port.
    /// </summary>
    public async Task StartPanelAsync()
    {
        Directory.CreateDirectory(_root);

        var services = new ServiceCollection();

        // One name, resolved once. The lambda runs per DbContext instance, so building the name
        // inside it would give the gateway's scope a different database from the test's — and the
        // node row and the CA would both be invisible to the half that has to read them.
        var database = "e2e-" + Guid.NewGuid();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddSingleton<ISecretProtector, PassthroughProtector>();
        services.AddScoped<NodeCertificateAuthority>();
        services.AddLogging();

        _services = services.BuildServiceProvider();
        Db = _services.GetRequiredService<HarboraDbContext>();

        // Picking a free port and binding it later is a race, and this suite lost it: with the
        // other test assemblies running in parallel, another process could take the port in the
        // gap. The gateway treats a failed bind as non-fatal and only logs it, so the old harness
        // then probed the port, reached the OTHER process's listener, and every request after that
        // failed somewhere deep in TLS. The gateway now reports the port it really bound; the
        // harness retries with a fresh pick until that report matches.
        for (var attempt = 0; ; attempt++)
        {
            GatewayPort = FreePort();

            var options = Options.Create(new NodeAgentControlPlaneOptions
            {
                GatewayListenPort = GatewayPort,
                // Must match what the node dials, or the certificate's name check fails — which is a
                // real failure mode and one this harness should not paper over.
                GatewayPublicHost = "localhost",
                // The registry walks upward from here and skips ports it cannot bind, so a
                // collision on these self-heals; only the gateway's own port needed the loop above.
                //
                // A random start below the ephemeral range, not an ephemeral pick: this was the
                // suite's longest-lived flake. FreePort() hands out whatever the OS's dynamic range
                // gives — on this machine, ports above 65000 — and a start above the fixed end is
                // an empty range: "every ingress port between 65469 and 65000 is in use", zero
                // ports tried. A collision inside the band self-heals via the registry's walk; an
                // empty band cannot.
                IngressPortStart = Random.Shared.Next(20000, 45000),
                IngressPortEnd = 65535,
                // Randomised per harness for the same reason: two suites sharing a fixed band
                // would hand out each other's ports.
                GatewayPublicPortStart = Random.Shared.Next(20000, 45000),
                GatewayPublicPortEnd = 65535,
            });

            Ingress = new NodeIngressRegistry(options, NullLogger<NodeIngressRegistry>.Instance);

            _gateway = new NodeTunnelGateway(
                _services.GetRequiredService<IServiceScopeFactory>(),
                Ingress,
                options,
                TimeProvider.System,
                NullLogger<NodeTunnelGateway>.Instance);

            await _gateway.StartAsync(_stopping.Token);

            // The gateway issues its certificate before it binds, so waiting for its report is
            // waiting for the whole of its start-up rather than a fixed sleep that would be flaky
            // on a slow runner.
            try
            {
                await WaitUntilAsync(() => _gateway.BoundPort is not null,
                    "the gateway to bind its port", timeoutSeconds: 15);
            }
            catch (TimeoutException) when (attempt < 5)
            {
                // Lost the race — the port went to somebody else between picking and binding.
                await _gateway.StopAsync(CancellationToken.None);
                continue;
            }

            if (_gateway.BoundPort != GatewayPort)
                throw new InvalidOperationException(
                    $"The gateway bound {_gateway.BoundPort} instead of the requested {GatewayPort}.");
            return;
        }
    }

    /// <summary>
    /// Enroll a node, sign it with the panel's CA, and open its ingress tunnel. Returns once the
    /// gateway has the tunnel attached, so a caller can send a request immediately.
    /// </summary>
    public async Task<TestNode> StartNodeAsync(string nodeId, params int[] publishedPorts)
    {
        var directory = Path.Combine(_root, nodeId);
        Directory.CreateDirectory(directory);

        var identities = new NodeIdentityStore(Path.Combine(directory, "identity"));
        var csr = identities.CreateSigningRequest(nodeId, newKey: true);

        using (var scope = _services!.CreateScope())
        {
            var ca = scope.ServiceProvider.GetRequiredService<NodeCertificateAuthority>();
            var signed = await ca.SignAsync(csr, nodeId, nodeId, CancellationToken.None);

            identities.StoreCertificate(signed.CertificatePem, signed.CaCertificatePem);

            // The gateway identifies a node by the thumbprint on its row, not by what the
            // registration frame claims. Without this the tunnel is refused, which is the point.
            Db.Nodes.Add(new Node
            {
                NodeId = nodeId,
                Name = nodeId,
                Status = NodeStatus.Online,
                Health = "healthy",
                CertificateThumbprint = signed.Thumbprint,
                IngressMode = NodeIngressMode.Tunnel,
            });

            await Db.SaveChangesAsync();
        }

        var agentOptions = new NodeAgentOptions
        {
            ControlPlaneUrl = "https://localhost",
            NodeName = nodeId,
            DataDirectory = directory,
        };

        var workloads = new WorkloadRegistry(
            new JsonFileStore<WorkloadRegistryState>(Path.Combine(directory, "workloads.json")));

        foreach (var port in publishedPorts) Publish(workloads, port);

        var tls = new ControlPlaneTls(agentOptions, NullLogger<ControlPlaneTls>.Instance);

        var supervisor = new TunnelSupervisor(
            Options.Create(agentOptions),
            new TlsTunnelConnectionFactory(tls),
            new TcpLocalDialer(),
            new NodeMetrics(TimeProvider.System),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<TunnelSupervisor>.Instance);

        var node = new TestNode(nodeId, supervisor, workloads, identities);
        _nodes.Add(node);

        await node.ConnectAsync($"localhost:{GatewayPort}", _stopping.Token);
        await WaitUntilAsync(() => Ingress.IsConnected(nodeId), $"node {nodeId} to attach its ingress tunnel");

        return node;
    }

    /// <summary>
    /// A node with a self-signed credential the panel's CA never saw — what an attacker who found
    /// the gateway port would have. Throws when the gateway refuses it, which is the expected end.
    /// </summary>
    public async Task<TestNode> StartUnknownNodeAsync(string nodeId)
    {
        var directory = Path.Combine(_root, nodeId);
        Directory.CreateDirectory(directory);

        var identities = new NodeIdentityStore(Path.Combine(directory, "identity"));
        var csr = identities.CreateSigningRequest(nodeId, newKey: true);

        // Signed by a CA of its own, so the certificate is well-formed and chains to nothing the
        // panel trusts. A malformed one would be refused for the wrong reason.
        var rogue = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("rogue-" + Guid.NewGuid()).Options);

        var rogueCa = new NodeCertificateAuthority(rogue, new PassthroughProtector(), NullLogger<NodeCertificateAuthority>.Instance);
        var signed = await rogueCa.SignAsync(csr, nodeId, nodeId, CancellationToken.None);

        identities.StoreCertificate(signed.CertificatePem, signed.CaCertificatePem);
        rogue.Dispose();

        var workloads = new WorkloadRegistry(
            new JsonFileStore<WorkloadRegistryState>(Path.Combine(directory, "workloads.json")));

        var agentOptions = new NodeAgentOptions
        {
            ControlPlaneUrl = "https://localhost",
            NodeName = nodeId,
            DataDirectory = directory,
        };

        var supervisor = new TunnelSupervisor(
            Options.Create(agentOptions),
            new TlsTunnelConnectionFactory(new ControlPlaneTls(agentOptions, NullLogger<ControlPlaneTls>.Instance)),
            new TcpLocalDialer(),
            new NodeMetrics(TimeProvider.System),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<TunnelSupervisor>.Instance);

        var node = new TestNode(nodeId, supervisor, workloads, identities);
        _nodes.Add(node);

        await node.ConnectAsync($"localhost:{GatewayPort}", _stopping.Token);

        return node;
    }

    /// <summary>An HTTP server on loopback, standing where a customer's container stands.</summary>
    public OriginApp StartApp(Func<HttpRequestLine, OriginResponse> handler)
    {
        var app = new OriginApp(handler);
        _disposables.Add(app);
        return app;
    }

    /// <summary>The panel-side port Traefik would be pointed at for this node's published port.</summary>
    public int Bind(string nodeId, int nodeHostPort) => Ingress.Bind(nodeId, nodeHostPort, preferredPort: null);

    /// <summary>An HTTP client that talks to a panel ingress port exactly as the proxy would.</summary>
    public static HttpClient ClientFor(int panelPort) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{panelPort}/"),
        Timeout = TimeSpan.FromSeconds(20),
    };

    public static void Publish(WorkloadRegistry registry, int hostPort) =>
        registry.Save(new WorkloadRecord
        {
            WorkloadId = $"w-{hostPort}",
            TenantId = "harbora-platform",
            Name = $"w-{hostPort}",
            ReleaseId = "r1",
            Spec = new WorkloadSpec
            {
                WorkloadId = $"w-{hostPort}",
                Name = $"w-{hostPort}",
                TenantId = "harbora-platform",
                Containers = [],
            },
            AllocatedPorts = new Dictionary<string, int> { ["app:8080"] = hostPort },
        });

    /// <summary>
    /// Poll until a condition holds. A deadline rather than a sleep: the whole point of these tests
    /// is real sockets, and real sockets take an unpredictable moment on a loaded CI runner.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for {what}.");
    }

    private static int FreePort()
    {
        // Port zero, then let go. Racy in principle; the gateway loop above turns a lost race into
        // a retry, and the registry's own candidate walk absorbs the rest.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }


    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        foreach (var node in _nodes) await node.DisposeAsync();
        foreach (var disposable in _disposables) disposable.Dispose();

        foreach (var binding in Ingress?.Bindings() ?? []) Ingress!.Release(binding.PanelPort);

        if (_gateway is not null) await _gateway.StopAsync(CancellationToken.None);
        _services?.Dispose();
        _stopping.Dispose();

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The CA's key is stored through this. What it does is irrelevant here — these tests are about
    /// whether the two ends agree on a certificate, not about how the panel keeps its own.
    /// </summary>
    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
        public byte[] DeriveKey(string purpose) => Encoding.UTF8.GetBytes(purpose.PadRight(32, '.'))[..32];
    }
}

/// <summary>One running node agent, with the parts of it this feature touches.</summary>
public sealed class TestNode(
    string nodeId,
    TunnelSupervisor tunnels,
    WorkloadRegistry workloads,
    NodeIdentityStore identities) : IAsyncDisposable
{
    public string NodeId { get; } = nodeId;
    public WorkloadRegistry Workloads { get; } = workloads;

    public TunnelState? Tunnel => tunnels.StateFor(TunnelRegistration.IngressKey);

    public async Task ConnectAsync(string gateway, CancellationToken ct)
    {
        var state = await tunnels.StartAsync(
            gateway,
            identities.Load()!,
            new TunnelRegistration
            {
                NodeId = NodeId,
                TunnelId = $"ingress-{NodeId}",
                Purpose = TunnelPurpose.Ingress,
                TenantId = string.Empty,
            },
            new PublishedPortTargetResolver(Workloads, NullLogger<PublishedPortTargetResolver>.Instance),
            TimeSpan.FromSeconds(15),
            ct);

        if (state.Status != TunnelStatus.Connected)
            throw new InvalidOperationException(
                $"Node {NodeId} could not open its ingress tunnel: {state.LastError?.Message ?? state.Status.ToString()}");
    }

    /// <summary>Drop the tunnel the way a network blip does — without telling the panel first.</summary>
    public Task DisconnectAsync() => tunnels.StopAsync(TunnelRegistration.IngressKey);

    public async ValueTask DisposeAsync() => await tunnels.StopAllAsync();
}

/// <summary>What the origin app was asked for.</summary>
public sealed record HttpRequestLine(string Method, string Path, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>What it answers.</summary>
public sealed record OriginResponse(int Status = 200, string Body = "", string ContentType = "text/plain");

/// <summary>
/// A minimal HTTP/1.1 server on loopback, standing where a customer's container stands.
///
/// <para>
/// Hand-rolled rather than <c>HttpListener</c> because that needs a URL ACL on Windows and would
/// make this suite refuse to run on a developer machine for a reason that has nothing to do with
/// tunnels.
/// </para>
/// </summary>
public sealed class OriginApp : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private int _served;
    private int _disposed;

    public int Port { get; }

    /// <summary>How many requests actually reached the app. Zero is an assertion in several tests.</summary>
    public int Served => Volatile.Read(ref _served);

    public OriginApp(Func<HttpRequestLine, OriginResponse> handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(() => AcceptAsync(handler), CancellationToken.None);
    }

    private async Task AcceptAsync(Func<HttpRequestLine, OriginResponse> handler)
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, handler), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, Func<HttpRequestLine, OriginResponse> handler)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(_stopping.Token);
                if (requestLine is null) return;

                var parts = requestLine.Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                string? line;
                while ((line = await reader.ReadLineAsync(_stopping.Token)) is { Length: > 0 })
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }

                var body = string.Empty;
                if (headers.TryGetValue("Content-Length", out var length) && int.TryParse(length, out var count) && count > 0)
                {
                    var buffer = new char[count];
                    var read = 0;
                    while (read < count)
                    {
                        var chunk = await reader.ReadAsync(buffer.AsMemory(read), _stopping.Token);
                        if (chunk == 0) break;
                        read += chunk;
                    }
                    body = new string(buffer, 0, read);
                }

                Interlocked.Increment(ref _served);

                var response = handler(new HttpRequestLine(parts[0], parts.Length > 1 ? parts[1] : "/", body, headers));
                var payload = Encoding.UTF8.GetBytes(response.Body);

                var head = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Error")}\r\n" +
                    $"Content-Type: {response.ContentType}\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Connection: close\r\n\r\n");

                await stream.WriteAsync(head, _stopping.Token);
                await stream.WriteAsync(payload, _stopping.Token);
                await stream.FlushAsync(_stopping.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Idempotent: a test that stops an app mid-way to simulate a crashed container leaves the
    /// harness to dispose it again at the end.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _stopping.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        _stopping.Dispose();
    }
}
