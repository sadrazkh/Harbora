namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// State of one outbound tunnel from this node to the Harbora TCP gateway.
///
/// <para>
/// Direction matters and is the reason this design was chosen: the node dials out, so publishing a
/// database costs the customer no inbound firewall rule, the public IP is Harbora's rather than
/// theirs, and revocation is a socket close the node controls — not a port the node hopes it
/// remembered to unbind.
/// </para>
/// </summary>
public sealed record TunnelState
{
    public required string TunnelId { get; init; }

    /// <summary>Grant this tunnel exists to serve, when it belongs to one.</summary>
    public string? GrantId { get; init; }

    public required TunnelStatus Status { get; init; }

    /// <summary>Public address the gateway published, as clients should use it.</summary>
    public string? PublicEndpoint { get; init; }

    /// <summary>Container and port on this node the tunnel forwards to.</summary>
    public string? LocalTarget { get; init; }

    public DateTimeOffset? ConnectedAt { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }

    public int ActiveConnections { get; init; }
    public long BytesIn { get; init; }
    public long BytesOut { get; init; }

    /// <summary>Number of reconnects since the tunnel was created. A climbing count is a signal.</summary>
    public int ReconnectCount { get; init; }

    public NodeError? LastError { get; init; }
}

public enum TunnelStatus
{
    Pending = 0,
    Connecting,
    Connected,
    Reconnecting,
    Closed,
    Failed,
}

/// <summary>
/// The node's opening frame to the TCP gateway. Sent over a mutually-authenticated TLS connection;
/// the gateway matches it against the grant the control plane registered and either allocates a
/// public port or hangs up.
/// </summary>
public sealed record TunnelRegistration
{
    public required string NodeId { get; init; }
    public required string TunnelId { get; init; }
    public required string GrantId { get; init; }
    public required string TenantId { get; init; }

    /// <summary>Enforced at the gateway, where the client's real address is visible.</summary>
    public IReadOnlyList<string> IpAllowlist { get; init; } = [];

    public int MaxConnections { get; init; } = 10;
    public int MaxConnectionsPerMinute { get; init; } = 60;

    /// <summary>Null asks the gateway to allocate a random high port.</summary>
    public int? RequestedPort { get; init; }

    public bool RequireMutualTls { get; init; }
    public int ProtocolVersion { get; init; } = NodeContract.ProtocolVersion;
}

/// <summary>The gateway's answer to a registration.</summary>
public sealed record TunnelRegistrationResponse
{
    public required bool Accepted { get; init; }
    public string? PublicEndpoint { get; init; }
    public int? PublicPort { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public NodeError? Error { get; init; }
}
