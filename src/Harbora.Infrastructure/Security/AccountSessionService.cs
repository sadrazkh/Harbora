using System.Security.Cryptography;
using Harbora.Data;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Security;

public sealed class AccountSessionService(HarboraDbContext db, Application.Abstractions.ISystemClock clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(24);

    public async Task<UserSession> CreateAsync(
        Guid userId, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var row = new UserSession
        {
            UserId = userId,
            LastSeenAt = now,
            ExpiresAt = now + Lifetime,
            IpAddress = Trim(ipAddress, 64),
            UserAgent = Trim(userAgent, 512),
            CreatedAt = now
        };
        db.UserSessions.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task RevokeAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var row = await db.UserSessions.FirstOrDefaultAsync(
            s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null, ct);
        if (row is null) return;
        row.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct)
    {
        var rows = await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null
            && (exceptSessionId == null || s.Id != exceptSessionId)).ToListAsync(ct);
        foreach (var row in rows) row.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public (string Token, EmailVerificationToken Row) IssueVerification(Guid userId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return (token, new EmailVerificationToken
        {
            UserId = userId,
            TokenHash = Hash(token),
            ExpiresAt = clock.UtcNow + VerificationLifetime,
            CreatedAt = clock.UtcNow
        });
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, max)];
}
