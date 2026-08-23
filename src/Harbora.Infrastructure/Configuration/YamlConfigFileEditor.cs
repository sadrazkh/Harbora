using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Domain.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Harbora.Infrastructure.Configuration;

/// <summary>
/// Rails <c>config/database.yml</c>, Spring <c>application.yml</c>. Key path is dot-separated
/// through nested mappings (<c>production.adapter</c>), not colon-separated — YAML's own convention
/// (Rails, Ansible, Helm values files) is a dot, and the plan is explicit that each format keeps its
/// own idiom rather than one syntax forced onto all five.
///
/// <para>
/// Parsed with YamlDotNet (already a dependency of this solution — <c>Harbora.Cli</c> references it
/// for the CLI's own config — so referencing it from here adds no new external dependency, only a
/// second project reference to a package already vetted and restored). The low-level
/// <see cref="Parser"/> event stream is used rather than YamlDotNet's object-mapping API, because
/// each event carries its exact character span in the source (<see cref="ParsingEvent.Start"/>/
/// <see cref="ParsingEvent.End"/>) — <see cref="Apply"/> walks the key path through these events and
/// splices only the matched scalar's span, leaving every other character — including comments, which
/// YamlDotNet's higher-level node API does not round-trip — untouched.
/// </para>
/// </summary>
public sealed class YamlConfigFileEditor : IConfigFileEditor
{
    public ConfigFileFormat Format => ConfigFileFormat.Yaml;

    public ConfigFileInspection Inspect(string content, string? keyPath)
    {
        var target = keyPath is null ? null : Split(keyPath);
        List<string> paths;
        (int Start, int End)? match;

        try { (paths, match) = Walk(content, target); }
        catch (YamlException ex) { return ConfigFileInspection.ParseFailure(ToParseError(ex)); }

        if (target is null || match is null) return new ConfigFileInspection(true, null, paths, false, null);

        var value = content[match.Value.Start..match.Value.End];
        return new ConfigFileInspection(true, null, paths, true, StripQuotes(value));
    }

    public ConfigFileEditOutcome Apply(string content, string keyPath, string newValue)
    {
        var target = Split(keyPath);
        List<string> paths;
        (int Start, int End)? match;

        try { (paths, match) = Walk(content, target); }
        catch (YamlException ex) { return ConfigFileEditOutcome.ParseFailure(ToParseError(ex)); }

        if (match is null) return ConfigFileEditOutcome.KeyNotFound(paths);

        var (start, end) = match.Value;
        var newContent = content[..start] + QuoteScalar(newValue) + content[end..];
        return ConfigFileEditOutcome.Success(newContent);
    }

    private static string[] Split(string keyPath) =>
        keyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string StripQuotes(string raw)
    {
        if (raw.Length >= 2 && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\'')))
            return raw[1..^1];
        return raw;
    }

    private static string QuoteScalar(string value)
    {
        var escaped = new StringBuilder();
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': escaped.Append("\\\\"); break;
                case '"': escaped.Append("\\\""); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': break;
                default: escaped.Append(c); break;
            }
        }
        return $"\"{escaped}\"";
    }

    private static ConfigFileParseError ToParseError(YamlException ex) =>
        new(ex.Message,
            ex.Start.Line > 0 ? (int)ex.Start.Line : null,
            ex.Start.Column > 0 ? (int)ex.Start.Column : null);

    private static (List<string> Paths, (int Start, int End)? Match) Walk(string content, string[]? target)
    {
        var parser = new Parser(new StringReader(content));
        var paths = new List<string>();
        (int, int)? match = null;
        var frames = new Stack<Frame>();
        var currentPath = new List<string>();

        while (parser.MoveNext())
        {
            var evt = parser.Current;
            if (evt is null or StreamStart or StreamEnd or DocumentStart or DocumentEnd) continue;

            // Inside a mapping, alternating events are key/value. A key is always a plain scalar for
            // any config file this covers; consume it into PendingKey and move on without treating it
            // as a value in its own right. Excludes MappingEnd: an empty mapping, or one that just
            // finished its last pair, is also "expecting a key" in this state machine, and its own
            // end-of-container event must fall through to the ordinary MappingEnd handling below
            // rather than being mistaken for a missing key.
            if (frames.Count > 0 && frames.Peek() is MappingFrame { ExpectingKey: true } mf && evt is not MappingEnd)
            {
                if (evt is Scalar keyScalar)
                {
                    mf.PendingKey = keyScalar.Value;
                    mf.ExpectingKey = false;
                    continue;
                }
                // A non-scalar (complex) key is outside what this editor promises to understand.
                throw new YamlException(evt.Start, evt.End, "A mapping key here is not a plain scalar.");
            }

            string? segment = null;
            if (frames.Count > 0)
            {
                var top = frames.Peek();
                if (top is SequenceFrame sf) { segment = sf.Index.ToString(); sf.Index++; }
                else if (top is MappingFrame mvf) { segment = mvf.PendingKey; mvf.ExpectingKey = true; mvf.PendingKey = null; }
            }

            switch (evt)
            {
                case MappingStart:
                    if (segment is not null) currentPath.Add(segment);
                    frames.Push(new MappingFrame());
                    break;

                case SequenceStart:
                    if (segment is not null) currentPath.Add(segment);
                    frames.Push(new SequenceFrame());
                    break;

                case MappingEnd or SequenceEnd:
                    frames.Pop();
                    if (currentPath.Count > 0) currentPath.RemoveAt(currentPath.Count - 1);
                    break;

                case Scalar scalar:
                    if (segment is not null)
                    {
                        currentPath.Add(segment);
                        paths.Add(string.Join('.', currentPath));
                        if (target is not null && match is null && PathEquals(currentPath, target))
                            match = ((int)scalar.Start.Index, (int)scalar.End.Index);
                        currentPath.RemoveAt(currentPath.Count - 1);
                    }
                    break;

                case AnchorAlias:
                    // An alias resolves to a node this walk did not visit at this position — treated
                    // as an opaque leaf (its own path, no expansion) rather than silently mangling a
                    // structure this editor does not fully understand.
                    if (segment is not null)
                    {
                        currentPath.Add(segment);
                        paths.Add(string.Join('.', currentPath));
                        currentPath.RemoveAt(currentPath.Count - 1);
                    }
                    break;
            }
        }

        return (paths, match);
    }

    private static bool PathEquals(List<string> path, string[] target)
    {
        if (path.Count != target.Length) return false;
        for (var i = 0; i < path.Count; i++)
            if (!string.Equals(path[i], target[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    private abstract class Frame;

    private sealed class MappingFrame : Frame
    {
        public bool ExpectingKey = true;
        public string? PendingKey;
    }

    private sealed class SequenceFrame : Frame
    {
        public int Index;
    }
}
