using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Domain.Configuration;

namespace Harbora.Infrastructure.Configuration;

/// <summary>
/// Laravel/Django/Node <c>.env</c> files. Key path is the bare variable name (<c>DATABASE_URL</c>)
/// — there is no nesting to speak of, so forcing a colon- or dot-separated path onto this format
/// would be exactly the "one syntax for all five" the owner ruled out.
///
/// <para>
/// Parsed as a real, if small, structured grammar: every line is tokenised into exactly one of
/// blank / comment / <c>KEY=value</c> (optionally <c>export</c>-prefixed, optionally quoted) —
/// never a regex applied to the whole file. Only the one changed line's bytes are ever rewritten;
/// every other line, including its exact whitespace and quoting style, is carried through
/// unchanged, which is the strongest round-trip guarantee of any of the five formats precisely
/// because this one has the least structure to lose.
/// </para>
/// </summary>
public sealed class EnvConfigFileEditor : IConfigFileEditor
{
    public ConfigFileFormat Format => ConfigFileFormat.Env;

    public ConfigFileInspection Inspect(string content, string? keyPath)
    {
        List<Line> lines;
        try { lines = Parse(content); }
        catch (EnvParseException ex) { return ConfigFileInspection.ParseFailure(ex.Error); }

        var keys = lines.OfType<KeyValueLine>().Select(l => l.Key).ToList();
        if (keyPath is null) return new ConfigFileInspection(true, null, keys, false, null);

        var match = lines.OfType<KeyValueLine>().FirstOrDefault(l => l.Key == keyPath);
        return match is null
            ? new ConfigFileInspection(true, null, keys, false, null)
            : new ConfigFileInspection(true, null, keys, true, Unquote(match.RawValue));
    }

    public ConfigFileEditOutcome Apply(string content, string keyPath, string newValue)
    {
        List<Line> lines;
        try { lines = Parse(content); }
        catch (EnvParseException ex) { return ConfigFileEditOutcome.ParseFailure(ex.Error); }

        var keys = lines.OfType<KeyValueLine>().Select(l => l.Key).ToList();
        var index = lines.FindIndex(l => l is KeyValueLine kv && kv.Key == keyPath);
        if (index < 0) return ConfigFileEditOutcome.KeyNotFound(keys);

        var target = (KeyValueLine)lines[index];
        lines[index] = target with { RawValue = Requote(newValue) };

        var newlineStyle = content.Contains("\r\n") ? "\r\n" : "\n";
        var rebuilt = string.Join(newlineStyle, lines.Select(l => l.Raw()));
        // A file ending in a newline should still end in one; string.Join alone would drop it.
        if (content.EndsWith(newlineStyle, StringComparison.Ordinal) && !rebuilt.EndsWith(newlineStyle, StringComparison.Ordinal))
            rebuilt += newlineStyle;

        return ConfigFileEditOutcome.Success(rebuilt);
    }

    private abstract record Line
    {
        public abstract string Raw();
    }

    private sealed record VerbatimLine(string Text) : Line
    {
        public override string Raw() => Text;
    }

    private sealed record KeyValueLine(string? ExportPrefix, string Key, string RawValue, string? TrailingComment) : Line
    {
        public override string Raw() =>
            $"{ExportPrefix}{Key}={RawValue}{TrailingComment}";
    }

    private static List<Line> Parse(string content)
    {
        var newlineStyle = content.Contains("\r\n") ? "\r\n" : "\n";
        var rawLines = content.Split(newlineStyle);
        var result = new List<Line>();

        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i];
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                result.Add(new VerbatimLine(line));
                continue;
            }

            string exportPrefix;
            var leadingWs = line[..(line.Length - trimmed.Length)];
            var afterWs = trimmed;
            if (afterWs.StartsWith("export ", StringComparison.Ordinal))
            {
                exportPrefix = leadingWs + "export ";
                afterWs = afterWs["export ".Length..];
            }
            else
            {
                exportPrefix = leadingWs;
            }

            var eq = afterWs.IndexOf('=');
            if (eq < 0)
            {
                // Not a recognisable directive — carried through verbatim rather than refusing the
                // whole file over one stray line (blank export, a shell-only construct, etc.).
                result.Add(new VerbatimLine(line));
                continue;
            }

            var key = afterWs[..eq].Trim();
            var rest = afterWs[(eq + 1)..];

            if (key.Length == 0 || !IsValidKey(key))
            {
                result.Add(new VerbatimLine(line));
                continue;
            }

            // Quoted values may contain '#'; only an unquoted value's '#' starts a trailing comment.
            string rawValue;
            string? trailingComment = null;
            if (rest.Length > 0 && (rest[0] == '"' || rest[0] == '\''))
            {
                var quote = rest[0];
                var closeAt = FindClosingQuote(rest, quote);
                if (closeAt < 0)
                    throw new EnvParseException(new ConfigFileParseError(
                        $"Line {i + 1}: a {quote} quote here is never closed.", i + 1, eq + 2));
                rawValue = rest[..(closeAt + 1)];
                trailingComment = rest[(closeAt + 1)..];
            }
            else
            {
                var hashAt = rest.IndexOf('#');
                rawValue = hashAt < 0 ? rest : rest[..hashAt];
                trailingComment = hashAt < 0 ? "" : rest[hashAt..];
                // Trim trailing whitespace off an unquoted value's own text, but keep it in the
                // "comment" bucket so Raw() still reconstructs the exact original line.
                var valueTrimmedEnd = rawValue.TrimEnd();
                trailingComment = rawValue[valueTrimmedEnd.Length..] + trailingComment;
                rawValue = valueTrimmedEnd;
            }

            result.Add(new KeyValueLine(exportPrefix, key, rawValue, trailingComment));
        }

        return result;
    }

    private static bool IsValidKey(string key) =>
        key.Length > 0 && (char.IsLetter(key[0]) || key[0] == '_') &&
        key.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static int FindClosingQuote(string text, char quote)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] == '\\' && quote == '"') { i++; continue; } // \" escape inside double quotes
            if (text[i] == quote) return i;
        }
        return -1;
    }

    private static string Unquote(string rawValue)
    {
        if (rawValue.Length >= 2 && (rawValue[0] == '"' || rawValue[0] == '\'') && rawValue[^1] == rawValue[0])
            return rawValue[1..^1];
        return rawValue;
    }

    /// <summary>
    /// Always double-quoted when the value needs it (whitespace, <c>#</c>, a quote character) so the
    /// result is always valid regardless of what quote style — if any — the placeholder used;
    /// otherwise written bare, matching how a short unquoted value normally looks in this format.
    /// </summary>
    private static string Requote(string value)
    {
        var needsQuoting = value.Length == 0 || value.Any(c => c is ' ' or '#' or '"' or '\'' or '\t');
        if (!needsQuoting) return value;

        var escaped = new StringBuilder();
        foreach (var c in value)
        {
            if (c is '"' or '\\') escaped.Append('\\');
            escaped.Append(c);
        }
        return $"\"{escaped}\"";
    }

    private sealed class EnvParseException(ConfigFileParseError error) : Exception(error.Message)
    {
        public ConfigFileParseError Error { get; } = error;
    }
}
