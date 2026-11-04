using System.Text.RegularExpressions;

namespace Harbora.Shared.Inspection;

/// <summary>
/// Reads <c>requirements.txt</c>, <c>pyproject.toml</c> and <c>Pipfile</c>. There is no TOML package in
/// this project, and these two files need very little of TOML: which section a line is in, and either
/// a <c>name = …</c> or one array of quoted strings.
/// </summary>
internal static class PythonManifests
{
    private const int MaxIncludes = 8;

    private static readonly Regex Requirement = new(@"^\s*([A-Za-z0-9][A-Za-z0-9._-]*)", RegexOptions.CultureInvariant);
    private static readonly Regex Egg = new(@"[#&]egg=([A-Za-z0-9][A-Za-z0-9._-]*)", RegexOptions.CultureInvariant);
    private static readonly Regex NamedUrl = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*(?:\[[^\]]*\])?\s*@", RegexOptions.CultureInvariant);
    private static readonly Regex Include = new(@"^(?:-r|--requirement)[\s=]*(\S+)", RegexOptions.CultureInvariant);
    private static readonly Regex Section = new(@"^\[+\s*([^\]]+?)\s*\]+", RegexOptions.CultureInvariant);
    private static readonly Regex TableKey = new(@"^""?([A-Za-z0-9][A-Za-z0-9._-]*)""?\s*=", RegexOptions.CultureInvariant);
    private static readonly Regex DependencyArray = new(@"^dependencies\s*=\s*\[", RegexOptions.CultureInvariant);

    public static void Read(SourceTree app, ProjectFacts facts)
    {
        ReadRequirements(app, facts, "requirements.txt", []);
        ReadPyproject(app, facts);
        ReadPipfile(app, facts);
    }

    /// <summary>PEP 503, which is why <c>PyMySQL</c>, <c>discord.py</c> and <c>aio_pika</c> can be found by one spelling each.</summary>
    public static string Normalise(string name) =>
        name.Trim().ToLowerInvariant().Replace('_', '-').Replace('.', '-');

    private static void Add(ProjectFacts facts, string name, string file) =>
        facts.Dependencies.Add(new Dependency(Ecosystem.Python, name, Normalise(name), file, Path.GetFileName(file), true));

    private static void ReadRequirements(SourceTree app, ProjectFacts facts, string file, HashSet<string> seen)
    {
        if (!seen.Add(file) || seen.Count > MaxIncludes || !app.HasFile(file)) return;

        if (app.ReadText(file) is not { } text)
        {
            facts.UnreadableFiles.Add(file);
            return;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // A Django-style project keeps its real list in requirements/base.txt and has only this
            // line at the top. Followed inside the tree and nowhere else.
            if (Include.Match(line) is { Success: true } include)
            {
                if (SourceTree.Resolve(file, include.Groups[1].Value) is { } target)
                    ReadRequirements(app, facts, target, seen);
                continue;
            }

            if (line[0] == '-' || (line.Contains("://") && !NamedUrl.IsMatch(line)))
            {
                if (Egg.Match(line) is { Success: true } egg) Add(facts, egg.Groups[1].Value, file);
                continue;
            }

            if (Requirement.Match(line) is { Success: true } requirement) Add(facts, requirement.Groups[1].Value, file);
        }
    }

    private static void ReadPyproject(SourceTree app, ProjectFacts facts)
    {
        const string file = "pyproject.toml";
        if (!app.HasFile(file)) return;

        if (app.ReadText(file) is not { } text)
        {
            facts.UnreadableFiles.Add(file);
            return;
        }

        string? section = null;
        var position = 0;
        while (position < text.Length)
        {
            var start = position;
            var end = text.IndexOf('\n', position);
            if (end < 0) end = text.Length;
            var line = text[position..end].Trim();
            position = end + 1;

            if (line.Length == 0 || line[0] == '#') continue;

            if (Section.Match(line) is { Success: true } header)
            {
                section = header.Groups[1].Value;
                continue;
            }

            if (section == "project" && DependencyArray.IsMatch(line))
            {
                var open = text.IndexOf('[', start);
                position = ReadArray(text, open + 1, value =>
                {
                    if (Requirement.Match(value) is { Success: true } requirement)
                        Add(facts, requirement.Groups[1].Value, file);
                });
                continue;
            }

            // Poetry spells it as a table of name = version. Its dev group is a table of its own and
            // so is left out by this exact match, as are extras: neither is what the app needs to run.
            if (section == "tool.poetry.dependencies" && TableKey.Match(line) is { Success: true } key &&
                !key.Groups[1].Value.Equals("python", StringComparison.OrdinalIgnoreCase))
                Add(facts, key.Groups[1].Value, file);
        }
    }

    /// <summary>
    /// Walks a TOML array of strings, returning the position after its closing bracket. Quotes have to
    /// be respected because <c>"uvicorn[standard]"</c> has a bracket in it that does not close anything.
    /// </summary>
    private static int ReadArray(string text, int position, Action<string> value)
    {
        while (position < text.Length)
        {
            var c = text[position];
            if (c == ']') return position + 1;

            if (c == '#')
            {
                var newline = text.IndexOf('\n', position);
                if (newline < 0) return text.Length;
                position = newline + 1;
                continue;
            }

            if (c is '"' or '\'')
            {
                var close = text.IndexOf(c, position + 1);
                if (close < 0) return text.Length;
                value(text[(position + 1)..close]);
                position = close + 1;
                continue;
            }

            position++;
        }
        return position;
    }

    private static void ReadPipfile(SourceTree app, ProjectFacts facts)
    {
        const string file = "Pipfile";
        if (!app.HasFile(file)) return;

        if (app.ReadText(file) is not { } text)
        {
            facts.UnreadableFiles.Add(file);
            return;
        }

        string? section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (Section.Match(line) is { Success: true } header) section = header.Groups[1].Value;
            else if (section == "packages" && TableKey.Match(line) is { Success: true } key) Add(facts, key.Groups[1].Value, file);
        }
    }
}
