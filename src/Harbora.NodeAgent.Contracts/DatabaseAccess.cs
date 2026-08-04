namespace Harbora.NodeAgent.Contracts;

/// <summary>Database engines a node can mint and revoke external access for.</summary>
public static class DatabaseEngines
{
    public const string PostgreSql = "postgresql";
    public const string MySql = "mysql";
    public const string MongoDb = "mongodb";
    public const string Redis = "redis";

    public static readonly IReadOnlyList<string> All = [PostgreSql, MySql, MongoDb, Redis];

    public static bool IsSupported(string? engine) =>
        engine is not null && All.Contains(engine, StringComparer.OrdinalIgnoreCase);

    /// <summary>The port the engine listens on inside its container, when the spec does not say.</summary>
    public static int DefaultPort(string engine) => engine.ToLowerInvariant() switch
    {
        PostgreSql => 5432,
        MySql => 3306,
        MongoDb => 27017,
        Redis => 6379,
        _ => 0,
    };
}

/// <summary>
/// Request to open external access to a database running on this node.
///
/// <para>
/// The node never binds the database to <c>0.0.0.0</c>. It dials the Harbora TCP gateway outbound
/// and the gateway publishes the endpoint, so the address a customer connects to belongs to
/// Harbora's infrastructure — the customer's firewall gains no new inbound rule, and revoking
/// access is hanging up a connection rather than hoping a port got closed.
/// </para>
/// </summary>
public sealed record DatabaseAccessGrantSpec
{
    public required string GrantId { get; init; }
    public required string TenantId { get; init; }

    /// <summary>Workload id of the database this grant targets.</summary>
    public required string WorkloadId { get; init; }

    public required string Engine { get; init; }

    /// <summary>Container the grant connects to. Resolved on the workload's private network.</summary>
    public required string TargetContainer { get; init; }

    public int? TargetPort { get; init; }

    /// <summary>Logical database / keyspace the credential is scoped to, when the engine has one.</summary>
    public string? DatabaseName { get; init; }

    public required DatabaseAccessMode Mode { get; init; }

    /// <summary>
    /// Lifetime for a temporary grant. Ignored for a persistent one. Enforced by the node itself,
    /// so a control plane that goes away mid-grant does not leave the door open.
    /// </summary>
    public int? TtlSeconds { get; init; }

    /// <summary>
    /// Source addresses allowed through, in CIDR form. Mandatory for a persistent grant; a
    /// temporary grant without one is accepted but flagged, because "briefly open to the whole
    /// internet" is still open to the whole internet.
    /// </summary>
    public IReadOnlyList<string> IpAllowlist { get; init; } = [];

    public int MaxConnections { get; init; } = 10;

    /// <summary>Cap on new connections per minute from all sources combined.</summary>
    public int MaxConnectionsPerMinute { get; init; } = 60;

    /// <summary>Ask the engine to require TLS for the issued credential, where it supports that.</summary>
    public bool RequireTls { get; init; }

    /// <summary>Require a client certificate at the gateway as well as the credential.</summary>
    public bool RequireMutualTls { get; init; }

    /// <summary>
    /// Read-only where the engine can express it. A grant handed to a support engineer for a
    /// diagnosis has no business being able to write.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Persistent grants only, and only when the operator explicitly confirmed in the panel. The
    /// node refuses a persistent grant without it rather than inferring consent from the request.
    /// </summary>
    public bool OperatorConfirmed { get; init; }

    public AuditMetadata? Audit { get; init; }
}

public enum DatabaseAccessMode
{
    /// <summary>Expires on its own. The default and the one that should cover almost every case.</summary>
    Temporary = 0,

    /// <summary>Stays until revoked. Off unless explicitly asked for and explicitly confirmed.</summary>
    Persistent,
}

public enum DatabaseAccessState
{
    Pending = 0,
    Active,
    Expired,
    Revoked,
    Failed,
}

/// <summary>What the node reports back after minting, rotating or revoking a grant.</summary>
public sealed record DatabaseAccessGrantState
{
    public required string GrantId { get; init; }
    public required DatabaseAccessState State { get; init; }
    public required string Engine { get; init; }
    public required DatabaseAccessMode Mode { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null for a persistent grant.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }
    public string? RevokedReason { get; init; }

    /// <summary>Username the node created on the engine. Not a secret.</summary>
    public string? Username { get; init; }

    /// <summary>
    /// The password, returned exactly once — on creation and on rotation. The node stores only an
    /// encrypted copy and never repeats it in a status response, so a leaked status read is not a
    /// credential leak.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>Public endpoint on the Harbora gateway, e.g. <c>db-gw.harbora.io:41823</c>.</summary>
    public string? Endpoint { get; init; }

    public TunnelState? Tunnel { get; init; }

    public int ActiveConnections { get; init; }
    public DateTimeOffset? LastConnectionAt { get; init; }

    /// <summary>Never contains the password; safe to log.</summary>
    public override string ToString() =>
        $"Grant {GrantId} [{Engine}/{Mode}] {State} user={Username ?? "-"} endpoint={Endpoint ?? "-"}";
}

/// <summary>Payload of <c>RevokeDatabaseAccessGrant</c>.</summary>
public sealed record RevokeDatabaseAccessRequest
{
    public required string GrantId { get; init; }
    public string? Reason { get; init; }

    /// <summary>
    /// Drop the engine-side user as well as closing the tunnel. Default true; false is for the
    /// case where the same user is intentionally shared by a rotation about to re-create it.
    /// </summary>
    public bool DropEngineUser { get; init; } = true;
}

/// <summary>Payload of <c>RotateDatabaseAccessCredential</c>.</summary>
public sealed record RotateDatabaseAccessRequest
{
    public required string GrantId { get; init; }

    /// <summary>Seconds the previous password keeps working, where the engine allows an overlap.</summary>
    public int OverlapSeconds { get; init; }
}
