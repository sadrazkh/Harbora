using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// One workspace event, addressed to one person — N3 (2026-08-16 notification-system spec, "told a
/// person, not a channel").
///
/// <para>
/// <b>The sink that cannot fail.</b> A <see cref="NotificationDelivery"/> can be <c>Suppressed</c>
/// because there is no SMTP configured, or <c>Failed</c> because a webhook keeps answering 502. This
/// row cannot: writing a database row needs no channel, no credentials and no network round trip, so
/// it is the one copy of an event that always lands. A workspace that has never configured a channel
/// stops being a workspace nobody can reach, which is what closes §3's "way two" for good rather than
/// only for the one admin-email fallback N1 already built.
/// </para>
///
/// <para>
/// <b>Everyone in the workspace, not merely admins.</b> N1's admin-only email fallback and this row
/// answer §7 Q2 differently on purpose: paging somebody's phone for every event they did not ask
/// about is noise a Viewer never signed up for, but a row waiting in an inbox nobody has to open
/// costs them nothing until N5 makes it tunable. See <c>NotificationService</c> for where each row is
/// actually written.
/// </para>
///
/// <para>
/// <b>Unfiltered but user-keyed</b> (doc 14 §3) — the same pattern <c>ApiToken</c> already uses, and
/// deliberately not the workspace query filter most tenant-scoped tables carry. A workspace filter
/// alone would still combine every member's rows into one set; the one column that actually answers
/// "whose inbox is this" is <see cref="UserId"/>, so every reader — the bell, <c>/notifications</c>,
/// the retention sweep — filters by it explicitly rather than leaning on an ambient scope that cannot
/// express the real boundary.
/// </para>
///
/// <para>
/// <b>Plain text, today's English, written once.</b> N4 is where a template renders this in the
/// recipient's own <c>PreferredCulture</c>; until then <see cref="Title"/>/<see cref="Body"/> are the
/// same strings the raiser already built for the channel senders. A row records what was said at the
/// time it was said, so N4 shipping later does not retro-translate history that has already been read
/// — or not read — in the language it was written in.
/// </para>
/// </summary>
public class UserNotification : BaseEntity
{
    /// <summary>The workspace this event concerns.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The recipient — one member of <see cref="WorkspaceId"/>, resolved from
    /// <c>WorkspaceMember</c> at the moment the event was raised.</summary>
    public Guid UserId { get; set; }

    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Null while unread. Set once, by the person it belongs to, marking it read — never by another
    /// member's own read of a different copy of the same event: each row is that one person's, so one
    /// colleague reading theirs can never clear another's.
    /// </summary>
    public DateTimeOffset? ReadAt { get; set; }
}
