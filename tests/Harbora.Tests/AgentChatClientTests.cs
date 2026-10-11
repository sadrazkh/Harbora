using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Assistant;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The in-panel agent's connection to a model, with the REAL <see cref="OpenAiCompatibleAgentChatClient"/>
/// running against a scripted provider at the HTTP layer. No test here can reach a real provider: the
/// only handler anywhere is <see cref="RecordingChatHandler"/>.
///
/// <para>
/// The cases that matter most are the failures. A client that turns a rate limit, a timeout or a
/// half-empty reply into something that looks like an answer is the defect this codebase keeps having
/// to remove, so each failure mode is its own test and each asserts on both languages.
/// </para>
/// </summary>
public class AgentChatClientTests
{
    // ---- harness ---------------------------------------------------------------------------

    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public string Unprotect(string ciphertext) =>
            ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : ciphertext;

        public byte[] DeriveKey(string purpose) => new byte[32];
    }

    private sealed record Rig(
        OpenAiCompatibleAgentChatClient Client, RecordingChatHandler Handler,
        RecordingChatHandler.Factory Factory, HarboraDbContext Db);

    /// <summary>
    /// A client over an in-memory settings table. <paramref name="settings"/> overrides the defaults
    /// (a configured OpenRouter, key "sk-test"); a null value removes that default.
    /// </summary>
    private static Rig Build(
        RecordingChatHandler handler, Dictionary<string, string?>? settings = null, TimeSpan? timeout = null)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("agent-chat-" + Guid.NewGuid()).Options);
        var protector = new PassthroughProtector();

        var values = new Dictionary<string, string?>
        {
            [AssistantSettingKeys.Enabled] = "true",
            [AssistantSettingKeys.Provider] = "openai",
            [AssistantSettingKeys.Model] = "assistant/model:free",
            [AssistantSettingKeys.ApiKey] = protector.Protect("sk-test"),
            [AssistantSettingKeys.BaseUrl] = "https://openrouter.ai/api"
        };
        foreach (var (key, value) in settings ?? [])
            values[key] = value;

        foreach (var (key, value) in values.Where(v => v.Value is not null))
            db.Settings.Add(new Setting { Key = key, Value = value!, IsSecret = key == AssistantSettingKeys.ApiKey });
        db.SaveChanges();

        var factory = new RecordingChatHandler.Factory(handler);
        var assistant = new AssistantService(
            db, new AssistantClient(factory), protector, NullLogger<AssistantService>.Instance);

        var client = new OpenAiCompatibleAgentChatClient(assistant, new AgentSettingsService(db), factory)
        {
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(90)
        };
        return new Rig(client, handler, factory, db);
    }

    private static AgentChatRequest Ask(string text = "hello", IReadOnlyList<AgentToolSpec>? tools = null, int max = 2048) =>
        new([new AgentChatMessage(AgentRole.User, text)], tools ?? [], max);

    private static readonly AgentToolSpec RestartApp = new(
        "restart_app", "Restart a running app.",
        """{"type":"object","properties":{"app":{"type":"string"}},"required":["app"]}""");

    private static void ShouldFailInBothLanguages(AgentChatResponse response)
    {
        response.Ok.Should().BeFalse();
        response.Message.Should().BeNull("a failure carries no message — nothing may read as an answer");
        response.Error.Should().NotBeNullOrWhiteSpace();
        response.ErrorFa.Should().NotBeNullOrWhiteSpace();
        response.ErrorFa.Should().NotBe(response.Error, "the Persian text is its own, not the English repeated");
    }

    // ---- request: URL, auth, headers --------------------------------------------------------

    [Theory]
    [InlineData(null, "https://api.openai.com/v1/chat/completions")]
    [InlineData("", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1/", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("  https://openrouter.ai/api/V1  ", "https://openrouter.ai/api/V1/chat/completions")]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://gateway.example.com/openai", "https://gateway.example.com/openai/v1/chat/completions")]
    public async Task The_url_is_the_base_plus_v1_chat_completions_without_doubling_v1(string? baseUrl, string expected)
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AssistantSettingKeys.BaseUrl] = baseUrl });

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error);
        rig.Handler.Only.Url.ToString().Should().Be(expected);
        rig.Handler.Only.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task It_asks_the_factory_for_its_own_named_client_not_the_default_one()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")));

        await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        rig.Factory.Names.Should().Equal(OpenAiCompatibleAgentChatClient.HttpClientName);
    }

    [Fact]
    public async Task The_key_goes_out_as_a_bearer_token_decrypted()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")));

        await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        rig.Handler.Only.Authorization.Should().NotBeNull();
        rig.Handler.Only.Authorization!.Scheme.Should().Be("Bearer");
        rig.Handler.Only.Authorization.Parameter.Should().Be("sk-test", "the stored value is ciphertext; the wire gets the key");
    }

    [Fact]
    public async Task A_local_endpoint_with_no_key_is_called_without_an_authorization_header()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AssistantSettingKeys.ApiKey] = null, [AssistantSettingKeys.BaseUrl] = "http://localhost:11434" });

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error);
        rig.Handler.Only.Authorization.Should().BeNull();
    }

    [Theory]
    [InlineData("https://openrouter.ai/api", true)]
    [InlineData("https://openrouter.ai/api/v1", true)]
    [InlineData("https://OpenRouter.ai/api", true)]
    [InlineData("https://api.openai.com", false)]
    [InlineData("http://localhost:11434", false)]
    [InlineData("https://notopenrouter.ai/api", false)]
    [InlineData("https://openrouter.ai.evil.example/api", false)]
    public async Task The_openrouter_headers_are_sent_to_openrouter_and_to_nobody_else(string baseUrl, bool expected)
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AssistantSettingKeys.BaseUrl] = baseUrl });

        await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        var headers = rig.Handler.Only.Headers;
        if (expected)
        {
            headers.Should().ContainKey("HTTP-Referer");
            headers["X-Title"].Should().Be("Harbora");
        }
        else
        {
            headers.Should().NotContainKey("HTTP-Referer");
            headers.Should().NotContainKey("X-Title");
        }
    }

    // ---- request: body ---------------------------------------------------------------------

    [Fact]
    public async Task The_body_carries_model_messages_and_max_tokens_and_no_tools_when_there_are_none()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")));

        await rig.Client.CompleteAsync(
            new AgentChatRequest(
                [new AgentChatMessage(AgentRole.System, "be brief"), new AgentChatMessage(AgentRole.User, "سلام")],
                [], 321),
            CancellationToken.None);

        var body = rig.Handler.Only.Json;
        body["model"]!.GetValue<string>().Should().Be("assistant/model:free");
        body["max_tokens"]!.GetValue<int>().Should().Be(321);
        body.ContainsKey("tools").Should().BeFalse("an empty tools array is rejected by some providers");
        body.ContainsKey("tool_choice").Should().BeFalse();

        var messages = body["messages"]!.AsArray();
        messages.Should().HaveCount(2);
        messages[0]!["role"]!.GetValue<string>().Should().Be("system");
        messages[0]!["content"]!.GetValue<string>().Should().Be("be brief");
        messages[1]!["role"]!.GetValue<string>().Should().Be("user");
        messages[1]!["content"]!.GetValue<string>().Should().Be("سلام", "Persian goes out as itself, not escaped into something else");
    }

    [Fact]
    public async Task Tools_go_out_as_functions_with_parameters_as_a_json_object_and_tool_choice_auto()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")));

        await rig.Client.CompleteAsync(Ask(tools: [RestartApp]), CancellationToken.None);

        var body = rig.Handler.Only.Json;
        body["tool_choice"]!.GetValue<string>().Should().Be("auto");

        var tool = body["tools"]!.AsArray().Single()!;
        tool["type"]!.GetValue<string>().Should().Be("function");
        tool["function"]!["name"]!.GetValue<string>().Should().Be("restart_app");
        tool["function"]!["description"]!.GetValue<string>().Should().Be("Restart a running app.");

        var parameters = tool["function"]!["parameters"]!;
        parameters.Should().BeAssignableTo<JsonObject>("a schema sent as a string is not a schema to the provider");
        parameters["type"]!.GetValue<string>().Should().Be("object");
        parameters["properties"]!["app"]!["type"]!.GetValue<string>().Should().Be("string");
        parameters["required"]!.AsArray().Single()!.GetValue<string>().Should().Be("app");
    }

    [Fact]
    public async Task A_full_tool_call_round_trip_is_sent_back_in_the_exact_openai_shape()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("done")));

        await rig.Client.CompleteAsync(
            new AgentChatRequest(
                [
                    new AgentChatMessage(AgentRole.System, "rules"),
                    new AgentChatMessage(AgentRole.User, "restart shop"),
                    new AgentChatMessage(AgentRole.Assistant, null,
                        [new AgentToolCall("call_abc", "restart_app", """{"app":"shop"}""")]),
                    new AgentChatMessage(AgentRole.Tool, "added as step 1; nothing has run yet", ToolCallId: "call_abc"),
                    new AgentChatMessage(AgentRole.User, "thanks")
                ],
                [RestartApp]),
            CancellationToken.None);

        var messages = rig.Handler.Only.Json["messages"]!.AsArray();
        messages.Should().HaveCount(5);

        var assistant = messages[2]!.AsObject();
        assistant["role"]!.GetValue<string>().Should().Be("assistant");
        assistant.ContainsKey("content").Should().BeTrue();
        assistant["content"].Should().BeNull("OpenAI's own shape for a turn that only called tools");
        var call = assistant["tool_calls"]!.AsArray().Single()!;
        call["id"]!.GetValue<string>().Should().Be("call_abc");
        call["type"]!.GetValue<string>().Should().Be("function");
        call["function"]!["name"]!.GetValue<string>().Should().Be("restart_app");
        call["function"]!["arguments"]!.GetValueKind().Should().Be(JsonValueKind.String,
            "the arguments travel as the JSON text the model wrote, not re-parsed into an object");
        call["function"]!["arguments"]!.GetValue<string>().Should().Be("""{"app":"shop"}""");

        var tool = messages[3]!.AsObject();
        tool["role"]!.GetValue<string>().Should().Be("tool");
        tool["tool_call_id"]!.GetValue<string>().Should().Be("call_abc");
        tool["content"]!.GetValue<string>().Should().Be("added as step 1; nothing has run yet");

        messages[4]!["role"]!.GetValue<string>().Should().Be("user");
    }

    [Fact]
    public async Task An_assistant_turn_with_text_and_calls_keeps_both_and_empty_arguments_become_an_empty_object()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("ok")));

        await rig.Client.CompleteAsync(
            new AgentChatRequest(
                [new AgentChatMessage(AgentRole.Assistant, "looking", [new AgentToolCall("c1", "list_resources", "")])],
                []),
            CancellationToken.None);

        var assistant = rig.Handler.Only.Json["messages"]!.AsArray()[0]!.AsObject();
        assistant["content"]!.GetValue<string>().Should().Be("looking");
        assistant["tool_calls"]!.AsArray().Single()!["function"]!["arguments"]!.GetValue<string>().Should().Be("{}");
    }

    [Fact]
    public async Task Models_is_sent_only_when_fallbacks_are_configured_primary_first()
    {
        var withFallbacks = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new()
            {
                [AgentSettingKeys.Model] = "main/model:free",
                [AgentSettingKeys.FallbackModels] = "second/model:free, third/model:free"
            });
        await withFallbacks.Client.CompleteAsync(Ask(), CancellationToken.None);

        var sent = withFallbacks.Handler.Only.Json;
        sent["model"]!.GetValue<string>().Should().Be("main/model:free");
        sent["models"]!.AsArray().Select(m => m!.GetValue<string>())
            .Should().Equal("main/model:free", "second/model:free", "third/model:free");

        var without = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AgentSettingKeys.Model] = "main/model:free" });
        await without.Client.CompleteAsync(Ask(), CancellationToken.None);

        without.Handler.Only.Json.ContainsKey("models").Should().BeFalse(
            "another OpenAI-compatible endpoint may reject a field it does not know");
    }

    [Fact]
    public async Task A_fallback_that_repeats_the_primary_is_not_a_fallback_and_does_not_trigger_models()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AgentSettingKeys.Model] = "main/model:free", [AgentSettingKeys.FallbackModels] = "MAIN/model:free" });

        await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        rig.Handler.Only.Json.ContainsKey("models").Should().BeFalse();
    }

    [Fact]
    public async Task The_agent_model_wins_and_blank_falls_back_to_the_assistant_model()
    {
        var own = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AgentSettingKeys.Model] = "agent/model:free" });
        await own.Client.CompleteAsync(Ask(), CancellationToken.None);
        own.Handler.Only.Json["model"]!.GetValue<string>().Should().Be("agent/model:free");

        var blank = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AgentSettingKeys.Model] = "   " });
        await blank.Client.CompleteAsync(Ask(), CancellationToken.None);
        blank.Handler.Only.Json["model"]!.GetValue<string>().Should().Be("assistant/model:free");
    }

    // ---- response --------------------------------------------------------------------------

    [Fact]
    public async Task A_text_reply_is_parsed_with_model_usage_and_finish_reason()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("سلام، چطور کمک کنم؟", "answering/model:free")));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error);
        response.Error.Should().BeNull();
        response.ErrorFa.Should().BeNull();
        response.Message!.Role.Should().Be(AgentRole.Assistant);
        response.Message.Content.Should().Be("سلام، چطور کمک کنم؟");
        response.Message.ToolCalls.Should().BeNull();
        response.Model.Should().Be("answering/model:free");
        response.FinishReason.Should().Be("stop");
        response.Usage.Should().Be(new AgentChatUsage(12, 5));
    }

    [Fact]
    public async Task A_tool_call_reply_with_null_content_is_an_answer_not_an_empty_one()
    {
        var rig = Build(RecordingChatHandler.Always(
            RecordingChatHandler.ToolCallReply("create_database", """{"engine":"postgres","name":"shop-db"}""", "call_77")));

        var response = await rig.Client.CompleteAsync(Ask(tools: [RestartApp]), CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error);
        response.Message!.Content.Should().BeNull();
        response.FinishReason.Should().Be("tool_calls");
        response.Usage.Should().Be(new AgentChatUsage(40, 9));

        var call = response.Message.ToolCalls!.Single();
        call.Id.Should().Be("call_77");
        call.Name.Should().Be("create_database");
        call.ArgumentsJson.Should().Be("""{"engine":"postgres","name":"shop-db"}""", "the model's raw text, unparsed");
    }

    [Fact]
    public async Task Several_tool_calls_and_text_beside_them_all_come_through_in_order()
    {
        const string body = """
            {"model":"m/x","choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":"On it.",
              "tool_calls":[
                {"id":"a","type":"function","function":{"name":"one","arguments":"{}"}},
                {"id":"b","type":"function","function":{"name":"two","arguments":"{\"x\":1}"}}]}}]}
            """;
        var rig = Build(RecordingChatHandler.Always(body));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        response.Message!.Content.Should().Be("On it.");
        response.Message.ToolCalls!.Select(c => (c.Id, c.Name, c.ArgumentsJson))
            .Should().Equal(("a", "one", "{}"), ("b", "two", """{"x":1}"""));
        response.Usage.Should().BeNull("the provider sent none; zero would be a made-up number");
    }

    [Fact]
    public async Task Object_shaped_arguments_are_normalised_to_their_raw_json_text()
    {
        const string body = """
            {"model":"m/x","choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"a","type":"function","function":{"name":"create_database","arguments":{"engine":"postgres","name":"shop-db"}}}]}}]}
            """;
        var rig = Build(RecordingChatHandler.Always(body));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error);
        var arguments = response.Message!.ToolCalls!.Single().ArgumentsJson;
        JsonNode.Parse(arguments)!["engine"]!.GetValue<string>().Should().Be("postgres");
        JsonNode.Parse(arguments)!["name"]!.GetValue<string>().Should().Be("shop-db");
    }

    [Fact]
    public async Task A_call_with_no_id_gets_one_and_a_call_with_no_arguments_gets_an_empty_object()
    {
        const string body = """
            {"model":"m/x","choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
              "tool_calls":[{"type":"function","function":{"name":"list_resources"}},
                            {"id":"","type":"function","function":{"name":"get_app","arguments":null}}]}}]}
            """;
        var rig = Build(RecordingChatHandler.Always(body));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        var calls = response.Message!.ToolCalls!;
        calls.Select(c => c.Id).Should().OnlyHaveUniqueItems().And.OnlyContain(id => !string.IsNullOrEmpty(id));
        calls.Select(c => c.ArgumentsJson).Should().Equal("{}", "{}");
    }

    [Fact]
    public async Task Content_sent_as_an_array_of_text_parts_is_joined()
    {
        const string body = """
            {"model":"m/x","choices":[{"finish_reason":"stop","message":{"role":"assistant",
              "content":[{"type":"text","text":"Hello, "},{"type":"text","text":"world"}]}}]}
            """;
        var rig = Build(RecordingChatHandler.Always(body));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        response.Message!.Content.Should().Be("Hello, world");
    }

    [Fact]
    public async Task The_answering_model_can_differ_from_the_one_asked_for_and_is_reported_as_answered()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi", "second/model:free")),
            new() { [AgentSettingKeys.Model] = "main/model:free", [AgentSettingKeys.FallbackModels] = "second/model:free" });

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        response.Model.Should().Be("second/model:free");
    }

    // ---- failures: always values, always bilingual -----------------------------------------

    [Fact]
    public async Task A_non_2xx_carries_the_providers_own_error_message()
    {
        var rig = Build(RecordingChatHandler.Always(
            """{"error":{"message":"No endpoints found for vendor/gone:free.","code":404}}""", HttpStatusCode.NotFound));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("404").And.Contain("No endpoints found for vendor/gone:free.");
        response.ErrorFa.Should().Contain("404").And.Contain("No endpoints found for vendor/gone:free.");
    }

    [Fact]
    public async Task A_429_says_rate_limited_plainly_in_both_languages_and_keeps_the_upstream_reason()
    {
        var rig = Build(RecordingChatHandler.Always(
            """{"error":{"message":"Provider returned error","code":429,"metadata":{"raw":"vendor/model:free is temporarily rate-limited upstream","provider_name":"Venice"}}}""",
            HttpStatusCode.TooManyRequests));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("rate limit").And.Contain("429")
            .And.Contain("temporarily rate-limited upstream");
        response.ErrorFa.Should().Contain("429").And.Contain("محدود")
            .And.Contain("temporarily rate-limited upstream");
    }

    [Fact]
    public async Task A_429_without_a_json_body_is_still_a_rate_limit_message()
    {
        var rig = Build(new RecordingChatHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("rate limit");
    }

    [Fact]
    public async Task A_non_json_error_body_is_quoted_trimmed_so_a_gateway_page_can_be_recognised()
    {
        var rig = Build(RecordingChatHandler.Always(
            "<html>\n  <body>502 Bad   Gateway " + new string('x', 600) + "</body></html>", HttpStatusCode.BadGateway));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("502").And.Contain("Bad Gateway", "whitespace is collapsed");
        response.Error!.Length.Should().BeLessThan(500, "it is going onto a page");
    }

    [Fact]
    public async Task A_timeout_is_a_failure_value_naming_the_wait_in_both_languages()
    {
        var hung = new RecordingChatHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return RecordingChatHandler.Reply("never");
        });
        var rig = Build(hung, timeout: TimeSpan.FromMilliseconds(50));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("did not answer");
        response.ErrorFa.Should().Contain("پاسخ نداد");
    }

    [Fact]
    public void The_shipped_timeout_is_ninety_seconds()
    {
        var fresh = new OpenAiCompatibleAgentChatClient(null!, null!, new RecordingChatHandler.Factory(Always()));

        fresh.RequestTimeout.Should().Be(TimeSpan.FromSeconds(90));

        static RecordingChatHandler Always() => RecordingChatHandler.Always("");
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_dressed_up_as_a_provider_timeout()
    {
        var hung = new RecordingChatHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return RecordingChatHandler.Reply("never");
        });
        var rig = Build(hung);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => rig.Client.CompleteAsync(Ask(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_connection_that_cannot_be_made_is_a_failure_value_with_the_reason()
    {
        var rig = Build(new RecordingChatHandler(_ => throw new HttpRequestException("Connection refused")));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("could not be reached").And.Contain("Connection refused");
        response.ErrorFa.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task A_200_that_is_not_json_is_a_failure()
    {
        var rig = Build(RecordingChatHandler.Always("<html><body>Welcome to nginx!</body></html>"));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("not JSON").And.Contain("Welcome to nginx!");
    }

    [Fact]
    public async Task A_200_whose_body_is_not_an_object_is_a_failure()
    {
        var rig = Build(RecordingChatHandler.Always("[1,2,3]"));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
    }

    [Fact]
    public async Task A_200_carrying_an_error_object_is_a_failure_even_with_choices_beside_it()
    {
        var rig = Build(RecordingChatHandler.Always(
            """{"error":{"message":"Upstream timed out","code":524},"choices":[{"message":{"role":"assistant","content":"partial"}}]}"""));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("Upstream timed out");
        response.ErrorFa.Should().Contain("Upstream timed out");
    }

    [Fact]
    public async Task A_200_with_an_error_string_is_a_failure()
    {
        var rig = Build(RecordingChatHandler.Always("""{"error":"model overloaded"}"""));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("model overloaded");
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"model":"m/x"}""")]
    [InlineData("""{"choices":null}""")]
    public async Task A_reply_with_no_choices_is_a_failure(string body)
    {
        var rig = Build(RecordingChatHandler.Always(body));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("no choices");
    }

    [Fact]
    public async Task A_choice_with_an_error_and_no_message_is_a_failure_with_that_error()
    {
        var rig = Build(RecordingChatHandler.Always(
            """{"choices":[{"finish_reason":"error","error":{"message":"stream dropped","code":500}}]}"""));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("stream dropped");
    }

    [Theory]
    [InlineData("""{"model":"m/x","choices":[{"finish_reason":"length","message":{"role":"assistant","content":""}}]}""")]
    [InlineData("""{"model":"m/x","choices":[{"finish_reason":"length","message":{"role":"assistant","content":null}}]}""")]
    [InlineData("""{"model":"m/x","choices":[{"finish_reason":"length","message":{"role":"assistant","content":"  ","tool_calls":[]}}]}""")]
    public async Task A_reply_with_neither_words_nor_a_call_is_a_failure_not_a_blank_answer(string body)
    {
        var rig = Build(RecordingChatHandler.Always(body));

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("neither text nor a tool call").And.Contain("length");
    }

    [Fact]
    public async Task A_base_url_that_is_not_a_url_fails_before_anything_is_sent()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AssistantSettingKeys.BaseUrl] = "not a url" });

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("not a valid");
        rig.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tool_whose_schema_is_not_json_fails_the_call_naming_the_tool_and_sends_nothing()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")));

        var response = await rig.Client.CompleteAsync(
            Ask(tools: [new AgentToolSpec("broken_tool", "x", "{not json")]), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("broken_tool");
        rig.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task When_the_agent_is_unavailable_the_call_is_refused_and_nothing_is_sent()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AssistantSettingKeys.Enabled] = "false" });

        var response = await rig.Client.CompleteAsync(Ask(), CancellationToken.None);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("turned off");
        rig.Handler.Requests.Should().BeEmpty("a check that only guards the UI is not a check");
    }

    // ---- availability and model ------------------------------------------------------------

    [Fact]
    public async Task A_fully_configured_agent_is_available()
    {
        var rig = Build(RecordingChatHandler.Always(""));

        (await rig.Client.GetAvailabilityAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_disabled_assistant_makes_the_agent_unavailable_in_both_languages()
    {
        var rig = Build(RecordingChatHandler.Always(""), new() { [AssistantSettingKeys.Enabled] = "false" });

        var why = await rig.Client.GetAvailabilityAsync(CancellationToken.None);

        why.Should().NotBeNull();
        why!.Reason.Should().Contain("turned off");
        why.ReasonFa.Should().Contain("خاموش");
    }

    [Fact]
    public async Task No_provider_makes_the_agent_unavailable()
    {
        var rig = Build(RecordingChatHandler.Always(""), new() { [AssistantSettingKeys.Provider] = "" });

        var why = await rig.Client.GetAvailabilityAsync(CancellationToken.None);

        why!.Reason.Should().Contain("provider");
        why.ReasonFa.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_anthropic_provider_is_refused_with_the_openrouter_hint_in_both_languages()
    {
        var rig = Build(RecordingChatHandler.Always(""), new() { [AssistantSettingKeys.Provider] = "anthropic" });

        var why = await rig.Client.GetAvailabilityAsync(CancellationToken.None);

        why.Should().NotBeNull();
        why!.Reason.Should().Contain("OpenAI-compatible provider such as OpenRouter");
        why.ReasonFa.Should().Contain("OpenRouter").And.Contain("OpenAI");
    }

    [Fact]
    public async Task Anthropic_is_refused_before_a_missing_key_because_the_key_would_not_help()
    {
        var rig = Build(RecordingChatHandler.Always(""),
            new() { [AssistantSettingKeys.Provider] = "anthropic", [AssistantSettingKeys.ApiKey] = null });

        var why = await rig.Client.GetAvailabilityAsync(CancellationToken.None);

        why!.Reason.Should().Contain("OpenRouter");
    }

    [Fact]
    public async Task A_disabled_anthropic_setup_says_it_is_turned_off_first()
    {
        var rig = Build(RecordingChatHandler.Always(""),
            new() { [AssistantSettingKeys.Provider] = "anthropic", [AssistantSettingKeys.Enabled] = "false" });

        (await rig.Client.GetAvailabilityAsync(CancellationToken.None))!.Reason.Should().Contain("turned off");
    }

    [Fact]
    public async Task With_no_model_anywhere_the_agent_is_unavailable_and_says_which_model()
    {
        var rig = Build(RecordingChatHandler.Always(""), new() { [AssistantSettingKeys.Model] = null });

        var why = await rig.Client.GetAvailabilityAsync(CancellationToken.None);

        why!.Reason.Should().Contain("No agent model");
        why.ReasonFa.Should().Contain("مدل");
        (await rig.Client.GetModelAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task An_agent_model_alone_is_enough_when_the_assistant_has_none()
    {
        var rig = Build(RecordingChatHandler.Always(""),
            new() { [AssistantSettingKeys.Model] = null, [AgentSettingKeys.Model] = "agent/only:free" });

        (await rig.Client.GetAvailabilityAsync(CancellationToken.None)).Should().BeNull();
        (await rig.Client.GetModelAsync(CancellationToken.None)).Should().Be("agent/only:free");
    }

    [Fact]
    public async Task A_missing_key_on_a_remote_endpoint_is_reported_but_not_on_a_local_one()
    {
        var remote = Build(RecordingChatHandler.Always(""), new() { [AssistantSettingKeys.ApiKey] = null });
        (await remote.Client.GetAvailabilityAsync(CancellationToken.None))!.Reason.Should().Contain("API key");

        var local = Build(RecordingChatHandler.Always(""),
            new() { [AssistantSettingKeys.ApiKey] = null, [AssistantSettingKeys.BaseUrl] = "http://localhost:11434" });
        (await local.Client.GetAvailabilityAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task The_model_shown_is_the_one_the_next_call_asks_for_first()
    {
        var rig = Build(RecordingChatHandler.Always(""));
        (await rig.Client.GetModelAsync(CancellationToken.None)).Should().Be("assistant/model:free");

        var own = Build(RecordingChatHandler.Always(""),
            new() { [AgentSettingKeys.Model] = " agent/model:free ", [AgentSettingKeys.FallbackModels] = "b/x:free" });
        (await own.Client.GetModelAsync(CancellationToken.None)).Should().Be("agent/model:free");
    }

    [Fact]
    public void The_client_is_wired_into_di_against_the_contract()
    {
        // A throwaway master key: registration refuses to run without one.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harbora:MasterKey"] = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData("tests"u8.ToArray()))
            })
            .Build();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Harbora.Infrastructure.DependencyInjection.AddHarboraInfrastructure(services, config);

        services.Should().Contain(d => d.ServiceType == typeof(IAgentChatClient)
            && d.ImplementationType == typeof(OpenAiCompatibleAgentChatClient)
            && d.Lifetime == Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped);
        services.Should().Contain(d => d.ServiceType == typeof(AgentModelBench));
        services.Should().Contain(d => d.ServiceType == typeof(AgentSettingsService));
        services.Any(d => d.ServiceType == typeof(IHttpClientFactory)).Should().BeTrue(
            "the named client is created through the factory AddHttpClient registers");
    }
}
