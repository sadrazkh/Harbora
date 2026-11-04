using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Harbora.Cli;

/// <summary>The three things <c>harbora launch</c> ever asks a person. A seam, so what it does with the answers can be tested.</summary>
public interface ILaunchPrompts
{
    string ChooseFolder(IReadOnlyList<string> folders);

    /// <summary>A blank answer is an answer: the web form allows it too, and it means the variable is not set.</summary>
    string Ask(string key, bool secret);

    bool Confirm(string question);
}

/// <summary>What one run was told to do. A class rather than a record, because a record prints every property and this carries a token and people's answers.</summary>
public sealed class LaunchRun
{
    public required string Dir { get; init; }
    public string? GitUrl { get; init; }
    public string? GitRef { get; init; }
    public string? GitToken { get; init; }
    public string? Folder { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<string> Set { get; init; } = [];
    public IReadOnlyList<string> NoService { get; init; } = [];
    public bool Yes { get; init; }
    public bool NoFollow { get; init; }

    /// <summary>How often to look while the source is being read.</summary>
    public TimeSpan ReadEvery { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How often to look once it has been confirmed.</summary>
    public TimeSpan FollowEvery { get; init; } = TimeSpan.FromSeconds(2.5);
}

/// <summary>
/// <c>harbora launch</c>: "here is my code, make it work", for somebody in a terminal.
///
/// <para>
/// A client of the API and nothing more. The panel reads the source and proposes; this says what it
/// proposed, asks only what only a person can answer, and watches. Everything it knows about what the
/// code needs came from the server, and <see cref="LaunchPlan"/> holds the parts that can be decided
/// without a terminal or a network.
/// </para>
/// </summary>
public sealed class LaunchCommand : AsyncCommand<LaunchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[path]"), Description("Folder to upload (default: current directory)")]
        public string? Path { get; init; }

        [CommandOption("--git <URL>"), Description("Read this repository instead of uploading a folder")]
        public string? Git { get; init; }

        [CommandOption("--ref <BRANCH>"), Description("With --git: a branch. Blank means its default branch")]
        public string? Ref { get; init; }

        [CommandOption("--git-token <TOKEN>"), Description("With --git: for a private repository. Also read from HARBORA_GIT_TOKEN")]
        public string? GitToken { get; init; }

        [CommandOption("--folder <FOLDER>"), Description("Which app, when a repository holds several")]
        public string? Folder { get; init; }

        [CommandOption("--name <NAME>"), Description("The app's name")]
        public string? Name { get; init; }

        [CommandOption("--set <KEY=VALUE>"), Description("Answer a question up front. Repeatable")]
        public string[]? Set { get; init; }

        [CommandOption("--no-service <TYPE>"), Description("Do not create a service the inspector proposed. Repeatable")]
        public string[]? NoService { get; init; }

        [CommandOption("-y|--yes"), Description("Accept the proposal without asking")]
        public bool Yes { get; init; }

        [CommandOption("--no-follow"), Description("Return as soon as it is confirmed (CI that will look later)")]
        public bool NoFollow { get; init; }

        [CommandOption("--server <URL>"), Description("Panel URL — for CI, instead of `harbora login`")]
        public string? Server { get; init; }

        [CommandOption("--token <TOKEN>"), Description("API token — for CI, instead of `harbora login`")]
        public string? Token { get; init; }

        [CommandOption("--account <EMAIL>"), Description("Which signed-in account to use, when several are")]
        public string? Account { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (LaunchPlan.OptionProblem(settings.Path, settings.Git, settings.Ref, settings.GitToken) is { } problem)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(problem)}[/]");
            return 1;
        }

        var dir = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        var config = ProjectConfig.Load(dir);

        var api = Session.Resolve(settings.Server, settings.Token, settings.Account, config.Server);
        if (api is null) return 1;

        var run = new LaunchRun
        {
            Dir = dir,
            GitUrl = settings.Git,
            GitRef = settings.Ref,
            GitToken = settings.Git is null ? null : settings.GitToken ?? Environment.GetEnvironmentVariable("HARBORA_GIT_TOKEN"),
            Folder = settings.Folder,
            Name = settings.Name,
            Set = settings.Set ?? [],
            NoService = settings.NoService ?? [],
            Yes = settings.Yes,
            NoFollow = settings.NoFollow
        };

