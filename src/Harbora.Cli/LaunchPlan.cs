using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harbora.Cli;

/// <summary>One line of a launch as a person reads it, before anything has decided how to draw it.</summary>
public sealed record LaunchLine(string Icon, string Color, string Text);

/// <summary>One question only a person can answer. A key, never a value.</summary>
public sealed record LaunchQuestion(string Key, bool IsSecret);

/// <summary>The proposal as sentences. Names only: nothing here ever holds a value.</summary>
/// <param name="Found">What the code turned out to be.</param>
/// <param name="Details">How it starts and what it runs first, when either is not the stack's own default.</param>
/// <param name="Notes">Everything the server thought the person should know about its guess.</param>
/// <param name="Creates">What exists after confirming, each service with why.</param>
/// <param name="Guesses">Services that were only suspected, and so will not be created.</param>
/// <param name="Generated">The names of the secrets it will generate.</param>
/// <param name="Kept">The names of the variables it will set from the example file.</param>
/// <param name="Unattached">Variables a service would have answered, had it not been turned down.</param>
public sealed record LaunchDescription(
    string Found,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Creates,
    IReadOnlyList<string> Guesses,
    IReadOnlyList<string> Generated,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Unattached);

/// <summary>
/// Everything about <c>harbora launch</c> that can be decided without a terminal or a network: what the
/// flags mean, what is sent, what a state prints as and what the exit code is. Reads the panel's answers
/// as JSON and nothing more, because the proposal is the server's: the inspector lives in a project this
/// CLI could reach, and two readings is how the two come to disagree.
/// </summary>
public static class LaunchPlan
{
    // The names the API sends. Pinned against the domain's enums by CliLaunchPlanTests, because this
    // project cannot reference them and a renamed status would otherwise just stop matching.
    public const string Inspecting = "Inspecting";
    public const string InspectFailed = "InspectFailed";
    public const string AwaitingConfirmation = "AwaitingConfirmation";
    public const string Succeeded = "Succeeded";
    public const string SucceededWithProblems = "SucceededWithProblems";
    public const string Failed = "Failed";
    public const string Abandoned = "Abandoned";

    private const string StepRunning = "Running";
    private const string StepDone = "Done";
    private const string StepFailed = "Failed";
    private const string StepSkipped = "Skipped";

    private const string CheckPassed = "Passed";
    private const string CheckFailed = "Failed";
    private const string CheckWarning = "Warning";
    private const string CheckSkipped = "Skipped";
    private const string CheckPending = "Pending";

    private const string NeedsAnswer = "NeedsAnswer";
    private const string GeneratedSecret = "GeneratedSecret";
    private const string KeepExample = "KeepExample";

    /// <summary>Flags that contradict each other. Said before anything leaves the machine.</summary>
    public static string? OptionProblem(string? path, string? git, string? gitRef, string? gitToken)
    {
        if (git is not null && path is not null)
            return "Give a folder or --git, not both.";
        if (git is null && (gitRef is not null || gitToken is not null))
            return "--ref and --git-token only mean something with --git.";
        return null;
    }

    /// <summary>
    /// The <c>--set</c> pairs. A pair with no key is reported by its position and never quoted: somebody
    /// who left out the <c>KEY=</c> has typed the value on its own, and this would print it.
    /// </summary>
    public static (Dictionary<string, string> Answers, IReadOnlyList<string> Problems) ParseSet(IEnumerable<string>? pairs)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        var position = 0;

        foreach (var pair in pairs ?? [])
        {
            position++;
            var equals = pair.IndexOf('=');
            if (equals <= 0 || pair[..equals].Trim().Length == 0)
            {
                problems.Add($"--set number {position} is not KEY=VALUE.");
                continue;
            }

            answers[pair[..equals].Trim()] = pair[(equals + 1)..];
        }

