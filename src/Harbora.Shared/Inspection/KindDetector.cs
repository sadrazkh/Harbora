using System.Text.RegularExpressions;

namespace Harbora.Shared.Inspection;

/// <summary>Web or worker, which decides whether the app gets an address and an HTTP probe.</summary>
internal static class KindDetector
{
    private static readonly Regex ProcfileLine = new(@"^([A-Za-z0-9_-]+)\s*:\s*(.+?)\s*$", RegexOptions.CultureInvariant);

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

        if (ReadProcfile(app) is { } declared) return declared;

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

    /// <summary>Null when there is no Procfile or it names neither process, so the other evidence gets its say.</summary>
    private static (DetectedAppKind, string?)? ReadProcfile(SourceTree app)
    {
        if (app.ReadText("Procfile") is not { } text) return null;

        string? worker = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (ProcfileLine.Match(line) is not { Success: true } match) continue;

            if (match.Groups[1].Value == "web") return (DetectedAppKind.Web, null);
            if (match.Groups[1].Value == "worker") worker ??= match.Groups[2].Value;
        }

        return worker is null ? null : (DetectedAppKind.Worker, worker);
    }
}
