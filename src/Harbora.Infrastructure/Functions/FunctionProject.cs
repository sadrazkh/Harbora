using System.Text;
using System.Text.Json;
using Harbora.Domain.Functions;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// Turns a function app's rows into a complete, buildable source tree.
///
/// <para>
/// This is the whole of "no Dockerfile, no repository": publishing writes these files into a work
/// directory and hands it to <c>DeploymentPipeline.BuildFromSourceAsync</c>, which is the same code
/// path a Git checkout takes from that point on. Nothing about building, health-checking, cutting
/// over or rolling back had to learn what a function is.
/// </para>
///
/// <para>
/// The generated dispatcher names every function explicitly — no reflection, no scanning a directory
/// at start-up. A misspelled handler is then a <b>compile error in the build log</b>, at publish
/// time, instead of a 404 at 3am from a function that was quietly never registered.
/// </para>
///
/// <para>
/// Pure on purpose: files in, text out, no filesystem and no Docker. It is the part of this feature
/// with behaviour worth testing, and it is tested on a machine that has neither.
/// </para>
/// </summary>
public static class FunctionProject
{
    /// <summary>One generated file, at a path relative to the build context root.</summary>
    public sealed record GeneratedFile(string Path, string Content);

    /// <summary>The port every host listens on unless the app says otherwise.</summary>
    public const int DefaultPort = 8080;

    /// <summary>Where the panel knocks when it invokes a function itself, rather than a visitor.</summary>
    public const string InvokePathPrefix = "/__harbora/invoke/";

    /// <summary>Answered by every host as soon as it is listening, whatever the functions do.</summary>
    public const string HealthPath = "/__harbora/health";

    /// <summary>The header carrying the app's invoke secret.</summary>
    public const string SecretHeader = "x-harbora-invoke";

    /// <summary>The environment variable each host reads that secret from.</summary>
    public const string SecretEnvVar = "HARBORA_FN_SECRET";

    /// <summary>
    /// The environment variable each host reads the panel's own report-back address from — where a
    /// public call gets POSTed back, fire-and-forget, once the host has answered it (see this file's
    /// own "the host reports what it actually did" note further down). Empty on an app deployed
    /// before this shipped; every host treats that as "nothing to report to", not an error — a
    /// visitor's own response must never depend on this address existing.
    /// </summary>
    public const string ReportUrlEnvVar = "HARBORA_REPORT_URL";

    /// <summary>
    /// The panel-side path a generated host's own report of a public call is POSTed to — matched by
    /// <c>FunctionInvocationReportController</c>. Shared here so <c>DeploymentPipeline</c> (which
    /// builds the full URL into <see cref="ReportUrlEnvVar"/>) and the controller's own route can
    /// never drift apart.
    /// </summary>
    public static string ReportPath(Guid appId) => $"/functions/{appId}/report";

    public static IReadOnlyList<GeneratedFile> Generate(
        FunctionRuntime runtime, IReadOnlyList<FunctionDefinition> functions, int port = DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(functions);

        // Published in slug order so the same set of functions always produces byte-identical
        // sources. Docker layer caching keys off content: an unstable ordering would rebuild every
        // layer on a publish that changed nothing.
        var ordered = functions.OrderBy(f => f.Slug, StringComparer.Ordinal).ToList();

        foreach (var fn in ordered)
        {
            if (!FunctionSlug.IsValid(fn.Slug))
                throw new InvalidOperationException(
                    $"Function '{fn.Name}' has an unusable identifier ('{fn.Slug}'). " +
                    "Rename it using letters, digits and hyphens.");
        }

        return runtime switch
        {
            FunctionRuntime.CSharp => CSharp(ordered, port),
            FunctionRuntime.JavaScript => JavaScript(ordered, port),
            FunctionRuntime.Python => Python(ordered, port),
            _ => throw new NotSupportedException($"Runtime {runtime} has no host image.")
        };
    }

    /// <summary>
    /// The routing table the hosts are generated from, and the one the panel's own explanation of
    /// "where does this answer" is built from — so the page and the container cannot disagree.
    /// </summary>
    public static string RouteFor(FunctionDefinition fn) =>
        string.IsNullOrWhiteSpace(fn.Route) ? fn.Slug : fn.Route.Trim().Trim('/');

    // ------------------------------------------------------------------ C#

