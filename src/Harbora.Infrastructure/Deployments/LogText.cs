using System.Text;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Makes captured output storable.
///
/// PostgreSQL cannot hold a NUL byte in a text column — not escaped, not encoded, at all. Docker's
/// log stream for a container without a TTY is framed: every chunk is prefixed with eight bytes
/// (stream id, three zeros, then a big-endian length) and those bytes arrive as text. So the first
/// feature to persist a one-off container's output rather than merely log it hit
/// <c>invalid byte sequence for encoding "UTF8": 0x00</c> — and because the failure happened inside
/// SaveChanges, the deployment could not even record that it had failed. It sat "in progress"
/// forever, which is the worst way for something to break.
///
/// Newlines and tabs survive, because output is read by people.
/// </summary>
public static class LogText
{
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var offending = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsStorable(text[i])) { offending = i; break; }
        }

        // The overwhelmingly common case: ordinary output, returned untouched and uncopied.
        if (offending < 0) return text;

        var kept = new StringBuilder(text.Length);
        kept.Append(text, 0, offending);
        for (var i = offending; i < text.Length; i++)
        {
            if (IsStorable(text[i])) kept.Append(text[i]);
        }
        return kept.ToString();
    }

    /// <summary>
    /// Control characters carry no meaning in a log line anyone reads, and the one that actually
    /// matters — NUL — cannot be stored at all. Whitespace that shapes the output is kept.
    /// </summary>
    private static bool IsStorable(char ch) =>
        ch is '\n' or '\r' or '\t' || !char.IsControl(ch);
}
