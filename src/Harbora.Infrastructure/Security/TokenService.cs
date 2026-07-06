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

        var token = await db.ApiTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Prefix == prefix && !t.IsRevoked, ct);
        if (token is null) return null;
        if (token.ExpiresAt is { } exp && exp < clock.UtcNow) return null;

        var presentedHash = Sha256(presentedToken);
        var a = Convert.FromHexString(presentedHash);
        var b = Convert.FromHexString(token.TokenHash);
        if (!CryptographicOperations.FixedTimeEquals(a, b)) return null;

        // best-effort last-used stamp
        await db.ApiTokens.Where(t => t.Id == token.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, clock.UtcNow), ct);

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