        return await RunAsync(api, run, Interactive.IsAvailable ? new TerminalPrompts() : null, ct);
    }

    /// <summary>
    /// The command over a client the caller supplies, and with nobody there when <paramref name="person"/>
    /// is null. Public for the same reason <see cref="CancelCommand.RunAsync"/> is: this is the part with
    /// behaviour in it.
    /// </summary>
    public static async Task<int> RunAsync(ApiClient api, LaunchRun run, ILaunchPrompts? person, CancellationToken ct)
    {
        var (answers, problems) = LaunchPlan.ParseSet(run.Set);
        if (problems.Count > 0)
        {
            foreach (var problem in problems) Say("✗", "red", problem);
            return 1;
        }

        string? id;
        try
        {
            id = await StartAsync(api, run, ct);
        }
        catch (ApiRefusedException refused)
        {
            foreach (var line in LaunchPlan.WhyRefused(refused)) Say("✗", "red", line);
            Say("", "grey", "Nothing was created.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Not the panel saying no. ApiClient's sentence for this blames an app name, and a launch
            // has none; and this is what a flaky connection looks like far more often than not.
            Say("✗", "red", $"The panel could not be reached, or the upload was cut off. Nothing was created. Try again. ({(ex.InnerException ?? ex).Message})");
            return 1;
        }

        if (id is null) return 1;

        try
        {
            return await WalkAsync(api, run, person, id, answers, ct);
        }
        catch (ApiRefusedException refused)
        {
            foreach (var line in LaunchPlan.WhyRefused(refused)) Say("✗", "red", line);
            Whole($"  Look at this launch on the panel: {LaunchPlan.LaunchPage(api.Server, id)}");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Say("✗", "red", $"Lost touch with the panel, so how far this got is not known. ({ex.Message})");
            Whole($"  Look at this launch on the panel: {LaunchPlan.LaunchPage(api.Server, id)}");
            return 1;
        }
    }

    /// <summary>Packs the folder or names the repository. Null once it has said why not.</summary>
    private static async Task<string?> StartAsync(ApiClient api, LaunchRun run, CancellationToken ct)
    {
        JsonElement started;
        if (run.GitUrl is not null)
        {
            started = await api.PostAsync("launch", new { cloneUrl = run.GitUrl, gitRef = run.GitRef, gitToken = run.GitToken }, ct);
        }
        else
        {
            if (!Directory.Exists(run.Dir))
            {
                Say("✗", "red", $"Folder not found: {run.Dir}");
                return null;
            }

            SourcePacker.Packed packed;
            try
            {
                packed = await SourcePacker.PackAsync(run.Dir, ProjectConfig.Load(run.Dir), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file open in something else, usually. Nothing has left the machine.
                Say("✗", "red", $"Could not read {run.Dir}: {ex.Message}");
                return null;
            }

            try
            {
                if (packed.Files == 0)
                {
                    Say("✗", "red", $"There is nothing to launch in {run.Dir}: it is empty, or everything in it is excluded from an upload.");
                    DeployCommand.ReportExcluded(packed.Excluded, verbose: false);
                    return null;
                }

                AnsiConsole.MarkupLine($"[grey]Packed[/] {packed.Files} files ({packed.Bytes / 1024.0 / 1024:0.#} MB) [grey]from[/] {Markup.Escape(run.Dir)}");
                DeployCommand.ReportExcluded(packed.Excluded, verbose: false);

                var folderName = new DirectoryInfo(run.Dir).Name;
                started = await api.PostFileAsync($"launch/archive?name={Uri.EscapeDataString(folderName)}", packed.ArchivePath, ct);
            }
            finally
            {
                try { File.Delete(packed.ArchivePath); } catch { /* temp file */ }
            }
        }

        return started.GetProperty("id").GetString();
    }

    /// <summary>From "it has the source" to "it has been told to go", or to the reason it was not.</summary>
    private static async Task<int> WalkAsync(
        ApiClient api, LaunchRun run, ILaunchPrompts? person, string id, Dictionary<string, string> answers, CancellationToken ct)
    {
        Say("", "grey", "Reading your code. Nothing is created or charged until you confirm.");

        JsonElement read;
        try
        {
            read = await UntilCtrlCAsync(ct, stop => WaitForReadingAsync(api, id, run.ReadEvery, stop));
        }
        catch (OperationCanceledException)
        {
            Say("", "grey", "Stopped. Nothing was created.");
            await AbandonAsync(api, id);
            return 1;
        }

        var status = LaunchPlan.Text(read, "status");
        if (status == LaunchPlan.InspectFailed)
        {
            var message = LaunchPlan.Text(read, "failureMessage") ?? "The source could not be read.";
            Say("✗", "red", message);
            if (!message.Contains("Nothing was created", StringComparison.Ordinal)) Say("", "grey", "Nothing was created.");
            return 1;
        }
        if (status != LaunchPlan.AwaitingConfirmation)
        {
            Say("✗", "red", $"This launch is {status}, so it cannot go any further from here.");
            return 1;
        }

        var proposal = await api.GetAsync($"launch/{id}/proposal", ct);

        // Which folder.
        var askingAllowed = person is not null && !run.Yes;
        if (LaunchPlan.NeedsFolder(proposal))
        {
            var folders = LaunchPlan.Candidates(proposal);
            var folder = !string.IsNullOrWhiteSpace(run.Folder) ? run.Folder : askingAllowed ? person!.ChooseFolder(folders) : null;
            if (folder is null)
            {
                Say("✗", "red", $"This holds several apps, and nothing says which: {string.Join(", ", folders)}. Choose one with --folder.");
                return await StoppedBeforeCreatingAsync(api, id);
            }

            try
            {
                proposal = await api.PostAsync($"launch/{id}/root", new { candidate = folder }, ct);
            }
            catch (ApiRefusedException refused) when (refused.StatusCode == HttpStatusCode.BadRequest)
            {
                foreach (var line in LaunchPlan.WhyRefused(refused)) Say("✗", "red", line);
                return await StoppedBeforeCreatingAsync(api, id);
            }
        }
        else if (LaunchPlan.FolderDisagrees(proposal, run.Folder))
        {
            Say("✗", "red", "--folder chooses between several apps, and this was read as one. Launch that folder itself, or leave --folder out.");
            return await StoppedBeforeCreatingAsync(api, id);
        }

        var (dropped, unknown) = LaunchPlan.ResolveNoService(proposal, run.NoService);
        if (unknown.Count > 0)
        {
            Say("✗", "red", $"--no-service does not know {string.Join(", ", unknown)}. These are: {string.Join(", ", LaunchPlan.ServiceTypes(proposal))}.");
            return await StoppedBeforeCreatingAsync(api, id);
        }

        // The proposal, as sentences, and what it costs.
        Show(LaunchPlan.Describe(proposal, run.Name, answers, dropped));

        var quote = await api.PostAsync($"launch/{id}/quote", LaunchPlan.ChoicesFrom(proposal, run.Name, answers, dropped), ct);
        if (LaunchPlan.QuoteRefusal(quote) is { } refusal)
        {
            Say("✗", "red", refusal);
            Say("", "default", "Nothing was created.");
            if (LaunchPlan.RefusalIsAboutMoney(quote))
                Whole($"  Add credit here, then run this again: {api.Server}/billing");
            await AbandonAsync(api, id);
            return 1;
        }
        Say("", "default", LaunchPlan.CostSentence(quote));

        // The questions. Only what a person alone can answer, and any of it can be left blank.
        var unanswered = new List<string>();
        foreach (var question in LaunchPlan.Questions(proposal, answers))
        {
            if (askingAllowed) answers[question.Key] = person!.Ask(question.Key, question.IsSecret);
            else unanswered.Add(question.Key);
        }

        if (unanswered.Count > 0)
            Say("!", "yellow", $"Not answered, so not set: {string.Join(", ", unanswered)}. Pass --set, or set them on the app's page if it needs them.");
        foreach (var key in LaunchPlan.UnaskedKeys(proposal, answers))
            Say("!", "yellow", $"--set {key}: nothing here asked for this. It will be set anyway, and kept as a secret.");

        // The one yes or no. Never hang waiting for it, and never create anything without being told to.
        if (!run.Yes)
        {
            if (person is null)
            {
                Say("✗", "red", "There is nobody here to confirm this, so nothing was created. Pass --yes to accept the proposal.");
                await AbandonAsync(api, id);
                return 1;
            }

            if (!person.Confirm("Create all of this?"))
            {
                Say("", "grey", "Nothing was created.");
                await AbandonAsync(api, id);
                return 1;
            }
        }

        JsonElement view;
        try
        {
            view = await api.PostAsync($"launch/{id}/confirm", LaunchPlan.ChoicesFrom(proposal, run.Name, answers, dropped), ct);
        }
        catch (ApiRefusedException refused) when (refused.StatusCode == HttpStatusCode.BadRequest)
        {
            foreach (var line in LaunchPlan.WhyRefused(refused)) Say("✗", "red", line);
            Say("", "default", "Nothing was created.");
            // Left in place, unlike the other stops: this is the one the panel's own screen can settle,
            // because it can change the things no flag here reaches, such as the size.
            Whole($"  You can change any of this on the panel: {LaunchPlan.LaunchPage(api.Server, id)}");
            return 1;
        }

        await VersionNotice.MaybeWarnAsync(api);

        if (run.NoFollow && !IsTerminal(view))
        {
            Say("✓", "green", "Confirmed. It carries on without you.");
            Whole($"  Look at it here: {LaunchPlan.LaunchPage(api.Server, id)}");
            return 0;
        }

        JsonElement? ended;
        try
        {
            ended = await UntilCtrlCAsync<JsonElement?>(ct, stop => FollowAsync(api, id, view, run.FollowEvery, stop));
        }
        catch (OperationCanceledException)
        {
            Say("", "grey", "Stopped watching. The launch carries on without you.");
            Whole($"  Look at it here: {LaunchPlan.LaunchPage(api.Server, id)}");
            return 1;
        }

        return ended is { } end ? await EndAsync(api, run, id, end, ct) : 1;
    }

    private static async Task<JsonElement> WaitForReadingAsync(ApiClient api, string id, TimeSpan every, CancellationToken ct)
    {
        while (true)
        {
            var view = await api.GetAsync($"launch/{id}", ct);
            if (LaunchPlan.Text(view, "status") != LaunchPlan.Inspecting) return view;
            await Task.Delay(every, ct);
        }
    }

    /// <summary>How many looks in a row may fail before saying so. A connection that drops for a moment is not news.</summary>
    private const int MissesBeforeGivingUp = 5;

    /// <summary>Prints each step as it changes, until the launch ends. Null when the panel stopped answering, having said so.</summary>
    private static async Task<JsonElement?> FollowAsync(ApiClient api, string id, JsonElement view, TimeSpan every, CancellationToken ct)
    {
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        string? shownDeployment = null;
        var misses = 0;

        while (true)
        {
            var launchFailed = LaunchPlan.Text(view, "status") == LaunchPlan.Failed;
            foreach (var step in LaunchPlan.Items(view, "steps"))
            {
                if (LaunchPlan.StepLine(step, launchFailed) is not { } line) continue;

                var key = LaunchPlan.Text(step, "key") ?? "";
                if (shown.GetValueOrDefault(key) == line.Text + line.Icon) continue;
                shown[key] = line.Text + line.Icon;
                Say(line);
            }

            if (LaunchPlan.Text(view, "deploymentId") is { Length: > 0 } deployment && deployment != shownDeployment)
            {
                shownDeployment = deployment;
                Whole($"  Its log: {LaunchPlan.DeploymentPage(api.Server, deployment)}");
                Whole($"  Or here: harbora logs {deployment}");
            }

            if (IsTerminal(view)) return view;

            await Task.Delay(every, ct);

            try
            {
                view = await api.GetAsync($"launch/{id}", ct);
                misses = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                if (++misses < MissesBeforeGivingUp) continue;

                Say("✗", "red", $"The panel has stopped answering, so what happened next is not known. ({ServerError.Message(ex.Message)})");
                Whole($"  The launch carries on without you. Look at it here: {LaunchPlan.LaunchPage(api.Server, id)}");
                return null;
            }
        }
    }

    private static bool IsTerminal(JsonElement view) =>
        view.TryGetProperty("isTerminal", out var terminal) && terminal.ValueKind == JsonValueKind.True;

    private static async Task<int> EndAsync(ApiClient api, LaunchRun run, string id, JsonElement view, CancellationToken ct)
    {
        var status = LaunchPlan.Text(view, "status");
        AnsiConsole.WriteLine();

        // Whatever the status still says. A failed launch goes on to say why it stopped, which is still true.
        var gone = LaunchPlan.AppGone(view);
        if (gone && status != LaunchPlan.Failed)
        {
            Say("!", "yellow", "The app this launch made has since been deleted, so there is nothing to open.");
            ShowWhatExists(api, view);
            return 1;
        }

        switch (status)
        {
            case LaunchPlan.Succeeded:
                ShowAddress(view, "Your app is running", good: true);
                foreach (var check in LaunchPlan.ChecksWorstFirst(view)) ShowCheck(api, check);
                break;

            case LaunchPlan.SucceededWithProblems:
                if (LaunchPlan.ProblemFound(view))
                {
                    ShowAddress(view, "It is running, but something is wrong", good: false);
                }
                else
                {
                    // Nothing was found to be wrong, and saying otherwise sends somebody looking for it.
                    ShowAddress(view, "It came up, but could not be confirmed as working", good: false);
                    if (LaunchPlan.NotChecked(view) is { Count: > 0 } notChecked)
                        Say("", "default", $"Could not be checked: {string.Join(", ", notChecked)}");
                }
                foreach (var check in LaunchPlan.ChecksWorstFirst(view)) ShowCheck(api, check);
                break;

            case LaunchPlan.Failed:
                Say("✗", "red", $"It stopped at: {LaunchPlan.FailedStepTitle(view) ?? "an unknown step"}");
                if (LaunchPlan.Text(view, "failureMessage") is { Length: > 0 } message) Say("", "default", message);
                if (gone) Say("!", "yellow", "The app this launch made has since been deleted.");

                ShowWhatExists(api, view);

                if (!gone && view.TryGetProperty("canRetry", out var canRetry) && canRetry.ValueKind == JsonValueKind.True)
                    Whole($"  Trying again could help. Do it on the panel: {LaunchPlan.LaunchPage(api.Server, id)}");
                break;

            default:
                Say("✗", "red", $"This launch ended as {status}. Nothing more will happen to it.");
                break;
        }

        // Last, because it is what the person does next. A launch that is running with a problem counts:
        // the app exists, and deploying again is how the problem gets fixed.
        if (run.GitUrl is null && status is LaunchPlan.Succeeded or LaunchPlan.SucceededWithProblems)
            await RememberAsync(api, run, view, ct);

        return LaunchPlan.ExitCode(status);
    }

    private static void ShowWhatExists(ApiClient api, JsonElement view)
    {
        var exists = LaunchPlan.WhatExists(view, api.Server);
        if (exists.Count == 0) return;

        Say("", "default", "What exists so far:");
        foreach (var line in exists) Whole($"  - {line}");
    }

    /// <summary>
    /// The address, large, or whole when it would not fit: inside a panel a long one would fold, and
    /// the line break would come with it when it is pasted. A worker has no address, and must not read
    /// like a website that lost its own.
    /// </summary>
    private static void ShowAddress(JsonElement view, string title, bool good)
    {
        var url = LaunchPlan.Text(view, "publicUrl");
        Say(good ? "✓" : "!", good ? "green" : "yellow", string.IsNullOrEmpty(url) ? $"{title}. It has no address of its own." : title);
        if (string.IsNullOrEmpty(url)) return;

        if (url.Length + PanelMargin <= AnsiConsole.Profile.Width)
            AnsiConsole.Write(new Panel(new Markup($"[bold green]{Markup.Escape(url)}[/]"))
                .Padding(2, 1).BorderColor(good ? Color.Green : Color.Yellow));
        else
            Whole($"  {url}");
    }

    /// <summary>What a panel's border and padding take from each side of the line.</summary>
    private const int PanelMargin = 6;

    private static void ShowCheck(ApiClient api, JsonElement check)
    {
        Say(LaunchPlan.CheckLine(check));
        if (!LaunchPlan.CheckNeedsAction(check)) return;

        if (LaunchPlan.Text(check, "fix") is { Length: > 0 } fix) Say("", "default", $"  What to do: {fix}");
        if (LaunchPlan.Text(check, "linkPath") is { Length: > 0 } link) Whole($"  {api.Server}{link}");
    }

    /// <summary>
    /// Saves the answer, so <c>harbora deploy</c> works from this folder from now on. Asked of the
    /// server by id, because a launch's view names the app for reading and the file needs its slug. A
    /// failure here is not a reason to fail a launch that worked.
    /// </summary>
    private static async Task RememberAsync(ApiClient api, LaunchRun run, JsonElement view, CancellationToken ct)
    {
        var appId = LaunchPlan.Text(view, "appId");
        string? slug = null;
        try
        {
            var apps = await api.GetAsync("apps", ct);
            slug = apps.EnumerateArray()
                .Where(a => string.Equals(LaunchPlan.Text(a, "id"), appId, StringComparison.OrdinalIgnoreCase))
                .Select(a => LaunchPlan.Text(a, "slug"))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Saying nothing is the right outcome of a convenience that could not be offered.
        }
        if (slug is null) return;

        AnsiConsole.WriteLine();
        if (Interactive.RememberApp(run.Dir, slug, api.Server))
            AnsiConsole.MarkupLine($"[grey]Wrote {ProjectConfig.DefaultFileName}. From now on, in this folder, just run[/] harbora deploy");
        else if (ProjectConfig.Locate(run.Dir) is { } existing)
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(Path.GetFileName(existing))} was already here and was left as it is. To deploy this app from here, run[/] harbora deploy {Markup.Escape(slug)}");
    }

    private static void Show(LaunchDescription description)
    {
        AnsiConsole.WriteLine();
        Say("", "default", description.Found);
        foreach (var detail in description.Details) Say("", "default", detail);
        foreach (var note in description.Notes) Say("!", "yellow", note);

        AnsiConsole.WriteLine();
        Say("", "default", "It will create:");
        foreach (var created in description.Creates) Say("  -", "default", created);

        foreach (var guess in description.Guesses) Say("", "grey", guess);
        if (description.Generated.Count > 0)
            Say("", "default", $"It will generate these secrets: {string.Join(", ", description.Generated)}");
        if (description.Kept.Count > 0)
            Say("", "default", $"It will set these from your example file: {string.Join(", ", description.Kept)}");
        if (description.Unattached.Count > 0)
            Say("!", "yellow", $"With no service to answer them, nothing will set {string.Join(", ", description.Unattached)}. Pass --set if the app needs them.");
    }

    /// <summary>Stops with nothing created, and lets go of the upload now rather than when the sweep finds it.</summary>
    private static async Task<int> StoppedBeforeCreatingAsync(ApiClient api, string id)
    {
        Say("", "grey", "Nothing was created.");
        await AbandonAsync(api, id);
        return 1;
    }

    /// <summary>Best effort: a launch nobody came back to is swept anyway, and this must not change the answer.</summary>
    private static async Task AbandonAsync(ApiClient api, string id)
    {
        try { await api.PostAsync($"launch/{id}/abandon"); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* see above */ }
    }

    /// <summary>
    /// Runs a wait that Ctrl-C ends. Spectre does not turn the key into the command's token, and nothing
    /// else in this CLI does either, so without this the token never fires and the key kills the process
    /// with nothing said. Only around a wait, not the whole command: a prompt blocks on the keyboard and
    /// would simply ignore a cancelled key, where the default still gets somebody out of it.
    /// </summary>
    private static async Task<T> UntilCtrlCAsync<T>(CancellationToken ct, Func<CancellationToken, Task<T>> wait)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler pressed = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        Console.CancelKeyPress += pressed;
        try { return await wait(stop.Token); }
        finally { Console.CancelKeyPress -= pressed; }
    }

    private static void Say(LaunchLine line) => Say(line.Icon, line.Color, line.Text);

    /// <summary>With an icon, the icon carries the colour; without one, the whole line does.</summary>
    private static void Say(string icon, string color, string text) => AnsiConsole.MarkupLine(icon.Length > 0
        ? $"[{color}]{Markup.Escape(icon)}[/] {Markup.Escape(text)}"
        : $"[{color}]{Markup.Escape(text)}[/]");

    /// <summary>
    /// Written as it is. Spectre folds a line at the console's width, which is 80 columns with no terminal
    /// behind it, and a panel's deployment address is longer than that: a URL with a line break in it
    /// cannot be clicked or pasted, and that is the one thing this line is for.
    /// </summary>
    private static void Whole(string text) => AnsiConsole.Profile.Out.Writer.WriteLine(text);

    /// <summary>The prompts on a real terminal.</summary>
    private sealed class TerminalPrompts : ILaunchPrompts
    {
        public string ChooseFolder(IReadOnlyList<string> folders) => AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("This holds several apps. Which one?")
                .PageSize(15)
                .UseConverter(Markup.Escape)
                .AddChoices(folders));

        public string Ask(string key, bool secret)
        {
            var prompt = new TextPrompt<string>($"{Markup.Escape(key)} [grey](blank to leave it unset)[/]:").AllowEmpty();
            if (secret) prompt.Secret();
            return AnsiConsole.Prompt(prompt);
        }

        public bool Confirm(string question) => AnsiConsole.Confirm(Markup.Escape(question));
    }
}
