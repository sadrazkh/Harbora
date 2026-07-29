using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Harbora.Cli;

internal static class Session
{
    public static ApiClient Require()
    {
        var cfg = HarboraConfig.Load();
        if (string.IsNullOrWhiteSpace(cfg.Server) || string.IsNullOrWhiteSpace(cfg.Token))
        {
            AnsiConsole.MarkupLine("[red]Not logged in.[/] Run [yellow]harbora login[/] first.");
            throw new InvalidOperationException("Not authenticated.");
        }
        return new ApiClient(cfg);
    }
}

public sealed class LoginCommand : AsyncCommand<LoginCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-s|--server <URL>"), Description("Harbora server URL, e.g. https://panel.example.com")]
        public string? Server { get; init; }

        [CommandOption("-t|--token <TOKEN>"), Description("API token created in Settings → API Tokens")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var server = settings.Server ?? AnsiConsole.Ask<string>("Server URL:");
        var token = settings.Token ?? AnsiConsole.Prompt(new TextPrompt<string>("API token:").Secret());

        var cfg = new HarboraConfig { Server = server, Token = token };
        try
        {
            var me = await new ApiClient(cfg).GetAsync("whoami");
            cfg.Save();
            AnsiConsole.MarkupLine($"[green]✓[/] Logged in as [bold]{me.GetProperty("email").GetString()}[/].");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Login failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}

public sealed class WhoAmICommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var me = await Session.Require().GetAsync("whoami");
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(me.GetProperty("email").GetString() ?? "")}[/]");
        return 0;
    }
}

