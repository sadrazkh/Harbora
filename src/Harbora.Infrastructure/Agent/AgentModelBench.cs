using System.Diagnostics;
using System.Text.Json;

namespace Harbora.Infrastructure.Agent;

/// <summary>What one prompt came to. A provider that never answered is not the model getting it wrong.</summary>
public enum AgentBenchOutcome
{
    /// <summary>Exactly one tool call, the expected tool, with the expected key arguments.</summary>
    Passed,

    /// <summary>The model answered, and the answer was not the expected call.</summary>
    Failed,

    /// <summary>The provider gave no usable answer (rate limit, error, timeout): nothing to judge.</summary>
    NoAnswer
}

/// <summary>One canned prompt and the call a capable model makes for it.</summary>
/// <param name="Language">"fa" or "en".</param>
/// <param name="Arguments">The arguments that must be present and equal (case-insensitive, trimmed).
/// Others the model adds — a version, a project — are not scored.</param>
public sealed record AgentBenchCase(
    string Language,
    string Prompt,
    string Tool,
    IReadOnlyDictionary<string, string> Arguments)
{
    /// <summary>How the expectation reads in the results table: <c>restart_app(app="shop")</c>.</summary>
    public string Expected =>
        $"{Tool}({string.Join(", ", Arguments.Select(a => $"{a.Key}=\"{a.Value}\""))})";
}

public sealed record AgentBenchRow(
    string Language,
    string Prompt,
    string Expected,
    AgentBenchOutcome Outcome,
    string Observed,
    string ObservedFa,
    long LatencyMs,
    string? Model);

/// <param name="Ran">False when the agent is not available: no call was made and <paramref name="Rows"/> is empty.</param>
/// <param name="Model">The model the calls asked for first.</param>
public sealed record AgentBenchReport(
    bool Ran,
    IReadOnlyList<AgentBenchRow> Rows,
    string? Model,
    int Passed,
    int Failed,
    int NoAnswer,
    string Verdict,
    string VerdictFa)
{
    public int Total => Rows.Count;
}

/// <summary>
/// "Does this model call the right tool?" — answered by asking it, with the owner's own settings, a
/// handful of canned Persian and English requests against fake tools, and reading what it asks for.
///
/// <para>
/// <b>Nothing is executed.</b> The tools here are specs and nothing else: they have no code behind
/// them, and the bench never looks at a real resource. All it does is read the tool calls the model
/// returns, which is also why it can run against a production panel safely.
/// </para>
///
/// <para>
/// <b>Scoring rule.</b> A prompt passes only if the model made exactly one tool call, to the tool the
/// prompt calls for, whose arguments are a JSON object holding every key argument with the expected
/// value (compared case-insensitively, trimmed). Text instead of a call fails. A second call fails
/// even when the first was right: the prompts ask for one thing, and an agent that does two has done
/// something nobody asked for. A prompt the provider never answered is counted separately — it says
/// nothing about the model.
/// </para>
/// </summary>
public sealed class AgentModelBench(IAgentChatClient client)
{
    /// <summary>Room for a tool call, and for a model that talks a little first.</summary>
    private const int MaxOutputTokens = 1024;

    /// <summary>
    /// The whole test's ceiling. Six calls at the client's 90 s each could keep one HTTP request open
    /// for nine minutes; past this the remaining prompts are reported as unanswered instead.
    /// </summary>
    internal TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(180);

    private const string SystemPrompt =
        "You are the assistant inside Harbora, a self-hosted deployment panel. The user writes in Persian or English. "
        + "Act on the request by calling exactly one of the provided tools with the right arguments; call the tool "
        + "instead of describing what you would do. Names of apps and databases are given in the request — use them "
        + "as written, in Latin letters, and never invent others.";