        return (answers, problems);
    }

    /// <summary>A repository holding several apps, with none chosen: nothing else it says is about the app the person means.</summary>
    public static bool NeedsFolder(JsonElement proposal)
    {
        var inspection = proposal.GetProperty("inspection");
        return string.IsNullOrWhiteSpace(Text(inspection, "rootDirectory")) && Candidates(proposal).Count > 0;
    }

    public static IReadOnlyList<string> Candidates(JsonElement proposal) =>
        Items(proposal.GetProperty("inspection"), "rootCandidates").Select(c => c.GetString() ?? "").ToList();

    /// <summary>
    /// Whether <c>--folder</c> says something the proposal did not take into account. With one app there
    /// is nothing to choose between, and that folder was never read, so pretending to honour it would
    /// launch this one's database, variables and port from a different folder.
    /// </summary>
    public static bool FolderDisagrees(JsonElement proposal, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        var read = Text(proposal.GetProperty("inspection"), "rootDirectory");
        return !string.Equals(Tidy(folder), Tidy(read), StringComparison.Ordinal);

        static string Tidy(string? path) => (path ?? "").Replace('\\', '/').Trim().Trim('/');
    }

    /// <summary>
    /// What <c>--no-service</c> turned down, as the API's type names. A name that matches nothing is
    /// reported rather than ignored, because the person who mistyped it believes they just saved the cost
    /// of a database. Any unambiguous start of a type will do: <c>postgres</c> is what people type.
    /// </summary>
    public static (IReadOnlySet<string> Dropped, IReadOnlyList<string> Unknown) ResolveNoService(
        JsonElement proposal, IEnumerable<string>? names)
    {
        var types = Items(proposal, "serviceOptions").Select(o => Text(o, "type") ?? "").ToList();
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();

        foreach (var name in names ?? [])
        {
            var wanted = name.Trim();
            var matches = types.Where(t => t.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0 && wanted.Length > 0)
                matches = types.Where(t => t.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 1) dropped.Add(matches[0]);
            else unknown.Add(name);
        }

        return (dropped, unknown);
    }

    public static IReadOnlyList<string> ServiceTypes(JsonElement proposal) =>
        Items(proposal, "serviceOptions").Select(o => Text(o, "type") ?? "").ToList();

    /// <summary>
    /// The proposal's own defaults with the person's answers on them, as what the API takes back. Kept as
    /// JSON and edited rather than read into a type of this CLI's own, so every field this does not ask
    /// about goes back exactly as it came, and an enum stays the name the panel sent.
    /// </summary>
    public static JsonObject ChoicesFrom(
        JsonElement proposal, string? name, IReadOnlyDictionary<string, string> answers, IReadOnlySet<string> dropped)
    {
        var choices = JsonNode.Parse(proposal.GetProperty("defaults").GetRawText())!.AsObject();

        if (!string.IsNullOrWhiteSpace(name)) choices["name"] = name.Trim();

        var services = choices["services"]!.AsArray();
        for (var i = services.Count - 1; i >= 0; i--)
            if (dropped.Contains(services[i]!["type"]!.GetValue<string>()))
                services.RemoveAt(i);

        var variables = choices["envVars"]!.AsArray();
        foreach (var (key, value) in answers)
        {
            // Blank means the variable is not set: an empty string in a variable is a value, and a
            // surprisingly confusing one for an app to read.
            var text = value.Length == 0 ? null : value;

            var existing = variables.FirstOrDefault(v => v!["key"]!.GetValue<string>() == key);
            if (existing is not null) existing["value"] = text;
            else variables.Add(new JsonObject { ["key"] = key, ["value"] = text, ["isSecret"] = IsSecret(proposal, key) });
        }

        return choices;
    }

    /// <summary>
    /// The server's answer for a name it asked about. For one it did not, a secret: masking a value that
    /// did not need it costs a click, and the reverse leaves somebody's key on a page.
    /// </summary>
    private static bool IsSecret(JsonElement proposal, string key)
    {
        foreach (var field in Items(proposal, "envFields"))
            if (Text(field, "key") == key)
                return field.GetProperty("isSecret").GetBoolean();
        return true;
    }

    /// <summary>What only a person can answer, and nobody has.</summary>
    public static IReadOnlyList<LaunchQuestion> Questions(JsonElement proposal, IReadOnlyDictionary<string, string> answers) =>
        Items(proposal, "envFields")
            .Where(f => Text(f, "disposition") == NeedsAnswer && !answers.ContainsKey(Text(f, "key") ?? ""))
            .Select(f => new LaunchQuestion(Text(f, "key") ?? "", f.GetProperty("isSecret").GetBoolean()))
            .ToList();

    /// <summary>Answers to things nobody asked: often a typo, which would leave the real one unset without a word.</summary>
    public static IReadOnlyList<string> UnaskedKeys(JsonElement proposal, IReadOnlyDictionary<string, string> answers)
    {
        var asked = Items(proposal, "envFields").Select(f => Text(f, "key") ?? "").ToHashSet(StringComparer.Ordinal);
        return answers.Keys.Where(k => !asked.Contains(k)).ToList();
    }

    public static LaunchDescription Describe(
        JsonElement proposal, string? name, IReadOnlyDictionary<string, string> answers, IReadOnlySet<string> dropped)
    {
        var inspection = proposal.GetProperty("inspection");
        var defaults = proposal.GetProperty("defaults");
        var options = Items(proposal, "serviceOptions").ToList();

        JsonElement? OptionFor(string? type) => options.Where(o => Text(o, "type") == type).Select(o => (JsonElement?)o).FirstOrDefault();

        var details = new List<string>();
        if (Text(inspection, "startCommand") is { Length: > 0 } start) details.Add($"It will start with: {start}");
        if (Text(inspection, "releaseCommand") is { Length: > 0 } release) details.Add($"Before traffic moves it will run: {release}");

        var appName = string.IsNullOrWhiteSpace(name) ? Text(defaults, "name") : name.Trim();
        var creates = new List<string> { $"The app {appName}" };
        var offered = Items(defaults, "services").ToList();
        foreach (var service in offered.Where(s => !dropped.Contains(Text(s, "type") ?? "")))
        {
            var type = Text(service, "type");
            var option = OptionFor(type);
            var shown = option is { } known ? Text(known, "displayName") ?? type : type;
            var why = option is { } evidenceFrom && evidenceFrom.TryGetProperty("detected", out var detected)
                ? Evidence(detected)
                : "";
            creates.Add(why.Length == 0 ? $"{shown}" : $"{shown}, because of {why}");
        }

        var offeredTypes = offered.Select(s => Text(s, "type")).ToHashSet(StringComparer.Ordinal);
        var guesses = options
            .Where(o => !offeredTypes.Contains(Text(o, "type"))
                        && o.TryGetProperty("detected", out var d) && d.ValueKind == JsonValueKind.Object
                        && !d.GetProperty("certain").GetBoolean())
            .Select(o => $"{Text(o, "displayName") ?? Text(o, "type")} is only a guess ({Evidence(o.GetProperty("detected"))}), so it will not be created.")
            .ToList();

        var fields = Items(proposal, "envFields").ToList();
        IReadOnlyList<string> Named(string disposition) => fields
            .Where(f => Text(f, "disposition") == disposition && !answers.ContainsKey(Text(f, "key") ?? ""))
            .Select(f => Text(f, "key") ?? "")
            .ToList();

        var unattached = offered
            .Where(s => dropped.Contains(Text(s, "type") ?? ""))
            .SelectMany(s => Items(s, "aliases"))
            .Select(a => Text(a, "name") ?? "")
            .Where(key => !answers.ContainsKey(key))
            .ToList();

        return new LaunchDescription(
            WhatItIs(inspection, defaults),
            details,
            Items(inspection, "notes").Select(n => Text(n, "message") ?? "").ToList(),
            creates,
            guesses,
            Named(GeneratedSecret),
            Named(KeepExample),
            unattached);
    }

    private static string WhatItIs(JsonElement inspection, JsonElement defaults)
    {
        var stack = Text(inspection, "stack") switch
        {
            "Dockerfile" => "a project with its own Dockerfile",
            "Compose" => "a Docker Compose project",
            "Node" => "a Node.js app",
            "DotNet" => "a .NET app",
            "Go" => "a Go app",
            "Php" => "a PHP app",
            "Python" => "a Python app",
            "Static" => "a static site",
            _ => null
        };

        var where = Text(inspection, "rootDirectory") is { Length: > 0 } folder ? $" in the folder {folder}" : "";

        if (stack is null) return $"It could not tell what this is built with{where}.";

        return Text(inspection, "kind") == "Worker"
            ? $"This looks like {stack}{where}, running as a worker with no address of its own."
            : $"This looks like {stack}{where}, listening on port {defaults.GetProperty("containerPort").GetInt32()}.";
    }

    private static string Evidence(JsonElement detected) => detected.ValueKind != JsonValueKind.Object
        ? ""
        : string.Join("; ", Items(detected, "evidence").Select(e => $"{Text(e, "reason")} ({Text(e, "file")})"));

    /// <summary>The one sentence about money, written the way the confirm screen writes it.</summary>
    public static string CostSentence(JsonElement quote)
    {
        if (quote.GetProperty("exempt").GetBoolean()) return "Nothing is charged to this workspace.";

        var perHour = Items(quote, "lines")
            .Sum(l => l.TryGetProperty("hourlyMinor", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt64() : 0);
        if (quote.GetProperty("firstHourMinor").GetInt64() == 0 && perHour == 0)
            return "This size is free: nothing comes off your balance.";

        var balance = Text(quote, "balance") is { Length: > 0 } left ? $" Balance: {left}." : "";
        return $"{Text(quote, "firstHour")} comes off your balance now (one hour of running), then {Text(quote, "perHour")} an hour.{balance}";
    }

    /// <summary>Why a quote would be refused, or null when it can be paid for.</summary>
    public static string? QuoteRefusal(JsonElement quote) =>
        quote.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.Object
            ? Text(refusal, "message")
            : null;

    /// <summary>What the panel said no with, one line each: the launch API's list, the older single sentence, or nothing.</summary>
    public static IReadOnlyList<string> RefusalLines(string body)
    {
        try
        {
            using var parsed = JsonDocument.Parse(body);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                return errors.EnumerateArray()
                    .Select(e => Text(e, "field") is { Length: > 0 } field ? $"{field}: {Text(e, "message")}" : Text(e, "message") ?? "")
                    .ToList();

            return root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? [error.GetString() ?? ""]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A refused request as sentences, with the plain ones a bare status code leaves to be written here.</summary>
    public static IReadOnlyList<string> WhyRefused(ApiRefusedException refused)
    {
        var said = RefusalLines(refused.Body);
        if (said.Count > 0) return said;

        return refused.StatusCode switch
        {
            HttpStatusCode.Unauthorized => ["The panel did not accept this token. Run harbora login, or check --token."],
            HttpStatusCode.Forbidden => ["This account may not create apps here. Ask an owner of the workspace."],
            HttpStatusCode.NotFound => ["This panel does not know about launches: it is older than this CLI. Create the app in the panel and use harbora deploy."],
            HttpStatusCode.RequestEntityTooLarge => ["This folder is over the panel's upload limit of 512 MB. Add what it does not need to .dockerignore."],
            _ => [ServerError.Message(refused.Message)]
        };
    }

    /// <summary>The exit code a launch that has ended, or is being left to carry on, finishes with.</summary>
    public static int ExitCode(string? status) => status switch
    {
        Failed or InspectFailed or Abandoned => 1,
        SucceededWithProblems => 2,
        _ => 0
    };

    /// <summary>
    /// A step as it now stands, or null while it has not begun. A failed launch's step leaves out its
    /// detail, because that is the failure message and the end of the run prints it with what to do next.
    /// </summary>
    public static LaunchLine? StepLine(JsonElement step, bool launchFailed)
    {
        var title = Text(step, "title") ?? "";
        var detail = Text(step, "detail");

        return Text(step, "state") switch
        {
            StepRunning => new LaunchLine("→", "grey", With(title, detail)),
            StepDone => new LaunchLine("✓", "green", With(title, detail)),
            StepFailed => new LaunchLine("✗", "red", launchFailed ? title : With(title, detail)),
            StepSkipped => new LaunchLine("-", "grey", With($"{title}: skipped", detail)),
            _ => null
        };
    }

    /// <summary>
    /// One check. A skipped or unsettled one says so in words and gets no tick, because "we could not
    /// check" and "it works" are different sentences, and a tick says the second.
    /// </summary>
    public static LaunchLine CheckLine(JsonElement check)
    {
        var title = Text(check, "title") ?? "";
        var detail = Text(check, "detail");

        return Text(check, "status") switch
        {
            CheckPassed => new LaunchLine("✓", "green", With(title, detail)),
            CheckFailed => new LaunchLine("✗", "red", With(title, detail)),
            CheckWarning => new LaunchLine("!", "yellow", With(title, detail)),
            CheckSkipped => new LaunchLine("-", "grey", With($"{title}: not checked", detail)),
            CheckPending => new LaunchLine("…", "yellow", With($"{title}: not settled yet", detail)),
            _ => new LaunchLine("?", "grey", With($"{title}: not known", detail))
        };
    }

    /// <summary>What went wrong first, because that is what the person needs and a long list buries it.</summary>
    public static IReadOnlyList<JsonElement> ChecksWorstFirst(JsonElement view) =>
        Items(view, "checks")
            .OrderBy(c => Text(c, "status") switch { CheckFailed => 0, CheckWarning => 1, _ => 2 })
            .ToList();

    public static bool CheckNeedsAction(JsonElement check) => Text(check, "status") is CheckFailed or CheckWarning;

    /// <summary>The step a failed launch stopped at, by title.</summary>
    public static string? FailedStepTitle(JsonElement view) =>
        Items(view, "steps").Where(s => Text(s, "state") == StepFailed).Select(s => Text(s, "title")).FirstOrDefault();

    /// <summary>
    /// What is left standing, because that is what a person is paying for while they read this. Only what
    /// the view can vouch for: a database made before a later step refused is named in the failure
    /// message itself, by the row that knows it.
    /// </summary>
    public static IReadOnlyList<string> WhatExists(JsonElement view, string server)
    {
        var lines = new List<string>();

        if (Text(view, "appId") is { Length: > 0 } appId)
            lines.Add($"The app {Text(view, "appName")}: {AppPage(server, appId)}");

        var services = Items(view, "steps").FirstOrDefault(s => Text(s, "key") == "services");
        if (services.ValueKind == JsonValueKind.Object && Text(services, "state") == StepDone)
            lines.Add(Text(services, "linkPath") is { Length: > 0 } link ? $"Its databases: {server}{link}" : "Its databases, on the app's page");

        if (Text(view, "deploymentId") is { Length: > 0 } deploymentId)
            lines.Add($"Its deployment: {DeploymentPage(server, deploymentId)}");

        return lines;
    }

    public static string LaunchPage(string server, string id) => $"{server}/launch/{id}";
    public static string AppPage(string server, string id) => $"{server}/apps/details/{id}";
    public static string DeploymentPage(string server, string id) => $"{server}/deployments/details/{id}";

    private static string With(string title, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? title : $"{title}: {detail}";

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];
}