public sealed class AppsCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var apps = await Session.Require().GetAsync("apps");
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Name", "Slug", "Status", "Source");
        foreach (var a in apps.EnumerateArray())
        {
            var status = a.GetProperty("status").GetString() ?? "";
            var color = status == "Running" ? "green" : status is "Failed" or "Crashed" ? "red" : "grey";
            table.AddRow(
                a.GetProperty("name").GetString() ?? "",
                a.GetProperty("slug").GetString() ?? "",
                $"[{color}]{status}[/]",
                a.GetProperty("source").GetString() ?? "");
        }
        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class DeployCommand : AsyncCommand<DeployCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[app]"), Description("App slug (defaults to ./harbora.yml)")]
        public string? App { get; init; }

        [CommandOption("--ref <REF>"), Description("Branch or tag to deploy")]
        public string? Ref { get; init; }

        [CommandOption("--tag <TAG>"), Description("Deploy a specific tag, e.g. v1.0.0")]
        public string? Tag { get; init; }

        [CommandOption("--follow")]
        [DefaultValue(true)]
        public bool Follow { get; init; }

        [CommandOption("--no-follow"), Description("Queue the deployment and return instead of streaming logs (CI)")]
        public bool NoFollow { get; init; }

        [CommandOption("--push"), Description("Upload this folder's code instead of letting the server pull from Git")]
        public bool Push { get; init; }

        [CommandOption("--path <PATH>"), Description("Folder to deploy (default: current directory)")]
        public string? Path { get; init; }

        [CommandOption("-i|--image <IMAGE>"), Description("Release an existing image, e.g. nginx:alpine (builds nothing)")]
        public string? Image { get; init; }

        [CommandOption("-t|--tar <FILE>"), Description("Upload an archive you already built (.tar.gz)")]
        public string? Tar { get; init; }

        [CommandOption("-b|--branch <BRANCH>"), Description("Upload a git branch's committed content (uncommitted changes are excluded)")]
        public string? Branch { get; init; }

        [CommandOption("--server <URL>"), Description("Panel URL — for CI, instead of `harbora login`")]
        public string? Server { get; init; }

        [CommandOption("--token <TOKEN>"), Description("API token — for CI, instead of `harbora login`")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var dir = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        var config = ProjectConfig.Load(dir);

        // --server/--token make a deploy self-contained, so CI needs no interactive login step.
        var api = settings.Server is not null || settings.Token is not null
            ? new ApiClient(new HarboraConfig
              {
                  Server = settings.Server ?? config.Server ?? HarboraConfig.Load().Server,
                  Token = settings.Token ?? HarboraConfig.Load().Token
              })
            : Session.Require();

        var slug = settings.App ?? config.App ?? HarboraConfig.ReadProjectSlug();
        if (string.IsNullOrWhiteSpace(slug))
        {
            AnsiConsole.MarkupLine("[red]No app specified[/] — pass one, or add [yellow]app:[/] to harbora.yml.");
            AnsiConsole.MarkupLine("[grey]Create the file with:[/] harbora init");
            return 1;
        }

        var plan = DeployPlan.Decide(
            settings.Image, settings.Tar, settings.Branch, settings.Tag ?? settings.Ref,
            settings.Push, config, Directory.Exists(System.IO.Path.Combine(dir, ".git")));

        AnsiConsole.MarkupLine($"[grey]Mode:[/] {plan.Mode} [grey]({plan.Reason})[/]");

        string? deploymentId;
        try
        {
            deploymentId = plan.Mode switch
            {
                DeployMode.Image        => await DeployImageAsync(api, slug, plan.Value!),
                DeployMode.PushTarball  => await UploadAsync(api, slug, plan.Value!, deleteAfter: false),
                DeployMode.PushGitBranch => await PushBranchAsync(api, slug, dir, plan.Value!, config, ct),
                DeployMode.PushFolder   => await PushFolderAsync(api, slug, dir, config, ct),
                _                       => await TriggerAsync(api, slug, plan.Value)
            };
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (deploymentId is null) return 1;
        AnsiConsole.MarkupLine($"[green]✓[/] Queued deployment [bold]{deploymentId}[/] for [bold]{slug}[/].");

        return settings.Follow && !settings.NoFollow ? await StreamLogs(api, deploymentId) : 0;
    }

    private static async Task<string?> TriggerAsync(ApiClient api, string slug, string? gitRef)
    {
        var res = await api.PostAsync($"apps/{slug}/deploy", new { gitRef });
        return res.GetProperty("deploymentId").GetString();
    }

    private static async Task<string?> DeployImageAsync(ApiClient api, string slug, string image)
    {
        AnsiConsole.MarkupLine($"[grey]Releasing image[/] {image}");
        var res = await api.PostAsync($"apps/{slug}/deploy", new { image });
        return res.GetProperty("deploymentId").GetString();
    }

    /// <summary>Packs the folder and streams it to the server.</summary>
    private static async Task<string?> PushFolderAsync(
        ApiClient api, string slug, string dir, ProjectConfig config, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) throw new FileNotFoundException($"Folder not found: {dir}");

        var packed = await SourcePacker.PackAsync(dir, config, ct);
        AnsiConsole.MarkupLine(
            $"[grey]Packed[/] {packed.Files} files ({packed.Bytes / 1024.0 / 1024:0.#} MB) [grey]from[/] {dir}");
        return await UploadAsync(api, slug, packed.ArchivePath, deleteAfter: true);
    }

    /// <summary>
    /// Archives a branch's committed content with `git archive` and uploads that. Deliberately not
    /// the working tree: deploying uncommitted edits is how "works on my machine" reaches production.
    /// </summary>
    private static async Task<string?> PushBranchAsync(
        ApiClient api, string slug, string dir, string branch, ProjectConfig config, CancellationToken ct)
    {
        if (!Directory.Exists(System.IO.Path.Combine(dir, ".git")))
            throw new FileNotFoundException($"--branch needs a git repository; {dir} is not one.");

        var archive = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"harbora-{Guid.NewGuid():N}.tar.gz");
        var exit = await RunGitAsync(dir, $"archive --format=tar.gz -o \"{archive}\" {branch}", ct);
        if (exit != 0)
        {
            AnsiConsole.MarkupLine($"[red]git archive failed[/] — does branch [bold]{branch}[/] exist?");
            return null;
        }

        AnsiConsole.MarkupLine($"[grey]Archived committed content of[/] {branch}");
        return await UploadAsync(api, slug, archive, deleteAfter: true);
    }

    private static async Task<string?> UploadAsync(ApiClient api, string slug, string archivePath, bool deleteAfter)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException($"Archive not found: {archivePath}");
        try
        {
            AnsiConsole.MarkupLine($"[grey]Uploading[/] {new FileInfo(archivePath).Length / 1024.0 / 1024:0.#} MB…");
            var res = await api.PostFileAsync($"apps/{slug}/deploy/archive", archivePath);
            return res.GetProperty("deploymentId").GetString();
        }
        finally
        {
            if (deleteAfter) { try { File.Delete(archivePath); } catch { /* temp file */ } }
        }
    }

    private static async Task<int> RunGitAsync(string workingDir, string args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return -1;
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    internal static async Task<int> StreamLogs(ApiClient api, string deploymentId)
    {
        long after = -1;
        var terminal = new[] { "Succeeded", "Failed", "Cancelled", "RolledBack" };
        while (true)
        {
            var lines = await api.GetAsync($"deployments/{deploymentId}/logs?after={after}");
            foreach (var l in lines.EnumerateArray())
            {
                Console.WriteLine(l.GetProperty("message").GetString());
                after = l.GetProperty("seq").GetInt64();
            }
            var d = await api.GetAsync($"deployments/{deploymentId}");
            var status = d.GetProperty("status").GetString() ?? "";
            if (terminal.Contains(status))
            {
                var color = status == "Succeeded" ? "green" : "red";
                AnsiConsole.MarkupLine($"[{color}]● {Markup.Escape(status)}[/]");
                return status == "Succeeded" ? 0 : 1;
            }
            await Task.Delay(1500);
        }
    }
}

