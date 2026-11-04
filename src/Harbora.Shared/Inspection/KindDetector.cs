using System.Text.RegularExpressions;

namespace Harbora.Shared.Inspection;

/// <summary>Web or worker, which decides whether the app gets an address and an HTTP probe.</summary>
internal static class KindDetector
{
    private static readonly Regex ProcfileLine = new(@"^([A-Za-z0-9_-]+)\s*:(.*)$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> PythonWorkers = new(StringComparer.Ordinal)
    {
        "python-telegram-bot", "aiogram", "pytelegrambotapi", "telethon", "discord-py", "celery"
    };

    private static readonly HashSet<string> PythonWeb = new(StringComparer.Ordinal)
    {
        "flask", "django", "fastapi", "uvicorn", "gunicorn", "aiohttp", "starlette"
    };

    private static readonly HashSet<string> NodeWorkers = new(StringComparer.Ordinal)
    {
        "telegraf", "grammy", "node-telegram-bot-api", "discord.js"
    };

    /// <summary>Also a producer's library, so on its own it says little: see <see cref="ProjectFacts.NodeHasWorkerScript"/>.</summary>
    private static readonly HashSet<string> NodeQueue = new(StringComparer.Ordinal) { "bullmq" };

    private static readonly HashSet<string> NodeWeb = new(StringComparer.Ordinal)
    {
        "express", "fastify", "koa", "next", "nuxt", "@nestjs/core", "hono"
    };

    public static (DetectedAppKind Kind, string? StartCommand) Detect(SourceTree app, DetectedStack stack, ProjectFacts facts)
    {
        // A compose stack is several things at once, and its file says what each is.
        if (stack == DetectedStack.Compose) return (DetectedAppKind.Web, null);

        if (ReadProcfile(app) is { } declared)
        {
            return declared.Kind == DetectedAppKind.Web && !ImageCanRun(stack, declared.Command)
                ? (DetectedAppKind.Web, null)
                : declared;
        }

        if (stack == DetectedStack.Node && IsNuxtServerBuild(app, facts))
            return (DetectedAppKind.Web, NuxtServer);

        if (facts.DotNetSdk is { } sdk)
        {
            if (sdk.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)) return (DetectedAppKind.Worker, null);
            if (sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)) return (DetectedAppKind.Web, null);
        }

        // Any web framework at all, in any section, keeps it Web: a wrong Web fails a visible health
        // check, while a wrong Worker quietly gives a website no address. The worker libraries count
        // only when the running app has them.
        var hasWeb = facts.Has(Ecosystem.Python, PythonWeb) || facts.Has(Ecosystem.Node, NodeWeb);
        var hasWorker = facts.HasRuntime(Ecosystem.Python, PythonWorkers) ||
                        facts.HasRuntime(Ecosystem.Node, NodeWorkers) ||
                        (facts.HasRuntime(Ecosystem.Node, NodeQueue) && facts.NodeHasWorkerScript);

        return (hasWorker && !hasWeb ? DetectedAppKind.Worker : DetectedAppKind.Web, null);
    }

    /// <summary>
    /// Whether a <c>web:</c> line can be what this stack's image runs. It usually says more than the image
    /// would: <c>gunicorn app:app</c> beside an <c>app.py</c> is the real server, where <c>python app.py</c>
    /// is a loopback development server at best. That only holds where the image keeps the source and its
    /// interpreter. A Go or .NET image holds one compiled output and already runs it, a static one holds
    /// nginx, and a line written for Heroku names paths that exist only there (<c>bin/server</c>), so taking
    /// it would break what works. A Dockerfile says for itself how it starts. Nor does
    /// <c>heroku-php-apache2</c> exist anywhere but on Heroku, while this PHP image has its own Apache.
    /// </summary>
    private static bool ImageCanRun(DetectedStack stack, string? command) => stack switch
    {
        DetectedStack.Node or DetectedStack.Python or DetectedStack.Unknown => true,
        DetectedStack.Php => command?.Contains("heroku-php-", StringComparison.Ordinal) != true,
        _ => false
    };

    private const string NuxtServer = "node .output/server/index.mjs";

    /// <summary>
    /// A default Nuxt project has a <c>build</c> and no <c>start</c>, which is the shape of a frontend, but
    /// its build writes a server rather than a site: the frontend image would stop saying there is no
    /// <c>index.html</c>. Its output is the same whatever the project's settings, so this is the one
    /// framework whose start command can be known. <c>nuxt generate</c> really does write a site, and is
    /// left to be one. SvelteKit is not treated this way because its adapter decides what comes out.
    /// </summary>
    private static bool IsNuxtServerBuild(SourceTree app, ProjectFacts facts) =>
        facts.Has(Ecosystem.Node, Nuxt) &&
        !NodeStartRule.CanStart(app) &&
        NodeManifest.Script(app, "build") is { } build &&
        !build.Contains("generate", StringComparison.Ordinal);

    private static readonly HashSet<string> Nuxt = new(StringComparer.Ordinal) { "nuxt" };

    /// <summary>Null when there is no Procfile or it names neither process, so the other evidence gets its say.</summary>
    private static (DetectedAppKind Kind, string? Command)? ReadProcfile(SourceTree app)
    {
        if (app.ReadText("Procfile") is not { } text) return null;

        string? worker = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (ProcfileLine.Match(line) is not { Success: true } match) continue;

            var command = match.Groups[2].Value.Trim();
            if (match.Groups[1].Value == "web") return (DetectedAppKind.Web, command.Length > 0 ? command : null);
            if (match.Groups[1].Value == "worker" && command.Length > 0) worker ??= command;
        }

        return worker is null ? null : (DetectedAppKind.Worker, worker);
    }
}