    private static IReadOnlyList<GeneratedFile> CSharp(IReadOnlyList<FunctionDefinition> fns, int port)
    {
        var files = new List<GeneratedFile>
        {
            new("Dockerfile.harbora",
                $"""
                # Auto-generated by Harbora (functions, C#)
                FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
                WORKDIR /src
                COPY . .
                RUN dotnet publish Host.csproj -c Release -o /app

                FROM mcr.microsoft.com/dotnet/aspnet:10.0
                WORKDIR /app
                COPY --from=build /app ./
                ENV ASPNETCORE_URLS=http://+:{port}
                ENV PORT={port}
                EXPOSE {port}
                ENTRYPOINT ["dotnet", "Host.dll"]
                """),

            new("Host.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <InvariantGlobalization>true</InvariantGlobalization>
                    <!-- A function that does not compile must fail the publish, not ship with a
                         warning nobody reads in a build log. -->
                    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                  </PropertyGroup>
                </Project>
                """),

            new("Harbora/Contract.cs", CSharpContract())
        };

        foreach (var fn in fns)
            files.Add(new GeneratedFile($"functions/{fn.Slug}.cs", CSharpFunctionFile(fn)));

        files.Add(new GeneratedFile("Program.cs", CSharpProgram(fns, port)));
        return files;
    }

    private static string CSharpContract() =>
        """
        // Auto-generated by Harbora. Do not edit — it is rewritten on every publish.
        using System.Text.Json;

        namespace Harbora.Functions;

        /// <summary>What arrived. Body is the raw text; Json&lt;T&gt;() parses it when you want it typed.</summary>
        public sealed record FnRequest(
            string Method,
            string Path,
            IReadOnlyDictionary<string, string> Query,
            IReadOnlyDictionary<string, string> Headers,
            string Body)
        {
            public T? Json<T>() => string.IsNullOrWhiteSpace(Body)
                ? default
                : JsonSerializer.Deserialize<T>(Body, JsonOptions.Default);
        }

        /// <summary>What the platform knows about this call.</summary>
        public sealed class FnContext
        {
            public required string FunctionName { get; init; }

            /// <summary>"http", "cron", "event" or "queue".</summary>
            public required string Trigger { get; init; }

            /// <summary>The app's environment variables — where secrets live, never the code.</summary>
            public required IReadOnlyDictionary<string, string> Env { get; init; }

            /// <summary>Set for an event trigger; null otherwise.</summary>
            public FnEvent? Event { get; init; }

            /// <summary>Writes to the container's stdout, which is the app's live log in the panel.</summary>
            public void Log(string message) =>
                Console.WriteLine($"[{FunctionName}] {message}");
        }

        /// <summary>The platform event that caused this call.</summary>
        public sealed record FnEvent(string Key, string? Subject, IReadOnlyDictionary<string, string?> Data);

        /// <summary>What to send back.</summary>
        public sealed record FnResponse(int Status, string Body, IReadOnlyDictionary<string, string>? Headers = null)
        {
            public static FnResponse Text(string body, int status = 200) =>
                new(status, body, new Dictionary<string, string> { ["content-type"] = "text/plain; charset=utf-8" });

            public static FnResponse Json(object? value, int status = 200) =>
                new(status, JsonSerializer.Serialize(value, JsonOptions.Default),
                    new Dictionary<string, string> { ["content-type"] = "application/json; charset=utf-8" });

            public static FnResponse Empty(int status = 204) => new(status, "");
        }

        internal static class JsonOptions
        {
            public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
        }
        """;

    private static string CSharpFunctionFile(FunctionDefinition fn)
    {
        // The user's file is wrapped rather than concatenated: a file-scoped namespace keeps two
        // functions that both declare `Function` from colliding, and it still allows the usings the
        // person typed, since C# permits them after a file-scoped namespace declaration.
        var type = FunctionSlug.ToPascalCase(fn.Slug);
        return
            $"""
            // {fn.Name} — written in the Harbora panel.
            namespace Harbora.Fn.{type};

            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Net.Http;
            using System.Text;
            using System.Text.Json;
            using System.Threading.Tasks;
            using Harbora.Functions;

            {fn.Code.TrimEnd()}
            """;
    }

    private static string CSharpProgram(IReadOnlyList<FunctionDefinition> fns, int port)
    {
        var registry = new StringBuilder();
        foreach (var fn in fns)
        {
            var type = FunctionSlug.ToPascalCase(fn.Slug);
            registry.Append(
                $"""
                        ["{fn.Slug}"] = new Registration(
                            "{fn.Slug}", "{Escape(RouteFor(fn))}", "{TriggerName(fn.Trigger)}", {Lower(fn.IsEnabled)}, {Lower(fn.IsPublic)},
                            (req, ctx) => Harbora.Fn.{type}.Function.Run(req, ctx)),

                """);
        }

        return
            $$"""
            // Auto-generated by Harbora. Do not edit — it is rewritten on every publish.
            using System.Diagnostics;
            using System.Text;
            using System.Text.Json;
            using Harbora.Functions;

            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.ClearProviders();
            var app = builder.Build();

            var secret = Environment.GetEnvironmentVariable("{{SecretEnvVar}}") ?? "";
            var env = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string)(e.Value ?? ""), StringComparer.OrdinalIgnoreCase);

            // The host reports what it actually did (2026-08-21 functions-and-services plan follow-up:
            // a public call was recorded nowhere, and the honest fix is not silence, it is the host
            // telling the panel afterwards). Empty on an app deployed before this shipped — every
            // report below is then a deliberate no-op, never a startup failure.
            var reportUrl = Environment.GetEnvironmentVariable("{{ReportUrlEnvVar}}") ?? "";
            var reportClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            var registry = new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase)
            {
            {{registry}}    };