public sealed class LogsCommand : AsyncCommand<LogsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<deploymentId>")]
        public string DeploymentId { get; init; } = string.Empty;
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct) =>
        DeployCommand.StreamLogs(Session.Require(), settings.DeploymentId);
}

public sealed class StatusCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var me = await Session.Require().GetAsync("whoami");
        AnsiConsole.MarkupLine($"[green]● online[/]  user: [bold]{me.GetProperty("email").GetString()}[/]");
        return 0;
    }
}

/// <summary>Scaffolds a harbora.yml in the current folder so `harbora deploy` works with no args.</summary>
public sealed class InitCommand : AsyncCommand<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-a|--app <SLUG>"), Description("App slug (defaults to the folder name)")]
        public string? App { get; init; }

        [CommandOption("-f|--force"), Description("Overwrite an existing harbora.yml")]
        public bool Force { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        const string file = "harbora.yml";
        if (File.Exists(file) && !settings.Force)
        {
            AnsiConsole.MarkupLine("[yellow]![/] harbora.yml already exists. Use [yellow]--force[/] to overwrite.");
            return Task.FromResult(1);
        }

        var slug = Slugify(settings.App ?? new DirectoryInfo(Directory.GetCurrentDirectory()).Name);
        var hasDockerfile = File.Exists("Dockerfile");
        var hasCompose = File.Exists("docker-compose.yml") || File.Exists("compose.yaml") || File.Exists("compose.yml");

        var yaml =
            $"""
            # Harbora project config — see docs/cli-deploy.md for the full schema.
            # Deploy with:  harbora deploy
            app: {slug}

            # Panel URL. Omit to use whichever server you last logged into.
            # server: https://panel.example.com

            build:
              # Path to the Dockerfile inside the context. If it doesn't exist, the stack is
              # auto-detected (Node, .NET, Go, PHP, Python, static) and a Dockerfile is generated.
              dockerfile: {(hasDockerfile ? "Dockerfile" : "Dockerfile")}
              context: .

            # Extra paths to keep out of the upload, on top of .dockerignore / .gitignore.
            # ignore:
            #   - coverage
            #   - "*.log"

            # Define the build inline instead of committing a Dockerfile:
            # dockerfileLines:
            #   - FROM node:20-alpine
            #   - WORKDIR /app
            #   - COPY . .
            #   - RUN npm ci --omit=dev
            #   - CMD ["npm", "start"]

            # Release a prebuilt image instead of building anything:
            # image: nginx:alpine

            # Deploy a branch's committed content instead of the working folder:
            # branch: main

            """;

        File.WriteAllText(file, yaml);
        AnsiConsole.MarkupLine($"[green]✓[/] Wrote [bold]harbora.yml[/] (app: [bold]{slug}[/]).");
        if (hasCompose && !hasDockerfile)
            AnsiConsole.MarkupLine("[grey]  Detected docker-compose — set the source to docker-compose when creating the app in the UI.[/]");
        if (!hasDockerfile && !hasCompose)
            AnsiConsole.MarkupLine("[yellow]  No Dockerfile found[/] — add one, or create the app from a prebuilt image/template in the UI.");
        AnsiConsole.MarkupLine("Next:  [yellow]harbora deploy[/]");
        return Task.FromResult(0);
    }

    private static string Slugify(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrWhiteSpace(slug) ? "app" : slug;
    }
}
