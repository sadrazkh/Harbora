using System.Globalization;
using System.Text;
using System.Text.Json;
using Harbora.Domain.Auditing;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Renders an audit-log export (CSV or JSON) from rows a caller's query already selected and bounded.
///
/// <para>
/// <b>Why this is a separate, pure class rather than inline in a controller:</b> the bound
/// (<paramref name="maxExportRows"/> on every method below) is a hard cap so a workspace with far
/// more history than anyone would open in a spreadsheet cannot make an export stream forever or hold
/// an unbounded list in memory. A cap that silently drops rows is worse than no cap — a file that
/// looks complete but is not is exactly the "reports success for work it never did" defect this
/// platform keeps tripping on — so every writer here is handed both the rows it actually got
/// (<paramref name="entries"/>, already <c>Take(maxExportRows)</c>-limited by the caller) and the true
/// total the caller's query matched (<paramref name="totalMatching"/>), and says so in the file itself
/// whenever the two differ. Being a pure function of those three inputs, independent of EF Core and
/// the live 50,000-row bound, is what lets the truncation path be proven with five fake rows instead
/// of fifty thousand real ones.
/// </para>
/// </summary>
public static class AuditExportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// CSV, UTF-8 with a byte-order mark so Excel does not mis-render non-ASCII actor names (Persian
    /// included) as mojibake. Every field goes through <see cref="CsvWriter.Field"/>, which already
    /// handles commas, quotes and embedded newlines, plus neutralises formula injection.
    /// </summary>
    public static byte[] Csv(IReadOnlyList<AuditLog> entries, int totalMatching, int maxExportRows)
    {
        const int columnCount = 7;
        var csv = new StringBuilder();

        if (totalMatching > maxExportRows)
        {
            // A comment row, not a data row: it starts with "# " so nothing downstream mistakes the
            // sentence in the id column for a real audit entry, and it is padded to the same seven
            // columns as every other row so the file stays rectangular for a strict CSV reader.
            var padding = string.Concat(Enumerable.Repeat(",", columnCount - 1));
            csv.Append(CsvWriter.Field("# " + TruncationNote(totalMatching, maxExportRows)));
            csv.AppendLine(padding);
        }

        csv.AppendLine("id,timestamp,actor,action,targetType,targetId,ipAddress");
        foreach (var e in entries)
            csv.AppendLine(CsvWriter.Row(
                e.Id.ToString(), e.CreatedAt.ToString("o", CultureInfo.InvariantCulture), e.ActorEmail,
                e.Action, e.TargetType, e.TargetId, e.IpAddress));

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    /// <summary>
    /// JSON with an explicit <c>truncated</c> flag and both counts alongside the rows, rather than a
    /// bare array a truncated file would be indistinguishable from a complete one inside.
    /// </summary>
    public static byte[] Json(IReadOnlyList<AuditLog> entries, int totalMatching, int maxExportRows)
    {
        var truncated = totalMatching > maxExportRows;
        var payload = new
        {
            totalMatchingRows = totalMatching,
            returnedRows = entries.Count,
            truncated,
            truncationNote = truncated ? TruncationNote(totalMatching, maxExportRows) : null,
            entries = entries.Select(e => new
            {
                id = e.Id,
                at = e.CreatedAt,
                actorEmail = e.ActorEmail,
                action = e.Action,
                targetType = e.TargetType,
                targetId = e.TargetId,
                ipAddress = e.IpAddress
            })
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    }

    private static string TruncationNote(int totalMatching, int maxExportRows) =>
        $"TRUNCATED: this export holds the {maxExportRows} most recent of {totalMatching} matching rows. " +
        "Narrow the audit log before exporting, or export again, to see the rest.";
}
