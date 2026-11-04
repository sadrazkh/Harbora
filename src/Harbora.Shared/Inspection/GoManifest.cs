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

    /// <summary>The <c>go 1.23</c> line, patch level ignored: the image for a minor is its newest patch. Null when there is none to read.</summary>
    public static Version? LanguageVersion(SourceTree app)
    {
        if (app.ReadText(File) is not { } text) return null;

        foreach (var raw in text.Split('\n'))
        {
            var words = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words is not ["go", var version, ..]) continue;

            var parts = version.Split('.');
            return parts.Length >= 2
                   && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
                   && int.TryParse(new string(parts[1].TakeWhile(char.IsAsciiDigit).ToArray()), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor)
                ? new Version(major, minor)
                : null;
        }

        return null;
    }

    /// <summary>
    /// The one <c>cmd/*</c> to build, for a project with no <c>main.go</c> at its top, which is the
    /// standard layout and where <c>go build .</c> finds nothing to compile. Null when the top has one, or
    /// when there are several and nothing says which. Only a name a shell line can hold plainly.
    /// </summary>
    public static string? SoleCommand(SourceTree app)
    {
        if (app.HasFile("main.go") || !app.HasDirectory("cmd")) return null;

        var cmd = app.Scope("cmd");
        var commands = cmd.AppFolders().Where(f => cmd.HasFile(f + "/main.go")).ToList();
        return commands is [var only] && only.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? "./cmd/" + only
            : null;
    }

    private static bool IsRequireBlock(string line) =>
        line.StartsWith("require", StringComparison.Ordinal) && line["require".Length..].TrimStart().StartsWith('(');

    private static string? FirstWord(string text)
    {
        var word = text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return word is null || word.StartsWith("//", StringComparison.Ordinal) ? null : word;
    }
}
