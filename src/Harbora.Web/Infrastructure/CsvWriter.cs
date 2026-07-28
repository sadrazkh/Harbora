namespace Harbora.Web.Infrastructure;

/// <summary>
/// CSV field encoding for exports.
///
/// Beyond ordinary quoting, this neutralises spreadsheet formula injection: a value beginning with
/// <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is executed by Excel and LibreOffice when the file is
/// opened. Exported audit rows carry attacker-influenced text — actor emails, target ids — and the
/// person opening the file is an administrator investigating an incident, which is the worst
/// possible moment for a logged value to run as a formula.
/// </summary>
public static class CsvWriter
{
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var text = value;
        if (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            text = "'" + text;   // leading apostrophe forces the cell to be read as text

        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    /// <summary>Joins pre-encoded fields into a row.</summary>
    public static string Row(params string?[] values) =>
        string.Join(',', values.Select(Field));
}
