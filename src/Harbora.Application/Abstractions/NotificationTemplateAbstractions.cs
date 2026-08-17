namespace Harbora.Application.Abstractions;

/// <summary>
/// What a template produced for one event in one culture (N4, 2026-08-16 notification-system spec,
/// "in the reader's own language") — a subject line, a plain-text body and an HTML alternative.
///
/// <para>
/// The HTML alternative only actually reaches a reader over email: Telegram and Discord already
/// speak their own formats (Markdown, a JSON embed) and a webhook is machine-read, so
/// <c>NotificationService</c> hands those three <see cref="TextBody"/> alone and builds a real
/// multipart message for email — the fix for doc 09 §6's own complaint that platform mail is
/// <c>MailMessage</c> without <c>IsBodyHtml</c>.
/// </para>
/// </summary>
public sealed record RenderedNotification(string Subject, string TextBody, string HtmlBody);

/// <summary>
/// Turns a structured <c>Harbora.Domain.Notifications.NotificationEventData</c> into words, for one
/// culture.
///
/// <para>
/// Behind an interface rather than a static method so the lookup stays indirect (doc 09 §4.2's own
/// requirement): a future per-workspace branding pass can swap the implementation for one that reads
/// a workspace's own override before falling back to this one, without <c>NotificationService</c> or
/// any raise site changing at all. N4 does not use that indirection for branding — it exists only so
/// N4 does not have to be revisited to add it.
/// </para>
/// </summary>
public interface INotificationTemplateCatalog
{
    /// <summary>
    /// Renders <paramref name="data"/> in <paramref name="culture"/>. <paramref name="culture"/> is
    /// ordinarily a recipient's own <c>User.PreferredCulture</c>; a null, empty or unrecognised value
    /// renders as the platform's own default ("fa" — the same default <c>User.PreferredCulture"</c>
    /// already documents) rather than throwing. A reader whose culture this catalog does not know
    /// must still receive a notification, not a failed background job.
    /// </summary>
    RenderedNotification Render(Domain.Notifications.NotificationEventData data, string? culture);
}
