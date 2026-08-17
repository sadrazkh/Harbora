using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Notifications;

/// <summary>Why a requested preference write was refused. <c>None</c> means it was not.</summary>
public enum NotificationPreferenceRejection
{
    None = 0,

    /// <summary><see cref="NotificationChannel.InApp"/> may not be set to
    /// <see cref="NotificationPreferenceMode.Digest"/> — see <c>NotificationPreferenceRules.IsLegalMode</c>.</summary>
    IllegalMode = 1,

    /// <summary>
    /// This write would leave a critical event with no channel resolving to
    /// <see cref="NotificationPreferenceMode.Immediate"/> — the one thing a preference is never
    /// allowed to do (<c>NotificationPreferenceRules.HasCriticalCoverage</c>).
    /// </summary>
    CriticalCoverageLost = 2
}

/// <summary>Whether a preference write succeeded, and why not when it did not — a reason code rather
/// than a message, so the caller (a Razor view rendering fa/en) supplies the words.</summary>
public sealed record NotificationPreferenceSetResult(NotificationPreferenceRejection Rejection)
{
    public bool Ok => Rejection == NotificationPreferenceRejection.None;
    public static readonly NotificationPreferenceSetResult Success = new(NotificationPreferenceRejection.None);
    public static NotificationPreferenceSetResult Reject(NotificationPreferenceRejection reason) => new(reason);
}

/// <summary>
/// Reads and writes one person's <see cref="NotificationPreference"/> rows — N5, 2026-08-16
/// notification-system spec, "noise control".
///
/// <para>
/// <b>An absent row means the default, and this is the one place that reads it that way.</b>
/// <see cref="ResolveAsync"/> and <see cref="ResolveAllAsync"/> never distinguish "no row" from "a row
/// nobody would recognise" — both fall through to <see cref="NotificationPreferenceDefaults"/> — so a
/// newly appended <c>AlertEvent</c> arrives already answered for every existing user, the way
/// N3's own in-app fan-out already does for everyone who has never opened this page.
/// </para>
///
/// <para>
/// <b>The critical-coverage invariant is enforced here, at write time</b> — not merely hoped for by
/// the UI that calls this. <see cref="SetAsync"/> resolves what the <i>other</i> channel would mean
/// for this event before accepting the change, so "off in-app, immediate by email" is accepted and
/// "off everywhere" for a critical event never reaches the database at all. This is the one guarantee
/// doc 09 §3 asks for stated as code: a customer may choose where the last warning before suspension
/// goes, not whether it exists.
/// </para>
/// </summary>
public sealed class NotificationPreferenceService(HarboraDbContext db)
{
    public async Task<NotificationPreferenceMode> ResolveAsync(
        Guid userId, AlertEvent evt, NotificationChannel channel, CancellationToken ct)
    {
        var row = await db.NotificationPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.EventType == evt && p.Channel == channel, ct);
        return row?.Mode ?? NotificationPreferenceDefaults.DefaultFor(evt, channel);
    }

    /// <summary>Both channels, resolved — the shape <see cref="NotificationPreferenceRules.HasCriticalCoverage"/>
    /// and a preferences page both need: not one channel in isolation, but the whole picture for one event.</summary>
    public async Task<Dictionary<NotificationChannel, NotificationPreferenceMode>> ResolveAllAsync(
        Guid userId, AlertEvent evt, CancellationToken ct)
    {
        var rows = await db.NotificationPreferences
            .Where(p => p.UserId == userId && p.EventType == evt)
            .ToListAsync(ct);

        var resolved = new Dictionary<NotificationChannel, NotificationPreferenceMode>();
        foreach (var channel in Enum.GetValues<NotificationChannel>())
            resolved[channel] = rows.FirstOrDefault(r => r.Channel == channel)?.Mode
                                 ?? NotificationPreferenceDefaults.DefaultFor(evt, channel);
        return resolved;
    }

    /// <summary>
    /// Records one explicit choice, or refuses it. Only ever writes a row for
    /// <paramref name="channel"/> — the other channel of the same event, if it also has an explicit
    /// row, is read but never touched, so setting one channel can never silently change the other's
    /// own stored choice, only whether this write is legal given it.
    /// </summary>
    public async Task<NotificationPreferenceSetResult> SetAsync(
        Guid userId, AlertEvent evt, NotificationChannel channel, NotificationPreferenceMode mode, CancellationToken ct)
    {
        if (!NotificationPreferenceRules.IsLegalMode(channel, mode))
            return NotificationPreferenceSetResult.Reject(NotificationPreferenceRejection.IllegalMode);

        if (NotificationEventClass.IsCritical(evt))
        {
            var resolved = await ResolveAllAsync(userId, evt, ct);
            resolved[channel] = mode;
            if (!NotificationPreferenceRules.HasCriticalCoverage(resolved))
                return NotificationPreferenceSetResult.Reject(NotificationPreferenceRejection.CriticalCoverageLost);
        }

        var existing = await db.NotificationPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.EventType == evt && p.Channel == channel, ct);

        if (existing is null)
            db.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = userId, EventType = evt, Channel = channel, Mode = mode
            });
        else
            existing.Mode = mode;

        await db.SaveChangesAsync(ct);
        return NotificationPreferenceSetResult.Success;
    }
}
