using Harbora.Domain.Common;

namespace Harbora.Domain.Networking;

/// <summary>
/// A workspace's own bring-your-own Cloudflare API token (F9, 2026-08-21 functions-and-services
/// plan, decision 5). Deliberately not the same store as the platform's own Cloudflare credential
/// (<c>Setting</c> rows read by <c>CloudflarePlatformService</c>), which routes the panel's and S3's
/// own TLS — that credential must never be reachable from a workspace, and this one must never be
/// reachable from the platform's own routing. One row per workspace: Cloudflare's token itself is
/// already scoped to whichever zones its owner granted it, so a second token for the same workspace
/// would only be a second grant of the same kind.
///
/// <para>
/// v1 is BYO-token only. Running authoritative DNS ourselves (PowerDNS or similar) stays deferred —
/// that is an ongoing operational commitment, not a feature, and nothing here starts down that road.
/// </para>
/// </summary>
public sealed class CustomerDnsCredential : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>The token, encrypted with the platform key like every other stored credential.</summary>
    public string EncryptedToken { get; set; } = string.Empty;

    /// <summary>The moment the token last proved it could list zones. Null means never yet.</summary>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>
    /// The exact reason the token most recently failed — set on save when a new token cannot verify,
    /// and on any later live call that starts failing (revoked scope, expired token). Read back onto
    /// the page instead of an empty table: an empty table reads as "you have no records", and that is
    /// exactly the fabrication this codebase keeps finding. Null means the token's last use worked.
    /// </summary>
    public string? LastVerificationError { get; set; }
}
