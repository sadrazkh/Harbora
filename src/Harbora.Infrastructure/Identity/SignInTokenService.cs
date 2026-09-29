using System.Security.Cryptography;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Identity;

/// <summary>What minting a token produced, or why it did not. <paramref name="PlaintextToken"/> is the
/// one and only time the secret exists outside a hash — the caller must show it and discard it.</summary>
public sealed record SignInTokenIssue(SignInToken? Token, string? PlaintextToken, string? Refusal)
{
    public bool Ok => Token is not null;
}

/// <summary>What redeeming a token produced, or why it did not.</summary>
public sealed record SignInTokenRedemption(SignInToken? Token, UserSession? Session, string? Refusal)
{
    public bool Ok => Session is not null;
}

/// <summary>
/// Mints, redeems, checks and revokes one-time sign-in tokens — the owner's-own-agent analogue of
/// <c>SupportSessionService</c>.
///
/// <para>
/// <b>The whole design is in the private <c>TryClaimAsync</c>, called from <see cref="RedeemAsync"/>.</b>
/// Everything else here writes or reads rows; that one method is what makes "one-time" a constraint. A
/// read of <c>RedeemedAt</c> followed by a separate write is exactly the shape commit <c>3c755ac</c>
/// (HARBORA-0057) removed from <c>DeploymentEngine.QueueDeploymentAsync</c>, because two callers can
/// both read null and both proceed — here that would mean two concurrent redemptions both opening a
/// session from the same token. Redemption instead composes the check and the write into one
/// conditional <c>ExecuteUpdateAsync</c> against Postgres, so the database — not this method's own
/// control flow — is what only ever lets one caller win. See <c>TryClaimAsync</c>'s own doc for the one
/// place that is not true: EF InMemory, which cannot run <c>ExecuteUpdateAsync</c> at all and falls back
/// to a plain, deliberately non-atomic read-then-write so the rest of the feature can still be tested
/// on a machine with no Postgres.
/// </para>
///
/// <para>
/// <see cref="LiveAsync"/> is called on every request under a token-opened session, the same way
/// <c>SupportSessionService.LiveAsync</c> is called on every request under a support session: it
/// answers from the row, not the cookie, so a cookie that survived its session or was issued before the
/// owner revoked it authorises nothing.
/// </para>
/// </summary>
public sealed class SignInTokenService(
    HarboraDbContext db, ISystemClock clock, Security.AccountSessionService accountSessions)
{
    /// <summary>
    /// Mints a token for <paramref name="userId"/>'s own account, scoped to <paramref
    /// name="workspaceId"/>. Refuses per <see cref="SignInTokenAccess.RefuseCreate"/> plus the one
    /// database fact it needs: whether the creator actually belongs to that workspace.
    /// </summary>
    public async Task<SignInTokenIssue> IssueAsync(
        Guid userId, Guid workspaceId, string? purpose, CancellationToken ct)
    {
        var isMember = await db.WorkspaceMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.UserId == userId && m.WorkspaceId == workspaceId, ct);

        if (SignInTokenAccess.RefuseCreate(isMember, purpose) is { } refusal)
            return new(null, null, refusal);

        var now = clock.UtcNow;

        // Same shape PasswordReset.Issue() uses — 256 bits, URL-safe — so the plaintext survives
        // equally well pasted into a form or carried in a link's query string. The hash is the one
        // TokenService.Sha256 already uses for API/CLI tokens: one hashing scheme for every bearer
        // credential this platform issues, not a second one invented here.
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var plaintext = Convert.ToBase64String(secretBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var token = new SignInToken
        {
            UserId = userId,
            WorkspaceId = workspaceId,
            TokenHash = Security.TokenService.Sha256(plaintext),
            Purpose = purpose!.Trim(),
            CreatedByUserId = userId,
            // Overrides BaseEntity's own DateTimeOffset.UtcNow default with the injected clock's,
            // the same way AccountSessionService.CreateAsync sets UserSession.CreatedAt — so a rule
            // measured against this row can be tested at a clock boundary rather than the wall clock.
            CreatedAt = now,
            ExpiresAt = now + SignInTokenAccess.RedemptionLifetime
        };
        db.SignInTokens.Add(token);
        await db.SaveChangesAsync(ct);

        return new(token, plaintext, null);
    }

    /// <summary>
    /// Redeems a presented token: one atomic conditional update claims it, and only the caller whose
    /// update actually matched a row goes on to open a session. Everyone else — a second honest attempt,
    /// or the loser of two that raced — is refused with the identical sentence, because from outside
    /// there is no difference between "already used" and "just lost the race to use it".
    /// </summary>
    public async Task<SignInTokenRedemption> RedeemAsync(
        string? presentedToken, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
            return new(null, null, SignInTokenAccess.UnknownTokenMessage);

        var hash = Security.TokenService.Sha256(presentedToken.Trim());
        var now = clock.UtcNow;

        var candidate = await db.SignInTokens.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (candidate is null) return new(null, null, SignInTokenAccess.UnknownTokenMessage);

        var claimed = await TryClaimAsync(candidate.Id, now, ipAddress, ct);

        if (claimed == 0)
        {
            // Diagnosed off the stored ExpiresAt, not recomputed from CreatedAt through
            // SignInTokenAccess.Expired — ExpiresAt is exactly what the WHERE clause above just
            // tested, so the reason given here can never disagree with the check that produced it,
            // including if RedemptionLifetime is ever changed after this row was already written.
            var refusal = SignInTokenAccess.RefuseRedeem(
                alreadyRedeemed: candidate.RedeemedAt is not null,
                isRevoked: candidate.RevokedAt is not null,
                isExpired: now >= candidate.ExpiresAt);
            return new(null, null, refusal ?? SignInTokenAccess.UnknownTokenMessage);
        }

        // Past this line this call is provably the only one that could have reached here for this
        // row — the update above is what proves it, not an assumption this method makes about its own
        // caller. AccountSessionService.CreateAsync is the ordinary sign-in mechanism, reused rather
        // than reinvented: "sign out all devices", a suspended account and a revoked membership all
        // apply to a token-opened session exactly as they apply to the owner's own.
        var session = await accountSessions.CreateAsync(candidate.UserId, ipAddress, userAgent, ct);

        // The session's own bound, written once now that RedeemedAt (the anchor it is measured from)
        // is finally known. Nothing else can touch this row's SessionId between the claim above and
        // here — this call already proved it is the only writer left — so there is no race left to
        // guard against, only the same InMemory/relational split every write to this table goes through.
        var sessionExpiresAt = now + SignInTokenAccess.SessionLifetime;
        await SetSessionAsync(candidate.Id, session.Id, sessionExpiresAt, ct);

        candidate.RedeemedAt = now;
        candidate.RedeemedIp = ipAddress;
        candidate.SessionId = session.Id;
        candidate.SessionExpiresAt = sessionExpiresAt;

        return new(candidate, session, null);
    }

    /// <summary>
    /// The one atomic operation the whole feature depends on: claims <paramref name="tokenId"/> by
    /// setting <c>RedeemedAt</c>/<c>RedeemedIp</c>, but only if it is still unredeemed, unrevoked and
    /// unexpired — all three folded into one <c>UPDATE</c>'s own <c>WHERE</c> clause, so the check and
    /// the write are a single database operation rather than a read this method reasons about and a
    /// write that trusts what it saw. Two concurrent calls reaching this for the same row are two
    /// genuinely concurrent <c>UPDATE</c> statements against it: Postgres serialises them, the second
    /// sees the first's committed row and matches zero, and "rows affected" (returned here) is the
    /// only signal either caller gets — there is no window in between for both to believe they won.
    /// This is HARBORA-0057's own fix (commit <c>3c755ac</c>) in the shape the brief for this feature
    /// asks for specifically: a conditional <c>ExecuteUpdateAsync</c> rather than that commit's unique
    /// index, because this race is between two updates of one already-existing row, not two inserts.
    ///
    /// <para>
    /// EF's InMemory provider does not implement <c>ExecuteUpdateAsync</c> at all — not "runs it
    /// unraced", it throws outright — so <see cref="Data.HarboraDbContext.Database"/>'s own
    /// <c>IsRelational()</c> flag picks a materialise-then-save fallback on that provider, the same
    /// shape <c>DataRetentionSweeper</c> and its siblings already use for <c>ExecuteDeleteAsync</c>.
    /// <b>The fallback is not atomic</b> — a read and a write are two steps again, and two truly
    /// concurrent InMemory callers could both win it. It exists only so the ordinary, single-caller
    /// path (mint, redeem, list, revoke, the banner) can be driven through this codebase's HTTP test
    /// harness at all; the one fact it cannot stand in for — two concurrent callers, one winner — is
    /// proven only in the Postgres lane, against the real <c>ExecuteUpdateAsync</c> above.
    /// </para>
    /// </summary>
    private async Task<int> TryClaimAsync(Guid tokenId, DateTimeOffset now, string? ipAddress, CancellationToken ct)
    {
        var unclaimed = db.SignInTokens.IgnoreQueryFilters()
            .Where(t => t.Id == tokenId && t.RedeemedAt == null && t.RevokedAt == null && t.ExpiresAt > now);

        if (db.Database.IsRelational())
        {
            var claimed = await unclaimed.ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RedeemedAt, now)
                .SetProperty(t => t.RedeemedIp, ipAddress), ct);
            await RefreshTrackedAsync(tokenId, ct);
            return claimed;
        }

        var row = await unclaimed.FirstOrDefaultAsync(ct);
        if (row is null) return 0;
        row.RedeemedAt = now;
        row.RedeemedIp = ipAddress;
        await db.SaveChangesAsync(ct);
        return 1;
    }

    /// <summary>The provisioning half of a claimed redemption: <see cref="TryClaimAsync"/>'s own
    /// relational/InMemory split, for the write that follows it.</summary>
    private async Task SetSessionAsync(Guid tokenId, Guid sessionId, DateTimeOffset sessionExpiresAt, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            await db.SignInTokens.IgnoreQueryFilters().Where(t => t.Id == tokenId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.SessionId, sessionId)
                    .SetProperty(t => t.SessionExpiresAt, sessionExpiresAt), ct);
            await RefreshTrackedAsync(tokenId, ct);
            return;
        }

        var row = await db.SignInTokens.IgnoreQueryFilters().FirstAsync(t => t.Id == tokenId, ct);
        row.SessionId = sessionId;
        row.SessionExpiresAt = sessionExpiresAt;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The row behind <paramref name="tokenId"/> if the session it opened still authorises anything,
    /// else null — closing it on the way out when its own hour is what ended it, so a later read (the
    /// owner's list, this same check on the next request) says "ended" rather than showing a session
    /// that appears to still be running.
    ///
    /// <para>Called on every request under a token-session claim. This is the enforcement: the cookie
    /// says only which row to look at.</para>
    /// </summary>
    /// <summary>
    /// Brings a tracked copy of this row back in line with the database after an
    /// <c>ExecuteUpdateAsync</c>, which writes straight to Postgres and never touches the change
    /// tracker.
    ///
    /// <para>
    /// Without this, a context that had already loaded the row — <see cref="IssueAsync"/> leaves the
    /// row it just added tracked — kept serving the pre-update copy to every later tracked query in
    /// the same scope. The first Postgres-lane run showed it: issue, redeem, then
    /// <see cref="LiveAsync"/> on one context answered "not live" for a session that had just been
    /// opened, because identity resolution handed back the tracked row with <c>RedeemedAt</c> still
    /// null. It never failed on EF InMemory, whose fallback path writes through tracked entities.
    /// </para>
    /// </summary>
    private async Task RefreshTrackedAsync(Guid tokenId, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<SignInToken>().FirstOrDefault(e => e.Entity.Id == tokenId);
        if (tracked is not null) await tracked.ReloadAsync(ct);
    }

    public async Task<SignInToken?> LiveAsync(Guid tokenId, Guid userId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var token = await db.SignInTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tokenId, ct);

        // A claim naming somebody else's token, or one that was never actually redeemed, is not a
        // live session — the ways either could happen are all ways nobody meant, which is exactly why
        // they are checked rather than assumed impossible.
        if (token is null || token.UserId != userId) return null;
        if (token.RevokedAt is not null) return null;
        if (token.RedeemedAt is null || token.SessionExpiresAt is null) return null;

        if (now >= token.SessionExpiresAt)
        {
            token.EndedBy = SignInTokenEnding.SessionEnded;
            await db.SaveChangesAsync(ct);
            return null;
        }

        return token;
    }

    /// <summary>
    /// Revokes a token by hand — before redemption, this simply spends it; after redemption, it also
    /// ends the session it opened, so "revoke" reads the same way whichever moment it happens at.
    /// Idempotent: a second press, or one racing the session's own hour running out, leaves the row
    /// naming whichever happened first.
    /// </summary>
    public async Task<SignInToken?> RevokeAsync(Guid ownerUserId, Guid tokenId, CancellationToken ct)
    {
        var token = await db.SignInTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.CreatedByUserId == ownerUserId, ct);
        if (token is null) return null;
        if (token.RevokedAt is not null) return token;

        var now = clock.UtcNow;
        token.RevokedAt = now;
        token.EndedBy = SignInTokenEnding.Revoked;

        // Kills the underlying browser session too, not only this row's own liveness check — the same
        // belt-and-suspenders StartSupport's ordinary UserSession gets, so "sign out all devices" and
        // this table agree about which sessions still exist rather than one of them going stale.
        if (token.SessionId is { } sessionId)
            await accountSessions.RevokeAsync(token.UserId, sessionId, ct);

        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>Every token this owner has ever minted, newest first — the list page's own read.</summary>
    public async Task<IReadOnlyList<SignInToken>> ForOwnerAsync(Guid ownerUserId, int take, CancellationToken ct) =>
        await db.SignInTokens.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.CreatedByUserId == ownerUserId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
}
