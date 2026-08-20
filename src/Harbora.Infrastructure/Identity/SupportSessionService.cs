using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Identity;

/// <summary>What starting a support session produced, or why it did not.</summary>
/// <param name="Session">The row. Null on refusal.</param>
/// <param name="Refusal">Why not, in words the administrator can act on.</param>
public sealed record SupportSessionStart(SupportSession? Session, string? Refusal)
{
    public bool Ok => Session is not null;
}

/// <summary>
/// Opens, checks and closes the periods during which a platform administrator is signed in as a
/// customer.
///
/// <para>
/// <b>The whole design is in <see cref="LiveAsync"/>.</b> Everything else here writes rows; that one
/// method is what makes the hour real. It is called on every single request carrying a support
/// claim, and it answers from the row rather than from the cookie — so a cookie that survived its
/// session, was copied, or was issued before the operator pressed "end now" authorises nothing.
/// Expiry encoded in a cookie is expiry the holder of the cookie decides.
/// </para>
///
/// <para>
/// Reads here never scope by workspace and must not: the middleware asking whether a session is
/// still live has no scope resolved yet, and the row belongs to the platform's side of the
/// arrangement rather than to the tenant's. <see cref="ForWorkspaceAsync"/> is the one read a
/// customer sees, and it names its workspace explicitly — the only tenant protection this table has.
/// </para>
/// </summary>
public sealed class SupportSessionService(HarboraDbContext db, ISystemClock clock)
{
    /// <summary>
    /// Opens a session, or refuses with a sentence. The refusal rules are
    /// <see cref="SupportAccess.RefuseStart"/>'s — pure, so they can be read without a request —
    /// plus the one rule that needs the database: an administrator already inside somebody's account
    /// does not get a second one.
    /// </summary>
    public async Task<SupportSessionStart> StartAsync(
        Guid adminUserId, string adminEmail, Guid targetUserId, Guid workspaceId,
        string? reason, string? ipAddress, CancellationToken ct)
    {
        var target = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (target is null) return new(null, "That account no longer exists.");

        var isMember = await db.WorkspaceMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.UserId == targetUserId && m.WorkspaceId == workspaceId, ct);

        if (SupportAccess.RefuseStart(adminUserId, targetUserId, target.IsActive, isMember, reason) is { } refusal)
            return new(null, refusal);

        var now = clock.UtcNow;

        // One at a time. Two live sessions for one administrator would leave the "end now" button
        // ending whichever the cookie happened to name, and the other one running unattended with
        // nobody's browser attached to notice.
        var existing = await db.SupportSessions
            .Where(s => s.AdminUserId == adminUserId && s.EndedAt == null)
            .ToListAsync(ct);
        foreach (var open in existing)
        {
            // Anything still inside its hour is a real session somebody is sitting in; anything past
            // it is a row the sweep of a request never reached because nobody made one.
            open.EndedAt = now;
            open.EndedBy = open.IsLiveAt(now)
                ? SupportSessionEnding.EndedByOperator
                : SupportSessionEnding.Expired;
        }

        var session = new SupportSession
        {
            AdminUserId = adminUserId,
            AdminEmail = adminEmail,
            TargetUserId = targetUserId,
            TargetWorkspaceId = workspaceId,
            Reason = reason!.Trim(),
            StartedAt = now,
            // Written once, not recomputed. A customer is shown an end time; changing the lifetime
            // later must not silently move an end time somebody was already told.
            ExpiresAt = now + SupportAccess.Lifetime,
            IpAddress = ipAddress,
            CreatedAt = now
        };
        db.SupportSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return new(session, null);
    }

    /// <summary>
    /// The row behind <paramref name="sessionId"/> if it still authorises anything, else null —
    /// and, when the hour is what ended it, the row is closed on the way out so the customer's page
    /// says "expired" rather than showing a session that appears to still be running.
    ///
    /// <para>
    /// Called on every request under a support claim. This is the enforcement: the cookie says only
    /// which row to look at.
    /// </para>
    /// </summary>
    public async Task<SupportSession?> LiveAsync(Guid sessionId, Guid targetUserId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var session = await db.SupportSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        // A claim naming somebody else's session is not a session. It cannot happen through any path
        // this platform issues a cookie by, which is exactly why it is checked: the ways it could
        // happen are all ways nobody meant.
        if (session is null || session.TargetUserId != targetUserId) return null;

        if (session.EndedAt is not null) return null;

        if (SupportAccess.Expired(session.StartedAt, now))
        {
            session.EndedAt = session.ExpiresAt;
            session.EndedBy = SupportSessionEnding.Expired;
            await db.SaveChangesAsync(ct);
            return null;
        }

        return session;
    }

    /// <summary>
    /// Closes a session by hand. Idempotent: a second press of the banner's button, or a press that
    /// races the hour running out, leaves the row saying whichever ended it first.
    /// </summary>
    public async Task<SupportSession?> EndAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.SupportSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return null;
        if (session.EndedAt is not null) return session;

        var now = clock.UtcNow;
        session.EndedAt = now;
        session.EndedBy = SupportAccess.Expired(session.StartedAt, now)
            ? SupportSessionEnding.Expired
            : SupportSessionEnding.EndedByOperator;
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// Every support session ever opened against one workspace, newest first — the customer's own
    /// side of the arrangement.
    ///
    /// <para>
    /// <c>TargetWorkspaceId ==</c> is written out here rather than left to a global filter because
    /// this table has none, deliberately. Read the remark on <c>HarboraDbContext.SupportSessions</c>
    /// before changing that: the middleware's expiry check runs before any scope exists.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SupportSession>> ForWorkspaceAsync(
        Guid workspaceId, int take, CancellationToken ct) =>
        await db.SupportSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TargetWorkspaceId == workspaceId)
            .OrderByDescending(s => s.StartedAt)
            .Take(take)
            .ToListAsync(ct);
}
