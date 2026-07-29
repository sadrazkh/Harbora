namespace Harbora.Cli;

/// <summary>
/// <c>harbora.yml</c> — the per-project deploy config, read by <c>harbora deploy</c> so a deploy in
/// CI needs no arguments. Documented in <c>docs/cli-deploy.md</c>; treat the key names as a public
/// contract, because other tools generate this file.
///
/// Parsed by hand rather than with a YAML library: the schema is a handful of scalars plus two
/// lists, and a self-contained CLI binary is better off without the dependency. Anything not
/// recognised is ignored, so a newer file stays usable with an older CLI.
/// </summary>
public sealed class ProjectConfig
{
    public const string DefaultFileName = "harbora.yml";

    /// <summary>Candidate filenames, in precedence order.</summary>
    public static readonly string[] FileNames = ["harbora.yml", "harbora.yaml", "deployx.yml"];

    /// <summary>App slug on the server. Required unless passed on the command line.</summary>
    public string? App { get; set; }

    /// <summary>Panel URL. Overrides the logged-in server for this project (handy in CI).</summary>
    public string? Server { get; set; }

    /// <summary>Dockerfile path within the build context.</summary>
    public string? Dockerfile { get; set; }

    /// <summary>Build context, relative to the config file.</summary>
    public string? Context { get; set; }

    /// <summary>Image reference for image-only deploys (`harbora deploy` with no source to build).</summary>
    public string? Image { get; set; }

    /// <summary>Git branch to archive when deploying committed content.</summary>
    public string? Branch { get; set; }

    /// <summary>
    /// An inline Dockerfile. Written into the upload as <c>Dockerfile.harbora</c>, so a project can
    /// describe its build without keeping a Dockerfile in the repository — the equivalent of
    /// CapRover's <c>dockerfileLines</c>.
    /// </summary>
    public List<string> DockerfileLines { get; } = [];

    /// <summary>Extra ignore patterns, on top of .dockerignore/.gitignore and the built-in list.</summary>
    public List<string> Ignore { get; } = [];

    /// <summary>Finds the config file in <paramref name="dir"/>, or null.</summary>
    public static string? Locate(string dir) =>
        FileNames.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);

    public static ProjectConfig Load(string dir)
    {
        var path = Locate(dir);
        return path is null ? new ProjectConfig() : Parse(File.ReadAllLines(path));
    }

    /// <summary>
    /// Supports the flat keys, the <c>build:</c> block, and list values written either inline
    /// (<c>[a, b]</c>) or as <c>- item</c> lines.
    /// </summary>
    public static ProjectConfig Parse(IEnumerable<string> lines)
    {
        var config = new ProjectConfig();
        string? listKey = null;

        foreach (var raw in lines)
        {
            var line = StripComment(raw);
            if (line.Trim().Length == 0) continue;

            var indented = char.IsWhiteSpace(line[0]);
            var trimmed = line.Trim();

            // Continuation of a "- item" list.
            if (trimmed.StartsWith("- ") || trimmed == "-")
            {
                var item = Unquote(trimmed.Length > 1 ? trimmed[1..].Trim() : "");
                if (item.Length == 0) continue;
                if (listKey == "dockerfilelines") config.DockerfileLines.Add(item);
                else if (listKey == "ignore") config.Ignore.Add(item);
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;

            var key = trimmed[..colon].Trim().ToLowerInvariant();
            var value = Unquote(trimmed[(colon + 1)..].Trim());

            // A key with no value opens a list or a block.
            if (value.Length == 0)
            {
                listKey = key is "dockerfilelines" or "ignore" ? key : null;
                continue;
            }
            listKey = null;

            // Inline lists: ignore: [a, b]
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                var items = value[1..^1].Split(',')
                    .Select(v => Unquote(v.Trim()))
                    .Where(v => v.Length > 0);
                foreach (var item in items)
                {
                    if (key == "dockerfilelines") config.DockerfileLines.Add(item);
                    else if (key == "ignore") config.Ignore.Add(item);
                }
                continue;
            }

            switch (key)
            {
                case "app" or "name": config.App = value; break;
                case "server" or "url": config.Server = value; break;
                case "image": config.Image = value; break;
                case "branch": config.Branch = value; break;
                // `dockerfile:`/`context:` appear at the top level or nested under `build:` — the
                // meaning is the same either way, so indentation doesn't change how they're read.
                case "dockerfile": config.Dockerfile = value; break;
                case "context": config.Context = value; break;
            }

            _ = indented;   // retained for readability of the rule above
        }

        return config;
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        if (hash < 0) return line;
        // A '#' inside quotes is content, not a comment.
        var quotes = line[..hash].Count(c => c is '"' or '\'');
        return quotes % 2 == 1 ? line : line[..hash];
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}
