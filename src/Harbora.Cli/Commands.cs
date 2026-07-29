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
            AnsiConsole.MarkupLine($"[red]Login failed:[/] {ex.Message}");
            return 1;
        }
    }
}

public sealed class WhoAmICommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var me = await Session.Require().GetAsync("whoami");
        AnsiConsole.MarkupLine($"[bold]{me.GetProperty("email").GetString()}[/]");
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

        [CommandOption("--push"), Description("Upload this folder's code instead of letting the server pull from Git")]
        public bool Push { get; init; }

        [CommandOption("--path <PATH>"), Description("Folder to push (default: current directory)")]
        public string? Path { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var api = Session.Require();
        var slug = settings.App ?? HarboraConfig.ReadProjectSlug();
        if (string.IsNullOrWhiteSpace(slug))
        {
            AnsiConsole.MarkupLine("[red]No app specified[/] and no ./harbora.yml found.");
            return 1;
        }

        var deploymentId = settings.Push || ShouldPush(settings, slug)
            ? await PushAsync(api, slug, settings, ct)
            : await TriggerAsync(api, slug, settings);

        if (deploymentId is null) return 1;
        AnsiConsole.MarkupLine($"[green]✓[/] Queued deployment [bold]{deploymentId}[/] for [bold]{slug}[/].");

        return settings.Follow ? await StreamLogs(api, deploymentId) : 0;
    }

    /// <summary>
    /// Push when the folder looks like a project the user means to deploy and they didn't ask for a
    /// specific Git ref. Asking for a branch or tag is an unambiguous "deploy from Git".
    /// </summary>
    private static bool ShouldPush(Settings settings, string slug)
    {
        if (settings.Ref is not null || settings.Tag is not null) return false;

        var dir = settings.Path ?? Directory.GetCurrentDirectory();
        // No git remote here → the server has nothing to pull, so pushing is the only thing that works.
        return !Directory.Exists(System.IO.Path.Combine(dir, ".git"));
    }

    private static async Task<string?> TriggerAsync(ApiClient api, string slug, Settings settings)
    {
        var gitRef = settings.Tag ?? settings.Ref;
        var res = await api.PostAsync($"apps/{slug}/deploy", new { gitRef });
        return res.GetProperty("deploymentId").GetString();
    }

    /// <summary>Packs the folder and streams it to the server, CapRover-style.</summary>
    private static async Task<string?> PushAsync(ApiClient api, string slug, Settings settings, CancellationToken ct)
    {
        var dir = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(dir))
        {
            AnsiConsole.MarkupLine($"[red]Folder not found:[/] {dir}");
            return null;
        }

        var packed = await SourcePacker.PackAsync(dir, ct);

        try
        {
            AnsiConsole.MarkupLine(
                $"[grey]Packed[/] {packed.Files} files ({packed.Bytes / 1024.0 / 1024:0.#} MB) [grey]from[/] {dir}");
            AnsiConsole.MarkupLine("[grey]Uploading…[/]");

            var res = await api.PostFileAsync($"apps/{slug}/deploy/archive", packed.ArchivePath);
            return res.GetProperty("deploymentId").GetString();
        }
        finally
        {
            try { File.Delete(packed.ArchivePath); } catch { /* temp file */ }
        }
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
                AnsiConsole.MarkupLine($"[{color}]● {status}[/]");
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
            # Harbora project config. Edit as needed, then run:  harbora deploy
            app: {slug}

            build:
              dockerfile: {(hasDockerfile ? "Dockerfile" : "Dockerfile   # add a Dockerfile to this repo, or deploy a prebuilt image from the UI")}
              context: .

            # Environment variables (or set them in the app's page / with `harbora env`):
            # env:
            #   NODE_ENV: production

            # Domains to route to this app (attach + get SSL automatically):
            # domains:
            #   - {slug}.example.com

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
