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

/// <summary>
/// The sentence the server meant to say, dug out of the raw HTTP failure.
///
/// <c>HttpRequestException</c> carries the status line and the whole body, which reads as noise to
/// somebody who only wants to know why their command was refused — and the body already contains a
/// written explanation, put there for exactly this moment.
/// </summary>
internal static class ServerError
{
    public static string Message(string raw)
    {
        var marker = raw.IndexOf("{\"error\":\"", StringComparison.Ordinal);
        if (marker < 0) return raw;
        var start = marker + 10;
        var end = raw.IndexOf('"', start);
        return end > start ? raw[start..end] : raw;
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
    private static string Clean(string message) => ServerError.Message(message);
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

/// <summary>
/// The checks a deploy is going to need anyway, run before anything is uploaded — <c>harbora
/// doctor</c>, and the same checks <c>harbora deploy</c> now runs on itself.
///
/// Built after DriveUnion's deploy failed twice for a reason the owner could not have diagnosed:
/// 130 of 345 files were silently dropped from the upload, the build was reported healthy while
/// failing inside the image, and the eventual error named the wrong thing. All three are fixed
/// elsewhere; this is the preflight that would have caught the underlying cause — a file the build
/// needs, sitting where the packer (or the project's own ignore file) treats it as build output —
/// before a single byte went to the server.
/// </summary>
public sealed class DoctorCommand : AsyncCommand<DoctorCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--path <PATH>"), Description("Project folder to check (default: current directory)")]
        public string? Path { get; init; }

        [CommandOption("--server <URL>"), Description("Check auth against this server instead of harbora.yml's")]
        public string? Server { get; init; }

        [CommandOption("--token <TOKEN>"), Description("Check this token instead of a stored login")]
        public string? Token { get; init; }

        [CommandOption("--account <EMAIL>"), Description("Which signed-in account to check, when several are")]
        public string? Account { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var dir = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        var config = ProjectConfig.Load(dir);

        var checks = await RunAsync(dir, config, settings.Server, settings.Token, settings.Account, ct);
        Print(checks);

        return checks.Any(c => c.Level == Doctor.Level.Fail) ? 1 : 0;
    }

    /// <summary>
    /// The full report: manifest, build, upload, and — only here, not in <c>deploy</c>'s automatic
    /// preflight, which already goes through its own login flow — whether the session for this
    /// server is still good.
    /// </summary>
    public static async Task<List<Doctor.Check>> RunAsync(
        string dir, ProjectConfig config, string? server, string? token, string? account, CancellationToken ct)
    {
        var checks = new List<Doctor.Check> { Doctor.CheckManifest(config, config.App) };

        var (buildChecks, referenced) = Doctor.CheckBuild(dir, config);
        checks.AddRange(buildChecks);

        if (string.IsNullOrWhiteSpace(config.Image))
            checks.AddRange(await Doctor.CheckUploadAsync(dir, config, referenced, ct));

        var resolvedServer = server ?? config.Server;
        ApiClient? api = null;
        if (!string.IsNullOrWhiteSpace(resolvedServer) && !string.IsNullOrWhiteSpace(token))
        {
            api = new ApiClient(resolvedServer, token);
        }
        else
        {
            var stored = HarboraConfig.Load().Resolve(account);
            if (stored is not null && (resolvedServer is null ||
                string.Equals(stored.Server, resolvedServer, StringComparison.OrdinalIgnoreCase)))
            {
                api = new ApiClient(stored);
                resolvedServer ??= stored.Server;
            }
        }
        checks.Add(await Doctor.CheckAuthAsync(api, resolvedServer, ct));

        return checks;
    }

    private static void Print(IReadOnlyList<Doctor.Check> checks)
    {
        foreach (var c in checks)
        {
            var (icon, color) = c.Level switch
            {
                Doctor.Level.Ok => ("✓", "green"),
                Doctor.Level.Warn => ("!", "yellow"),
                _ => ("✗", "red")
            };
            AnsiConsole.MarkupLine($"[{color}]{icon}[/] [bold]{Markup.Escape(c.Name)}[/] — {Markup.Escape(c.Detail)}");
        }

        var fails = checks.Count(c => c.Level == Doctor.Level.Fail);
        var warns = checks.Count(c => c.Level == Doctor.Level.Warn);
        if (fails > 0)
            AnsiConsole.MarkupLine($"[red]{fails} problem(s) would stop this deploy.[/]");
        else if (warns > 0)
            AnsiConsole.MarkupLine($"[yellow]{warns} warning(s) — deploy would likely still work.[/]");
        else
            AnsiConsole.MarkupLine("[green]No configuration problems found.[/]");
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

        [CommandOption("-y|--yes"), Description("Don't ask which app — use the one already configured")]
        public bool Yes { get; init; }

        [CommandOption("--path <PATH>"), Description("Folder to deploy (default: current directory)")]
        public string? Path { get; init; }

        [CommandOption("--verbose"), Description("With --push, list every file the upload excluded and why")]
        public bool Verbose { get; init; }

        [CommandOption("-i|--image <IMAGE>"), Description("Release an existing image, e.g. nginx:alpine (builds nothing)")]
        public string? Image { get; init; }

        [CommandOption("--skip-doctor"), Description("Skip the harbora doctor preflight checks and deploy anyway")]
        public bool SkipDoctor { get; init; }

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

        if (await PreflightAsync(dir, config, settings.SkipDoctor, ct) is { } preflightExit)
            return preflightExit;

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


        // A name that does not exist is the same situation as no name at all — the list is already
        // in hand, so offer it rather than making someone go and look the slug up.
        var choice = AppChoice.Resolve(slug, apps, Interactive.IsAvailable, settings.Yes);
        if (choice.Problem is not null)
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(choice.Problem)}");

        var app = choice.NeedsPrompt ? Interactive.ChooseApp(apps, choice.Current) : choice.Current;
        if (app is null)
        {
            if (apps.Count > 0)
                AnsiConsole.MarkupLine(
                    $"[grey]Available:[/] {Markup.Escape(string.Join(", ", apps.Select(a => a.Slug)))}");
            return 1;
        }

        // From here the app is the only thing that names itself. The string the user typed matched an
        // app case-insensitively; the server compares ordinally, so sending it back is a 404 — and one
        // that arrives mid-upload, where it reads as a broken stream.
        slug = app.Slug;

        var plan = DeployPlan.Decide(
            settings.Image, settings.Tar, settings.Branch, settings.Tag ?? settings.Ref,
            settings.Push, config, Directory.Exists(System.IO.Path.Combine(dir, ".git")),
            serverCanPull: app.CanServerPull);

        // Save the answer so this folder never has to be asked — or told — again. Whether the app was
        // picked from a list or typed once on the command line, repeating it is work the CLI can do.
        // RememberApp never overwrites: a project that already has a config has already decided. When
        // it does have one and the choice differs, ask rather than leaving behind a name the server
        // does not answer to — that is how one typo became permanent.
        if (Interactive.RememberApp(dir, slug, api.Server))
            AnsiConsole.MarkupLine(
                $"[grey]Wrote {ProjectConfig.DefaultFileName} — next time just run[/] harbora deploy");
        else if (!settings.Yes)
            Interactive.OfferSlugUpdate(dir, slug);


        AnsiConsole.MarkupLine($"[grey]Mode:[/] {plan.Mode} [grey]({plan.Reason})[/]");

        string? deploymentId;
        try
        {
            deploymentId = plan.Mode switch
            {
                DeployMode.Image        => await DeployImageAsync(api, app, plan.Value!),
                DeployMode.PushTarball  => await UploadAsync(api, app, plan.Value!, deleteAfter: false),
                DeployMode.PushGitBranch => await PushBranchAsync(api, app, dir, plan.Value!, config, ct),
                DeployMode.PushFolder   => await PushFolderAsync(api, app, dir, config, settings.Verbose, ct),
                _                       => await TriggerAsync(api, app, plan.Value)
            };
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (deploymentId is null) return 1;
        AnsiConsole.MarkupLine($"[green]✓[/] Queued deployment [bold]{deploymentId}[/] for [bold]{slug}[/].");
        await VersionNotice.MaybeWarnAsync(api);

        return settings.Follow && !settings.NoFollow ? await StreamLogs(api, deploymentId, ct) : 0;
    }

    /// <summary>
    /// Runs <see cref="Doctor"/>'s local, no-network checks before anything is uploaded, and turns a
    /// Fail-level result into an early exit — this is the preflight the DriveUnion incident exists to
    /// justify. Returns null to mean "continue with the deploy"; a non-null value is the exit code to
    /// return immediately, without touching the network at all.
    ///
    /// Deliberately does not repeat <see cref="Doctor.CheckManifest"/> or
    /// <see cref="Doctor.CheckAuthAsync"/> here: this method runs before the app name and the account
    /// are resolved, and <c>deploy</c> already has its own, better error messages for both — a second,
    /// earlier "No app specified" would only be confusing about which one is real.
    /// </summary>
    public static async Task<int?> PreflightAsync(string dir, ProjectConfig config, bool skip, CancellationToken ct)
    {
        if (skip)
        {
            AnsiConsole.MarkupLine("[grey]Skipped the harbora doctor preflight (--skip-doctor).[/]");
            return null;
        }

        var (buildChecks, referenced) = Doctor.CheckBuild(dir, config);
        var checks = new List<Doctor.Check>(buildChecks);
        if (string.IsNullOrWhiteSpace(config.Image))
            checks.AddRange(await Doctor.CheckUploadAsync(dir, config, referenced, ct));

        foreach (var c in checks.Where(c => c.Level != Doctor.Level.Ok))
            AnsiConsole.MarkupLine(
                $"[{(c.Level == Doctor.Level.Fail ? "red" : "yellow")}]{(c.Level == Doctor.Level.Fail ? "✗" : "!")}[/] " +
                $"[bold]{Markup.Escape(c.Name)}[/] — {Markup.Escape(c.Detail)}");

        if (!checks.Any(c => c.Level == Doctor.Level.Fail)) return null;

        AnsiConsole.MarkupLine(
            "[red]harbora doctor found a problem that would break this deploy — stopping before anything is " +
            "uploaded.[/] [grey]Run[/] harbora doctor [grey]for the full report, or pass[/] --skip-doctor " +
            "[grey]to deploy anyway.[/]");
        return 1;
    }

    // Every one of these takes the app rather than a slug. The name a user typed is for finding an
    // app, never for addressing one: `Kousar-kolie` found `kousar-kolie` here and was then sent to a
    // server that compares ordinally. Taking a RemoteApp makes that mistake unspellable.

    private static async Task<string?> TriggerAsync(ApiClient api, RemoteApp app, string? gitRef)
    {
        var res = await api.PostAsync($"apps/{app.Slug}/deploy", new { gitRef });
        return res.GetProperty("deploymentId").GetString();
    }

    private static async Task<string?> DeployImageAsync(ApiClient api, RemoteApp app, string image)
    {
        AnsiConsole.MarkupLine($"[grey]Releasing image[/] {image}");
        var res = await api.PostAsync($"apps/{app.Slug}/deploy", new { image });
        return res.GetProperty("deploymentId").GetString();
    }

    /// <summary>Packs the folder and streams it to the server.</summary>
    private static async Task<string?> PushFolderAsync(
        ApiClient api, RemoteApp app, string dir, ProjectConfig config, bool verbose, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) throw new FileNotFoundException($"Folder not found: {dir}");

        var packed = await SourcePacker.PackAsync(dir, config, ct);
        AnsiConsole.MarkupLine(
            $"[grey]Packed[/] {packed.Files} files ({packed.Bytes / 1024.0 / 1024:0.#} MB) [grey]from[/] {dir}");
        ReportExcluded(packed.Excluded, verbose);
        return await UploadAsync(api, app, packed.ArchivePath, deleteAfter: true);
    }

