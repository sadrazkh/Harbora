using Harbora.Domain.Common;
using Harbora.Shared.Inspection;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Zero-config buildpacks. When a repo has no Dockerfile, this inspects the checkout and generates
/// a sensible Dockerfile for the detected stack — so "just give it a repo" works. The generated
/// image listens on <c>port</c> (via PORT / ASPNETCORE_URLS) to match the app's configured port.
/// Detection order is most-specific first; returns null when nothing recognisable is found.
/// </summary>
public static class Buildpacks
{
    /// <summary>
    /// Python entry-point candidates, most-specific/likely first. `manage.py` is deliberately
    /// absent: `python manage.py` with no arguments prints usage and exits 0, so a Django repo
    /// "detected" through it would build, start, exit and restart forever under a restart policy of
    /// `UnlessStopped` — an honest "couldn't detect" beats a silent crash loop. This buildpack does
    /// not support Django; add a Dockerfile for one.
    /// </summary>
    private static readonly string[] PythonEntryCandidates =
        ["app.py", "main.py", "wsgi.py", "server.py", "bot.py", "run.py", "start.py", "__main__.py"];

    /// <summary>The name <see cref="Frontend"/> goes by, in the deploy log and to anything comparing stacks.</summary>
    public const string FrontendStack = "Frontend (Nginx)";

    /// <summary>
    /// A <c>package.json</c> wins when it can start something, and otherwise gives way to any other
    /// stack's marker: see <see cref="NodeStartRule"/> for why. With no other marker and a <c>build</c>
    /// script it is a frontend, built and served, unless <paramref name="kind"/> or
    /// <paramref name="startCommand"/> says <c>npm start</c> was never going to run. With neither it is
    /// still Node, and fails at <c>npm start</c> as it always has. All of that comes ahead of the
    /// static build, because that <c>index.html</c> is usually the frontend's own source.
    /// <c>SourceInspector</c> mirrors this order, and <c>SourceInspectorParityTests</c> holds the two
    /// together.
    /// </summary>
    /// <param name="startCommand">
    /// What overrides the image's CMD: <c>App.StartCommand</c>, or a compose service's <c>command</c>.
    /// A TypeScript backend with a <c>build</c> script and <c>node dist/index.js</c> here runs today,
    /// and must not be mistaken for a site.
    /// </param>
    /// <param name="publicVariables">
    /// The app's variables with a public prefix, which the Node and frontend images read at build with
    /// nobody having ticked anything. Any name <see cref="PublicBuildVariables.IsPublic"/> refuses is
    /// ignored.
    /// </param>
    /// <param name="markedVariables">
    /// The app's variables marked <c>AvailableAtBuild</c>, which every image with a build step declares,
    /// except the frontend's: what that one produces is sent to every visitor. The result's
    /// <see cref="BuildpackResult.BuildArgNames"/> says which of all these the chosen image declared.
    /// </param>
    public static BuildpackResult? Detect(
        string contextPath, int port, ServiceKind kind = ServiceKind.Web,
        string? startCommand = null, IEnumerable<string>? publicVariables = null,
        IEnumerable<string>? markedVariables = null)
    {
        bool Has(string file) => File.Exists(Path.Combine(contextPath, file));
        bool HasAny(params string[] files) => files.Any(Has);

        var marked = Declarable(markedVariables);
        var onNode = Declarable(markedVariables, publicVariables?.Where(PublicBuildVariables.IsPublic));

        // What the chosen image will not do, said in the deploy log because this deploys and serves a
        // page with no CSS. Without the sentence, nothing would explain why.
        BuildpackResult? BesidePackage(BuildpackResult? pack) =>
            pack is { } chosen && NodeStartRule.HasBuildScript(contextPath)
                ? chosen with { Warning = NodeStartRule.AssetsNotBuilt }
                : pack;

        var hasPackage = Has("package.json");
        if (hasPackage && NodeStartRule.CanStart(contextPath)) return Node(contextPath, port, onNode);
        if (FindCsproj(contextPath) is { } csproj) return BesidePackage(DotNet(csproj, port, marked));
        if (Has("go.mod")) return BesidePackage(Go(port, marked));
        if (HasAny("composer.json", "index.php")) return BesidePackage(Php(contextPath, port, marked));
        if (HasAny("requirements.txt", "pyproject.toml", "Pipfile")) return BesidePackage(Python(contextPath, port, kind, marked, startCommand));
        if (hasPackage)
        {
            return kind == ServiceKind.Web && string.IsNullOrWhiteSpace(startCommand) && NodeStartRule.HasBuildScript(contextPath)
                ? Frontend(contextPath, port, publicVariables)
                : Node(contextPath, port, onNode);
        }

        if (HasAny("index.html", "index.htm")) return Static();
        return null;
    }

