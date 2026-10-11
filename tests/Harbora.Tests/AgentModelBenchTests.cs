using System.Text.Json;
using FluentAssertions;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Assistant;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// "Test the agent model": what the bench asks, and how it scores what comes back. The model is a
/// scripted <see cref="IAgentChatClient"/>, so every verdict here is a verdict on a known answer —
/// the point is that the scoring rule fails a model that is wrong and does not pass one that is merely
/// talkative. Nothing is executed by the bench, and a test below proves the tools it offers are
/// descriptions only.
/// </summary>
public class AgentModelBenchTests
{
    /// <summary>Answers each call from a script keyed by the user's prompt, and records what it was asked.</summary>
    private sealed class ScriptedClient(Func<string, AgentChatResponse> answer) : IAgentChatClient
    {
        public List<AgentChatRequest> Requests { get; } = [];
        public AssistantUnavailable? Unavailable { get; init; }
        public string? Model { get; init; } = "vendor/model:free";

        public Task<AssistantUnavailable?> GetAvailabilityAsync(CancellationToken ct) => Task.FromResult(Unavailable);
        public Task<string?> GetModelAsync(CancellationToken ct) => Task.FromResult(Model);

        public Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var prompt = request.Messages.Single(m => m.Role == AgentRole.User).Content!;
            return Task.FromResult(answer(prompt));
        }
    }

    private static AgentChatResponse Called(string tool, string arguments, string model = "vendor/model:free") =>
        new(true, new AgentChatMessage(AgentRole.Assistant, null, [new AgentToolCall("call_1", tool, arguments)]),
            model, new AgentChatUsage(10, 5), "tool_calls", null, null);

    private static AgentChatResponse Said(string text) =>
        new(true, new AgentChatMessage(AgentRole.Assistant, text), "vendor/model:free", null, "stop", null, null);

    /// <summary>The perfect model: the expected call for whichever case it is asked.</summary>
    private static AgentChatResponse Perfect(string prompt)
    {
        var testCase = AgentModelBench.Cases.Single(c => c.Prompt == prompt);
        return Called(testCase.Tool, JsonSerializer.Serialize(testCase.Arguments));
    }

    private static Task<AgentBenchReport> Run(Func<string, AgentChatResponse> answer, out ScriptedClient client)
    {
        client = new ScriptedClient(answer);
        return new AgentModelBench(client).RunAsync(CancellationToken.None);
    }

    // ---- the fixed prompts and fake tools --------------------------------------------------

    [Fact]
    public void There_are_six_prompts_three_persian_and_three_english_including_the_owners_examples()
    {
        var cases = AgentModelBench.Cases;

        cases.Should().HaveCount(6);
        cases.Count(c => c.Language == "fa").Should().Be(3);
        cases.Count(c => c.Language == "en").Should().Be(3);
        cases.Select(c => c.Prompt).Should().OnlyHaveUniqueItems()
            .And.Contain("یک دیتابیس Postgres به اسم shop-db بساز")
            .And.Contain("list my apps")
            .And.Contain("اپ shop را ری‌استارت کن");

        // The Persian prompts really are Persian, and the English really are not.
        cases.Where(c => c.Language == "fa").Should().OnlyContain(c => c.Prompt.Any(ch => ch >= '؀' && ch <= 'ۿ'));
        cases.Where(c => c.Language == "en").Should().OnlyContain(c => c.Prompt.All(ch => ch < '؀'));
    }

    [Fact]
    public void Every_expectation_names_a_fake_tool_and_arguments_that_tool_actually_declares()
    {
        foreach (var testCase in AgentModelBench.Cases)
        {
            var spec = AgentModelBench.ToolSpecs.SingleOrDefault(t => t.Name == testCase.Tool);
            spec.Should().NotBeNull($"the expected tool {testCase.Tool} must be offered to the model");

            using var schema = JsonDocument.Parse(spec!.ParametersJsonSchema);
            var properties = schema.RootElement.GetProperty("properties");
            foreach (var key in testCase.Arguments.Keys)
                properties.TryGetProperty(key, out _).Should().BeTrue(
                    $"{testCase.Tool} must declare \"{key}\" or the model is graded on an argument it was never offered");
        }
    }

    [Fact]
    public void The_fake_tools_are_valid_json_schema_objects_with_distinct_names_and_include_the_ones_the_design_lists()
    {
        var specs = AgentModelBench.ToolSpecs;

        specs.Select(s => s.Name).Should().OnlyHaveUniqueItems()
            .And.Contain(["list_resources", "create_database", "attach_database", "restart_app"]);
        foreach (var spec in specs)
        {
            spec.Description.Should().NotBeNullOrWhiteSpace();
            using var schema = JsonDocument.Parse(spec.ParametersJsonSchema);
            schema.RootElement.GetProperty("type").GetString().Should().Be("object", spec.Name);
        }
    }

    [Fact]
    public async Task Each_prompt_is_one_call_with_a_system_message_the_prompt_and_exactly_the_fake_tools()
    {
        await Run(Perfect, out var client);

        client.Requests.Should().HaveCount(6, "one model call per prompt, no more");
        foreach (var request in client.Requests)
        {
            request.Messages.Select(m => m.Role).Should().Equal(AgentRole.System, AgentRole.User);
            request.Tools.Should().BeSameAs(AgentModelBench.ToolSpecs);
        }
        client.Requests.Select(r => r.Messages[1].Content)
            .Should().Equal(AgentModelBench.Cases.Select(c => c.Prompt), "in the fixed order");
    }

    [Fact]
    public async Task Nothing_is_executed_the_tool_specs_are_descriptions_with_no_code_behind_them()
    {
        // AgentToolSpec is a record of three strings; the bench holds no IAgentTool and no service
        // provider. The only thing that could be "executed" is the model's call, and it is only read.
        typeof(AgentToolSpec).GetProperties().Select(p => p.PropertyType).Should().OnlyContain(t => t == typeof(string));
        // The bench's only collaborator is the chat client.
        typeof(AgentModelBench).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
            .Should().Equal([typeof(IAgentChatClient)]);

        var report = await Run(Perfect, out _);
        report.Passed.Should().Be(6);
    }

    // ---- scoring ---------------------------------------------------------------------------

    [Fact]
    public async Task A_model_that_makes_every_expected_call_passes_all_six()
    {
        var report = await Run(Perfect, out _);

        report.ShouldAllBe(AgentBenchOutcome.Passed);
        report.Ran.Should().BeTrue();
        report.Passed.Should().Be(6);
        report.Failed.Should().Be(0);
        report.NoAnswer.Should().Be(0);
        report.Model.Should().Be("vendor/model:free");
        report.Verdict.Should().StartWith("Passed 6 of 6").And.Contain("Persian 3/3, English 3/3");
        report.VerdictFa.Should().Contain("6 از 6").And.Contain("فارسی 3/3");
        report.Rows.Select(r => r.Prompt).Should().Equal(AgentModelBench.Cases.Select(c => c.Prompt));
        report.Rows.Select(r => r.Expected).Should().Contain("create_database(engine=\"postgres\", name=\"shop-db\")");
        report.Rows.Should().OnlyContain(r => r.Model == "vendor/model:free" && r.LatencyMs >= 0);
    }

    [Fact]
    public async Task A_wrong_tool_fails_even_with_plausible_arguments()
    {
        var report = await Run(prompt => prompt.Contains("ری‌استارت")
            ? Called("stop_app", """{"app":"shop"}""")
            : Perfect(prompt), out _);

        var row = report.Rows.Single(r => r.Outcome == AgentBenchOutcome.Failed);
        row.Prompt.Should().Contain("ری‌استارت");
        row.Observed.Should().Contain("stop_app").And.Contain("wrong tool");
        row.ObservedFa.Should().Contain("stop_app").And.Contain("ابزار اشتباه");
        report.Passed.Should().Be(5);
        report.Failed.Should().Be(1);
    }

    [Theory]
    [InlineData("""{"engine":"mysql","name":"shop-db"}""", "engine")]
    [InlineData("""{"engine":"postgres","name":"other-db"}""", "name")]
    [InlineData("""{"engine":"postgres"}""", "name")]
    [InlineData("""{"name":"shop-db"}""", "engine")]
    [InlineData("""{"engine":"postgres","name":null}""", "name")]
    public async Task A_wrong_or_missing_key_argument_fails_and_names_the_argument(string arguments, string culprit)
    {
        var report = await Run(prompt => prompt.StartsWith("یک دیتابیس")
            ? Called("create_database", arguments)
            : Perfect(prompt), out _);

        var row = report.Rows.Single(r => r.Outcome == AgentBenchOutcome.Failed);
        row.Observed.Should().Contain($"\"{culprit}\"");
        row.ObservedFa.Should().Contain($"«{culprit}»");
        report.Passed.Should().Be(5);
    }

    [Fact]
    public async Task Arguments_are_compared_ignoring_case_and_surrounding_spaces_and_extra_arguments_are_not_scored()
    {
        var report = await Run(prompt => prompt.StartsWith("یک دیتابیس")
            ? Called("create_database", """{"engine":" Postgres ","name":"SHOP-DB","version":"16","project":"shop"}""")
            : Perfect(prompt), out _);

        report.Passed.Should().Be(6);
    }

    [Fact]
    public void A_non_string_argument_is_compared_by_its_json_text()
    {
        // No shipped case expects a number, so this drives the scorer directly.
        var testCase = new AgentBenchCase("en", "scale shop to 3", "scale_app",
            new Dictionary<string, string> { ["app"] = "shop", ["replicas"] = "3" });

        AgentModelBench.Judge(testCase, Called("scale_app", """{"app":"shop","replicas":3}""")).Outcome
            .Should().Be(AgentBenchOutcome.Passed);
        AgentModelBench.Judge(testCase, Called("scale_app", """{"app":"shop","replicas":4}""")).Outcome
            .Should().Be(AgentBenchOutcome.Failed);
    }

    [Fact]
    public async Task Plain_text_instead_of_a_call_fails_and_quotes_what_the_model_said()
    {
        var report = await Run(prompt => prompt == "list my apps"
            ? Said("Sure! You have two apps: shop and blog.")
            : Perfect(prompt), out _);

        var row = report.Rows.Single(r => r.Outcome == AgentBenchOutcome.Failed);
        row.Observed.Should().Contain("called no tool").And.Contain("You have two apps");
        row.ObservedFa.Should().Contain("You have two apps");
        report.Passed.Should().Be(5);
    }

    [Fact]
    public async Task A_second_call_fails_even_when_the_first_was_right()
    {
        var report = await Run(prompt => prompt.Contains("ری‌استارت")
            ? new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, null,
                [new AgentToolCall("a", "restart_app", """{"app":"shop"}"""), new AgentToolCall("b", "deploy_app", """{"app":"shop"}""")]),
                "m", null, "tool_calls", null, null)
            : Perfect(prompt), out _);

        var row = report.Rows.Single(r => r.Outcome == AgentBenchOutcome.Failed);
        row.Observed.Should().Contain("2 tool calls").And.Contain("restart_app").And.Contain("deploy_app");
    }

    [Fact]
    public async Task Arguments_that_are_not_valid_json_or_not_an_object_fail()
    {
        var notJson = await Run(prompt => prompt == "list my apps" ? Called("list_resources", "{kind: apps") : Perfect(prompt), out _);
        notJson.Rows.Single(r => r.Outcome == AgentBenchOutcome.Failed).Observed.Should().Contain("not valid JSON");

        var notObject = await Run(prompt => prompt == "list my apps" ? Called("list_resources", """["apps"]""") : Perfect(prompt), out _);
        notObject.Rows.Single(r => r.Outcome == AgentBenchOutcome.Failed).Observed.Should().Contain("not a JSON object");
    }

    [Fact]
    public async Task A_model_that_only_ever_talks_fails_everything_and_is_not_recommended()
    {
        var report = await Run(_ => Said("I would create the database for you."), out _);

        report.ShouldAllBe(AgentBenchOutcome.Failed);
        report.Passed.Should().Be(0);
        report.Verdict.Should().Contain("Passed 0 of 6").And.Contain("Not recommended");
        report.VerdictFa.Should().Contain("پیشنهاد نمی‌شود");
    }

    // ---- a provider that does not answer is not the model getting it wrong ------------------

    [Fact]
    public async Task A_provider_failure_is_no_answer_with_the_providers_words_not_a_model_failure()
    {
        var report = await Run(prompt => prompt == "list my apps"
            ? AgentChatResponse.Failed("The AI provider is rate limiting requests (429).", "ارائه‌دهنده محدود کرده است (429).")
            : Perfect(prompt), out _);

        var row = report.Rows.Single(r => r.Outcome == AgentBenchOutcome.NoAnswer);
        row.Observed.Should().Contain("429");
        row.ObservedFa.Should().Contain("429").And.Contain("محدود");
        report.Failed.Should().Be(0, "a rate limit says nothing about whether the model can call tools");
        report.NoAnswer.Should().Be(1);
        report.Verdict.Should().Contain("not a full result").And.NotContain("Passed 6");
    }

    [Fact]
    public async Task When_nothing_got_an_answer_the_verdict_says_the_model_could_not_be_judged()
    {
        var report = await Run(_ => AgentChatResponse.Failed("boom", "بوم"), out _);

        report.NoAnswer.Should().Be(6);
        report.Passed.Should().Be(0);
        report.Verdict.Should().Contain("could not be judged");
        report.VerdictFa.Should().Contain("ارزیابی نشد");
    }

    [Fact]
    public async Task Failures_and_no_answers_are_both_counted_in_a_mixed_verdict()
    {
        var report = await Run(prompt => prompt switch
        {
            "list my apps" => Said("here you go"),
            var p when p.Contains("ری‌استارت") => AgentChatResponse.Failed("timeout", "زمان"),
            _ => Perfect(prompt)
        }, out _);

        report.Passed.Should().Be(4);
        report.Failed.Should().Be(1);
        report.NoAnswer.Should().Be(1);
        report.Verdict.Should().Contain("Passed 4 of 5 answered prompts").And.Contain("Usable with care")
            .And.Contain("1 got no answer").And.Contain("Persian 2/3, English 2/3");
    }

    // ---- verdict thresholds ----------------------------------------------------------------

    [Fact]
    public async Task Two_thirds_of_the_answered_prompts_is_usable_with_care_and_less_is_not_recommended()
    {
        // 4 of 6: exactly two thirds.
        var usable = await Run(prompt => prompt is "list my apps" or "Create a MySQL database called blog-db"
            ? Said("no") : Perfect(prompt), out _);
        usable.Passed.Should().Be(4);
        usable.Verdict.Should().Contain("Usable with care");
        usable.VerdictFa.Should().Contain("با احتیاط قابل‌استفاده");

        // 3 of 6: below it.
        var poor = await Run(prompt => prompt is "list my apps" or "Create a MySQL database called blog-db" or "Attach the database blog-db to the app blog"
            ? Said("no") : Perfect(prompt), out _);
        poor.Passed.Should().Be(3);
        poor.Verdict.Should().Contain("Not recommended");
    }

    [Fact]
    public async Task The_persian_and_english_shares_are_reported_separately_so_a_persian_only_failure_is_visible()
    {
        var report = await Run(prompt => AgentModelBench.Cases.Single(c => c.Prompt == prompt).Language == "fa"
            ? Said("متن") : Perfect(prompt), out _);

        report.Passed.Should().Be(3);
        report.Verdict.Should().Contain("Persian 0/3, English 3/3");
        report.VerdictFa.Should().Contain("فارسی 0/3، انگلیسی 3/3");
    }

    // ---- availability, budget, cancellation ------------------------------------------------

    [Fact]
    public async Task When_the_agent_is_unavailable_nothing_is_asked_and_the_reason_is_the_verdict()
    {
        var client = new ScriptedClient(Perfect)
        {
            Unavailable = new AssistantUnavailable("The assistant is turned off.", "دستیار خاموش است.")
        };

        var report = await new AgentModelBench(client).RunAsync(CancellationToken.None);

        client.Requests.Should().BeEmpty("the bench spends the owner's requests; it does not spend them on a refusal");
        report.Ran.Should().BeFalse();
        report.Rows.Should().BeEmpty();
        report.Verdict.Should().Be("The assistant is turned off.");
        report.VerdictFa.Should().Be("دستیار خاموش است.");
    }

    [Fact]
    public async Task Running_out_of_the_time_budget_reports_the_remaining_prompts_as_unanswered()
    {
        var calls = 0;
        var client = new HangingClient(() => Interlocked.Increment(ref calls));
        var bench = new AgentModelBench(client) { TimeBudget = TimeSpan.FromMilliseconds(60) };

        var report = await bench.RunAsync(CancellationToken.None);

        report.Total.Should().Be(6, "every prompt gets a row, answered or not");
        report.NoAnswer.Should().Be(6);
        report.Rows.Should().OnlyContain(r => r.Outcome == AgentBenchOutcome.NoAnswer);
        report.Rows.First().Observed.Should().Contain("budget");
        report.Rows.First().ObservedFa.Should().Contain("زمان");
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed_into_a_report()
    {
        var client = new HangingClient(() => { });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => new AgentModelBench(client).RunAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class HangingClient(Action onCall) : IAgentChatClient
    {
        public Task<AssistantUnavailable?> GetAvailabilityAsync(CancellationToken ct) => Task.FromResult<AssistantUnavailable?>(null);
        public Task<string?> GetModelAsync(CancellationToken ct) => Task.FromResult<string?>("m/x");

        public async Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, CancellationToken ct)
        {
            onCall();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}

internal static class AgentBenchAssertions
{
    /// <summary>Every row of the report has the same outcome.</summary>
    public static void ShouldAllBe(this AgentBenchReport report, AgentBenchOutcome outcome)
    {
        report.Rows.Should().HaveCount(6);
        report.Rows.Should().OnlyContain(r => r.Outcome == outcome,
            string.Join("; ", report.Rows.Select(r => $"{r.Prompt} -> {r.Outcome}: {r.Observed}")));
    }
}
