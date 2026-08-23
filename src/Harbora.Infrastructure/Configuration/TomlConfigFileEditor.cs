using Harbora.Application.Abstractions;
using Harbora.Domain.Configuration;
using Tomlyn;
using Tomlyn.Model;

namespace Harbora.Infrastructure.Configuration;

/// <summary>
/// TOML — grouped with INI in the plan's own "INI/TOML" bucket, but with its own real dependency:
/// <see href="https://github.com/xoofx/Tomlyn">Tomlyn</see>, a maintained, widely used TOML parser
/// (also used by dotnet-format and several JetBrains tools). Reported here rather than silently
/// added, per the plan's own instruction: it is a small (~180 KB), dependency-free package, and
/// unlike INI, TOML has a real published spec that a hand-rolled parser would otherwise have to
/// half-reimplement.
///
/// <para>
/// Key path is dot-separated through tables (<c>section.key</c>, the same shape as INI's own
/// idiom) — <c>Toml.ToModel</c> is used to validate the file and to enumerate every leaf path
/// correctly, including nesting this editor's own writer does not attempt to touch. Writing,
/// though, is deliberately narrower than reading: only a plain <c>[table]</c> header followed by
/// <c>key = value</c> lines is edited in place (line-for-line, everything else untouched, the same
/// approach as <see cref="IniConfigFileEditor"/>); a value that is itself a table/array, or a file
/// using TOML features this narrower writer does not fully understand (array-of-tables
/// <c>[[..]]</c>, inline tables, multi-line strings), is refused with a clear reason rather than
/// silently mangled.
/// </para>
/// </summary>
public sealed class TomlConfigFileEditor : IConfigFileEditor
{
    public ConfigFileFormat Format => ConfigFileFormat.Toml;

    public ConfigFileInspection Inspect(string content, string? keyPath)
    {
        var parseError = Validate(content);
        if (parseError is not null) return ConfigFileInspection.ParseFailure(parseError);

        var model = Toml.ToModel(content);
        var paths = new List<string>();
        Flatten(model, [], paths);

        if (keyPath is null) return new ConfigFileInspection(true, null, paths, false, null);

        var found = TryGetLeaf(model, Split(keyPath), out var value);
        return found
            ? new ConfigFileInspection(true, null, paths, true, value)
            : new ConfigFileInspection(true, null, paths, false, null);
    }

    public ConfigFileEditOutcome Apply(string content, string keyPath, string newValue)
    {
        var parseError = Validate(content);
        if (parseError is not null) return ConfigFileEditOutcome.ParseFailure(parseError);

        var model = Toml.ToModel(content);
        var segments = Split(keyPath);
        var paths = new List<string>();
        Flatten(model, [], paths);

        if (!TryGetLeaf(model, segments, out _))
            return ConfigFileEditOutcome.KeyNotFound(paths);

        if (HasArrayOfTables(content))
            return ConfigFileEditOutcome.ParseFailure(new ConfigFileParseError(
                "This file uses an array-of-tables ([[...]]) header. Harbora's TOML writer does not " +
                "edit those safely yet — this key path was found, but cannot be changed in place.", null, null));

        var table = segments.Length > 1 ? string.Join('.', segments[..^1]) : null;
        var key = segments[^1];

        var edited = TryEditLine(content, table, key, newValue, out var newContent, out var reason);
        return edited
            ? ConfigFileEditOutcome.Success(newContent!)
            : ConfigFileEditOutcome.ParseFailure(new ConfigFileParseError(reason!, null, null));
    }

    private static string[] Split(string keyPath) =>
        keyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ConfigFileParseError? Validate(string content)
    {
        var doc = Toml.Parse(content);
        if (!doc.HasErrors) return null;

        var first = doc.Diagnostics[0];
        return new ConfigFileParseError(first.Message, first.Span.Start.Line + 1, first.Span.Start.Column + 1);
    }

    private static bool HasArrayOfTables(string content) =>
        content.Split('\n').Any(l => l.TrimStart().StartsWith("[["));

    private static void Flatten(TomlTable table, List<string> prefix, List<string> paths)
    {
        foreach (var (key, value) in table)
        {
            var path = new List<string>(prefix) { key };
            switch (value)
            {
                case TomlTable nested:
                    Flatten(nested, path, paths);
                    break;
                case TomlTableArray array:
                    for (var i = 0; i < array.Count; i++)
                        Flatten(array[i], [.. path, i.ToString()], paths);
                    break;
                default:
                    paths.Add(string.Join('.', path));
                    break;
            }
        }
    }

    private static bool TryGetLeaf(TomlTable root, string[] segments, out string? value)
    {
        object current = root;
        foreach (var segment in segments)
        {
            if (current is TomlTable table)
            {
                if (!table.TryGetValue(segment, out var next)) { value = null; return false; }
                current = next!;
            }
            else if (current is TomlTableArray array && int.TryParse(segment, out var index) && index < array.Count)
            {
                current = array[index];
            }
            else { value = null; return false; }
        }

        if (current is TomlTable or TomlTableArray or TomlArray) { value = null; return false; }
        value = current.ToString();
        return true;
    }

    /// <summary>
    /// Line-oriented rewrite, the same shape <see cref="IniConfigFileEditor"/> uses: find the
    /// <c>[table]</c> header (or the top, when <paramref name="table"/> is null), then the first
    /// <c>key = value</c> line under it, and replace only that line's value.
    /// </summary>
    private static bool TryEditLine(
        string content, string? table, string key, string newValue, out string? newContent, out string? reason)
    {
        var newlineStyle = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Split(newlineStyle);
        var inTargetTable = table is null; // top-of-file keys start "in" the implicit root table.
        var seenAnyHeader = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith('[') && !trimmed.StartsWith("[["))
            {
                seenAnyHeader = true;
                var headerName = string.Join('.', trimmed[1..^1].Split('.').Select(s => s.Trim().Trim('"', '\'')));
                inTargetTable = table is not null && headerName == table;
                continue;
            }

            if (!inTargetTable) continue;
            if (table is null && seenAnyHeader) break; // root keys only precede the first header.

            var eq = lines[i].IndexOf('=');
            if (eq < 0) continue;

            var candidateKey = lines[i][..eq].Trim().Trim('"', '\'');
            if (candidateKey != key) continue;

            var afterEq = lines[i][(eq + 1)..];
            var valueText = afterEq.TrimStart();
            var leading = afterEq[..(afterEq.Length - valueText.Length)];

            if (valueText.StartsWith('{') || valueText.StartsWith('[') || valueText.StartsWith("\"\"\"") || valueText.StartsWith("'''"))
            {
                newContent = null;
                reason = $"The value at '{key}' is an inline table, array or multi-line string — " +
                          "Harbora's TOML writer only replaces plain single-line values.";
                return false;
            }

            lines[i] = lines[i][..eq] + "=" + leading + TomlString(newValue);
            newContent = string.Join(newlineStyle, lines);
            reason = null;
            return true;
        }

        newContent = null;
        reason = $"'{key}' was found while reading this file, but its exact line could not be located to edit.";
        return false;
    }

    private static string TomlString(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        return $"\"{escaped}\"";
    }
}