    /// <summary>
    /// Why detection found nothing, for the repos that look like a known stack but have no runnable
    /// entry point — so the operator learns what's missing rather than just that nothing was found.
    /// Null when there's nothing more specific to say than "nothing recognised".
    /// </summary>
    public static string? DetectionHint(string contextPath)
    {
        bool Has(string file) => File.Exists(Path.Combine(contextPath, file));
        if (!Has("requirements.txt") && !Has("pyproject.toml") && !Has("Pipfile")) return null;

        if (Has("manage.py"))
            return "found manage.py, which looks like Django — this buildpack does not auto-detect " +
                   "Django. Add a Dockerfile.";

        return "looks like a Python project (requirements.txt/pyproject.toml/Pipfile) but none of " +
               $"its recognised entry files were found ({string.Join(", ", PythonEntryCandidates)}). " +
               "Add a Dockerfile, or add one of those files.";
    }

    // --- Build variables ---

    /// <summary>
    /// The <c>ARG</c> lines a generated image declares, every one of them from here. A build arg only
    /// reaches a build if the Dockerfile declares it, so <c>AvailableAtBuild</c> used to do nothing on
    /// any generated image, with no error to say so. Nothing to declare gives an empty string, which is
    /// what keeps those images byte for byte what they were.
    ///
    /// <para>
    /// <c>ARG</c> and never <c>ENV</c>, so nothing lands in the running image's environment by this
    /// route. But an <c>ARG</c>'s value stays readable in <c>docker history</c>, on the final image of a
    /// single-stage build, which is what ticking <c>AvailableAtBuild</c> means. A hand-written Dockerfile
    /// never comes through here: it declares its own.
    /// </para>
    /// </summary>
    private static string ArgLines(string[] names) => string.Concat(names.Select(name => $"ARG {name}\n"));

