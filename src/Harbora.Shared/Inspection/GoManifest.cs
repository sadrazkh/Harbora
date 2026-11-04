namespace Harbora.Shared.Inspection;

/// <summary>Reads the <c>require</c> lines of a <c>go.mod</c>.</summary>
internal static class GoManifest
{
    private const string File = "go.mod";

    public static void Read(SourceTree app, ProjectFacts facts)
    {
        if (!app.HasFile(File)) return;

        if (app.ReadText(File) is not { } text)
        {
            facts.UnreadableFiles.Add(File);
            return;
        }

        var inBlock = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            string? module = null;

            if (inBlock && line.StartsWith(')')) inBlock = false;
            else if (inBlock) module = FirstWord(line);
            else if (IsRequireBlock(line)) inBlock = true;
            else if (line.StartsWith("require ", StringComparison.Ordinal)) module = FirstWord(line["require ".Length..]);

            // `// indirect` is what a dependency of a dependency is marked with. A library the code
            // never imports is not evidence that this app talks to that database.
            if (module is null || line.Contains("// indirect", StringComparison.Ordinal)) continue;
            facts.Dependencies.Add(new Dependency(Ecosystem.Go, module, module.ToLowerInvariant(), File, File, true));
        }
    }

    private static bool IsRequireBlock(string line) =>
        line.StartsWith("require", StringComparison.Ordinal) && line["require".Length..].TrimStart().StartsWith('(');

    private static string? FirstWord(string text)
    {
        var word = text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return word is null || word.StartsWith("//", StringComparison.Ordinal) ? null : word;
    }
}
