using System.Security.Cryptography;
using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Backups;

/// <summary>What a mint produced: the raw token to put in the link, and when it stops working.</summary>
public sealed record BackupDownloadMint(string Token, DateTimeOffset ExpiresAt);

/// <summary>What a redemption found, or nothing at all.</summary>
public sealed record BackupDownloadRedemption(bool Ok, Guid BackupId)
{
    public static readonly BackupDownloadRedemption Refused = new(false, default);
}

/// <summary>
/// Mints and redeems the one-time links a self-serve database export is downloaded through — sub-project
/// 10. See <see cref="BackupDownloadToken"/> for the rules that make handing one to somebody with no
/// panel session acceptable; this reuses the exact shape <c>VolumeDownloadTokens</c> (D4) established
/// for the same purpose, retargeted at a <see cref="Backup"/> instead of a volume file.
/// </summary>
public sealed class BackupDownloadTokens(HarboraDbContext db, ISystemClock clock)
{
    /// <summary>
    /// Mints a token for exactly one backup. <paramref name="backup"/> must already have been
    /// resolved through the caller's own tenant-filtered collection — this method does not re-check
    /// workspace ownership, the same split <c>AppDataController</c>/<c>VolumeDownloadTokens</c> make
    /// between resolving a request and acting on it.
    /// </summary>
    public async Task<BackupDownloadMint> MintAsync(Backup backup, CancellationToken ct)
    {
        var raw = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = clock.UtcNow;

        var token = new BackupDownloadToken
        {
            // Overrides BaseEntity's own DateTimeOffset.UtcNow default — see VolumeDownloadTokens'
            // own MintAsync for why both ends of the expiry comparison must come from the same clock.
            CreatedAt = now,
            BackupId = backup.Id,
            TokenHash = Hash(raw)
        };

        db.BackupDownloadTokens.Add(token);
        await db.SaveChangesAsync(ct);

        return new BackupDownloadMint(raw, now + AdminerSession.Lifetime);
    }

    /// <summary>
    /// Spends a token, or explains why it cannot be. Runs with no workspace in scope — the caller has
    /// no session, by design — so resolving the backup this token names has to reach around the
    /// tenant filter deliberately, the same way <c>VolumeDownloadTokens.RedeemAsync</c> does for apps.
    /// That is not a second authorization decision: the backup's ownership was proved once, through
    /// the tenant filter, when the token was minted.
    /// </summary>
    public async Task<BackupDownloadRedemption> RedeemAsync(string rawToken, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var now = clock.UtcNow;

        var record = await db.BackupDownloadTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        // Never existed, already spent, or past its hour — one answer for all three, the same
        // "expired, spent and never-existed are one answer" the volume download route gives.
        if (record is null || record.IsSpent || AdminerSession.Expired(record.CreatedAt, now))
            return BackupDownloadRedemption.Refused;

        var backup = await db.Backups.IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.Id == record.BackupId, ct);
        if (backup is null || backup.Status != Domain.Common.BackupStatus.Completed || backup.ArtifactPath is null)
            return BackupDownloadRedemption.Refused;

        // Spent now, before anything is streamed — not after. A stream that fails partway must not
        // leave the token usable a second time.
        record.UsedAt = now;
        await db.SaveChangesAsync(ct);

        return new BackupDownloadRedemption(true, backup.Id);
    }

    /// <summary>
    /// Retires spent and expired rows, the way <c>VolumeDownloadTokens.SweepAsync</c> retires its own
    /// — an unbounded table of dead tokens is its own problem, even though each row only ever unlocked
    /// one artifact for an hour.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        var dead = await db.BackupDownloadTokens
            .Where(t => t.UsedAt != null || now - t.CreatedAt >= AdminerSession.Lifetime)
            .ToListAsync(ct);

        if (dead.Count == 0) return 0;

        db.BackupDownloadTokens.RemoveRange(dead);
        await db.SaveChangesAsync(ct);
        return dead.Count;
    }

    private static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}

/// <summary>Retires expired and spent backup download tokens on the same tick <c>VolumeDownloadTokenSweeper</c> uses.</summary>
public sealed class BackupDownloadTokenSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<BackupDownloadTokenSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var tokens = scope.ServiceProvider.GetRequiredService<BackupDownloadTokens>();

                var closed = await tokens.SweepAsync(stoppingToken);
                if (closed > 0) logger.LogInformation("Retired {Count} backup download token(s).", closed);
            }
            catch (Exception ex) { logger.LogError(ex, "Sweeping backup download tokens failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