    /// <summary>In a stable order, since it decides the layer cache, and only what an <c>ARG</c> line can legally hold.</summary>
    private static string[] Declarable(params IEnumerable<string>?[] names) =>
        names.SelectMany(n => n ?? []).Where(IsArgName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static bool IsArgName(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string[]? Declared(string[] names) => names.Length > 0 ? names : null;

    // --- Node ---

    /// <param name="OptionalBuild">For an app that can start without it, so a failing build does not stop the deploy.</param>
    /// <param name="Build">The bare command, for an image that is nothing without its output.</param>
    private readonly record struct NodeTooling(string Install, string OptionalBuild, string Build, string Start);

    private static NodeTooling ToolingFor(string ctx) =>
        File.Exists(Path.Combine(ctx, "pnpm-lock.yaml"))
            ? new("RUN corepack enable && pnpm install", "RUN pnpm run build || true", "pnpm run build", "pnpm start")
        : File.Exists(Path.Combine(ctx, "yarn.lock"))
            ? new("RUN corepack enable && yarn install --frozen-lockfile || yarn install", "RUN yarn build || true", "yarn build", "yarn start")
            : new("RUN npm install", "RUN npm run build --if-present", "npm run build", "npm start");

    // The ARGs come before the install, not just the build: a marked NPM_TOKEN is no use to a private
    // package any later. The price is a reinstall when a value changes.
    private static BuildpackResult Node(string ctx, int port, string[] args)
    {
        var tooling = ToolingFor(ctx);
        var df =
            $"""
            # Auto-generated by Harbora (Node buildpack)
            FROM node:20-alpine
            WORKDIR /app
            COPY . .
            {ArgLines(args)}{tooling.Install}
            {tooling.OptionalBuild}
            ENV NODE_ENV=production
            ENV PORT={port}
            EXPOSE {port}
            CMD ["sh", "-c", "{tooling.Start}"]
            """;
        return new BuildpackResult(df, "Node.js", BuildArgNames: Declared(args));
    }

    // --- Frontend ---

    /// <summary>
    /// A <c>package.json</c> with a <c>build</c> script and nothing to start: a Vite-style frontend. It
    /// is built, and what it produces is served.
    ///
    /// <para>
    /// The output is looked for on disk in the image, after the build, rather than guessed from
    /// <c>package.json</c>: a dependency says which tool is present, and a config file can send the
    /// output anywhere. Nothing with an <c>index.html</c> means this was not a site at all, which most
    /// often means a compiled backend with no start script. That has to stop the deployment with a
    /// sentence saying so, because the alternative is an nginx serving an empty folder and a deployment
    /// reported as a success.
    /// </para>
    ///
    /// <para>
    /// The <c>ARG</c>s come after the install, so that changing a value does not reinstall everything,
    /// and only in the build stage, so that the image that ships has nothing of them but what the
    /// framework itself wrote into the site. Public names only, marked or not, and the example files
    /// add to them: a name with no value yet does no harm, and this image cannot be rebuilt later just
    /// because one turned up.
    /// </para>
    /// </summary>
    private static BuildpackResult Frontend(string ctx, int port, IEnumerable<string>? publicVariables)
    {
        var tooling = ToolingFor(ctx);
        var args = Declarable(
            PublicBuildVariables.NamedInExampleFiles(ctx),
            publicVariables?.Where(PublicBuildVariables.IsPublic));

        var df =
            $$"""
            # Auto-generated by Harbora (frontend buildpack)
            FROM node:20-alpine AS build
            WORKDIR /app
            COPY . .
            {{tooling.Install}}
            {{ArgLines(args)}}RUN {{tooling.Build}}
            {{FrontendOutputStep}}

            FROM nginx:alpine
            RUN rm -rf /usr/share/nginx/html/*
            COPY --from=build /app/.harbora-site/ /usr/share/nginx/html/
            RUN printf '%s\n' \
                'server {' \
                '    listen {{port}};' \
                '    root /usr/share/nginx/html;' \
                '    index index.html;' \
                '    autoindex off;' \
                '    location / {' \
                '        try_files $uri $uri/ /index.html;' \
                '    }' \
                '}' > /etc/nginx/conf.d/default.conf
            EXPOSE {{port}}
            """;

        // This text's line endings are the source file's, and a checkout on Windows has CRLF. A shell
        // step is the one place that could matter, so this image does not depend on it.
        return new BuildpackResult(df.ReplaceLineEndings("\n"), FrontendStack, tooling.Build, BuildArgNames: Declared(args));
    }

    /// <summary>
    /// Where the build put the site: <c>dist</c> (Vite), <c>build</c> (Create React App, SvelteKit),
    /// <c>out</c> (a Next export), <c>.output/public</c> (Nuxt), then a shallow search under <c>dist</c>
    /// and <c>build</c>, where Angular nests it under the project's name. Last, <c>public</c>, which is
    /// Gatsby's output and only that: everywhere else that folder holds the source's own
    /// <c>index.html</c>, and serving that would be a blank page reported as a success, so it needs a
    /// <c>gatsby-config</c> beside it. Plain sh and busybox, since this runs on Alpine, and a test runs
    /// this exact text under a real shell.
    /// </summary>
    private const string FrontendOutputStep =
        """
        RUN found=""; \
            for dir in dist build out .output/public; do \
              if [ -f "$dir/index.html" ]; then found="$dir"; break; fi; \
            done; \
            if [ -z "$found" ]; then \
              index="$(find dist build -mindepth 2 -maxdepth 4 -type f -name index.html 2>/dev/null | sort | head -n 1)"; \
              if [ -n "$index" ]; then found="$(dirname "$index")"; fi; \
            fi; \
            if [ -z "$found" ] && [ -f public/index.html ] && ls gatsby-config.* >/dev/null 2>&1; then found="public"; fi; \
            if [ -z "$found" ]; then \
              echo "The build finished without producing an index.html, so this is not a site. It needs a start script in package.json, or a Dockerfile." >&2; \
              exit 1; \
            fi; \
            echo "Serving the site the build wrote to $found/"; \
            mkdir .harbora-site && cp -R "$found/." .harbora-site/
        """;

    // --- .NET ---
    private static BuildpackResult DotNet(string csprojRel, int port, string[] args)
    {
        var assembly = Path.GetFileNameWithoutExtension(csprojRel);
        var df =
            $"""
            # Auto-generated by Harbora (.NET buildpack)
            FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
            WORKDIR /src
            COPY . .
            {ArgLines(args)}RUN dotnet publish "{csprojRel.Replace('\\', '/')}" -c Release -o /app

            FROM mcr.microsoft.com/dotnet/aspnet:9.0
            WORKDIR /app
            COPY --from=build /app ./
            ENV ASPNETCORE_URLS=http://+:{port}
            EXPOSE {port}
            ENTRYPOINT ["dotnet", "{assembly}.dll"]
            """;
        return new BuildpackResult(df, ".NET", BuildArgNames: Declared(args));
    }

    // --- Go ---
    private static BuildpackResult Go(int port, string[] args)
    {
        var df =
            $"""
            # Auto-generated by Harbora (Go buildpack)
            FROM golang:1.22-alpine AS build
            WORKDIR /src
            {ArgLines(args)}COPY go.* ./
            RUN go mod download || true
            COPY . .
            RUN CGO_ENABLED=0 go build -o /app/server .

            FROM alpine:3.20
            RUN apk add --no-cache ca-certificates
            COPY --from=build /app/server /server
            ENV PORT={port}
            EXPOSE {port}
            CMD ["/server"]
            """;
        return new BuildpackResult(df, "Go", BuildArgNames: Declared(args));
    }

    // --- PHP ---
    private static BuildpackResult Php(string ctx, int port, string[] args)
    {
        var composer = File.Exists(Path.Combine(ctx, "composer.json"))
            ? "RUN if [ -f composer.json ]; then curl -sS https://getcomposer.org/installer | php -- --install-dir=/usr/local/bin --filename=composer && composer install --no-dev --no-interaction || true; fi"
            : "";
        var df =
            $"""
            # Auto-generated by Harbora (PHP buildpack)
            FROM php:8.3-apache
            RUN a2enmod rewrite
            # Serve on the app's port instead of 80.
            RUN sed -ri "s/Listen 80/Listen {port}/" /etc/apache2/ports.conf \
             && sed -ri "s/:80>/:{port}>/" /etc/apache2/sites-enabled/000-default.conf
            WORKDIR /var/www/html
            COPY . /var/www/html/
            {ArgLines(args)}{composer}
            EXPOSE {port}
            """;
        return new BuildpackResult(df, "PHP", BuildArgNames: Declared(args));
    }

    // --- Python ---
    private static BuildpackResult? Python(string ctx, int port, ServiceKind kind, string[] args, string? startCommand)
    {
        var entry = PythonEntryCandidates.FirstOrDefault(f => File.Exists(Path.Combine(ctx, f)));

        // No guessing, because an image that can't start is worse than none. A start command is not a
        // guess, though: it is what the container runs in place of the image's CMD, so `worker: python
        // worker.py` in a Procfile, the commonest way a worker says what it is, needs no entry file.
        var command = string.IsNullOrWhiteSpace(startCommand) ? null : startCommand.Trim();
        if (entry is null && command is null) return null;

        var install = File.Exists(Path.Combine(ctx, "requirements.txt"))
            ? "RUN pip install --no-cache-dir -r requirements.txt"
            : "RUN pip install --no-cache-dir . || true";

        var lines = new List<string>
        {
            "# Auto-generated by Harbora (Python buildpack)",
            "FROM python:3.12-slim",
            "WORKDIR /app",
            "COPY . .",
            ArgLines(args) + install,
            // Python buffers stdout when it isn't a TTY. Without this, a working bot's print()
            // sits in the buffer and the panel's log view stays empty forever.
            "ENV PYTHONUNBUFFERED=1",
        };

        // A worker/cron/release task has nothing listening on a port — claiming one would be a
        // claim nobody can act on. ServicePlan.HasHttpHealthCheck already answers "does this kind
        // serve HTTP" (true for Private too: it listens, just not publicly), so reuse it instead of
        // a second list of kinds.
        if (ServicePlan.HasHttpHealthCheck(kind))
        {
            lines.Add($"ENV PORT={port}");
            lines.Add($"EXPOSE {port}");
        }

        if (entry is not null) lines.Add($"CMD [\"python\", \"{entry}\"]");

        return new BuildpackResult(string.Join('\n', lines), "Python", entry ?? command, BuildArgNames: Declared(args));
    }

    /// <summary>Public entry for a forced static-site (Nginx) build, used by StaticSite deploys.</summary>
    public static BuildpackResult ForStaticSite() => Static();

    // --- Static site ---
    private static BuildpackResult Static()
    {
        var df =
            """
            # Auto-generated by Harbora (static buildpack)
            FROM nginx:alpine
            COPY . /usr/share/nginx/html
            EXPOSE 80
            """;
        return new BuildpackResult(df, "Static (Nginx)");
    }

    /// <summary>First .csproj (skipping bin/obj), preferring a web/api/server-looking project.</summary>
    private static string? FindCsproj(string contextPath)
    {
        var projects = Directory.EnumerateFiles(contextPath, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(p => Path.GetRelativePath(contextPath, p))
            .ToList();
        if (projects.Count == 0) return null;
        return projects.FirstOrDefault(p =>
                   p.Contains("web", StringComparison.OrdinalIgnoreCase) ||
                   p.Contains("api", StringComparison.OrdinalIgnoreCase) ||
                   p.Contains("server", StringComparison.OrdinalIgnoreCase))
               ?? projects[0];
    }
}

/// <summary>
/// <paramref name="Entry"/> is the chosen entry point / start command when the stack has one worth
/// naming (currently just Python's <c>CMD</c> file) — null for stacks where "the stack" already is
/// the answer. Lets the pipeline log which guess it made instead of just which stack it picked.
/// <paramref name="Warning"/> is what this build will leave undone without failing, for the deploy log.
/// <paramref name="BuildArgNames"/> is what this image declares and so the only build args worth
/// passing it, or null for none. Each image's own rule for which variables those are lives in
/// <see cref="Buildpacks.Detect"/>.
/// </summary>
public readonly record struct BuildpackResult(
    string Dockerfile, string Stack, string? Entry = null, string? Warning = null, string[]? BuildArgNames = null);
