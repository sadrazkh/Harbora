using Harbora.Domain.Common;

namespace Harbora.Domain.Identity;

/// <summary>A server-side browser session. The signed cookie carries only this row's id.</summary>
public sealed class UserSession : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
}

/// <summary>Single-use digest proving control of a registration email address.</summary>
public sealed class EmailVerificationToken : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
