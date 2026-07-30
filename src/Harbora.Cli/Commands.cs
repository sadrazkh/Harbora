using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Harbora.Cli;

internal static class Session
{
    /// <summary>The account to act as, asking when several are signed in and nobody said which.</summary>
    public static Profile RequireProfile(string? account = null)
    {
        var cfg = HarboraConfig.Load();
        var profile = Interactive.ChooseAccount(cfg, account);
        if (profile is null)
        {
            AnsiConsole.MarkupLine("[red]Not logged in.[/] Run [yellow]harbora login[/] first.");
            throw new InvalidOperationException("Not authenticated.");
        }
        return profile;
    }

    public static ApiClient Require(string? account = null) => new(RequireProfile(account));
}

public sealed class LoginCommand : AsyncCommand<LoginCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-s|--server <URL>"), Description("Harbora server URL, e.g. https://panel.example.com")]
        public string? Server { get; init; }

        [CommandOption("-t|--token <TOKEN>"), Description("API token created in Settings → API Tokens")]
        public string? Token { get; init; }

        [CommandOption("-e|--email <EMAIL>"), Description("Sign in with your panel account instead of a token")]
        public string? Email { get; init; }

        [CommandOption("-p|--password <PASSWORD>"), Description("Password for --email (prompted if omitted)")]
        public string? Password { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var server = settings.Server
                     ?? (Interactive.IsAvailable ? AnsiConsole.Ask<string>("Server URL:") : null);
        if (string.IsNullOrWhiteSpace(server))
        {
            AnsiConsole.MarkupLine("[red]No server given.[/] Use [yellow]--server[/].");
            return 1;
        }

        // Email and password are the way in that needs nothing prepared in advance; a token still
        // works, and is what CI should use.
        var useEmail = settings.Email is not null
                       || (settings.Token is null && Interactive.IsAvailable && AnsiConsole.Prompt(
                           new SelectionPrompt<string>()
                               .Title("How do you want to sign in?")
                               .AddChoices("Email and password", "API token")) == "Email and password");

        try
        {
            var (token, who) = useEmail
                ? await SignInWithPasswordAsync(server, settings, ct)
                : (settings.Token ?? Secret("API token:"), null);

            if (string.IsNullOrWhiteSpace(token))
            {
                AnsiConsole.MarkupLine("[red]No credentials given.[/]");
                return 1;
            }

            var api = new ApiClient(server, token);
            var me = await api.GetAsync("whoami");
            var email = who ?? me.GetProperty("email").GetString() ?? server;

            var cfg = HarboraConfig.Load();
            cfg.Upsert(email, server, token);
            cfg.Save();

            AnsiConsole.MarkupLine($"[green]✓[/] Signed in as [bold]{Markup.Escape(email)}[/] on [grey]{Markup.Escape(server)}[/].");
            if (cfg.NeedsAccountChoice)
                AnsiConsole.MarkupLine($"[grey]{cfg.Profiles.Count} accounts signed in — deploy will ask which one, or pass --account.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Login failed:[/] {Markup.Escape(Clean(ex.Message))}");
            return 1;
        }
    }

    /// <summary>Exchanges the panel account for a CLI token, so nothing has to be created by hand first.</summary>
    private static async Task<(string Token, string Email)> SignInWithPasswordAsync(
        string server, Settings settings, CancellationToken ct)
    {
        var email = settings.Email ?? AnsiConsole.Ask<string>("Email:");
        var password = settings.Password ?? Secret("Password:");
        var label = $"harbora CLI on {Environment.MachineName}";

        var res = await new ApiClient(server, null)
            .PostAsync("auth/token", new { email, password, name = label });

        return (res.GetProperty("token").GetString()!, res.GetProperty("email").GetString() ?? email);
    }

    private static string Secret(string prompt) =>
        Interactive.IsAvailable ? AnsiConsole.Prompt(new TextPrompt<string>(prompt).Secret()) : "";

    /// <summary>Turns the raw HTTP error into the sentence the server meant to say.</summary>
    private static string Clean(string message)
    {
        var marker = message.IndexOf("{\"error\":\"", StringComparison.Ordinal);
        if (marker < 0) return message;
        var start = marker + 10;
        var end = message.IndexOf('"', start);
        return end > start ? message[start..end] : message;
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

        [CommandOption("--account <EMAIL>"), Description("Which signed-in account to use, when several are")]
        public string? Account { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var dir = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        var config = ProjectConfig.Load(dir);

        // --server/--token make a deploy self-contained, so CI needs no interactive login step.
        ApiClient api;
        if (settings.Server is not null || settings.Token is not null)
        {
            var stored = HarboraConfig.Load().Resolve(settings.Account);
            var server = settings.Server ?? config.Server ?? stored?.Server;
            if (string.IsNullOrWhiteSpace(server))
            {
                AnsiConsole.MarkupLine("[red]No server.[/] Pass [yellow]--server[/] with [yellow]--token[/].");
                return 1;
            }
            api = new ApiClient(server, settings.Token ?? stored?.Token);
        }
        else api = new ApiClient(Session.RequireProfile(settings.Account));

        var slug = settings.App ?? config.App ?? HarboraConfig.ReadProjectSlug();

        // What the server knows about this account's apps. Fetched even when the slug is already
        // known, because whether the app has a Git remote decides how the code has to get there —
        // and guessing that from the local folder is what made a deploy fail with nothing uploaded.
        IReadOnlyList<RemoteApp> apps;
        try { apps = await Interactive.ListAppsAsync(api); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not reach the server:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }


        if (string.IsNullOrWhiteSpace(slug))
        {
            // Nothing said which app. Everything needed to answer is already here, so ask instead of
            // failing with "App not found." — which named neither the problem nor the way out.
            if (!Interactive.IsAvailable)
            {
                AnsiConsole.MarkupLine("[red]No app specified[/] — pass one, or add [yellow]app:[/] to harbora.yml.");
                if (apps.Count > 0)
                    AnsiConsole.MarkupLine($"[grey]Available:[/] {Markup.Escape(string.Join(", ", apps.Select(a => a.Slug)))}");
                return 1;
            }

            var picked = Interactive.ChooseApp(apps);
            if (picked is null) return 1;
            slug = picked.Slug;

        }

        var app = apps.FirstOrDefault(a => a.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
        if (app is null)
        {
            AnsiConsole.MarkupLine($"[red]No app called[/] [bold]{Markup.Escape(slug!)}[/] [red]on this account.[/]");
            if (apps.Count > 0)
                AnsiConsole.MarkupLine($"[grey]Available:[/] {Markup.Escape(string.Join(", ", apps.Select(a => a.Slug)))}");
            return 1;
        }

        var plan = DeployPlan.Decide(
            settings.Image, settings.Tar, settings.Branch, settings.Tag ?? settings.Ref,
            settings.Push, config, Directory.Exists(System.IO.Path.Combine(dir, ".git")),
            serverCanPull: app.CanServerPull);

        // Save the answer so this folder never has to be asked — or told — again. Whether the app was
        // picked from a list or typed once on the command line, repeating it is work the CLI can do.
        // RememberApp never overwrites: a project that already has a config has already decided.
        if (Interactive.RememberApp(dir, slug!, api.Server))
            AnsiConsole.MarkupLine(
                $"[grey]Wrote {ProjectConfig.DefaultFileName} — next time just run[/] harbora deploy");
        

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

/// <summary>Shows which accounts are signed in, and switches between them.</summary>
public sealed class AccountsCommand : AsyncCommand<AccountsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[account]"), Description("Switch to this account (email)")]
        public string? Account { get; init; }

        [CommandOption("--logout <EMAIL>"), Description("Forget an account's token")]
        public string? Logout { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var cfg = HarboraConfig.Load();

        if (settings.Logout is not null)
        {
            var gone = cfg.Resolve(settings.Logout);
            if (gone is null)
            {
                AnsiConsole.MarkupLine($"[yellow]![/] No account matching [bold]{Markup.Escape(settings.Logout)}[/].");
                return Task.FromResult(1);
            }
            cfg.Remove(gone);
            cfg.Save();
            AnsiConsole.MarkupLine($"[green]✓[/] Signed out of [bold]{Markup.Escape(gone.Name)}[/].");
            return Task.FromResult(0);
        }

        if (settings.Account is not null)
        {
            var next = cfg.Resolve(settings.Account);
            if (next is null)
            {
                AnsiConsole.MarkupLine($"[yellow]![/] Not signed in as [bold]{Markup.Escape(settings.Account)}[/].");
                return Task.FromResult(1);
            }
            cfg.Current = HarboraConfig.Key(next);
            cfg.Save();
            AnsiConsole.MarkupLine($"[green]✓[/] Now using [bold]{Markup.Escape(next.Name)}[/].");
            return Task.FromResult(0);
        }

        if (!cfg.HasAny)
        {
            AnsiConsole.MarkupLine("[yellow]No accounts.[/] Run [yellow]harbora login[/].");
            return Task.FromResult(1);
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("", "Account", "Server");
        foreach (var p in cfg.Profiles)
            table.AddRow(HarboraConfig.Key(p) == cfg.Current ? "[green]*[/]" : " ",
                Markup.Escape(p.Name), Markup.Escape(p.Server));
        AnsiConsole.Write(table);
        return Task.FromResult(0);
    }
}
