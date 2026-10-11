using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Assistant;
using Harbora.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Harbora.Tests.AgentChatClientTests;
using static Harbora.Tests.Fakes.SseScript;

namespace Harbora.Tests;

/// <summary>
/// The streamed round trip, with the REAL <see cref="OpenAiCompatibleAgentChatClient"/> reading a
/// scripted server-sent-events body through a real <c>HttpClient</c> (<see cref="SseScript"/>): cut
/// mid-line and mid-character, paused, abandoned, or cut off. No test here can reach a real provider.
///
/// <para>
/// What has to hold: the text pieces arrive in order; the returned response is the one the whole-reply
/// call would have produced for the same answer; and every way a stream can go wrong — an error in
/// it, a silence, a cut — is a failure value in both languages, never a half-answer that looks whole.
/// </para>
/// </summary>
public class AgentChatClientStreamingTests
{
    private sealed class Pieces
    {
        public List<string> Seen { get; } = [];
        public Func<string, Task> Callback => text =>
        {
            Seen.Add(text);
            return Task.CompletedTask;
        };
    }

    private static RecordingChatHandler Serving(Func<SseScript> script) =>
        new(_ => script().ToResponse());

    private static string Events(params string[] chunks) => string.Concat(chunks.Select(Event));

    private static async Task<AgentChatResponse> Stream(Rig rig, Pieces pieces, AgentChatRequest? request = null,
        CancellationToken ct = default) =>
        await rig.Client.CompleteAsync(request ?? Ask(tools: [RestartApp]), pieces.Callback, ct);

    // ---- the request -----------------------------------------------------------------------

    [Fact]
    public async Task Streaming_asks_for_a_stream_with_usage_and_is_otherwise_the_request_the_whole_call_sends()
    {
        var streaming = Build(Serving(() => new SseScript().Send(Events(Chunk("hi", "stop")) + Done)),
            new() { [AgentSettingKeys.Model] = "main/model:free", [AgentSettingKeys.FallbackModels] = "second/model:free" });
        await Stream(streaming, new Pieces());

        var whole = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")),
            new() { [AgentSettingKeys.Model] = "main/model:free", [AgentSettingKeys.FallbackModels] = "second/model:free" });
        await whole.Client.CompleteAsync(Ask(tools: [RestartApp]), CancellationToken.None);

        var sent = streaming.Handler.Only.Json;
        sent["stream"]!.GetValue<bool>().Should().BeTrue();
        sent["stream_options"]!["include_usage"]!.GetValue<bool>().Should().BeTrue();

        var wholeSent = whole.Handler.Only.Json;
        wholeSent.ContainsKey("stream").Should().BeFalse("the whole-reply request must not change");
        wholeSent.ContainsKey("stream_options").Should().BeFalse();

