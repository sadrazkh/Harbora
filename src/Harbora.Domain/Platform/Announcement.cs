using Harbora.Domain.Common;

namespace Harbora.Domain.Platform;

/// <summary>
/// An operator-authored, platform-wide notice — shown as a dismissible banner on every panel page
/// while it is active (Sub-project 4, 2026-08-20 platform-options plan).
///
/// <para>
/// <b>Both languages are required, not optional.</b> The panel is Persian-first and bilingual, so an
/// announcement half its users cannot read is not an announcement — <see cref="AnnouncementRules.RefuseSave"/>
/// is where that is enforced, the same "refuse with a reason" idiom every other admin write in this
/// codebase uses rather than a data-annotation a controller could forget to check.
/// </para>
///
/// <para>
/// <see cref="Severity"/> reuses <see cref="AlertSeverity"/> rather than a second, parallel two-value
/// enum: the plan asks for exactly info/warn, which are <see cref="AlertSeverity.Info"/> and
/// <see cref="AlertSeverity.Warning"/> here — <see cref="AlertSeverity.Critical"/> is refused by
/// <see cref="AnnouncementRules.RefuseSave"/> rather than given a meaning nobody asked for.
/// <see cref="AlertSeverity.Warning"/> is also exactly the value <c>INotificationService.NotifyInAppOnlyAsync</c>
/// is called with when this fans out to <c>UserNotification</c> rows — one value, not a translation
/// between two enums that happen to agree today and could silently stop agreeing later.
/// </para>
/// </summary>
public class Announcement : BaseEntity
{
    /// <summary>English title. Required — see the class doc.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>English body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Persian title. Required — see the class doc. Named with the <c>Fa</c> suffix the rest
    /// of this codebase's bilingual columns already use (<c>AppTemplate.NameFa</c>,
    /// <c>Plan.NameFa</c>), English carrying the bare property name.</summary>
    public string TitleFa { get; set; } = string.Empty;

    /// <summary>Persian body.</summary>
    public string BodyFa { get; set; } = string.Empty;

    /// <summary>Info stays banner-only; Warning additionally fans out through the N3 in-app path —
    /// see <c>Harbora.Infrastructure.Platform.AnnouncementNotifier</c>.</summary>
    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;

    /// <summary>Null means "already active" — no wait for a start time nobody set.</summary>
    public DateTimeOffset? StartsAt { get; set; }

    /// <summary>Null means "active indefinitely" until an operator edits or removes it.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Who wrote it.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>
    /// Copied at creation, the same reasoning <c>SupportSession.AdminEmail</c> documents: the admin
    /// list must still name who posted an announcement after that administrator's own account is
    /// renamed or removed, and a join that returns nothing would blank the byline instead.
    /// </summary>
    public string CreatedByEmail { get; set; } = string.Empty;

    /// <summary>Whether this announcement's window covers <paramref name="now"/> — the only thing
    /// that decides if it is shown or fanned out; nothing here reads <see cref="BaseEntity.CreatedAt"/>
    /// for that question.</summary>
    public bool IsActiveAt(DateTimeOffset now) => AnnouncementRules.IsActiveAt(this, now);
}

/// <summary>
/// The rules an announcement's fields and window are checked against. Pure, so they can be asserted
/// without a database — the same shape <c>SupportAccess</c> beside <c>SupportSession</c> already
/// established for this codebase's other platform-admin write.
/// </summary>
public static class AnnouncementRules
{
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 4000;

    /// <summary>Whether <paramref name="announcement"/>'s window covers <paramref name="now"/>.
    /// A null bound is an open one on that side, not "never" or "always" the other way.</summary>
    public static bool IsActiveAt(Announcement announcement, DateTimeOffset now) =>
        (announcement.StartsAt is null || announcement.StartsAt <= now)
        && (announcement.EndsAt is null || announcement.EndsAt >= now);

    /// <summary>
    /// Null when an announcement with these fields may be saved; otherwise why not, in words an
    /// operator can act on. Checked on every create and edit — the view hides what it must not offer,
    /// this refuses it again, the same division of labour <see cref="Harbora.Domain.Authorization.UserAdministration"/>
    /// documents for user administration.
    /// </summary>
    public static string? RefuseSave(
        string title, string body, string titleFa, string bodyFa,
        AlertSeverity severity, DateTimeOffset? startsAt, DateTimeOffset? endsAt)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            return "An English title and body are both required.";

        if (string.IsNullOrWhiteSpace(titleFa) || string.IsNullOrWhiteSpace(bodyFa))
            return "A Persian title and body are both required.";

        if (title.Length > MaxTitleLength || titleFa.Length > MaxTitleLength)
            return $"Keep titles under {MaxTitleLength} characters.";

        if (body.Length > MaxBodyLength || bodyFa.Length > MaxBodyLength)
            return $"Keep the body under {MaxBodyLength} characters.";

        // Info and Warning are the two the plan asks for. Critical is refused rather than given a
        // meaning nobody designed — an announcement is an operator's message, not the same "the
        // platform is about to stop" signal AlertSeverity.Critical carries for a workspace's own
        // billing/monitoring alerts.
        if (severity is not (AlertSeverity.Info or AlertSeverity.Warning))
            return "Severity must be info or warn.";

        if (startsAt is { } s && endsAt is { } e && e <= s)
            return "The end of the window must be after its start.";

        return null;
    }
}
