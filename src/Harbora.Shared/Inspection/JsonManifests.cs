using System.Text.Json;

namespace Harbora.Shared.Inspection;

/// <summary>Reads a project's <c>package.json</c>.</summary>
internal static class NodeManifest
{
    private const string File = "package.json";

    private static readonly (string Section, bool Runtime)[] Sections =
    [
        ("dependencies", true),
        ("optionalDependencies", false),
        ("peerDependencies", false),
        ("devDependencies", false)
    ];

    private static readonly string[] WorkerFiles =
        ["worker.js", "worker.mjs", "worker.cjs", "worker.ts", "src/worker.js", "src/worker.ts"];

    public static void Read(SourceTree app, ProjectFacts facts)
    {
        facts.NodeHasWorkerScript = WorkerFiles.Any(app.HasFile);

        if (!app.HasFile(File)) return;
        JsonManifest.Read(app, facts, File, root =>
        {
            foreach (var (section, runtime) in Sections)
            foreach (var name in JsonManifest.Names(root, section))
                facts.Dependencies.Add(new Dependency(Ecosystem.Node, name, name.ToLowerInvariant(), File, section, runtime));

            if (NamesAWorker(root)) facts.NodeHasWorkerScript = true;
        });
    }

    /// <summary>False for a script that is not there, is not text or is blank, and for a file that cannot be read at all.</summary>
    public static bool HasScript(SourceTree folder, string name)
    {
        if (folder.ReadText(File) is not { } text) return false;

        try
        {
            using var document = JsonManifest.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("scripts", out var scripts) &&
                   scripts.ValueKind == JsonValueKind.Object &&
                   scripts.TryGetProperty(name, out var script) &&
                   script.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(script.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool NamesAWorker(JsonElement root)
    {
        if (root.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.String &&
            main.GetString()!.Contains("worker", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object) return false;

        return scripts.EnumerateObject().Any(s =>
            s.Name.Contains("worker", StringComparison.OrdinalIgnoreCase) ||
            (s.Value.ValueKind == JsonValueKind.String &&
             s.Value.GetString()!.Contains("worker", StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Reads a project's <c>composer.json</c>.</summary>
internal static class ComposerManifest
{
    private const string File = "composer.json";

    public static void Read(SourceTree app, ProjectFacts facts)
    {
        if (!app.HasFile(File)) return;
        JsonManifest.Read(app, facts, File, root =>
        {
            foreach (var (section, runtime) in new[] { ("require", true), ("require-dev", false) })
            foreach (var name in JsonManifest.Names(root, section))
                facts.Dependencies.Add(new Dependency(Ecosystem.Php, name, name.ToLowerInvariant(), File, section, runtime));
        });
    }
}

internal static class JsonManifest
{
    // Comments and trailing commas are not JSON, but npm's parser is not the only thing that has
    // ever written one of these, and being stricter than the build would only lose information.
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static JsonDocument Parse(string text) => JsonDocument.Parse(text, Lenient);

    public static void Read(SourceTree app, ProjectFacts facts, string file, Action<JsonElement> read)
    {
        if (app.ReadText(file) is not { } text)
        {
            facts.UnreadableFiles.Add(file);
            return;
        }

        try
        {
            using var document = Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object) read(document.RootElement);
            else facts.UnreadableFiles.Add(file);
        }
        catch (JsonException)
        {
            facts.UnreadableFiles.Add(file);
        }
    }

    public static IEnumerable<string> Names(JsonElement root, string section) =>
        root.TryGetProperty(section, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().Select(p => p.Name).ToList()
            : [];
}