    /// <summary>
    /// The fixed prompts. Three Persian, three English; the first Persian one and "list my apps" are
    /// the owner's own examples. Six is enough to tell "calls tools in Persian" from "does not" and
    /// few enough not to spend a free-tier day on a test button; they go one at a time, because free
    /// models rate limit hard and six at once would measure the limiter.
    /// </summary>
    public static IReadOnlyList<AgentBenchCase> Cases { get; } =
    [
        new("fa", "یک دیتابیس Postgres به اسم shop-db بساز", "create_database",
            Args(("engine", "postgres"), ("name", "shop-db"))),
        new("fa", "اپ shop را ری‌استارت کن", "restart_app",
            Args(("app", "shop"))),
        new("fa", "دیتابیس shop-db را به اپ shop وصل کن", "attach_database",
            Args(("app", "shop"), ("database", "shop-db"))),
        new("en", "list my apps", "list_resources",
            Args(("kind", "apps"))),
        new("en", "Create a MySQL database called blog-db", "create_database",
            Args(("engine", "mysql"), ("name", "blog-db"))),
        new("en", "Attach the database blog-db to the app blog", "attach_database",
            Args(("app", "blog"), ("database", "blog-db")))
    ];

    /// <summary>
    /// Fake tools, defined here and nowhere else: names and schemas shaped like the real ones, with a
    /// near neighbour or two (<c>stop_app</c> beside <c>restart_app</c>) so a wrong pick is possible.
    /// </summary>
    public static IReadOnlyList<AgentToolSpec> ToolSpecs { get; } =
    [
        new("list_resources",
            "List what the user can see: projects, apps or databases, with their names and states.",
            """
            {"type":"object","properties":{
              "kind":{"type":"string","enum":["all","projects","apps","databases"],"description":"What to list."},
              "project":{"type":"string","description":"Project slug to limit the list to (optional)."}},
             "required":["kind"]}
            """),
        new("get_app",
            "Show one app's details: status, domains, attached databases, last deployment.",
            """{"type":"object","properties":{"app":{"type":"string","description":"The app's name or slug."}},"required":["app"]}"""),
        new("create_project",
            "Create a project, the group apps and databases live in.",
            """{"type":"object","properties":{"name":{"type":"string","description":"The project's name."}},"required":["name"]}"""),
        new("create_database",
            "Create a managed database. Nothing is created until the user approves the plan.",
            """
            {"type":"object","properties":{
              "engine":{"type":"string","enum":["postgres","mysql","mariadb","redis","mongodb"],"description":"The database engine."},
              "name":{"type":"string","description":"The new database's name, e.g. shop-db."},
              "version":{"type":"string","description":"Engine version (optional)."},
              "project":{"type":"string","description":"Project slug (optional)."}},
             "required":["engine","name"]}
            """),
        new("attach_database",
            "Attach an existing database to an app, so the app receives its connection settings.",
            """
            {"type":"object","properties":{
              "app":{"type":"string","description":"The app's name or slug."},
              "database":{"type":"string","description":"The database's name."}},
             "required":["app","database"]}
            """),
        new("set_env_vars",
            "Set environment variables on an app.",
            """
            {"type":"object","properties":{
              "app":{"type":"string","description":"The app's name or slug."},
              "variables":{"type":"object","additionalProperties":{"type":"string"},"description":"Names and values."}},
             "required":["app","variables"]}
            """),
        new("deploy_app",
            "Deploy the app's current source again.",
            """{"type":"object","properties":{"app":{"type":"string","description":"The app's name or slug."}},"required":["app"]}"""),
        new("restart_app",
            "Restart a running app without rebuilding it.",
            """{"type":"object","properties":{"app":{"type":"string","description":"The app's name or slug."}},"required":["app"]}"""),
        new("stop_app",
            "Stop an app. It stays stopped until started again.",
            """{"type":"object","properties":{"app":{"type":"string","description":"The app's name or slug."}},"required":["app"]}""")
    ];

