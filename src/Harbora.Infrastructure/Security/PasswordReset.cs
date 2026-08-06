using System.Security.Cryptography;
using Harbora.Domain.Identity;

namespace Harbora.Infrastructure.Security;

/// <summary>Why a reset link was refused, or null on success — never a boolean pair to disagree.</summary>
public enum PasswordResetRefusal
{
    /// <summary>No row matches the token — mistyped, or never issued.</summary>
    Unknown = 0,
    Expired = 1,
    AlreadyUsed = 2
}

/// <summary>
/// The rules of a password-reset link.
///
/// Small on purpose, and separate from the controller that uses it: every branch here is an
/// account-takeover path if it leans the wrong way. The token travels as a URL; only its hash is
/// stored; it works once, inside its window, and both checks are decided against a clock the
/// caller supplies — this codebase has already paid for tests pinned to a wall clock.
/// </summary>
public static class PasswordReset
{
    /// <summary>How long a link works. Long enough for a slow inbox, short enough to limit a leak.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>A fresh token for the email, and the hash for the row. The token itself is never stored.</summary>
    public static (string Token, string Hash) Issue()
    {
        // 256 bits, URL-safe. Guessing is not a strategy at this size, and the rate limiter on the
        // endpoint makes it not a strategy at any size.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (token, HashOf(token));
    }

    /// <summary>The stored shape of a token that just arrived in a request.</summary>
    public static string HashOf(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Whether this row redeems the presented token, against the supplied clock.
    /// The row is matched by hash before this is called; null row means no match existed.
    /// </summary>
    public static PasswordResetRefusal? Check(PasswordResetToken? row, DateTimeOffset now)
    {
        if (row is null) return PasswordResetRefusal.Unknown;
        if (row.UsedAt is not null) return PasswordResetRefusal.AlreadyUsed;
        if (now >= row.ExpiresAt) return PasswordResetRefusal.Expired;
        return null;
    }
}
