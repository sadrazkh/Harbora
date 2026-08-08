using System.Security.Cryptography;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Security;

/// <summary>
/// Issues opaque tokens of the form "hbr_{env}_{prefix}_{secret}". Only the SHA-256 hash
/// and the public prefix are stored; the plaintext is shown once. Validation looks the row
/// up by prefix then compares hashes in constant time.
/// </summary>
public sealed class TokenService(HarboraDbContext db, ISystemClock clock) : ITokenService
{
    public NewToken Issue(Guid userId, string name, TokenType type, TimeSpan? lifetime)
    {
        var prefix = "hbr_" + (type == TokenType.Cli ? "cli_" : "api_") + RandomAlphaNum(8);
        var secret = RandomAlphaNum(40);
        var plaintext = $"{prefix}_{secret}";
        var hash = Sha256(plaintext);
        return new NewToken(prefix, plaintext, hash);
    }

    public async Task<Guid?> ValidateAsync(string presentedToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presentedToken)) return null;

        // prefix = first three underscore-delimited groups: hbr_api_XXXX
        var parts = presentedToken.Split('_');
        if (parts.Length < 4) return null;
        var prefix = string.Join('_', parts[0], parts[1], parts[2]);

        // Tracked, because the row is stamped below. Reading it untracked and then writing it with
        // ExecuteUpdate cost the same two round trips and made bearer authentication a relational-only
        // code path — which is why no test could reach it until the HTTP lane needed to.
        var token = await db.ApiTokens
            .FirstOrDefaultAsync(t => t.Prefix == prefix && !t.IsRevoked, ct);
        if (token is null) return null;
        if (token.ExpiresAt is { } exp && exp < clock.UtcNow) return null;

        var presentedHash = Sha256(presentedToken);
        var a = Convert.FromHexString(presentedHash);
        var b = Convert.FromHexString(token.TokenHash);
        if (!CryptographicOperations.FixedTimeEquals(a, b)) return null;

        // Best-effort last-used stamp.
        //
        // Two things this SaveChanges carries that the ExecuteUpdate it replaced did not, both
        // deliberate and both worth knowing before adding anything above it:
        //
        //  * The context's SaveChangesAsync override stamps UpdatedAt on every modified BaseEntity,
        //    so an authenticated request now also writes ApiToken.UpdatedAt. Nothing reads that
        //    column, and the cost is one more column in the same UPDATE — but "when was this token
        //    record last edited" is no longer distinct from "when was it last used".
        //  * TokenAuthenticationHandler resolves the request-scoped HarboraDbContext, so this
        //    flushes that request's whole change tracker. It is empty today: authentication runs
        //    before anything that writes. Middleware added ahead of UseAuthentication that writes
        //    to this context would be committed here, as a side effect of presenting a token.
        token.LastUsedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return token.UserId;
    }

    public static string Sha256(string input) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static string RandomAlphaNum(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new System.Text.StringBuilder(length);
        foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }
}
