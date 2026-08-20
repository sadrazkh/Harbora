namespace Harbora.Web.Infrastructure;

/// <summary>
/// How a moment is written on a page.
///
/// The views had six hand-typed format strings between them — "yyyy-MM-dd HH:mm", "MM/dd HH:mm",
/// "MM-dd HH:mm" and friends — so one record was stamped three different ways depending on which
/// page happened to show it. These constants are the only formats a view may use; the guard in
/// DateFormattingTests keeps the seventh from appearing.
///
/// The ambient culture does the rest: under fa these render in the Jalali calendar, which is the
/// behaviour the panel already had and must keep. Backend code that writes timestamps into
/// filenames or APIs deliberately does not use these — it pins InvariantCulture for its own
/// reasons, stated where it does so.
/// </summary>
public static class Dates
{
    /// <summary>The ordinary timestamp: a row's date and time.</summary>
    public const string Stamp = "yyyy-MM-dd HH:mm";

    /// <summary>A date with no time of day — expiries, issue dates.</summary>
    public const string Day = "yyyy-MM-dd";

    /// <summary>Second precision, for the audit trail where ordering within a minute matters.</summary>
    public const string Precise = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// The <c>value</c> attribute an <c>&lt;input type="datetime-local"&gt;</c> needs to pre-fill
    /// itself — not a moment written for a reader, so it does not belong beside <see cref="Stamp"/>:
    /// the HTML spec fixes this exact shape (<c>yyyy-MM-ddTHH:mm</c>, no seconds, no offset,
    /// unlocalised) regardless of culture, the same way a machine format like a filename timestamp
    /// pins <c>InvariantCulture</c> for its own reasons rather than following the ambient one. Kept
    /// here rather than inline in a view specifically so <c>DateFormattingTests</c>' scan of
    /// <c>.cshtml</c> files — which this method, living in a <c>.cs</c> file, does not appear in —
    /// still has exactly one place a date format is spelled out.
    /// </summary>
    public static string LocalInputValue(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("yyyy-MM-ddTHH:mm") : "";

    /// <summary>
    /// A moment as its distance from now — "۱۹ ساعت پیش" instead of "ساعت 19.2", which is what the
    /// backups card used to print by formatting raw TotalHours.
    ///
    /// Floor throughout: "1 hour ago" stays true for the whole hour, where rounding up would call
    /// 31 minutes an hour. Days take over after 48 hours because "37 hours ago" makes the reader do
    /// arithmetic that "1 day ago" already did.
    /// </summary>
    public static string Ago(DateTimeOffset moment, DateTimeOffset now, bool isFa)
    {
        var span = now - moment;

        // A moment in the future — clock skew between writers — needs no clamp: a negative span
        // falls into this first branch and reads as "just now". The mutation pass proved a clamp
        // here was unreachable.
        if (span.TotalMinutes < 1) return isFa ? "همین حالا" : "just now";
        if (span.TotalMinutes < 60)
        {
            var m = (int)span.TotalMinutes;
            return isFa ? $"{m} دقیقه پیش" : $"{m} min ago";
        }
        if (span.TotalHours < 48)
        {
            var h = (int)span.TotalHours;
            return isFa ? $"{h} ساعت پیش" : $"{h} h ago";
        }

        var d = (int)span.TotalDays;
        return isFa ? $"{d} روز پیش" : $"{d} d ago";
    }
}