    /// <summary>
    /// What went missing, and why — never just a smaller file count than the repo has. A push that
    /// silently dropped 130 of a project's 345 files (a real DriveUnion incident: an ordinary source
    /// folder happened to be named "build") gave no sign anything was wrong until the image failed to
    /// build with no useful error. This is always printed, one line per distinct rule that matched, so
    /// "why" is visible without asking; --verbose additionally names every file, for "which".
    /// </summary>
    private static void ReportExcluded(IReadOnlyList<SourcePacker.ExcludedEntry> excluded, bool verbose)
    {
        if (excluded.Count == 0) return;

        var byReason = excluded
            .GroupBy(e => e.Reason, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} by {g.Key}");

        AnsiConsole.MarkupLine(
            $"[yellow]Excluded[/] {excluded.Count} file(s): {Markup.Escape(string.Join(", ", byReason))}" +
            (verbose ? "" : " [grey](--verbose lists every file)[/]"));

        if (!verbose) return;
        foreach (var entry in excluded)
            AnsiConsole.MarkupLine($"  [grey]-[/] {Markup.Escape(entry.Path)} [grey]— {Markup.Escape(entry.Reason)}[/]");
    }

    /// <summary>
    /// Archives a branch's committed content with `git archive` and uploads that. Deliberately not
    /// the working tree: deploying uncommitted edits is how "works on my machine" reaches production.
    /// </summary>
    private static async Task<string?> PushBranchAsync(
        ApiClient api, RemoteApp app, string dir, string branch, ProjectConfig config, CancellationToken ct)
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
        return await UploadAsync(api, app, archive, deleteAfter: true);
    }

    private static async Task<string?> UploadAsync(ApiClient api, RemoteApp app, string archivePath, bool deleteAfter)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException($"Archive not found: {archivePath}");
        try
        {
            AnsiConsole.MarkupLine($"[grey]Uploading[/] {new FileInfo(archivePath).Length / 1024.0 / 1024:0.#} MB…");
            var res = await api.PostFileAsync($"apps/{app.Slug}/deploy/archive", archivePath);
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

    /// <summary>
    /// Follows a deployment to its end, or until the person following it stops.
    ///
    /// <para>
    /// The poll used to be a bare <c>Task.Delay(1500)</c>, which is where this loop spends nearly all
    /// of its time — so Ctrl+C was not observed until the process was killed hard enough to skip
    /// every cleanup the CLI has. The token reaches both the requests and the wait now, and stopping
    /// says what it did and did not do: following a deployment and running one are different things,
    /// and only one of them ends here.
    /// </para>
    ///
    /// <para>Public so the follow loop can be driven against a stand-in for the panel.</para>
    /// </summary>
    public static async Task<int> StreamLogs(ApiClient api, string deploymentId, CancellationToken ct)
    {
        long after = -1;
        var terminal = new[] { "Succeeded", "Failed", "Cancelled", "RolledBack" };
        try
        {
            while (true)
            {
                var lines = await api.GetAsync($"deployments/{deploymentId}/logs?after={after}", ct);
                foreach (var l in lines.EnumerateArray())
                {
                    Console.WriteLine(l.GetProperty("message").GetString());
                    after = l.GetProperty("seq").GetInt64();
                }
                var d = await api.GetAsync($"deployments/{deploymentId}", ct);
                var status = d.GetProperty("status").GetString() ?? "";
                if (terminal.Contains(status))
                {
                    var color = status == "Succeeded" ? "green" : "red";
                    AnsiConsole.MarkupLine($"[{color}]● {Markup.Escape(status)}[/]");
                    return status == "Succeeded" ? 0 : 1;
                }
                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Said out loud, because it is the one thing somebody pressing Ctrl+C here is likely to
            // have wrong: the build carries on without them.
            AnsiConsole.MarkupLine(
                "[grey]Stopped following. The deployment is still running — " +
                $"stop it with[/] harbora cancel {Markup.Escape(deploymentId)}");
            return 1;
        }
    }
}

/// <summary>
/// Stops a deployment that is queued or already building.
///
/// The panel could do neither until now, and the CLI could not either: a deploy pushed at the wrong
/// commit, or one queued behind a twenty-minute build that nobody wants any more, could only be
/// waited out. The engine has always been able to stop one — the row goes to Cancelled through the
/// state machine and the job is interrupted — so this is a way to ask, not a new capability.
/// </summary>
public sealed class CancelCommand : AsyncCommand<CancelCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<deploymentId>"), Description("The deployment to stop, as `harbora deploy` printed it")]
        public string DeploymentId { get; init; } = string.Empty;

        [CommandOption("--account <EMAIL>"), Description("Which signed-in account to use, when several are")]
        public string? Account { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct) =>
        RunAsync(Session.Require(settings.Account), settings.DeploymentId, ct);

    /// <summary>
    /// The command over a client the caller supplies. Public for the same reason
    /// <see cref="DeployCommand.StreamLogs"/> is: this is the part with behaviour in it.
    /// </summary>
    public static async Task<int> RunAsync(ApiClient api, string deploymentId, CancellationToken ct)
    {
        try
        {
            await api.PostAsync($"deployments/{deploymentId}/cancel", null, ct);
            AnsiConsole.MarkupLine(
                $"[green]✓[/] Cancelled deployment [bold]{Markup.Escape(deploymentId)}[/].");
            return 0;
        }
        catch (Exception ex)
        {
            // The server's own sentence. It already knows whether the deployment had finished, was
            // never there, or belongs to somebody else — repeating it beats guessing at it.
            AnsiConsole.MarkupLine($"[red]Could not cancel:[/] {Markup.Escape(ServerError.Message(ex.Message))}");
            return 1;
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
        DeployCommand.StreamLogs(Session.Require(), settings.DeploymentId, ct);
}

public sealed class StatusCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[grey]CLI:[/] {SelfUpdate.CurrentVersion}");
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

/// <summary>
/// Replaces this binary with the newest published one.
///
/// Without it, updating meant re-running an install script people had to go and find — so an install
/// that broke, or simply aged, tended to stay that way. Downloads from the project's GitHub releases,
/// which is where the binaries are published; the panel only says which version it expects.
/// </summary>
public sealed class UpdateCommand : AsyncCommand<UpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--check"), Description("Only report whether a newer version exists")]
        public bool CheckOnly { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var current = SelfUpdate.CurrentVersion;
        AnsiConsole.MarkupLine($"[grey]Installed:[/] {current}");

        var asset = SelfUpdate.AssetNameForThisMachine();
        if (asset is null)
        {
            AnsiConsole.MarkupLine("[yellow]![/] No published binary for this platform. Build from source instead.");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"harbora-cli/{current}");

        JsonElement release;
        try
        {
            var body = await http.GetStringAsync(
                $"https://api.github.com/repos/{SelfUpdate.Repository}/releases/latest", ct);
            release = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not check for updates:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var latest = release.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        if (!SelfUpdate.IsNewer(latest, current))
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Already up to date ([bold]{Markup.Escape(latest ?? current)}[/]).");
            return 0;
        }

        AnsiConsole.MarkupLine($"[yellow]→[/] A newer version is available: [bold]{Markup.Escape(latest!)}[/]");
        if (settings.CheckOnly) return 0;

        var url = FindAsset(release, asset);
        if (url is null)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] Release {Markup.Escape(latest!)} has no [bold]{asset}[/] build.");
            return 1;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            AnsiConsole.MarkupLine("[red]Could not locate this executable to replace it.[/]");
            return 1;
        }

        try
        {
            var staged = executable + ".new";
            await using (var download = await http.GetStreamAsync(url, ct))
            await using (var file = File.Create(staged))
                await download.CopyToAsync(file, ct);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staged,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            // A running executable cannot be overwritten on Windows, but it can be renamed out of the
            // way. The leftover is deleted the next time the CLI starts.
            var retired = SelfUpdate.RetiredPathFor(executable);
            if (File.Exists(retired)) File.Delete(retired);
            File.Move(executable, retired);
            File.Move(staged, executable);

            AnsiConsole.MarkupLine($"[green]✓[/] Updated to [bold]{Markup.Escape(latest!)}[/].");
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            // The usual case: installed to /usr/local/bin by root. Name the command rather than the error.
            AnsiConsole.MarkupLine($"[red]No permission to replace[/] {Markup.Escape(executable)}");
            AnsiConsole.MarkupLine($"[grey]Try:[/] sudo harbora update");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Update failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static string? FindAsset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
            if (a.TryGetProperty("name", out var n) && n.GetString() == name)
                return a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
        return null;
    }
}

/// <summary>
/// Tells the user when their CLI is older than the panel it just talked to.
///
/// A stale CLI does not announce itself: it fails in ways that look like server bugs, or quietly
/// misses whatever the panel learned to do since. The panel already knows its own version, so the
/// check costs one small request — made only after the real work has succeeded, given a short
/// deadline, and never allowed to turn a good command into a bad one.
/// </summary>
internal static class VersionNotice
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static async Task MaybeWarnAsync(ApiClient api)
    {
        try
        {
            using var deadline = new CancellationTokenSource(Timeout);
            var payload = await api.GetAsync("version", deadline.Token);
            var server = payload.TryGetProperty("cli", out var cli) ? cli.GetString() : null;
            var current = SelfUpdate.CurrentVersion;

            if (!SelfUpdate.IsNewer(server, current)) return;

            AnsiConsole.MarkupLine(
                $"[yellow]![/] This CLI is [bold]{Markup.Escape(current)}[/]; the server expects " +
                $"[bold]{Markup.Escape(server!)}[/]. Run [yellow]harbora update[/].");
        }
        catch
        {
            // An older panel has no /version, and a network hiccup is not the user's problem right
            // now. Saying nothing is the correct outcome of a check that could not be made.
        }
    }
}