        // Everything else is the same request: model, models, messages, tools, tool_choice, max_tokens.
        sent.Remove("stream");
        sent.Remove("stream_options");
        sent.ToJsonString().Should().Be(wholeSent.ToJsonString());
        streaming.Handler.Only.Url.Should().Be(whole.Handler.Only.Url);
    }

    [Fact]
    public async Task Streaming_carries_the_same_key_and_openrouter_headers()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("hi", "stop")) + Done)));

        await Stream(rig, new Pieces());

        rig.Handler.Only.Authorization!.Parameter.Should().Be("sk-test");
        rig.Handler.Only.Headers["X-Title"].Should().Be("Harbora");
        rig.Handler.Only.Headers.Should().ContainKey("HTTP-Referer");
        rig.Handler.Only.Url.ToString().Should().Be("https://openrouter.ai/api/v1/chat/completions");
    }

    [Fact]
    public async Task Without_a_callback_nothing_is_streamed()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("hi")));

        var response = await rig.Client.CompleteAsync(Ask(), onTextDelta: null, CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error);
        rig.Handler.Only.Json.ContainsKey("stream").Should().BeFalse();
    }

    [Fact]
    public async Task Through_the_interface_the_streaming_overload_streams_and_is_not_the_default_that_does_not()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("a"), Chunk("b", "stop")) + Done)));
        IAgentChatClient client = rig.Client;
        var pieces = new Pieces();

        var response = await client.CompleteAsync(Ask(), pieces.Callback, CancellationToken.None);

        pieces.Seen.Should().Equal("a", "b");
        response.Message!.Content.Should().Be("ab");
    }

    // ---- text ------------------------------------------------------------------------------

    [Fact]
    public async Task Deltas_arrive_in_order_across_chunk_boundaries_even_inside_a_persian_letter()
    {
        var wire = Events(
            Chunk("", first: true), Chunk("سلام، "), Chunk("چطور "), Chunk("کمک کنم؟"), Chunk(null, "stop")) + Done;

        // Every 5 bytes: mid-line, mid-JSON, and (the first Persian letter is two bytes wide) mid-character.
        var everyFive = Enumerable.Range(1, Encoding.UTF8.GetByteCount(wire) / 5).Select(i => i * 5).ToArray();
        var insideALetter = Encoding.UTF8.GetByteCount(wire[..wire.IndexOf('س')]) + 1;

        foreach (var cuts in new[] { everyFive, new[] { insideALetter }, Array.Empty<int>() })
        {
            var pieces = new Pieces();
            var rig = Build(Serving(() => new SseScript().SendCutAt(wire, cuts)));

            var response = await Stream(rig, pieces);

            response.Ok.Should().BeTrue(response.Error);
            pieces.Seen.Should().Equal("سلام، ", "چطور ", "کمک کنم؟");
            response.Message!.Content.Should().Be("سلام، چطور کمک کنم؟");
            response.FinishReason.Should().Be("stop");
            response.Model.Should().Be("vendor/model:free");
        }
    }

    [Fact]
    public async Task Usage_comes_from_the_last_chunk_after_the_finish()
    {
        var rig = Build(Serving(() => new SseScript().Send(
            Events(Chunk("Hello", model: "answering/one:free"), Chunk(" there", "stop", "answering/one:free"),
                UsageChunk(31, 7, "answering/one:free")) + Done)));
        var pieces = new Pieces();

        var response = await Stream(rig, pieces);

        pieces.Seen.Should().Equal("Hello", " there");
        response.Usage.Should().Be(new AgentChatUsage(31, 7));
        response.Model.Should().Be("answering/one:free", "the model that answered, as the last chunk names it");
        response.FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task A_stream_with_no_usage_has_null_usage_not_zeros()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("x", "stop")) + Done)));

        var response = await Stream(rig, new Pieces());

        response.Usage.Should().BeNull();
    }

    [Fact]
    public async Task Comments_other_fields_and_crlf_line_endings_are_ignored_or_read()
    {
        var wire =
            Comment() + ": OPENROUTER PROCESSING\r\n\r\n"
            + "event: message\nid: 1\nretry: 100\ndata: " + Chunk("Hel") + "\n\n"
            + Comment("keep-alive")
            + "data: " + Chunk("lo") + "\r\n\r\n"
            + ": a comment in the middle of an event\ndata: " + Chunk(null, "stop") + "\n\n"
            + Done;
        var pieces = new Pieces();
        var rig = Build(Serving(() => new SseScript().Send(wire)));

        var response = await Stream(rig, pieces);

        response.Ok.Should().BeTrue(response.Error);
        pieces.Seen.Should().Equal("Hel", "lo");
        response.Message!.Content.Should().Be("Hello");
    }

    [Fact]
    public async Task Data_without_a_space_and_a_last_event_with_no_blank_line_are_still_read()
    {
        var wire = "data:" + Chunk("A") + "\n\n" + "data:" + Chunk("B", "stop");
        var pieces = new Pieces();
        var rig = Build(Serving(() => new SseScript().Send(wire)));

        var response = await Stream(rig, pieces);

        response.Ok.Should().BeTrue(response.Error);
        pieces.Seen.Should().Equal("A", "B");
    }

    [Fact]
    public async Task A_finish_reason_without_done_and_a_done_without_a_finish_reason_are_both_complete()
    {
        var noDone = await Stream(Build(Serving(() => new SseScript().Send(Events(Chunk("x", "stop"))))), new Pieces());
        noDone.Ok.Should().BeTrue(noDone.Error);
        noDone.FinishReason.Should().Be("stop");

        var noFinish = await Stream(Build(Serving(() => new SseScript().Send(Events(Chunk("x")) + Done))), new Pieces());
        noFinish.Ok.Should().BeTrue(noFinish.Error);
        noFinish.FinishReason.Should().BeNull();
        noFinish.Message!.Content.Should().Be("x");
    }

    [Fact]
    public async Task The_streamed_response_equals_the_whole_response_for_the_same_text_answer()
    {
        var whole = await Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("سلام دنیا", "answering/model:free")))
            .Client.CompleteAsync(Ask(), CancellationToken.None);

        var streamed = await Stream(Build(Serving(() => new SseScript().Send(
            Events(Chunk("سلام ", model: "answering/model:free"), Chunk("دنیا", "stop", "answering/model:free"),
                UsageChunk(12, 5, "answering/model:free")) + Done))), new Pieces());

        streamed.Should().BeEquivalentTo(whole);
    }

    // ---- tool calls ------------------------------------------------------------------------

    [Fact]
    public async Task Tool_calls_are_assembled_from_fragments_by_index_and_text_before_them_is_kept()
    {
        var wire = Events(
            Chunk("On it. ", first: true),
            ToolFragment(0, "call_a", "create_database", ""),
            ToolFragment(0, null, null, "{\"engine\":"),
            ToolFragment(1, "call_b", "restart_app", "{\"app\""),
            ToolFragment(0, null, null, "\"postgres\",\"name\":\"shop-db\"}"),
            ToolFragment(1, null, null, ":\"shop\"}"),
            ToolFragment(null, finish: "tool_calls"),
            UsageChunk(40, 9)) + Done;
        var pieces = new Pieces();
        var rig = Build(Serving(() => new SseScript().SendCutAt(wire, 33, 150, 151, 400, 777)));

        var response = await Stream(rig, pieces);

        response.Ok.Should().BeTrue(response.Error);
        pieces.Seen.Should().Equal("On it. ");
        response.Message!.Content.Should().Be("On it. ");
        response.Message.ToolCalls!.Select(c => (c.Id, c.Name, c.ArgumentsJson)).Should().Equal(
            ("call_a", "create_database", """{"engine":"postgres","name":"shop-db"}"""),
            ("call_b", "restart_app", """{"app":"shop"}"""));
        response.FinishReason.Should().Be("tool_calls");
        response.Usage.Should().Be(new AgentChatUsage(40, 9));
    }

    [Fact]
    public async Task A_streamed_tool_call_equals_the_whole_response_for_the_same_answer()
    {
        var whole = await Build(RecordingChatHandler.Always(
                RecordingChatHandler.ToolCallReply("restart_app", """{"app":"shop"}""", "call_9")))
            .Client.CompleteAsync(Ask(tools: [RestartApp]), CancellationToken.None);

        var streamed = await Stream(Build(Serving(() => new SseScript().Send(Events(
            ToolFragment(0, "call_9", "restart_app", ""), ToolFragment(0, null, null, "{\"app\":"),
            ToolFragment(0, null, null, "\"shop\"}"), ToolFragment(null, finish: "tool_calls"),
            UsageChunk(40, 9)) + Done))), new Pieces());

        streamed.Should().BeEquivalentTo(whole);
        streamed.Message!.Content.Should().BeNull("a tool-call-only reply has no text");
    }

    [Fact]
    public async Task A_name_repeated_on_every_fragment_is_not_doubled()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(
            ToolFragment(0, "c1", "restart_app", "{\"app\":"),
            ToolFragment(0, "c1", "restart_app", "\"shop\"}"),
            ToolFragment(null, finish: "tool_calls")) + Done)));

        var response = await Stream(rig, new Pieces());

        response.Message!.ToolCalls!.Single().Should().Be(new AgentToolCall("c1", "restart_app", """{"app":"shop"}"""));
    }

    [Fact]
    public async Task Parallel_calls_a_gateway_numbers_all_zero_are_told_apart_by_id()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(
            ToolFragment(0, "c1", "restart_app", "{\"app\":\"a\"}"),
            ToolFragment(0, "c2", "restart_app", "{\"app\":\"b\"}"),
            ToolFragment(null, finish: "tool_calls")) + Done)));

        var response = await Stream(rig, new Pieces());

        response.Message!.ToolCalls!.Select(c => (c.Id, c.ArgumentsJson))
            .Should().Equal(("c1", """{"app":"a"}"""), ("c2", """{"app":"b"}"""));
    }

    [Fact]
    public async Task Fragments_with_no_id_go_to_the_newest_call_under_their_wire_index()
    {
        // Every call is numbered 0; only the first fragment of each carries its id. The argument
        // fragments after "c2" must extend c2, not the older call that also lives at index 0.
        var rig = Build(Serving(() => new SseScript().Send(Events(
            ToolFragment(0, "c1", "restart_app", "{\"app\":"),
            ToolFragment(0, null, null, "\"a\"}"),
            ToolFragment(0, "c2", "deploy_app", "{\"app\":"),
            ToolFragment(0, null, null, "\"b\"}"),
            ToolFragment(null, finish: "tool_calls")) + Done)));

        var response = await Stream(rig, new Pieces());

        response.Message!.ToolCalls!.Select(c => (c.Id, c.Name, c.ArgumentsJson)).Should().Equal(
            ("c1", "restart_app", """{"app":"a"}"""), ("c2", "deploy_app", """{"app":"b"}"""));
    }

    [Fact]
    public async Task A_repeated_id_under_a_shared_wire_index_returns_to_the_call_it_names()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(
            ToolFragment(0, "c1", "restart_app", "{\"app\":"),
            ToolFragment(0, "c2", "deploy_app", "{\"app\":\"b\"}"),
            ToolFragment(0, "c1", null, "\"a\"}"),
            ToolFragment(null, finish: "tool_calls")) + Done)));

        var response = await Stream(rig, new Pieces());

        response.Message!.ToolCalls!.Select(c => (c.Id, c.ArgumentsJson)).Should().Equal(
            ("c1", """{"app":"a"}"""), ("c2", """{"app":"b"}"""));
    }

    [Fact]
    public async Task Calls_with_no_index_follow_their_ids_and_a_fragment_with_neither_extends_the_last_call()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(
            ToolFragment(null, "c1", "restart_app", "{\"app\":"),
            ToolFragment(null, null, null, "\"a\"}"),
            ToolFragment(null, "c2", "deploy_app", "{\"app\":\"b\"}"),
            ToolFragment(null, finish: "tool_calls")) + Done)));

        var response = await Stream(rig, new Pieces());

        response.Message!.ToolCalls!.Select(c => (c.Id, c.Name, c.ArgumentsJson)).Should().Equal(
            ("c1", "restart_app", """{"app":"a"}"""), ("c2", "deploy_app", """{"app":"b"}"""));
    }

    [Fact]
    public async Task A_call_with_no_id_gets_one_and_no_arguments_become_an_empty_object_as_in_the_whole_reply()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(
            ToolFragment(0, null, "list_resources"),
            ToolFragment(1, "", "get_app", ""),
            ToolFragment(null, finish: "tool_calls")) + Done)));

        var response = await Stream(rig, new Pieces());

        response.Message!.ToolCalls!.Select(c => (c.Id, c.Name, c.ArgumentsJson))
            .Should().Equal(("call_1", "list_resources", "{}"), ("call_2", "get_app", "{}"));
    }

    [Fact]
    public async Task Arguments_sent_whole_as_an_object_are_kept_as_raw_json()
    {
        const string chunk = """
            {"model":"m/x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","type":"function",
              "function":{"name":"create_database","arguments":{"engine":"postgres","name":"shop-db"}}}]},"finish_reason":"tool_calls"}]}
            """;
        var rig = Build(Serving(() => new SseScript().Send(Event(chunk.ReplaceLineEndings("")) + Done)));

        var response = await Stream(rig, new Pieces());

        var arguments = JsonNode.Parse(response.Message!.ToolCalls!.Single().ArgumentsJson)!;
        arguments["engine"]!.GetValue<string>().Should().Be("postgres");
        arguments["name"]!.GetValue<string>().Should().Be("shop-db");
    }

    // ---- failures inside the stream --------------------------------------------------------

    [Fact]
    public async Task An_error_in_the_stream_is_a_failure_in_both_languages_and_earlier_text_is_not_an_answer()
    {
        var error = new JsonObject
        {
            ["id"] = "gen-1",
            ["error"] = new JsonObject { ["code"] = "server_error", ["message"] = "Provider disconnected unexpectedly" },
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = "" }, ["finish_reason"] = "error" })
        }.ToJsonString();
        var pieces = new Pieces();
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("Starting to answer"), error) + Done)));

        var response = await Stream(rig, pieces);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("Provider disconnected unexpectedly");
        response.ErrorFa.Should().Contain("Provider disconnected unexpectedly");
        pieces.Seen.Should().Equal("Starting to answer");
    }

    [Fact]
    public async Task An_error_string_and_a_choice_level_error_are_failures_too()
    {
        var asString = await Stream(Build(Serving(() => new SseScript().Send(
            Events("""{"error":"model overloaded"}""") + Done))), new Pieces());
        ShouldFailInBothLanguages(asString);
        asString.Error.Should().Contain("model overloaded");

        var inChoice = await Stream(Build(Serving(() => new SseScript().Send(
            Events("""{"choices":[{"index":0,"error":{"message":"stream dropped"},"finish_reason":"error"}]}""") + Done))), new Pieces());
        ShouldFailInBothLanguages(inChoice);
        inChoice.Error.Should().Contain("stream dropped");
    }

    [Fact]
    public async Task A_finish_reason_of_error_with_no_message_is_still_a_failure()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("par"), Chunk(null, "error")) + Done)));

        var response = await Stream(rig, new Pieces());

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("finish reason: error");
    }

    [Fact]
    public async Task A_stream_that_ends_without_done_or_a_finish_is_cut_off_not_an_answer()
    {
        var pieces = new Pieces();
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("The first half of"), Chunk(" an answer")))));

        var response = await Stream(rig, pieces);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("cut off");
        response.ErrorFa.Should().Contain("قطع");
        response.Message.Should().BeNull("half an answer must not be presentable as one");
        pieces.Seen.Should().HaveCount(2, "the pieces were shown as they came; the failure says they were not the whole");
    }

    [Theory]
    [InlineData("")]
    [InlineData(": OPENROUTER PROCESSING\n\n")]
    [InlineData("data: not json at all\n\n")]
    public async Task A_stream_with_nothing_in_it_is_cut_off(string wire)
    {
        var rig = Build(Serving(() => new SseScript().Send(wire)));

        var response = await Stream(rig, new Pieces());

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("cut off");
    }

    [Fact]
    public async Task A_stream_that_finishes_with_no_text_and_no_call_fails_like_an_empty_whole_reply()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("", first: true), Chunk(null, "length")) + Done)));

        var response = await Stream(rig, new Pieces());

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("neither text nor a tool call").And.Contain("length");
    }

    [Fact]
    public async Task A_connection_that_dies_mid_stream_is_a_failure_value_with_the_reason()
    {
        var rig = Build(Serving(() => new SseScript()
            .Send(Events(Chunk("The first")))
            .Fail(new IOException("Connection reset by peer"))));
        var pieces = new Pieces();

        var response = await Stream(rig, pieces);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("connection dropped").And.Contain("Connection reset by peer");
        response.ErrorFa.Should().Contain("Connection reset by peer");
        pieces.Seen.Should().Equal("The first");
    }

    [Fact]
    public async Task A_connection_that_dies_after_the_finish_still_gives_the_finished_answer()
    {
        var rig = Build(Serving(() => new SseScript()
            .Send(Events(Chunk("All of it", "stop")))
            .Fail(new IOException("Connection reset by peer"))));

        var response = await Stream(rig, new Pieces());

        response.Ok.Should().BeTrue(response.Error);
        response.Message!.Content.Should().Be("All of it");
        response.Usage.Should().BeNull();
    }

    // ---- silence and time ------------------------------------------------------------------

    [Fact]
    public async Task A_stream_that_goes_silent_is_a_timeout_naming_the_silence()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("Hel"))).Hang()),
            streamIdle: TimeSpan.FromMilliseconds(80));
        var pieces = new Pieces();

        var response = await Stream(rig, pieces);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("stopped sending the answer");
        response.ErrorFa.Should().Contain("پاسخ را ادامه نمی‌دهد");
        pieces.Seen.Should().Equal("Hel");
    }

    [Fact]
    public async Task The_time_the_callback_takes_is_never_counted_as_the_providers_silence()
    {
        // The second read is ready the moment it is asked for; the pane is slow to show the first
        // piece, for more than twice the silence limit. The provider said nothing wrong.
        var rig = Build(Serving(() => new SseScript()
                .Send(Events(Chunk("slow ")))
                .Send(Events(Chunk("pane", "stop")) + Done)),
            streamIdle: TimeSpan.FromMilliseconds(150));
        var seen = new List<string>();

        var response = await rig.Client.CompleteAsync(Ask(), async text =>
        {
            seen.Add(text);
            if (seen.Count == 1) await Task.Delay(350);
        }, CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error);
        seen.Should().Equal("slow ", "pane");
        response.Message!.Content.Should().Be("slow pane");
    }

    [Fact]
    public async Task A_cancellation_that_is_neither_our_budget_nor_our_silence_timer_is_a_timeout_not_a_silence()
    {
        // Something inside the HTTP stack cancelling on its own: not the caller, not the 90 s, not the
        // 45 s. "Stopped sending for 45 seconds" would be a claim nobody measured.
        var rig = Build(Serving(() => new SseScript()
                .Send(Events(Chunk("Hel")))
                .Fail(new OperationCanceledException("the stack gave up"))),
            streamIdle: TimeSpan.FromSeconds(30));

        var response = await Stream(rig, new Pieces());

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("did not answer within").And.NotContain("stopped sending");
    }

    [Fact]
    public async Task Keep_alive_comments_count_as_life_so_a_slow_start_is_not_a_silence()
    {
        var rig = Build(Serving(() => new SseScript()
                .Send(Comment()).Wait(TimeSpan.FromMilliseconds(150))
                .Send(Comment()).Wait(TimeSpan.FromMilliseconds(150))
                .Send(Comment()).Wait(TimeSpan.FromMilliseconds(150))
                .Send(Events(Chunk("late but whole", "stop")) + Done)),
            streamIdle: TimeSpan.FromMilliseconds(400));

        var response = await Stream(rig, new Pieces());

        response.Ok.Should().BeTrue(response.Error);
        response.Message!.Content.Should().Be("late but whole");
    }

    [Fact]
    public async Task The_overall_budget_still_applies_to_a_stream_that_only_ever_sends_comments()
    {
        var script = () =>
        {
            var s = new SseScript();
            for (var i = 0; i < 40; i++) s.Send(Comment()).Wait(TimeSpan.FromMilliseconds(40));
            return s;
        };
        var rig = Build(Serving(script), timeout: TimeSpan.FromMilliseconds(300), streamIdle: TimeSpan.FromSeconds(30));

        var response = await Stream(rig, new Pieces());

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("did not answer within");
    }

    [Fact]
    public async Task Silence_after_the_finish_is_not_a_failure_only_the_usage_is_missing()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("Done and dusted", "stop"))).Hang()),
            streamIdle: TimeSpan.FromMilliseconds(80));

        var response = await Stream(rig, new Pieces());

        response.Ok.Should().BeTrue(response.Error);
        response.Message!.Content.Should().Be("Done and dusted");
        response.FinishReason.Should().Be("stop");
        response.Usage.Should().BeNull();
    }

    [Fact]
    public async Task A_finish_plus_usage_ends_the_read_without_waiting_for_the_connection_to_close()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("quick", "stop"), UsageChunk(3, 2))).Hang()),
            streamIdle: TimeSpan.FromSeconds(30));

        var response = await Stream(rig, new Pieces()).WaitAsync(TimeSpan.FromSeconds(10));

        response.Ok.Should().BeTrue(response.Error);
        response.Usage.Should().Be(new AgentChatUsage(3, 2));
    }

    // ---- before the stream starts ----------------------------------------------------------

    [Fact]
    public async Task A_non_2xx_before_the_stream_is_a_failure_with_the_providers_message_and_no_retry()
    {
        var rig = Build(RecordingChatHandler.Always(
            """{"error":{"message":"No endpoints found for vendor/gone:free.","code":404}}""", HttpStatusCode.NotFound));
        var pieces = new Pieces();

        var response = await Stream(rig, pieces);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("404").And.Contain("No endpoints found for vendor/gone:free.");
        pieces.Seen.Should().BeEmpty();
        rig.Handler.Requests.Should().HaveCount(1, "only a refusal of streaming itself earns a second try");
    }

    [Fact]
    public async Task A_429_before_the_stream_says_rate_limited_in_both_languages()
    {
        var rig = Build(RecordingChatHandler.Always(
            """{"error":{"message":"Provider returned error","code":429,"metadata":{"raw":"rate-limited upstream, streaming or not"}}}""",
            HttpStatusCode.TooManyRequests));

        var response = await Stream(rig, new Pieces());

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("rate limit").And.Contain("429");
        rig.Handler.Requests.Should().HaveCount(1, "a 429 that mentions streaming is still about the person, not about streaming");
    }

    [Fact]
    public async Task An_authentication_failure_that_mentions_streaming_is_not_a_reason_to_fall_back()
    {
        var rig = Build(RecordingChatHandler.Always(
            """{"error":{"message":"Invalid API key for streaming requests"}}""", HttpStatusCode.Unauthorized));

        var response = await Stream(rig, new Pieces());

        ShouldFailInBothLanguages(response);
        rig.Handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_connection_that_cannot_be_made_is_the_same_failure_as_without_streaming()
    {
        var rig = Build(new RecordingChatHandler(_ => throw new HttpRequestException("Connection refused")));

        var response = await Stream(rig, new Pieces());

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("could not be reached").And.Contain("Connection refused");
    }

    // ---- falling back ----------------------------------------------------------------------

    [Fact]
    public async Task A_provider_that_refuses_streaming_is_asked_once_more_without_it_and_the_text_still_arrives()
    {
        var call = 0;
        var handler = new RecordingChatHandler(_ => Interlocked.Increment(ref call) == 1
            ? RecordingChatHandler.Reply("""{"error":{"message":"Streaming is not supported for this model","code":400}}""", HttpStatusCode.BadRequest)
            : RecordingChatHandler.Reply(RecordingChatHandler.TextReply("The whole answer", "plain/model:free")));
        var rig = Build(handler);
        var pieces = new Pieces();

        var response = await Stream(rig, pieces);

        response.Ok.Should().BeTrue(response.Error);
        response.Message!.Content.Should().Be("The whole answer");
        response.Model.Should().Be("plain/model:free");
        pieces.Seen.Should().Equal("The whole answer");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Json["stream"]!.GetValue<bool>().Should().BeTrue();
        handler.Requests[1].Json.ContainsKey("stream").Should().BeFalse("the retry is the plain request");
        handler.Requests[1].Json.ContainsKey("stream_options").Should().BeFalse();
        handler.Requests[1].Json["messages"]!.ToJsonString().Should().Be(handler.Requests[0].Json["messages"]!.ToJsonString());
    }

    [Fact]
    public async Task The_fallback_gets_what_is_left_of_the_budget_not_a_second_one()
    {
        // Budget 1 s. The refusal takes 0.6 s to arrive; the plain retry then hangs. With a fresh
        // budget the call would last about 1.6 s; with what is left, about 1 s.
        var call = 0;
        var handler = new RecordingChatHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                await Task.Delay(600, ct);
                return RecordingChatHandler.Reply("""{"error":{"message":"Streaming is not supported"}}""", HttpStatusCode.BadRequest);
            }

            await Task.Delay(Timeout.Infinite, ct);
            return RecordingChatHandler.Reply("never");
        });
        var rig = Build(handler, timeout: TimeSpan.FromSeconds(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var response = await Stream(rig, new Pieces());
        watch.Stop();

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("did not answer within");
        handler.Requests.Should().HaveCount(2);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1400), "the retry shares the call's one budget");
    }

    [Theory]
    // not about streaming: "upstream" is not "stream", and a body that merely quotes the request is not an error text
    [InlineData("""{"error":{"message":"Upstream provider returned error","code":400}}""", false)]
    [InlineData("""{"error":{"message":"Bad request to downstream service"}}""", false)]
    [InlineData("""{"error":{"message":"Invalid request body"},"request":{"model":"x","stream":true}}""", false)]
    [InlineData("""{"stream":true,"model":"x"}""", false)]
    [InlineData("""{"error":{"message":"Provider returned error","metadata":{"raw":"upstream said no","provider_name":"X"}}}""", false)]
    [InlineData("""<html><body>400 Bad Request: upstream rejected the body</body></html>""", false)]
    // about streaming: in OpenAI's shape, in the bare shapes some servers use, in plain text, in metadata
    [InlineData("""{"error":{"message":"Streaming is not supported for this model"}}""", true)]
    [InlineData("""{"error":{"message":"Unrecognized request argument supplied: stream_options"}}""", true)]
    [InlineData("""{"object":"error","message":"stream mode is not supported by this engine","code":400}""", true)]
    [InlineData("""{"detail":"Streaming responses are disabled"}""", true)]
    [InlineData("""{"error":{"message":"Provider returned error","metadata":{"raw":"stream=true is not allowed for this model"}}}""", true)]
    [InlineData("""streaming is not supported""", true)]
    public async Task Only_an_error_that_talks_about_streaming_as_a_word_makes_a_4xx_a_refusal_to_stream(string body, bool refusesStreaming)
    {
        var call = 0;
        var handler = new RecordingChatHandler(_ => Interlocked.Increment(ref call) == 1
            ? RecordingChatHandler.Reply(body, HttpStatusCode.BadRequest)
            : RecordingChatHandler.Reply(RecordingChatHandler.TextReply("plain answer")));
        var rig = Build(handler);

        var response = await Stream(rig, new Pieces());

        if (refusesStreaming)
        {
            response.Ok.Should().BeTrue(response.Error);
            response.Message!.Content.Should().Be("plain answer");
            handler.Requests.Should().HaveCount(2);
        }
        else
        {
            ShouldFailInBothLanguages(response);
            handler.Requests.Should().HaveCount(1, "nothing here said the provider will not stream");
        }
    }

    [Fact]
    public async Task A_rejected_stream_options_field_also_falls_back()
    {
        var call = 0;
        var handler = new RecordingChatHandler(_ => Interlocked.Increment(ref call) == 1
            ? RecordingChatHandler.Reply("""{"error":{"message":"Unrecognized request argument supplied: stream_options"}}""", HttpStatusCode.UnprocessableEntity)
            : RecordingChatHandler.Reply(RecordingChatHandler.TextReply("ok")));
        var rig = Build(handler);

        var response = await Stream(rig, new Pieces());

        response.Ok.Should().BeTrue(response.Error);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_fallback_happens_once_and_its_own_failure_is_returned()
    {
        var rig = Build(RecordingChatHandler.Always(
            """{"error":{"message":"stream: unsupported"}}""", HttpStatusCode.BadRequest));
        var pieces = new Pieces();

        var response = await Stream(rig, pieces);

        ShouldFailInBothLanguages(response);
        rig.Handler.Requests.Should().HaveCount(2, "streaming once, the plain call once, then it is a failure");
        pieces.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task A_4xx_that_does_not_talk_about_streaming_does_not_fall_back()
    {
        var rig = Build(RecordingChatHandler.Always(
            """{"error":{"message":"max_tokens is too large for this model"}}""", HttpStatusCode.BadRequest));

        var response = await Stream(rig, new Pieces());

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("max_tokens is too large");
        rig.Handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_provider_that_ignores_stream_and_sends_one_json_body_is_answering_all_the_same()
    {
        var rig = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("All at once", "plain/model:free")));
        var pieces = new Pieces();

        var response = await Stream(rig, pieces);

        response.Ok.Should().BeTrue(response.Error);
        response.Message!.Content.Should().Be("All at once");
        response.Usage.Should().Be(new AgentChatUsage(12, 5));
        pieces.Seen.Should().Equal("All at once");
        rig.Handler.Requests.Should().HaveCount(1, "it answered; there is nothing to retry");
    }

    // ---- a real socket ---------------------------------------------------------------------

    private sealed class RealHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// Every other test reads a scripted body; this one goes through a real HttpClient and a real
    /// loopback socket (no provider is involved — the "provider" is a Kestrel endpoint in this test).
    /// It proves the property the feature exists for: the first piece reaches the callback while the
    /// server is still holding the rest back. A client that buffered the body would deadlock the
    /// server's wait and fail here.
    /// </summary>
    [Fact]
    public async Task Over_a_real_socket_the_first_piece_arrives_before_the_server_has_sent_the_rest()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var server = builder.Build();
        server.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(Events(Chunk("first ", first: true)));
            await context.Response.Body.FlushAsync();

            // Held until the client has shown the first piece.
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await context.Response.WriteAsync(Events(Chunk("second", "stop"), UsageChunk(4, 2)) + Done);
        });
        await server.StartAsync();

        var db = new HarboraDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("agent-stream-socket-" + Guid.NewGuid()).Options);
        foreach (var (key, value) in new Dictionary<string, string>
        {
            [AssistantSettingKeys.Enabled] = "true",
            [AssistantSettingKeys.Provider] = "openai",
            [AssistantSettingKeys.Model] = "local/model",
            [AssistantSettingKeys.BaseUrl] = server.Urls.Single()   // loopback: no key needed
        })
            db.Settings.Add(new Setting { Key = key, Value = value });
        db.SaveChanges();

        var clients = new RealHttpClients();
        var client = new OpenAiCompatibleAgentChatClient(
            new AssistantService(db, new AssistantClient(clients), new AgentChatClientTests.PassthroughProtector(), NullLogger<AssistantService>.Instance),
            new AgentSettingsService(db), clients);

        var seen = new List<string>();
        var response = await client.CompleteAsync(Ask(), text =>
        {
            seen.Add(text);
            if (seen.Count == 1) release.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error);
        seen.Should().Equal("first ", "second");
        response.Message!.Content.Should().Be("first second");
        response.Usage.Should().Be(new AgentChatUsage(4, 2));
    }

    // ---- the caller ------------------------------------------------------------------------

    [Fact]
    public async Task The_callers_cancellation_mid_stream_propagates_and_is_not_dressed_up_as_a_timeout()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("Hel"))).Hang()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var act = () => Stream(rig, new Pieces(), ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public static TheoryData<string> CallbackFaults => new()
    {
        "cancelled",        // what a callback writing to a gone browser throws first
        "task-cancelled",
        "io",               // Kestrel's ConnectionResetException is an IOException
        "http",
        "timeout",
        "invalid"
    };

    private static Exception FaultOf(string kind) => kind switch
    {
        "cancelled" => new OperationCanceledException("the browser left"),
        "task-cancelled" => new TaskCanceledException("the browser left"),
        "io" => new IOException("The client reset the request stream."),
        "http" => new HttpRequestException("pipe closed"),
        "timeout" => new TimeoutException("write timed out"),
        _ => new InvalidOperationException("the pane went away")
    };

    [Theory]
    [MemberData(nameof(CallbackFaults))]
    public async Task Whatever_the_callback_throws_reaches_the_caller_as_itself_and_never_as_a_provider_failure(string kind)
    {
        // These are exactly what a closed browser tab produces. Turned into "stopped sending for 45
        // seconds" or "the AI provider's connection dropped", they would blame the provider for it.
        var original = FaultOf(kind);
        var pieces = 0;
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("one"), Chunk("two"), Chunk("three", "stop")) + Done)));

        var thrown = await Record.ExceptionAsync(() => rig.Client.CompleteAsync(Ask(), text =>
        {
            pieces++;
            throw original;
        }, CancellationToken.None));

        thrown.Should().BeSameAs(original);
        pieces.Should().Be(1, "the stream is abandoned at the first fault");
    }

    [Theory]
    [MemberData(nameof(CallbackFaults))]
    public async Task A_callback_that_fails_asynchronously_is_the_same(string kind)
    {
        var original = FaultOf(kind);
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("one", "stop")) + Done)));

        var thrown = await Record.ExceptionAsync(() => rig.Client.CompleteAsync(Ask(), async _ =>
        {
            await Task.Yield();
            throw original;
        }, CancellationToken.None));

        thrown.Should().BeSameAs(original);
    }

    [Theory]
    [MemberData(nameof(CallbackFaults))]
    public async Task A_callback_fault_is_also_the_callers_when_the_provider_answered_with_plain_json_or_the_fallback_ran(string kind)
    {
        var original = FaultOf(kind);

        var json = Build(RecordingChatHandler.Always(RecordingChatHandler.TextReply("All at once")));
        (await Record.ExceptionAsync(() => json.Client.CompleteAsync(Ask(), _ => throw original, CancellationToken.None)))
            .Should().BeSameAs(original);

        var call = 0;
        var refusing = Build(new RecordingChatHandler(_ => Interlocked.Increment(ref call) == 1
            ? RecordingChatHandler.Reply("""{"error":{"message":"Streaming is not supported"}}""", HttpStatusCode.BadRequest)
            : RecordingChatHandler.Reply(RecordingChatHandler.TextReply("The whole answer"))));
        (await Record.ExceptionAsync(() => refusing.Client.CompleteAsync(Ask(), _ => throw original, CancellationToken.None)))
            .Should().BeSameAs(original);
    }

    [Fact]
    public async Task A_callback_that_throws_is_the_callers_problem_not_a_provider_failure()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("Hel"), Chunk("lo", "stop")) + Done)));

        var act = () => rig.Client.CompleteAsync(Ask(),
            _ => throw new InvalidOperationException("the pane went away"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("the pane went away");
    }

    [Fact]
    public async Task When_the_agent_is_unavailable_nothing_is_sent_and_nothing_is_streamed()
    {
        var rig = Build(Serving(() => new SseScript().Send(Events(Chunk("x", "stop")))),
            new() { [AssistantSettingKeys.Enabled] = "false" });
        var pieces = new Pieces();

        var response = await Stream(rig, pieces);

        ShouldFailInBothLanguages(response);
        response.Error.Should().Contain("turned off");
        rig.Handler.Requests.Should().BeEmpty();
        pieces.Seen.Should().BeEmpty();
    }
}
