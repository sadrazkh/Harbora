using Harbora.Domain.Common;

namespace Harbora.Domain.Identity;

/// <summary>
/// One chance to set a password without knowing the old one.
///
/// Only the token's hash is stored: the table must not be a list of working reset links, or a
/// database read becomes an account takeover. Single-use and short-lived, and consumed even when
/// the reset ultimately fails — a link from an email survives in forwarded threads and provider
/// logs far longer than the minutes it is meant to live.
/// </summary>
public class PasswordResetToken : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>SHA-256 of the token that went into the email, hex, lowercase.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set the moment it is redeemed; a used token never works twice.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
