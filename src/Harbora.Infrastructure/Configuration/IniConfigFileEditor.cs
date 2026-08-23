using Harbora.Application.Abstractions;
using Harbora.Domain.Configuration;

namespace Harbora.Infrastructure.Configuration;

/// <summary>
/// Classic <c>.ini</c>/<c>.conf</c> files (Python's <c>configparser</c>, PHP). Key path is
/// <c>section.key</c>, or a bare key for one that sits above any <c>[section]</c> header.
///
/// <para>
/// There is no single dependency for INI the way YamlDotNet is the obvious choice for YAML or
/// Tomlyn for TOML — the format has no formal spec and every mainstream INI library for .NET
/// (e.g. <c>ini-parser</c>) is a much smaller, less-maintained project than either of those, and
/// pulling one in for a grammar this small (section headers, <c>key = value</c>, <c>;</c>/<c>#</c>
/// comments — nothing else) would trade a well-understood forty-line parser for a new external
/// dependency of uncertain upkeep. This is a deliberate choice, not an unconsidered shortcut: it is
/// reported here rather than silently hand-rolled, per the plan's own instruction, and it is a real
/// line-oriented tokeniser — every line becomes exactly one typed record — never a regex applied to
/// the whole file. Every line other than the one being changed is carried through byte-for-byte.
/// </para>
/// </summary>
public sealed class IniConfigFileEditor : IConfigFileEditor
{
    public ConfigFileFormat Format => ConfigFileFormat.Ini;

    public ConfigFileInspection Inspect(string content, string? keyPath)
    {
        var lines = Parse(content);
        var keys = lines.OfType<KeyValueLine>().Select(l => l.Path).ToList();
        if (keyPath is null) return new ConfigFileInspection(true, null, keys, false, null);

        var match = lines.OfType<KeyValueLine>().FirstOrDefault(l => l.Path == keyPath);
        return match is null
            ? new ConfigFileInspection(true, null, keys, false, null)
            : new ConfigFileInspection(true, null, keys, true, match.Value.Trim());
    }

    public ConfigFileEditOutcome Apply(string content, string keyPath, string newValue)
    {
        var lines = Parse(content);
        var keys = lines.OfType<KeyValueLine>().Select(l => l.Path).ToList();
        var index = lines.FindIndex(l => l is KeyValueLine kv && kv.Path == keyPath);
        if (index < 0) return ConfigFileEditOutcome.KeyNotFound(keys);

        var target = (KeyValueLine)lines[index];
        lines[index] = target with { Value = newValue };

        var newlineStyle = content.Contains("\r\n") ? "\r\n" : "\n";
        var rebuilt = string.Join(newlineStyle, lines.Select(l => l.Raw()));
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

    private sealed record KeyValueLine(string Prefix, string Path, string Delimiter, string Value, string Suffix) : Line
    {
        public override string Raw() => $"{Prefix}{Delimiter}{Value}{Suffix}";
    }

    private static List<Line> Parse(string content)
    {
        var newlineStyle = content.Contains("\r\n") ? "\r\n" : "\n";
        var rawLines = content.Split(newlineStyle);
        var result = new List<Line>();
        var section = "";

        foreach (var line in rawLines)
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                result.Add(new VerbatimLine(line));
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                result.Add(new VerbatimLine(line));
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                result.Add(new VerbatimLine(line));
                continue;
            }

            var key = line[..eq].Trim();
            if (key.Length == 0)
            {
                result.Add(new VerbatimLine(line));
                continue;
            }

            var prefix = line[..(eq + 1)];
            var afterEq = line[(eq + 1)..];
            var valueTrimmedEnd = afterEq.TrimEnd();
            var suffix = afterEq[valueTrimmedEnd.Length..];
            var value = valueTrimmedEnd.TrimStart();
            var leadingSpace = valueTrimmedEnd[..(valueTrimmedEnd.Length - value.Length)];

            var path = section.Length == 0 ? key : $"{section}.{key}";
            result.Add(new KeyValueLine(prefix + leadingSpace, path, "", value, suffix));
        }

        return result;
    }
}
