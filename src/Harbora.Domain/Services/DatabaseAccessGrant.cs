using Harbora.Domain.Common;

namespace Harbora.Domain.Services;

/// <summary>How long an outside connection may be reached for.</summary>
public enum DatabaseAccessKind
{
    /// <summary>Expires on its own. The normal case.</summary>
    Temporary = 0,

    /// <summary>Stays until somebody turns it off. Off by default, and warned about.</summary>
    Persistent = 1
}

public enum DatabaseAccessStatus
{
    /// <summary>Requested; the node has not confirmed the login and tunnel yet.</summary>
    Pending = 0,

    Active = 1,

    /// <summary>Its time ran out and the sweeper closed it.</summary>
    Expired = 2,

    /// <summary>A person turned it off.</summary>
    Revoked = 3,

    /// <summary>The node could not create it. The reason is on the audit record, not here.</summary>
    Failed = 4
}

/// <summary>
/// Permission for something outside Harbora to reach a managed database.
///
/// The row is the permission — the credential is derived from it and the tunnel is torn down with
/// it. That is deliberate: a design where the credential is the permission leaves a working password
/// behind every time a cleanup step is missed, and the missed step is invisible until somebody uses
/// the password months later.
///
/// The password is never stored in plaintext. It is shown once, at creation, and after that only its
/// hash survives — so a leaked database backup does not hand over live database logins.
/// </summary>
public class DatabaseAccessGrant : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid ManagedServiceId { get; set; }
    public ManagedService? ManagedService { get; set; }

    /// <summary>Who asked for it. Kept for the audit trail even after they leave.</summary>
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByEmail { get; set; }

    public DatabaseAccessKind Kind { get; set; } = DatabaseAccessKind.Temporary;
    public DatabaseAccessStatus Status { get; set; } = DatabaseAccessStatus.Pending;

    /// <summary>The login created on the database for this grant alone.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the generated password. The plaintext is returned once from the service that made it
    /// and never persisted.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Where a client connects: the gateway, never the node.</summary>
    public string? GatewayHost { get; set; }
    public int? GatewayPort { get; set; }

    /// <summary>The node's handle for the tunnel, so it can be taken down.</summary>
    public string? TunnelId { get; set; }

    /// <summary>
    /// Comma-separated CIDRs. Empty means anywhere, which is why the interface asks for it and
    /// says plainly what empty means.
    /// </summary>
    public string? AllowedIps { get; set; }

    /// <summary>Null for a persistent grant. Set for every temporary one.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>How many times the window has been pushed out, so it cannot be extended for ever.</summary>
    public int ExtensionCount { get; set; }

    /// <summary>Whether the connection is encrypted, as reported when the tunnel was made.</summary>
    public bool TlsEnabled { get; set; }
}

/// <summary>
/// What happened to a grant, kept separately from the grant itself.
///
/// A grant can be deleted; what was done with it should not vanish at the same moment. This is the
/// record that answers "who opened production to the internet in March".
/// </summary>
public class DatabaseAccessAudit : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid? GrantId { get; set; }
    public Guid ManagedServiceId { get; set; }

    /// <summary>created | activated | extended | rotated | revoked | expired | failed | connected</summary>
    public string Action { get; set; } = string.Empty;

    public string? ActorEmail { get; set; }
    public string? ClientIp { get; set; }

    /// <summary>Free text — a refusal reason, a node error. Never a password.</summary>
    public string? Detail { get; set; }
}
