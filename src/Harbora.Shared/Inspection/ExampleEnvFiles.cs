using System.Text;
using System.Text.RegularExpressions;

namespace Harbora.Shared.Inspection;

/// <param name="Value">Empty when the file gives the name and nothing else.</param>
internal sealed record ExampleVar(string Name, string Value, string File);

/// <summary>Reads the example env files, and only those: <c>.env</c> is not on this list and nothing else in the inspector reads env files.</summary>
internal static class ExampleEnvFiles
{
    /// <summary>The same list the upload rule keeps sending (<see cref="EnvFiles"/>), or Launch would ask about nothing.</summary>
    private static IReadOnlyList<string> Names => EnvFiles.ExampleNames;

    private static readonly Regex Assignment = new(
        @"^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>In file order, first found winning per name.</summary>
    public static IReadOnlyList<ExampleVar> Read(SourceTree app, out IReadOnlyList<string> unreadable)
    {
        var found = new List<ExampleVar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failed = new List<string>();

        foreach (var file in Names)
        {
            if (!app.HasFile(file)) continue;

            if (app.ReadText(file) is not { } text)
            {
                failed.Add(file);
                continue;
            }

            foreach (var (name, value) in Parse(text))
                if (seen.Add(name)) found.Add(new ExampleVar(name, value, file));
        }

        unreadable = failed;
        return found;
    }

    private static IEnumerable<(string Name, string Value)> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (Assignment.Match(line) is not { Success: true } match) continue;

            var value = match.Groups[2].Value.TrimStart();
            if (value.Length == 0)
            {
                yield return (match.Groups[1].Value, "");
                continue;
            }

            if (value[0] is not ('"' or '\''))
            {
                yield return (match.Groups[1].Value, WithoutComment(value));
                continue;
            }

            var quote = value[0];
            var close = ClosingQuote(value, 1, quote);
            if (close >= 0)
            {
                yield return (match.Groups[1].Value, value[1..close]);
                continue;
            }

            // A quoted value can run over several lines, as a private key does, and those lines have
            // to be swallowed or a line of base64 ending in = reads as a variable of its own. If the
            // quote is never closed anywhere it is a typo, and the names below it still count.
            var last = LineClosing(lines, i + 1, quote);
            if (last < 0)
            {
                yield return (match.Groups[1].Value, value[1..]);
                continue;
            }

            var whole = new StringBuilder(value, 1, value.Length - 1, value.Length);
            for (var next = i + 1; next < last; next++) whole.Append('\n').Append(lines[next]);
            whole.Append('\n').Append(lines[last], 0, ClosingQuote(lines[last], 0, quote));
            i = last;

            yield return (match.Groups[1].Value, whole.ToString());
        }
    }

    private static int LineClosing(string[] lines, int from, char quote)
    {
        for (var i = from; i < lines.Length; i++)
        {
            if (ClosingQuote(lines[i], 0, quote) >= 0) return i;
        }
        return -1;
    }

    private static int ClosingQuote(string value, int from, char quote)
    {
        for (var i = from; i < value.Length; i++)
        {
            if (value[i] == quote && (quote == '\'' || i == 0 || value[i - 1] != '\\')) return i;
        }
        return -1;
    }

    // Only with a space before it, as dotenv has it, because a # is common inside a real value.
    private static string WithoutComment(string value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] == '#' && char.IsWhiteSpace(value[i - 1])) return value[..i].TrimEnd();
        }
        return value[0] == '#' ? "" : value.TrimEnd();
    }
}
