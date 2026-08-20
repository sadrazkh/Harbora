using Harbora.Domain.Common;

namespace Harbora.Domain.Platform;

/// <summary>
/// One person having put away one <see cref="Announcement"/>'s banner.
///
/// <para>
/// <b>Per-user and per-announcement, never the other shape.</b> This is the bug the design exists to
/// avoid: dismissing one announcement must never dismiss the next, and one member closing a banner
/// must never close it for a colleague. Both are true here because the only thing that ever reads this
/// table is a lookup keyed on <em>both</em> <see cref="AnnouncementId"/> and <see cref="UserId"/>
/// together (see the partial that draws the banner) — there is no "mark seen" flag on
/// <see cref="Announcement"/> itself for a moment of carelessness to reach for instead.
/// </para>
///
/// <para>
/// Unfiltered by EF and keyed by <see cref="UserId"/> rather than a workspace, the same reasoning
/// <c>UserNotification</c> and <c>NotificationPreference</c> already document: a dismissal is a
/// person's, not a workspace's — the same person dismisses the same platform-wide announcement once,
/// however many workspaces they belong to.
/// </para>
/// </summary>
public class AnnouncementDismissal : BaseEntity
{
    public Guid AnnouncementId { get; set; }
    public Announcement? Announcement { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset DismissedAt { get; set; }
}
