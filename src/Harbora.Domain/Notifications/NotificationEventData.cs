using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// What happened, and what it happened to — a raise site's whole job now (N4, 2026-08-16
/// notification-system spec, "in the reader's own language"). Before this, a raise site composed an
/// English sentence directly (<c>MetricsCollector.cs</c>'s threshold/crash/disk paths chief among
/// them) and handed it straight to <c>INotificationService.NotifyAsync</c>, which meant every reader
/// saw the same words in the same language regardless of <c>User.PreferredCulture</c>. This carries
/// the facts instead — an event <see cref="Type"/> plus a flat bag of named values — and leaves the
/// words to whichever culture's template <c>INotificationTemplateCatalog</c> renders.
///
/// <para>
/// Deliberately the same shape as <c>Harbora.Domain.Functions.FunctionEvent</c>'s own (Key, Data)
/// pair: this codebase already has one place that turns "something happened" into a flat,
/// already-named bag of facts rather than a sentence — this is the same idea for a human reader
/// instead of a function, and the two are related: <c>NotificationService.NotifyAsync</c> is also
/// where <c>FunctionEvents.ForAlert</c> publishes from.
/// </para>
///
/// <para>
/// Values are strings by design, not <c>object</c>: a template substitutes them verbatim (a name,
/// a host, a percentage already formatted by the raise site that knows its own precision), and a
/// free-text explanation (a deploy's failure reason, a backup engine's exception message) is a field
/// like any other — passed through untranslated, the same way a stack trace stays in whatever
/// language it was thrown in even inside a fully localized product.
/// </para>
/// </summary>
public sealed record NotificationEventData(AlertEvent Type, IReadOnlyDictionary<string, string?> Fields)
{
    public static NotificationEventData Create(AlertEvent type, params (string Key, string? Value)[] fields) =>
        new(type, fields.ToDictionary(f => f.Key, f => f.Value));

    /// <summary>A field's value, or "" if the raise site did not supply it. A template must never
    /// throw over a missing fact — an incomplete sentence reaches a reader; an unhandled exception in
    /// a background job reaches nobody.</summary>
    public string Get(string key) => Fields.TryGetValue(key, out var v) ? v ?? "" : "";
}
