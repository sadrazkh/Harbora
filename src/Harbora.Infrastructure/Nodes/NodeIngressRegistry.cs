using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Harbora.NodeAgent.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// The panel's end of every ingress tunnel: which nodes are dialled in, and the local listeners that
/// carry traffic back down to them.
///
/// <para>
/// A node behind NAT publishes its containers on ports only its own machine can reach. This binds a
/// port <em>here</em> for each of those, so Traefik has something to route to. A request arrives at
/// <c>panel:42017</c>, becomes a stream on that node's tunnel, and comes out at <c>127.0.0.1:32017</c>
/// on the node — with the app none the wiser and the route configuration no different from any
/// other remote server's.
/// </para>
///
/// <para>
/// A singleton because it holds live sockets, and the one place both halves meet: the gateway
/// registers a node here when its tunnel connects, and the deployment pipeline reserves a listener
/// here when it places an app. Neither half waits for the other — a listener bound while the node is
/// away accepts and immediately closes, which is what a proxy expects from an upstream that is down,
/// and is far better than Traefik pointing at a port nothing has bound.
/// </para>
/// </summary>
public sealed class NodeIngressRegistry(
    IOptions<NodeAgentControlPlaneOptions> options,
    ILogger<NodeIngressRegistry> log)
{
    private readonly NodeAgentControlPlaneOptions _options = options.Value;

    /// <summary>Nodes whose ingress tunnel is attached to this panel instance, by node id.</summary>
    private readonly ConcurrentDictionary<string, INodeIngressChannel> _channels = new(StringComparer.Ordinal);

    /// <summary>Bound listeners, by the panel port they occupy.</summary>
    private readonly ConcurrentDictionary<int, Listener> _listeners = new();

    public int ActiveTunnels => _channels.Count;
    public int BoundPorts => _listeners.Count;

    public bool IsConnected(string nodeId) => _channels.ContainsKey(nodeId);

    /// <summary>
    /// The gateway calls this when a node's ingress tunnel comes up. Replaces any previous one: a
    /// node that reconnected after a blip has exactly one live tunnel, and it is the new socket.
    /// </summary>
    public void Attach(string nodeId, INodeIngressChannel channel)
    {
        _channels[nodeId] = channel;
        log.LogInformation("Ingress tunnel for node {NodeId} is attached; {Count} port(s) already bound.",
            nodeId, _listeners.Count(l => l.Value.NodeId == nodeId));
    }

    /// <summary>
    /// The gateway calls this when the tunnel drops. The listeners stay bound on purpose — the node
    /// will reconnect, and unbinding would mean rewriting every route it serves twice per blip.
    /// </summary>
    public void Detach(string nodeId, INodeIngressChannel channel)
    {
        // Compare before removing: a reconnect that raced a disconnect would otherwise have the old
        // socket's teardown delete the new socket's registration.
        if (_channels.TryGetValue(nodeId, out var current) && ReferenceEquals(current, channel))
            _channels.TryRemove(nodeId, out _);

        log.LogInformation("Ingress tunnel for node {NodeId} is gone; its apps are unreachable until it returns.", nodeId);
    }

    /// <summary>
    /// Bind a panel port that forwards to <paramref name="nodeHostPort"/> on this node, or confirm
    /// the one already bound for that pair.
    ///
    /// <para>
    /// <paramref name="preferredPort"/> is what a previous reservation recorded. Honouring it is the
    /// whole of restart recovery: Traefik's configuration already names that port, so binding a
    /// different one would leave every route on this node pointing at nothing.
    /// </para>
    /// </summary>
    public int Bind(string nodeId, int nodeHostPort, int? preferredPort)
    {
        if (Existing(nodeId, nodeHostPort) is { } already) return already;

        if (preferredPort is { } wanted && TryBind(nodeId, nodeHostPort, wanted)) return wanted;

        if (preferredPort is { } missed)
            log.LogWarning(
                "Could not rebind ingress port {Port} for node {NodeId}; routes naming it will break until they are rewritten.",
                missed, nodeId);

        for (var candidate = _options.IngressPortStart; candidate <= _options.IngressPortEnd; candidate++)
            if (TryBind(nodeId, nodeHostPort, candidate)) return candidate;

        throw new InvalidOperationException(
            $"Every ingress port between {_options.IngressPortStart} and {_options.IngressPortEnd} is in use. " +
            "Widen NodeAgent:IngressPortStart–IngressPortEnd, or move some apps off tunnelled nodes.");
    }

    /// <summary>Release a listener. Idempotent — a released reservation may be released again.</summary>
    public void Release(int panelPort)
    {
        if (!_listeners.TryRemove(panelPort, out var listener)) return;

        listener.Dispose();
        log.LogDebug("Released ingress port {Port}.", panelPort);
    }

    /// <summary>Every panel port currently forwarding to a node, for diagnostics and the node page.</summary>
    public IReadOnlyList<(string NodeId, int PanelPort, int NodeHostPort)> Bindings() =>
        _listeners.Select(l => (l.Value.NodeId, l.Key, l.Value.NodeHostPort)).ToList();

    private int? Existing(string nodeId, int nodeHostPort) =>
        _listeners.FirstOrDefault(l => l.Value.NodeId == nodeId && l.Value.NodeHostPort == nodeHostPort) is
            { Value: not null } match
            ? match.Key
            : null;

    private bool TryBind(string nodeId, int nodeHostPort, int panelPort)
    {
        if (_listeners.ContainsKey(panelPort)) return false;

        Listener listener;
        try
        {
            // IPAddress.Any, not loopback: Traefik runs in its own container and reaches the panel
            // over the Docker network. The panel's own ports are not published to the host, so this
            // is the same exposure the panel's HTTP port already has.
            listener = new Listener(nodeId, nodeHostPort, panelPort, this, log);
        }
        catch (SocketException)
        {
            // Something else holds it. Not worth a log line per candidate on a busy panel.
            return false;
        }

        if (!_listeners.TryAdd(panelPort, listener))
        {
            listener.Dispose();
            return false;
        }

        listener.Start();

        log.LogInformation(
            "Ingress port {PanelPort} now forwards to {NodeHostPort} on node {NodeId}.",
            panelPort, nodeHostPort, nodeId);

        return true;
    }

    private INodeIngressChannel? ChannelFor(string nodeId) =>
        _channels.TryGetValue(nodeId, out var channel) ? channel : null;

    /// <summary>One bound panel port, forwarding everything it accepts down one node's tunnel.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly NodeIngressRegistry _registry;
        private readonly ILogger _log;
        private readonly CancellationTokenSource _stopping = new();

        public string NodeId { get; }
        public int NodeHostPort { get; }
        public int PanelPort { get; }

        public Listener(string nodeId, int nodeHostPort, int panelPort, NodeIngressRegistry registry, ILogger log)
        {
            NodeId = nodeId;
            NodeHostPort = nodeHostPort;
            PanelPort = panelPort;
            _registry = registry;
            _log = log;

            _listener = new TcpListener(IPAddress.Any, panelPort);
            _listener.Start();
        }

        public void Start() => _ = Task.Run(AcceptAsync, CancellationToken.None);

        private async Task AcceptAsync()
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

                client.NoDelay = true;

                if (_registry.ChannelFor(NodeId) is not { } channel)
                {
                    // Refuse rather than hold. A proxy reads a closed connection as "upstream is
                    // down" and says so; a connection held open until it times out looks to the
                    // user like the app is hanging.
                    _log.LogWarning(
                        "Refused a request for node {NodeId} on ingress port {Port}: its tunnel is not connected.",
                        NodeId, PanelPort);

                    client.Dispose();
                    continue;
                }

                _ = Task.Run(() => channel.ServeAsync(NodeHostPort, client, _stopping.Token), CancellationToken.None);
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();

            try { _listener.Stop(); } catch (SocketException) { }

            _stopping.Dispose();
        }
    }
}

/// <summary>
/// One node's live ingress tunnel, as the listeners see it.
///
/// <para>
/// An interface so the registry can be exercised without a socket: what a test needs to assert about
/// binding, rebinding and refusing has nothing to do with TLS.
/// </para>
/// </summary>
public interface INodeIngressChannel
{
    /// <summary>
    /// Carry this client's bytes to <paramref name="nodeHostPort"/> on the node and back, until
    /// either end closes. Owns the client from here, including disposing it.
    /// </summary>
    Task ServeAsync(int nodeHostPort, TcpClient client, CancellationToken ct);
}
