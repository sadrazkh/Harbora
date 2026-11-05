using System.Globalization;

namespace Harbora.Shared.Inspection;

/// <summary>
/// The port an app listens on: <c>EXPOSE</c>, then <c>PORT=</c> in the example file, then the stack's
/// default. Where those defaults come from is the point, because a number that disagrees with what
/// <c>Buildpacks</c> generates is a health check that fails on a working app:
///
/// <list type="bullet">
/// <item><b>Static: 80, always.</b> The generated image is plain nginx with <c>EXPOSE 80</c>; it never reads the port it is given, so neither an example file nor a preference can change this.</item>
/// <item><b>Frontend: 80.</b> The generated nginx listens on whatever it is given, so any number works, and this is the proposal rather than a limit. A <c>PORT=</c> in a frontend's example file is its dev server's, which never runs here, so it is not consulted.</item>
/// <item><b>PHP: 80.</b> The buildpack rewrites Apache's <c>Listen 80</c> to the configured port, so any number works, and 80 is Apache's own and <c>App.ContainerPort</c>'s default.</item>
/// <item><b>.NET: 8080.</b> The buildpack sets <c>ASPNETCORE_URLS</c>, so any number works, and 8080 is what ASP.NET's images have used since .NET 8.</item>
/// <item><b>Go: 8080, Node: 3000.</b> The buildpack sets <c>PORT</c>, so the number is only a convention: the one each community's own code falls back to. These are not framework defaults; the framework is not consulted.</item>
/// <item><b>Python: the framework's own, else 8000.</b> The buildpack sets <c>PORT</c> here as well, but what runs is <c>python app.py</c>, and neither Flask's <c>app.run()</c> nor <c>uvicorn.run()</c> reads it. Proposing the framework's own number makes the two agree whether or not the code reads <c>PORT</c>, since the proposal is what <c>PORT</c> gets set to.</item>
/// <item><b>Dockerfile with no <c>EXPOSE</c>: the framework's well-known default, else null.</b> No buildpack is involved and nothing sets <c>PORT</c>, so the framework's default is the only honest guess, and with no framework there is none to make.</item>
/// <item><b>Compose, Unknown: null.</b> Nothing to go on.</item>
/// </list>
/// </summary>
internal static class PortDetector
{
    private static readonly (string Library, int Port)[] PythonFrameworks =
    [
        ("uvicorn", 8000), ("fastapi", 8000), ("starlette", 8000),
        ("flask", 5000),
        ("gunicorn", 8000), ("django", 8000),
        ("aiohttp", 8080)
    ];

    private static readonly HashSet<string> NodeFrameworks = new(StringComparer.Ordinal)
    {
        "next", "nuxt", "express", "fastify", "koa", "@nestjs/core", "hono"
    };

    public static int? Detect(
        SourceTree app, DetectedStack stack, DetectedAppKind kind, IReadOnlyList<ExampleVar> example, ProjectFacts facts)
    {
        if (kind == DetectedAppKind.Worker) return null;
        if (stack is DetectedStack.Static or DetectedStack.Frontend) return 80;

        if (stack == DetectedStack.Dockerfile && ExposedPort(app) is { } exposed) return exposed;
        if (ExamplePort(example) is { } declared) return declared;

        return stack switch
        {
            DetectedStack.Node => 3000,
            DetectedStack.DotNet => 8080,
            DetectedStack.Go => 8080,
            DetectedStack.Php => 80,
            DetectedStack.Python => PythonFramework(facts) ?? 8000,
            DetectedStack.Dockerfile => PythonFramework(facts) ?? (facts.Has(Ecosystem.Node, NodeFrameworks) ? 3000 : (int?)null),
            _ => null
        };
    }

    private static int? PythonFramework(ProjectFacts facts)
    {
        foreach (var (library, port) in PythonFrameworks)
        {
            if (facts.Dependencies.Any(d => d.Ecosystem == Ecosystem.Python && d.Key == library)) return port;
        }
        return null;
    }

    private static int? ExamplePort(IReadOnlyList<ExampleVar> example) =>
        example.FirstOrDefault(v => v.Name == "PORT") is { } port ? ParsePort(port.Value) : null;

    private static int? ExposedPort(SourceTree app)
    {
        foreach (var file in StackDetector.DockerfileNames)
        {
            if (app.ReadText(file) is { } text) return DockerfileExpose(text);
        }
        return null;
    }

    /// <summary>
    /// The first TCP port the final stage exposes. An <c>EXPOSE</c> in a build stage says nothing about
    /// the running image, which is why this resets at every <c>FROM</c>.
    /// </summary>
    private static int? DockerfileExpose(string text)
    {
        int? exposed = null;
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in text.Replace("\r\n", "\n").Replace("\\\n", " ").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (words[0].ToUpperInvariant())
            {
                case "FROM":
                    exposed = null;
                    variables.Clear();
                    break;

                case "ENV" or "ARG":
                    ReadVariables(words, variables);
                    break;

                case "EXPOSE":
                    exposed ??= words.Skip(1).Select(w => ExposedNumber(w, variables)).FirstOrDefault(p => p is not null);
                    break;
            }
        }

        return exposed;
    }

    private static void ReadVariables(string[] words, Dictionary<string, string> variables)
    {
        // Both spellings: ENV PORT=8080 A=1, and the older ENV PORT 8080.
        if (words.Length == 3 && !words[1].Contains('=') && !words[2].Contains('='))
        {
            variables[words[1]] = words[2];
            return;
        }

        foreach (var word in words.Skip(1))
        {
            var equals = word.IndexOf('=');
            if (equals > 0) variables[word[..equals]] = word[(equals + 1)..].Trim('"', '\'');
        }
    }

    private static int? ExposedNumber(string word, Dictionary<string, string> variables)
    {
        var slash = word.IndexOf('/');
        if (slash >= 0)
        {
            if (!word[(slash + 1)..].Equals("tcp", StringComparison.OrdinalIgnoreCase)) return null;
            word = word[..slash];
        }

        if (word.StartsWith('$'))
        {
            var name = word.TrimStart('$').Trim('{', '}');
            var fallback = name.IndexOf(":-", StringComparison.Ordinal);
            var value = fallback < 0
                ? variables.GetValueOrDefault(name)
                : variables.GetValueOrDefault(name[..fallback]) ?? name[(fallback + 2)..];
            return value is null ? null : ParsePort(value);
        }

        return ParsePort(word);
    }

    private static int? ParsePort(string value) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : null;
}