            // Answered before any function is touched, so the deployment's health check reports on the
            // host being up rather than on whatever the first function happens to do.
            app.MapGet("{{HealthPath}}", () => Results.Json(new
            {
                status = "ok",
                functions = registry.Count,
                runtime = "csharp"
            }));

            // The panel's own door: cron and events arrive here, never through a public route.
            app.MapPost("{{InvokePathPrefix}}{slug}", async (string slug, HttpRequest http) =>
            {
                if (string.IsNullOrEmpty(secret) || http.Headers["{{SecretHeader}}"] != secret)
                    return Results.StatusCode(401);
                if (!registry.TryGetValue(slug, out var fn))
                    return Results.NotFound();

                using var reader = new StreamReader(http.Body);
                var raw = await reader.ReadToEndAsync();
                var envelope = string.IsNullOrWhiteSpace(raw)
                    ? null
                    : JsonSerializer.Deserialize<Envelope>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));

                var ctx = new FnContext
                {
                    FunctionName = fn.Slug,
                    Trigger = envelope?.Trigger ?? "event",
                    Env = env,
                    Event = envelope?.Event is { } e ? new FnEvent(e.Key, e.Subject, e.Data ?? new Dictionary<string, string?>()) : null
                };
                var request = new FnRequest("POST", "/" + fn.Slug,
                    new Dictionary<string, string>(), Headers(http), envelope?.Body ?? "");

