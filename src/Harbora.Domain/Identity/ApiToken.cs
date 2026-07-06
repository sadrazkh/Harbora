using Harbora.Domain.Common;

namespace Harbora.Domain.Identity;

/// <summary>
/// API / CLI access token. Only a SHA-256 hash of the secret is stored; the plaintext
/// (prefix + secret) is shown to the user exactly once at creation time.
/// </summary>
public class ApiToken : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Public, non-secret prefix used to locate the token row (e.g. "hbr_live_ab12cd").</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 hash of the full token secret.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public TokenType Type { get; set; } = TokenType.Api;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public bool IsRevoked { get; set; }

    /// <summary>Space-separated scopes, e.g. "deploy read logs". Empty = full user scope.</summary>
    public string Scopes { get; set; } = string.Empty;
}