    public async Task<AgentBenchReport> RunAsync(CancellationToken ct)
    {
        // Asked again here, not only by the button: the bench spends the owner's requests.
        if (await client.GetAvailabilityAsync(ct) is { } unavailable)
            return new AgentBenchReport(false, [], null, 0, 0, 0, unavailable.Reason, unavailable.ReasonFa);

        var model = await client.GetModelAsync(ct);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeBudget);

        var rows = new List<AgentBenchRow>();
        foreach (var testCase in Cases)
        {
            var request = new AgentChatRequest(
                [
                    new AgentChatMessage(AgentRole.System, SystemPrompt),
                    new AgentChatMessage(AgentRole.User, testCase.Prompt)
                ],
                ToolSpecs,
                MaxOutputTokens);

            var started = Stopwatch.GetTimestamp();
            AgentChatResponse response;
            try
            {
                response = await client.CompleteAsync(request, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The test's own ceiling, not the caller leaving.
                var seconds = (int)TimeBudget.TotalSeconds;
                response = AgentChatResponse.Failed(
                    $"The test ran out of its {seconds}-second budget before this prompt was answered.",
                    $"زمان {seconds} ثانیه‌ای آزمون پیش از پاسخ به این پرسش تمام شد.");
            }

            var latency = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var (outcome, observed, observedFa) = Judge(testCase, response);
            rows.Add(new AgentBenchRow(testCase.Language, testCase.Prompt, testCase.Expected, outcome,
                observed, observedFa, latency, response.Model));
        }

        return Summarise(rows, model);
    }

    // ---- scoring ---------------------------------------------------------------------------

    internal static (AgentBenchOutcome Outcome, string Observed, string ObservedFa) Judge(
        AgentBenchCase testCase, AgentChatResponse response)
    {
        if (!response.Ok)
            return (AgentBenchOutcome.NoAnswer,
                response.Error ?? "No answer.",
                response.ErrorFa ?? response.Error ?? "پاسخی نیامد.");

        var calls = response.Message?.ToolCalls ?? [];

        if (calls.Count == 0)
        {
            var text = Shorten(response.Message?.Content, 120);
            return (AgentBenchOutcome.Failed,
                $"Replied in text and called no tool: \"{text}\"",
                $"به‌جای فراخوانی ابزار، متن نوشت: «{text}»");
        }

        if (calls.Count > 1)
        {
            var all = string.Join("; ", calls.Select(Describe));
            return (AgentBenchOutcome.Failed,
                $"Made {calls.Count} tool calls where one was asked for: {all}",
                $"{calls.Count} ابزار را فراخوانی کرد درحالی‌که یکی خواسته شده بود: {all}");
        }

        var call = calls[0];
        var described = Describe(call);

        if (!string.Equals(call.Name, testCase.Tool, StringComparison.Ordinal))
            return (AgentBenchOutcome.Failed,
                $"{described} — wrong tool",
                $"{described} — ابزار اشتباه");

        JsonElement arguments;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            arguments = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (AgentBenchOutcome.Failed,
                $"{described} — the arguments are not valid JSON",
                $"{described} — آرگومان‌ها JSON معتبر نیستند");
        }

        if (arguments.ValueKind != JsonValueKind.Object)
            return (AgentBenchOutcome.Failed,
                $"{described} — the arguments are not a JSON object",
                $"{described} — آرگومان‌ها یک شیء JSON نیستند");

        foreach (var (key, expected) in testCase.Arguments)
        {
            if (!arguments.TryGetProperty(key, out var actual))
                return (AgentBenchOutcome.Failed,
                    $"{described} — \"{key}\" is missing",
                    $"{described} — «{key}» ندارد");

            var actualText = actual.ValueKind == JsonValueKind.String ? actual.GetString() : actual.GetRawText();
            if (!string.Equals(actualText?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                return (AgentBenchOutcome.Failed,
                    $"{described} — \"{key}\" should be \"{expected}\"",
                    $"{described} — «{key}» باید «{expected}» باشد");
        }

        return (AgentBenchOutcome.Passed, described, described);
    }

    /// <summary>The call as it reads in the table: <c>name({"compact":"json"})</c>.</summary>
    private static string Describe(AgentToolCall call)
    {
        string arguments;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            arguments = doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            arguments = call.ArgumentsJson;
        }

        return $"{call.Name}({Shorten(arguments, 160)})";
    }

