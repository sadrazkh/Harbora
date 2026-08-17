using Harbora.Data;
using Harbora.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Persisted replacement for <see cref="AlertThrottle"/> (N2, 2026-08-16 notification-system spec,
/// "say it once, across a restart").
///
/// <para>
/// <see cref="AlertThrottle"/> was honest about its own limit in its own doc comment — "a restart
/// allows one extra alert" — and that was the right trade for a disk warning nobody would otherwise
/// see repeated inside an hour. It stopped being the right trade the moment SSL joined disk as a
/// second caller: doc 09 §6 asks for at most one SSL warning per host per day, and a dictionary a
/// restart empties cannot promise that at all — a panel bounced twice in one day sends the email
/// twice, which is exactly what <c>CertificateWatcher</c> did before this existed, because nothing
/// stopped it doing so.
/// </para>
///
/// <para>
/// This answers the same question — "has this exact subject, in this exact window, already fired" —
/// from a database row instead of a process-lifetime dictionary, so the answer survives exactly the
/// event that used to reset it. The caller bakes the window into <paramref name="key"/> itself
/// (<see cref="AlertDedupWindow"/> for the disk case, a plain <c>yyyy-MM-dd</c> for SSL's daily one),
/// so there is no "has enough time passed" arithmetic here — only "does this row exist yet", which is
/// exactly what a unique index is for.
/// </para>
///
/// <para>
/// Scoped, not a singleton — <see cref="AlertThrottle"/> had to be a singleton to survive between the
/// fresh DI scopes <c>MetricsCollector</c> and <c>CertificateWatcher</c> open on every tick; this
/// writes through whichever scope's <see cref="HarboraDbContext"/> it is given, and durability is now
/// the database's job rather than a long-lived object's.
/// </para>
/// </summary>
public sealed class AlertDedup(HarboraDbContext db)
{
    /// <summary>
    /// True the first time <paramref name="key"/> is asked for; false every time after, for as long as
    /// a caller keeps asking with the same key. Writes a row on the first call so the answer is durable
    /// from that point on — including across a restart of this process.
    /// </summary>
    public async Task<bool> ShouldFireAsync(string key, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.AlertDedupMarks.AnyAsync(m => m.Key == key, ct)) return false;

        var mark = new AlertDedupMark { Key = key, FiredAt = now };
        db.AlertDedupMarks.Add(mark);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another pass wrote this exact key between the check above and this insert — that pass
            // is the one that gets to fire. Detach only the row this call added rather than clearing
            // the whole change tracker: MetricsCollector and CertificateWatcher both share this same
            // context with other pending work across one tick (incidents opened, metrics samples
            // added), and a ChangeTracker.Clear() here would silently drop that work rather than only
            // the losing insert.
            db.Entry(mark).State = EntityState.Detached;
            return false;
        }
    }
}
