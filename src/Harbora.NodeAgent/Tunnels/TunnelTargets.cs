using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Runtime;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Tunnels;

/// <summary>Where a tunnel forwards to, on this node.</summary>
public sealed record TunnelTarget(string Host, int Port);

/// <summary>
/// Decides what a gateway's <c>open</c> frame is allowed to reach on this machine.
///
/// <para>
/// This is the boundary, and it is why the decision is a type rather than a parameter. The gateway
/// is the one party in the design that is neither the node nor the customer, and "open a stream to
/// X" is the single frame where it gets to name something on the node's side of the firewall. An
/// implementation that took X at face value would be a port-forward into the customer's private
/// network wearing a tunnel's clothes — reachable at <c>127.0.0.1:22</c>, at the Docker socket's
/// TCP port if one is open, at anything on the LAN.
/// </para>
/// </summary>
public interface ITunnelTargetResolver
{
    /// <summary>
    /// The target this <c>open</c> may reach, or null to refuse it. Refusing is answered with a
    /// <c>close</c>, so the client's connect fails immediately rather than hanging.
    /// </summary>
    TunnelTarget? Resolve(ReadOnlySpan<byte> openPayload);
}

/// <summary>
/// One target, fixed when the tunnel registered. What a database grant uses: the control plane
/// named the container and port when it issued the grant, and no frame afterwards can change it.
/// </summary>
public sealed class FixedTunnelTarget(TunnelTarget target) : ITunnelTargetResolver
{
    public TunnelTarget Target { get; } = target;

    public TunnelTarget? Resolve(ReadOnlySpan<byte> openPayload) => Target;
}

/// <summary>
/// A port this node published, and nothing else. What an ingress tunnel uses.
///
/// <para>
/// The check is against <see cref="WorkloadRegistry.AllocatedPorts"/> — the ports the node itself
/// chose when a workload asked for one to be published. So the reachable set is exactly the set the
/// control plane already deployed here, and it shrinks the moment a workload is deleted, without
/// anyone having to remember to withdraw anything.
/// </para>
///
/// <para>
/// Always loopback. A published port is bound on every interface by Docker, but dialling it by
/// <c>127.0.0.1</c> means this resolver cannot be talked into reaching another machine even if the
/// port number happens to collide with something on the LAN.
/// </para>
/// </summary>
public sealed class PublishedPortTargetResolver(
    WorkloadRegistry registry, ILogger<PublishedPortTargetResolver> log) : ITunnelTargetResolver
{
    public TunnelTarget? Resolve(ReadOnlySpan<byte> openPayload)
    {
        if (TunnelFraming.DecodeTarget(openPayload) is not { } port)
        {
            log.LogWarning("The gateway opened an ingress stream without naming a port.");
            return null;
        }

        if (!registry.AllocatedPorts().Contains(port))
        {
            // Loud, because on a healthy fleet it never happens: the control plane only asks for a
            // port this node told it about. It happening means the gateway is confused about which
            // node it is talking to, or something is probing.
            log.LogWarning(
                "Refused an ingress stream to port {Port}: no workload on this node publishes it.", port);
            return null;
        }

        return new TunnelTarget("127.0.0.1", port);
    }
}