                // report: false — the panel already wrote this invocation's row before it dialled in
                // and will complete it from this very response; the host reporting it too would be a
                // second, uncorrelated row for a call the panel already watched happen.
                return await RunAsync(fn, request, ctx, report: false);
            });

            // Everything else is a visitor. Longest matching route wins; a single HTTP function also
            // answers the root, because an app with one function and a 404 on "/" reads as broken.
            app.Map("/{**path}", async (HttpRequest http) =>
            {
                var path = http.Path.Value?.Trim('/') ?? "";
                var http_fns = registry.Values.Where(f => f.Trigger == "http" && f.Enabled).ToList();

                var fn = http_fns
                    .Where(f => path == f.Route || (f.Route.Length > 0 && path.StartsWith(f.Route + "/", StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(f => f.Route.Length)
                    .FirstOrDefault()
                    ?? (http_fns.Count == 1 ? http_fns[0] : null);

                if (fn is null) return Results.NotFound(new { error = "No function is routed here.", path });
                // Protected is the default and the closedness every function had before this flag
                // existed: only a function whose owner flipped it Public answers a visitor here.
                // Everyone else — cron, events, a manual Run now — still reaches it through the
                // panel's signed door above, which this check never touches.
                if (!fn.Public) return Results.StatusCode(401);

                using var reader = new StreamReader(http.Body);
                var body = await reader.ReadToEndAsync();
                var request = new FnRequest(
                    http.Method, http.Path.Value ?? "/",
                    http.Query.ToDictionary(q => q.Key, q => q.Value.ToString()),
                    Headers(http), body);

                var ctx = new FnContext { FunctionName = fn.Slug, Trigger = "http", Env = env };
                // report: true — this is the one path the panel never watches: a visitor calling a
                // Public function's own URL directly. The host is the only witness, so it tells the
                // panel what happened once it has already answered.
                return await RunAsync(fn, request, ctx, report: true);
            });

            app.Run("http://0.0.0.0:{{port}}");
            return;

            static Dictionary<string, string> Headers(HttpRequest http) =>
                http.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            // One place decides what a failure looks like: 500 with the exception text on stdout, so it
            // lands in the app's live log, and a short body so a caller is told something went wrong
            // without being handed a stack trace. Not static (unlike the two helpers below it) because
            // a "report: true" call needs to reach ReportAsync, which closes over reportUrl/secret/
            // reportClient above — see that function's own doc for why this must never block a reply.
            async Task<IResult> RunAsync(Registration fn, FnRequest request, FnContext ctx, bool report)
            {
                if (!fn.Enabled)
                {
                    if (report) ReportAsync(fn.Slug, 503, 0, "Function is disabled.");
                    return Results.StatusCode(503);
                }

                var started = Stopwatch.GetTimestamp();
                try
                {
                    var response = await fn.Handler(request, ctx);
                    var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Console.WriteLine($"[{fn.Slug}] {ctx.Trigger} {response.Status} {elapsed}ms");
                    if (report) ReportAsync(fn.Slug, response.Status, elapsed,
                        response.Status >= 400 ? $"The function answered {response.Status}." : null);
                    return Results.Content(response.Body, ContentType(response), statusCode: response.Status);
                }
                catch (Exception ex)
                {
                    var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Console.WriteLine($"[{fn.Slug}] {ctx.Trigger} FAILED after {elapsed}ms: {ex}");
                    if (report) ReportAsync(fn.Slug, 500, elapsed, ex.Message);
                    return Results.Content("The function threw. See the application log.", "text/plain", statusCode: 500);
                }
            }

            // Fire-and-forget, deliberately not awaited by any caller: a visitor already has their
            // response (RunAsync calls this after computing it, never before) by the time this runs, and
            // a slow or unreachable panel — restarting, mid-deploy, simply down — must never be the
            // reason a customer's own webhook looks slow or fails. Exceptions are swallowed for the same
            // reason: there is nobody left on the other end of this call to hand them to.
            void ReportAsync(string slug, int? statusCode, int durationMs, string? error)
            {
                if (string.IsNullOrEmpty(reportUrl)) return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, reportUrl);
                        request.Headers.TryAddWithoutValidation("{{SecretHeader}}", secret);
                        request.Content = new StringContent(
                            JsonSerializer.Serialize(new { slug, statusCode, durationMs, error }),
                            Encoding.UTF8, "application/json");
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        using var response = await reportClient.SendAsync(request, cts.Token);
                    }
                    catch
                    {
                        // Best-effort: nothing here may ever surface to the visitor whose call already
                        // completed, and the panel being unreachable is not this host's problem to solve.
                    }
                });
            }

            static string ContentType(FnResponse response) =>
                response.Headers is not null && response.Headers.TryGetValue("content-type", out var ct)
                    ? ct : "text/plain; charset=utf-8";

            internal sealed record Registration(
                string Slug, string Route, string Trigger, bool Enabled, bool Public,
                Func<FnRequest, FnContext, Task<FnResponse>> Handler);

            internal sealed record Envelope(string? Trigger, string? Body, EnvelopeEvent? Event);
            internal sealed record EnvelopeEvent(string Key, string? Subject, Dictionary<string, string?>? Data);
            """;
    }

    // ---------------------------------------------------------- JavaScript

    private static IReadOnlyList<GeneratedFile> JavaScript(IReadOnlyList<FunctionDefinition> fns, int port)
    {
        var files = new List<GeneratedFile>
        {
            new("Dockerfile.harbora",
                $"""
                # Auto-generated by Harbora (functions, JavaScript)
                FROM node:22-alpine
                WORKDIR /app
                COPY . .
                ENV NODE_ENV=production
                ENV PORT={port}
                EXPOSE {port}
                CMD ["node", "server.mjs"]
                """),

            // Present so Node treats .mjs siblings as modules under any Node version, and so anybody
            // who exports the build context has a recognisable project rather than loose files.
            new("package.json",
                """
                {
                  "name": "harbora-functions",
                  "private": true,
                  "type": "module"
                }
                """)
        };

        foreach (var fn in fns)
            files.Add(new GeneratedFile($"functions/{fn.Slug}.mjs",
                $"""
                // {fn.Name} — written in the Harbora panel.
                {fn.Code.TrimEnd()}
                """));

        files.Add(new GeneratedFile("server.mjs", JavaScriptServer(fns, port)));
        return files;
    }

    private static string JavaScriptServer(IReadOnlyList<FunctionDefinition> fns, int port)
    {
        var imports = new StringBuilder();
        var registry = new StringBuilder();
        foreach (var fn in fns)
        {
            var id = FunctionSlug.ToIdentifier(fn.Slug);
            imports.AppendLine($"import fn_{id} from './functions/{fn.Slug}.mjs';");
            registry.AppendLine(
                $"  {{ slug: '{fn.Slug}', route: '{Escape(RouteFor(fn))}', trigger: '{TriggerName(fn.Trigger)}', enabled: {Lower(fn.IsEnabled)}, public: {Lower(fn.IsPublic)}, handler: fn_{id} }},");
        }

        return
            $$"""
            // Auto-generated by Harbora. Do not edit — it is rewritten on every publish.
            import { createServer } from 'node:http';
            {{imports}}
            const PORT = Number(process.env.PORT || {{port}});
            const SECRET = process.env.{{SecretEnvVar}} || '';
            // The host reports what it actually did (2026-08-21 functions-and-services plan follow-up:
            // a public call was recorded nowhere, and the honest fix is not silence, it is the host
            // telling the panel afterwards). Empty on an app deployed before this shipped — reportAsync
            // below then does nothing, never fails startup.
            const REPORT_URL = process.env.{{ReportUrlEnvVar}} || '';

            const FUNCTIONS = [
            {{registry}}];
            const BY_SLUG = new Map(FUNCTIONS.map(f => [f.slug, f]));

            function readBody(req) {
              return new Promise((resolve, reject) => {
                let data = '';
                req.on('data', chunk => { data += chunk; });
                req.on('end', () => resolve(data));
                req.on('error', reject);
              });
            }

            // A handler may return a string, an object, or {status, body, headers}. Anything else is
            // JSON. Guessing here rather than in every function is the point: the ten-line function
            // stays ten lines.
            function normalise(result) {
              if (result === undefined || result === null) return { status: 204, headers: {}, body: '' };
              if (typeof result === 'string') {
                return { status: 200, headers: { 'content-type': 'text/plain; charset=utf-8' }, body: result };
              }
              if (typeof result === 'object' && typeof result.status === 'number') {
                const body = typeof result.body === 'string'
                  ? result.body
                  : result.body === undefined ? '' : JSON.stringify(result.body);
                return { status: result.status, headers: result.headers || {}, body };
              }
              return {
                status: 200,
                headers: { 'content-type': 'application/json; charset=utf-8' },
                body: JSON.stringify(result)
              };
            }

            function send(res, out) {
              res.writeHead(out.status, out.headers);
              res.end(out.body);
            }

            // "report" is true only for the visitor route below — the panel's own invoke door already
            // wrote this invocation's row before it dialled in and completes it from this response, so
            // reporting there too would be a second, uncorrelated row for a call the panel already
            // watched happen.
            async function run(fn, request, ctx, res, report) {
              if (!fn.enabled) {
                send(res, { status: 503, headers: {}, body: '' });
                if (report) reportAsync(fn.slug, 503, 0, 'Function is disabled.');
                return;
              }
              const started = Date.now();
              try {
                const out = normalise(await fn.handler(request, ctx));
                const elapsed = Date.now() - started;
                console.log(`[${fn.slug}] ${ctx.trigger} ${out.status} ${elapsed}ms`);
                send(res, out);
                if (report) reportAsync(fn.slug, out.status, elapsed, out.status >= 400 ? `The function answered ${out.status}.` : null);
              } catch (err) {
                const elapsed = Date.now() - started;
                console.log(`[${fn.slug}] ${ctx.trigger} FAILED after ${elapsed}ms: ${err && err.stack || err}`);
                send(res, { status: 500, headers: { 'content-type': 'text/plain' }, body: 'The function threw. See the application log.' });
                if (report) reportAsync(fn.slug, 500, elapsed, String(err && err.message || err));
              }
            }

            // Fire-and-forget, deliberately not awaited by any caller: the visitor already has their
            // response (run() calls this after send(), never before) by the time this runs, and a slow
            // or unreachable panel — restarting, mid-deploy, simply down — must never be the reason a
            // customer's own webhook looks slow or fails. Errors are swallowed for the same reason:
            // there is nobody left on the other end of this call to hand them to.
            function reportAsync(slug, statusCode, durationMs, error) {
              if (!REPORT_URL) return;
              const controller = new AbortController();
              const timeout = setTimeout(() => controller.abort(), 5000);
              fetch(REPORT_URL, {
                method: 'POST',
                headers: { 'content-type': 'application/json', '{{SecretHeader}}': SECRET },
                body: JSON.stringify({ slug, statusCode, durationMs, error }),
                signal: controller.signal
              }).catch(() => {}).finally(() => clearTimeout(timeout));
            }

            function context(slug, trigger, event) {
              return {
                functionName: slug,
                trigger,
                event: event || null,
                env: process.env,
                log: (...args) => console.log(`[${slug}]`, ...args)
              };
            }

            createServer(async (req, res) => {
              const url = new URL(req.url, 'http://localhost');
              const path = url.pathname;

              if (path === '{{HealthPath}}') {
                send(res, {
                  status: 200,
                  headers: { 'content-type': 'application/json' },
                  body: JSON.stringify({ status: 'ok', functions: FUNCTIONS.length, runtime: 'javascript' })
                });
                return;
              }

              // The panel's own door: cron and events arrive here, never through a public route.
              if (path.startsWith('{{InvokePathPrefix}}')) {
                if (!SECRET || req.headers['{{SecretHeader}}'] !== SECRET) {
                  send(res, { status: 401, headers: {}, body: '' });
                  return;
                }
                const fn = BY_SLUG.get(path.slice('{{InvokePathPrefix}}'.length));
                if (!fn) { send(res, { status: 404, headers: {}, body: '' }); return; }

                const raw = await readBody(req);
                let envelope = {};
                try { envelope = raw ? JSON.parse(raw) : {}; } catch { envelope = {}; }

                const request = { method: 'POST', path: '/' + fn.slug, query: {}, headers: req.headers, body: envelope.body || '' };
                await run(fn, request, context(fn.slug, envelope.trigger || 'event', envelope.event), res, false);
                return;
              }

              const trimmed = path.replace(/^\/+|\/+$/g, '');
              const candidates = FUNCTIONS.filter(f => f.trigger === 'http' && f.enabled);
              let fn = candidates
                .filter(f => trimmed === f.route || (f.route && trimmed.startsWith(f.route + '/')))
                .sort((a, b) => b.route.length - a.route.length)[0];
              // One HTTP function also answers the root: an app with a single function and a 404 on
              // "/" reads as broken, and it is never ambiguous.
              if (!fn && candidates.length === 1) fn = candidates[0];
              if (!fn) {
                send(res, { status: 404, headers: { 'content-type': 'application/json' }, body: JSON.stringify({ error: 'No function is routed here.', path }) });
                return;
              }
              // Protected is the default and the closedness every function had before this flag
              // existed: only a function whose owner flipped it Public answers a visitor here.
              // Everyone else — cron, events, a manual Run now — still reaches it through the panel's
              // signed door above, which this check never touches.
              if (!fn.public) {
                send(res, { status: 401, headers: {}, body: '' });
                return;
              }

              const body = await readBody(req);
              const request = {
                method: req.method,
                path,
                query: Object.fromEntries(url.searchParams),
                headers: req.headers,
                body
              };
              // report: true — this is the one path the panel never watches: a visitor calling a Public
              // function's own URL directly. The host is the only witness, so it tells the panel what
              // happened once it has already answered.
              await run(fn, request, context(fn.slug, 'http'), res, true);
            }).listen(PORT, '0.0.0.0', () => console.log(`Harbora functions listening on ${PORT}`));
            """;
    }

    // -------------------------------------------------------------- Python

    private static IReadOnlyList<GeneratedFile> Python(IReadOnlyList<FunctionDefinition> fns, int port)
    {
        var files = new List<GeneratedFile>
        {
            new("Dockerfile.harbora",
                $"""
                # Auto-generated by Harbora (functions, Python)
                FROM python:3.12-alpine
                WORKDIR /app
                COPY . .
                ENV PYTHONUNBUFFERED=1
                ENV PORT={port}
                EXPOSE {port}
                CMD ["python", "server.py"]
                """)
        };

        foreach (var fn in fns)
            files.Add(new GeneratedFile($"functions/{fn.Slug.Replace('-', '_')}.py",
                $"""
                # {fn.Name} — written in the Harbora panel.
                {fn.Code.TrimEnd()}
                """));

        files.Add(new GeneratedFile("server.py", PythonServer(fns, port)));
        return files;
    }

    private static string PythonServer(IReadOnlyList<FunctionDefinition> fns, int port)
    {
        var imports = new StringBuilder();
        var registry = new StringBuilder();
        foreach (var fn in fns)
        {
            var module = FunctionSlug.ToIdentifier(fn.Slug);
            imports.AppendLine($"from functions import {module} as fn_{module}");
            registry.AppendLine(
                $"    {{'slug': '{fn.Slug}', 'route': '{Escape(RouteFor(fn))}', 'trigger': '{TriggerName(fn.Trigger)}', 'enabled': {(fn.IsEnabled ? "True" : "False")}, 'public': {(fn.IsPublic ? "True" : "False")}, 'handler': fn_{module}.run}},");
        }

        // Only the standard library: pip install at image build would need the network, would fail
        // on a server behind a proxy, and would turn a three-second publish into a minute.
        return
            $$""""
            # Auto-generated by Harbora. Do not edit — it is rewritten on every publish.
            import json
            import os
            import threading
            import time
            import traceback
            import urllib.request
            from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
            from urllib.parse import urlparse, parse_qs

            {{imports}}
            PORT = int(os.environ.get('PORT', {{port}}))
            SECRET = os.environ.get('{{SecretEnvVar}}', '')
            # The host reports what it actually did (2026-08-21 functions-and-services plan follow-up:
            # a public call was recorded nowhere, and the honest fix is not silence, it is the host
            # telling the panel afterwards). Empty on an app deployed before this shipped —
            # report_async below then does nothing, never fails startup.
            REPORT_URL = os.environ.get('{{ReportUrlEnvVar}}', '')

            FUNCTIONS = [
            {{registry}}]
            BY_SLUG = {f['slug']: f for f in FUNCTIONS}


            def report_async(slug, status_code, duration_ms, error):
                # Fire-and-forget, deliberately not joined by any caller: the visitor already has their
                # response (_run sends it before calling this) by the time this runs, and a slow or
                # unreachable panel — restarting, mid-deploy, simply down — must never be the reason a
                # customer's own webhook looks slow or fails. A daemon thread never blocks interpreter
                # exit either, for the same reason.
                if not REPORT_URL:
                    return

                def send():
                    try:
                        payload = json.dumps({
                            'slug': slug, 'statusCode': status_code, 'durationMs': duration_ms, 'error': error
                        }).encode('utf-8')
                        req = urllib.request.Request(
                            REPORT_URL, data=payload, method='POST',
                            headers={'content-type': 'application/json', '{{SecretHeader}}': SECRET})
                        urllib.request.urlopen(req, timeout=5).close()
                    except Exception:
                        # Best-effort: nothing here may ever surface to the visitor whose call already
                        # completed, and the panel being unreachable is not this host's problem to solve.
                        pass

                threading.Thread(target=send, daemon=True).start()


            def normalise(result):
                """A handler may return a string, a dict, or a (status, body, headers) response dict."""
                if result is None:
                    return 204, {}, ''
                if isinstance(result, str):
                    return 200, {'content-type': 'text/plain; charset=utf-8'}, result
                if isinstance(result, dict) and 'status' in result:
                    body = result.get('body', '')
                    if not isinstance(body, str):
                        body = json.dumps(body)
                    return int(result['status']), result.get('headers', {}), body
                return 200, {'content-type': 'application/json; charset=utf-8'}, json.dumps(result)


            def context(slug, trigger, event=None):
                return {
                    'function_name': slug,
                    'trigger': trigger,
                    'event': event,
                    'env': dict(os.environ),
                    'log': lambda *a: print('[%s]' % slug, *a),
                }


            class Handler(BaseHTTPRequestHandler):
                # The default logs every request to stderr in a format nothing here reads; the
                # per-invocation line below is the one that belongs in the application's log.
                def log_message(self, fmt, *args):
                    pass

                def _send(self, status, headers, body):
                    payload = body.encode('utf-8') if isinstance(body, str) else body
                    self.send_response(status)
                    for key, value in (headers or {}).items():
                        self.send_header(key, value)
                    self.send_header('content-length', str(len(payload)))
                    self.end_headers()
                    self.wfile.write(payload)

                def _body(self):
                    length = int(self.headers.get('content-length') or 0)
                    return self.rfile.read(length).decode('utf-8') if length else ''

                # "report" is true only for the visitor route below — the panel's own invoke door
                # already wrote this invocation's row before it dialled in and completes it from this
                # response, so reporting there too would be a second, uncorrelated row for a call the
                # panel already watched happen.
                def _run(self, fn, request, ctx, report=False):
                    if not fn['enabled']:
                        self._send(503, {}, '')
                        if report:
                            report_async(fn['slug'], 503, 0, 'Function is disabled.')
                        return
                    started = time.time()
                    try:
                        status, headers, body = normalise(fn['handler'](request, ctx))
                        duration = int((time.time() - started) * 1000)
                        print('[%s] %s %s %dms' % (fn['slug'], ctx['trigger'], status, duration))
                        self._send(status, headers, body)
                        if report:
                            report_async(fn['slug'], status, duration,
                                         ('The function answered %d.' % status) if status >= 400 else None)
                    except Exception:
                        duration = int((time.time() - started) * 1000)
                        print('[%s] %s FAILED after %dms:\n%s' % (
                            fn['slug'], ctx['trigger'], duration, traceback.format_exc()))
                        self._send(500, {'content-type': 'text/plain'}, 'The function threw. See the application log.')
                        if report:
                            report_async(fn['slug'], 500, duration, traceback.format_exc())

                def _dispatch(self):
                    parsed = urlparse(self.path)
                    path = parsed.path

                    if path == '{{HealthPath}}':
                        self._send(200, {'content-type': 'application/json'},
                                   json.dumps({'status': 'ok', 'functions': len(FUNCTIONS), 'runtime': 'python'}))
                        return

                    # The panel's own door: cron and events arrive here, never through a public route.
                    if path.startswith('{{InvokePathPrefix}}'):
                        if not SECRET or self.headers.get('{{SecretHeader}}') != SECRET:
                            self._send(401, {}, '')
                            return
                        fn = BY_SLUG.get(path[len('{{InvokePathPrefix}}'):])
                        if fn is None:
                            self._send(404, {}, '')
                            return
                        try:
                            envelope = json.loads(self._body() or '{}')
                        except ValueError:
                            envelope = {}
                        request = {'method': 'POST', 'path': '/' + fn['slug'], 'query': {},
                                   'headers': dict(self.headers), 'body': envelope.get('body', '')}
                        self._run(fn, request, context(fn['slug'], envelope.get('trigger', 'event'), envelope.get('event')))
                        return

                    trimmed = path.strip('/')
                    candidates = [f for f in FUNCTIONS if f['trigger'] == 'http' and f['enabled']]
                    matches = [f for f in candidates
                               if trimmed == f['route'] or (f['route'] and trimmed.startswith(f['route'] + '/'))]
                    matches.sort(key=lambda f: len(f['route']), reverse=True)
                    fn = matches[0] if matches else (candidates[0] if len(candidates) == 1 else None)
                    if fn is None:
                        self._send(404, {'content-type': 'application/json'},
                                   json.dumps({'error': 'No function is routed here.', 'path': path}))
                        return
                    # Protected is the default and the closedness every function had before this flag
                    # existed: only a function whose owner flipped it Public answers a visitor here.
                    # Everyone else — cron, events, a manual Run now — still reaches it through the
                    # panel's signed door above, which this check never touches.
                    if not fn['public']:
                        self._send(401, {}, '')
                        return

                    request = {'method': self.command, 'path': path,
                               'query': {k: v[0] for k, v in parse_qs(parsed.query).items()},
                               'headers': dict(self.headers), 'body': self._body()}
                    # report=True — this is the one path the panel never watches: a visitor calling a
                    # Public function's own URL directly. The host is the only witness, so it tells the
                    # panel what happened once it has already answered.
                    self._run(fn, request, context(fn['slug'], 'http'), report=True)

                do_GET = _dispatch
                do_POST = _dispatch
                do_PUT = _dispatch
                do_PATCH = _dispatch
                do_DELETE = _dispatch


            if __name__ == '__main__':
                print('Harbora functions listening on %d' % PORT)
                ThreadingHTTPServer(('0.0.0.0', PORT), Handler).serve_forever()
            """";
    }

    // ------------------------------------------------------------- helpers

    private static string TriggerName(FunctionTrigger trigger) => trigger switch
    {
        FunctionTrigger.Http => "http",
        FunctionTrigger.Cron => "cron",
        FunctionTrigger.Queue => "queue",
        _ => "event"
    };

    private static string Lower(bool value) => value ? "true" : "false";

    /// <summary>
    /// A route is written into three generated languages as a string literal. It is validated before
    /// it is stored, so this is the second line rather than the first — but a quote that reached the
    /// generator unescaped would not fail to build, it would produce a host that routes somewhere
    /// nobody asked for.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");

    /// <summary>The JSON body the panel posts when it invokes a function itself.</summary>
    public static string InvokeEnvelope(string trigger, FunctionEvent? evt, string? body = null) =>
        JsonSerializer.Serialize(new
        {
            trigger,
            body = body ?? "",
            @event = evt is null ? null : new { key = evt.Key, subject = evt.Subject, data = evt.Data }
        });
}
