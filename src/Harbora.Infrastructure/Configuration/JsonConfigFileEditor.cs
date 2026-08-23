using System.Text;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Configuration;

namespace Harbora.Infrastructure.Configuration;

/// <summary>
/// <c>appsettings.json</c> and friends — key path is colon-separated
/// (<c>ConnectionStrings:Default</c>), the exact idiom ASP.NET Core's own configuration binder uses,
/// so a rule reads the same way a developer already writes <c>IConfiguration["ConnectionStrings:Default"]</c>.
///
/// <para>
/// Parsed with <see cref="Utf8JsonReader"/> rather than <c>JsonNode</c> deliberately: the reader
/// exposes each token's exact byte span (<see cref="Utf8JsonReader.TokenStartIndex"/> /
/// <see cref="Utf8JsonReader.BytesConsumed"/>), which lets <see cref="Apply"/> splice only the one
/// value's bytes and leave every other byte in the file — comments a linter tolerates, indentation,
/// key order, unrelated values — completely untouched. A <c>JsonNode</c> round trip would reformat
/// the whole file to the writer's own canonical style, and a developer diffing this against Git would
/// see a wall of noise around the one value that actually changed.
/// </para>
/// </summary>
public sealed class JsonConfigFileEditor : IConfigFileEditor
{
    public ConfigFileFormat Format => ConfigFileFormat.Json;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 256
    };

    public ConfigFileInspection Inspect(string content, string? keyPath)
    {
        var target = keyPath is null ? null : Split(keyPath);
        List<string> allPaths;
        (long Start, long End)? match;

        try
        {
            (allPaths, match) = Walk(content, target);
        }
        catch (JsonException ex)
        {
            return ConfigFileInspection.ParseFailure(ToParseError(ex));
        }

        if (target is null) return new ConfigFileInspection(true, null, allPaths, false, null);

        if (match is null) return new ConfigFileInspection(true, null, allPaths, false, null);

        var value = ExtractValueText(content, match.Value);
        return new ConfigFileInspection(true, null, allPaths, true, value);
    }

    public ConfigFileEditOutcome Apply(string content, string keyPath, string newValue)
    {
        var target = Split(keyPath);
        List<string> allPaths;
        (long Start, long End)? match;

        try
        {
            (allPaths, match) = Walk(content, target);
        }
        catch (JsonException ex)
        {
            return ConfigFileEditOutcome.ParseFailure(ToParseError(ex));
        }

        if (match is null) return ConfigFileEditOutcome.KeyNotFound(allPaths);

        var bytes = Encoding.UTF8.GetBytes(content);
        var (start, end) = match.Value;
        var prefix = Encoding.UTF8.GetString(bytes, 0, (int)start);
        var suffix = Encoding.UTF8.GetString(bytes, (int)end, bytes.Length - (int)end);
        var literal = JsonSerializer.Serialize(newValue);

        return ConfigFileEditOutcome.Success(prefix + literal + suffix);
    }

    private static string[] Split(string keyPath) =>
        keyPath.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ExtractValueText(string content, (long Start, long End) span)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var raw = Encoding.UTF8.GetString(bytes, (int)span.Start, (int)(span.End - span.Start));
        // A string token still carries its own quotes; a scalar rendered back to the operator should
        // read like the value, not like JSON source.
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.GetString() ?? raw;
        }
        return raw;
    }

    private static ConfigFileParseError ToParseError(JsonException ex) =>
        new(ex.Message, ex.LineNumber is { } l ? (int)l + 1 : null,
            ex.BytePositionInLine is { } c ? (int)c + 1 : null);

    /// <summary>
    /// One pass over the document: every leaf's flattened colon-path (objects contribute property
    /// names, arrays contribute their index), and — when <paramref name="target"/> is given — the
    /// exact byte span of the one leaf matching it, if any.
    /// </summary>
    private static (List<string> Paths, (long Start, long End)? Match) Walk(string content, string[]? target)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);

        var paths = new List<string>();
        (long, long)? match = null;

        var frames = new Stack<Frame>();
        var currentPath = new List<string>();
        string? pendingName = null;

        while (reader.Read())
        {
            var type = reader.TokenType;
            string? segment = null;

            if (frames.Count > 0)
            {
                var top = frames.Peek();
                if (top.IsArray)
                {
                    if (type != JsonTokenType.EndArray)
                    {
                        segment = top.Index.ToString();
                        top.Index++;
                    }
                }
                else
                {
                    if (type == JsonTokenType.PropertyName)
                        pendingName = reader.GetString();
                    else if (type != JsonTokenType.EndObject)
                    {
                        segment = pendingName;
                        pendingName = null;
                    }
                }
            }

            switch (type)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    if (segment is not null) currentPath.Add(segment);
                    frames.Push(new Frame(type == JsonTokenType.StartArray));
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    frames.Pop();
                    if (currentPath.Count > 0) currentPath.RemoveAt(currentPath.Count - 1);
                    break;

                case JsonTokenType.PropertyName:
                    break;

                default: // String, Number, True, False, Null — a leaf.
                    if (segment is not null)
                    {
                        currentPath.Add(segment);
                        var joined = string.Join(':', currentPath);
                        paths.Add(joined);
                        if (target is not null && match is null && PathEquals(currentPath, target))
                            match = (reader.TokenStartIndex, reader.BytesConsumed);
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

    private sealed class Frame(bool isArray)
    {
        public bool IsArray { get; } = isArray;
        public int Index;
    }
}