    private static string Shorten(string? text, int max)
    {
        text = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > max ? text[..max] + "…" : text;
    }

    // ---- verdict ---------------------------------------------------------------------------

    private static AgentBenchReport Summarise(List<AgentBenchRow> rows, string? model)
    {
        int Count(AgentBenchOutcome outcome) => rows.Count(r => r.Outcome == outcome);
        string Share(string language) =>
            $"{rows.Count(r => r.Language == language && r.Outcome == AgentBenchOutcome.Passed)}/{rows.Count(r => r.Language == language)}";

        var passed = Count(AgentBenchOutcome.Passed);
        var failed = Count(AgentBenchOutcome.Failed);
        var noAnswer = Count(AgentBenchOutcome.NoAnswer);
        var total = rows.Count;
        var judged = passed + failed;

        var breakdownEn = $"Persian {Share("fa")}, English {Share("en")}.";
        var breakdownFa = $"فارسی {Share("fa")}، انگلیسی {Share("en")}.";

        string verdict, verdictFa;

        if (judged == 0)
        {
            verdict = $"The provider gave no usable answer to any of the {total} prompts, so the model could not be judged. The errors are in the table.";
            verdictFa = $"ارائه‌دهنده به هیچ‌یک از {total} پرسش پاسخ قابل‌استفاده‌ای نداد، پس مدل ارزیابی نشد. خطاها در جدول آمده است.";
        }
        else if (failed == 0 && noAnswer == 0)
        {
            verdict = $"Passed {passed} of {total}: the model called the right tool with the right arguments every time. {breakdownEn}";
            verdictFa = $"{passed} از {total} درست: مدل هر بار ابزار درست را با آرگومان‌های درست فراخوانی کرد. {breakdownFa}";
        }
        else if (failed == 0)
        {
            verdict = $"Every prompt that was answered passed ({passed}), but {noAnswer} got no answer from the provider, so this is not a full result. Run the test again. {breakdownEn}";
            verdictFa = $"هر پرسشی که پاسخ گرفت درست بود ({passed})، اما {noAnswer} پرسش از ارائه‌دهنده پاسخی نگرفت؛ پس نتیجه کامل نیست. آزمون را دوباره اجرا کنید. {breakdownFa}";
        }
        else
        {
            // Two thirds of the answered prompts: a model that is right most of the time still gets
            // plans wrong often enough that every plan must be read before it is approved.
            var usable = passed * 3 >= judged * 2;
            var advice = usable
                ? "Usable with care: expect mistakes, and read each plan before approving it."
                : "Not recommended: it often picks the wrong tool or the wrong arguments.";
            var adviceFa = usable
                ? "با احتیاط قابل‌استفاده است: انتظار اشتباه داشته باشید و هر برنامه را پیش از تأیید بخوانید."
                : "پیشنهاد نمی‌شود: اغلب ابزار یا آرگومان نادرست انتخاب می‌کند.";

            var unanswered = noAnswer > 0 ? $" {noAnswer} got no answer from the provider." : "";
            var unansweredFa = noAnswer > 0 ? $" برای {noAnswer} پرسش ارائه‌دهنده پاسخی نداد." : "";

            verdict = $"Passed {passed} of {judged} answered prompts. {advice}{unanswered} {breakdownEn}";
            verdictFa = $"{passed} از {judged} پرسشِ پاسخ‌داده‌شده درست بود. {adviceFa}{unansweredFa} {breakdownFa}";
        }

        return new AgentBenchReport(true, rows, model, passed, failed, noAnswer, verdict, verdictFa);
    }

    private static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
