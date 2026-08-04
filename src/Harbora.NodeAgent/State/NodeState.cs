using Harbora.NodeAgent.Contracts;

namespace Harbora.NodeAgent.State;

/// <summary>
/// What the agent must remember across restarts. Everything here is either impossible to
/// re-derive (the node id, the resume position) or expensive to (the granted scopes, the drain
/// flag) — a restarted agent that forgot it was draining would happily accept the deploy the
/// operator drained it to avoid.
/// </summary>
public sealed record NodeState
{
    public string? NodeId { get; init; }

    /// <summary>Version agreed on the last successful session. Re-negotiated on every connect.</summary>
    public int NegotiatedProtocolVersion { get; init; } = NodeContract.ProtocolVersion;

    public IReadOnlyList<string> GrantedScopes { get; init; } = [];

    public string? ResumeToken { get; init; }

    /// <summary>Highest control-plane sequence this node durably processed.</summary>
    public long LastReceivedSequence { get; init; }

    /// <summary>Highest sequence this node has sent. Continues across reconnects within a session.</summary>
    public long LastSentSequence { get; init; }

    public bool Draining { get; init; }
    public string? DrainReason { get; init; }

    /// <summary>
    /// Whether this node keeps an ingress tunnel open so the control plane's proxy can reach its
    /// published ports.
    ///
    /// <para>
    /// Remembered for the same reason the drain flag is: on a node behind NAT this is the only path
    /// its apps have to their users, and a restarted agent that forgot would leave every site down
    /// until somebody noticed and re-sent the command.
    /// </para>
    /// </summary>
    public bool IngressEnabled { get; init; }

    public string? ControlPlaneUrl { get; init; }
    public string? TunnelGatewayUrl { get; init; }
    public string? MinimumAgentVersion { get; init; }
    public int HeartbeatIntervalSeconds { get; init; } = 30;

    public DateTimeOffset? EnrolledAt { get; init; }
    public DateTimeOffset? LastConnectedAt { get; init; }

    /// <summary>Version running before the last agent update, so a rollback knows what to restore.</summary>
    public string? PreviousAgentVersion { get; init; }

    public bool IsEnrolled => !string.IsNullOrEmpty(NodeId);

    /// <summary>
    /// Whether a scope was granted at enrollment. An empty list is treated as "not yet told", which
    /// is the state a node is in between enrollment and its first hello-ack — it must not be read
    /// as "granted everything".
    /// </summary>
    public bool HasScope(string scope) =>
        GrantedScopes.Contains(scope, StringComparer.Ordinal);
}
