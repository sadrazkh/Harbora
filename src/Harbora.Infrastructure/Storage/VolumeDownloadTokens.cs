using System.Security.Cryptography;
using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Storage;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Storage;

/// <summary>What a mint produced: the raw token to put in the link, and when it stops working.</summary>
public sealed record VolumeDownloadMint(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// What a redemption found — enough to stream the file through <see cref="VolumeFileService"/> — or
/// nothing at all.
/// </summary>
public sealed record VolumeDownloadRedemption(
    bool Ok, Guid ServerId, string VolumeName, string Path, Guid WorkspaceId = default)
{
    public static readonly VolumeDownloadRedemption Refused = new(false, default, string.Empty, string.Empty);
}

/// <summary>
/// Mints and redeems the one-time links that reach a single file in a volume without a panel
/// session. See <see cref="VolumeDownloadToken"/> for the four rules that make handing one to
/// somebody who never signed in acceptable — this type enforces them, the route that calls
/// <see cref="RedeemAsync"/> adds no gate of its own.
/// </summary>
public sealed class VolumeDownloadTokens(HarboraDbContext db, ISystemClock clock)
{
    /// <summary>
    /// Mints a token for exactly one file. <paramref name="app"/> and <paramref name="volume"/> must
    /// already have been resolved through the caller's tenant-filtered collection — this method does
    /// not re-check that the volume belongs to the app or to anybody's workspace, the same split
    /// <c>AppDataController</c> already makes between resolving a request and acting on it.
    /// </summary>
    public async Task<VolumeDownloadMint> MintAsync(
        Harbora.Domain.Apps.App app, Harbora.Domain.Apps.Volume volume, string path, CancellationToken ct)
    {
        var raw = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = clock.UtcNow;

        var token = new VolumeDownloadToken
        {
            // Overrides BaseEntity's own DateTimeOffset.UtcNow default. Expiry is measured from this
            // value against clock.UtcNow in RedeemAsync/SweepAsync, so both ends of that comparison
            // must come from the same clock — the injected one, not the wall clock — or the two
            // could disagree by however far the real clock and this platform's notion of "now" have
            // drifted.
            CreatedAt = now,
            AppId = app.Id,
            VolumeId = volume.Id,
            Path = path,
            TokenHash = Hash(raw)
        };

        db.VolumeDownloadTokens.Add(token);
        await db.SaveChangesAsync(ct);

        return new VolumeDownloadMint(raw, now + AdminerSession.Lifetime);
    }

    /// <summary>
    /// Spends a token, or explains why it cannot be. Runs with no workspace in scope — the caller has
    /// no session, by design — so resolving the app this token names has to reach around the tenant
    /// filter deliberately, the way <c>AdminerService</c>'s own sweeper already does when it removes
    /// routes. That is not a second authorization decision: the app/volume/path pairing was proved
    /// once, through the tenant filter, when the token was minted. This only completes the bounded,
    /// self-expiring grant mint already made.
    /// </summary>
    public async Task<VolumeDownloadRedemption> RedeemAsync(string rawToken, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var now = clock.UtcNow;

        var record = await db.VolumeDownloadTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        // Never existed, already spent, or past its hour — one answer for all three, the same
        // "expired, spent and never-existed are one answer" the redemption route gives the caller as
        // a single 404.
        if (record is null || record.IsSpent || AdminerSession.Expired(record.CreatedAt, now))
            return VolumeDownloadRedemption.Refused;

        var app = await db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == record.AppId, ct);
        var volume = await db.Volumes.FirstOrDefaultAsync(v => v.Id == record.VolumeId, ct);

        if (app is null || volume is null || volume.AppId != app.Id)
            return VolumeDownloadRedemption.Refused;

        // Spent now, before anything is streamed — not after. A stream that fails partway must not
        // leave the token usable a second time, the same reason PasswordResetToken is consumed even
        // when the reset it started ultimately fails.
        record.UsedAt = now;
        await db.SaveChangesAsync(ct);

        return new VolumeDownloadRedemption(true, app.ServerId, volume.Name, record.Path, app.WorkspaceId);
    }

    /// <summary>
    /// Retires spent and expired rows, the way <c>AdminerService.SweepAsync</c> retires its own
    /// sessions (<c>AdminerService.cs:186</c>) — an unbounded table of dead tokens is its own problem,
    /// even though each row only ever unlocked a single file for an hour.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        // The same shape as AdminerSession.Expired(startedAt, now) — now - startedAt >= Lifetime —
        // inlined because EF Core cannot translate a call to that method inside a query.
        var dead = await db.VolumeDownloadTokens
            .Where(t => t.UsedAt != null || now - t.CreatedAt >= AdminerSession.Lifetime)
            .ToListAsync(ct);

        if (dead.Count == 0) return 0;

        db.VolumeDownloadTokens.RemoveRange(dead);
        await db.SaveChangesAsync(ct);
        return dead.Count;
    }

    private static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}

/// <summary>Retires expired and spent volume download tokens on the same tick <see cref="AdminerSweeper"/> uses.</summary>
public sealed class VolumeDownloadTokenSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<VolumeDownloadTokenSweeper> logger) : BackgroundService
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
                var tokens = scope.ServiceProvider.GetRequiredService<VolumeDownloadTokens>();

                var closed = await tokens.SweepAsync(stoppingToken);
                if (closed > 0) logger.LogInformation("Retired {Count} volume download token(s).", closed);
            }
            catch (Exception ex) { logger.LogError(ex, "Sweeping volume download tokens failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
