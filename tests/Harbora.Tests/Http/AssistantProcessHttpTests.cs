using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The names, timings and decided plans as they cross the wire: the property names the chat pane reads
/// (camelCase, exactly as <c>AgentViews.cs</c> and <c>AgentCoreModels.cs</c> spell them), through the real
/// pipeline with the real read tool and only the model scripted.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AssistantProcessHttpTests(AssistantPanelFixture fixture) : IClassFixture<AssistantPanelFixture>
{
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string csrf, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static List<(string Event, JsonElement Data)> Frames(string body)
    {
        var frames = new List<(string, JsonElement)>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            frames.Add((name, JsonDocument.Parse(data).RootElement.Clone()));
        }
        return frames;
    }

    [Fact]
    public async Task Tool_events_and_the_reopened_conversation_carry_the_fields_the_pane_reads()
    {
        fixture.GivenUser(fixture.WorkspaceId, "process-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.222", "process-owner@example.com");
        fixture.Chat.Clear();
        fixture.Chat.ThenCalls(fixture.Chat.Call("get_usage"), fixture.Chat.Call("test_safe_write", """{"name":"shop-db"}"""))
            .ThenText("Approve the plan to create shop-db.");

        var send = await PostAsync(client, "/assistant/conversations/new/messages", csrf, new { text = "how much room is left, and create shop-db" });
        send.StatusCode.Should().Be(HttpStatusCode.OK);
        var frames = Frames(await send.Content.ReadAsStringAsync());

        // The stream: the running line has the words and no time; the finished one has both.
        var usage = frames.Where(f => f.Event == "tool" && f.Data.GetProperty("name").GetString() == "get_usage").Select(f => f.Data).ToList();
        usage.Select(t => t.GetProperty("state").GetString()).Should().Equal("running", "done");
        foreach (var tool in usage)
        {
            tool.GetProperty("labelEn").GetString().Should().Be("Checking your plan's usage and limits");
            tool.GetProperty("labelFa").GetString().Should().Be("بررسی مصرف و سقف‌های پلن");
            tool.GetProperty("category").GetString().Should().Be("usage");
        }
        usage[0].GetProperty("durationMs").ValueKind.Should().Be(JsonValueKind.Null);
        usage[1].GetProperty("durationMs").GetInt32().Should().BeGreaterThanOrEqualTo(0);

        var id = frames.Single(f => f.Event == "started").Data.GetProperty("conversationId").GetGuid();
        var planId = frames.Single(f => f.Event == "plan").Data.GetProperty("id").GetGuid();

        // Reopened with the card still waiting: the lines have their names, the proposal names its plan.
        var pending = await (await client.GetAsync($"/assistant/conversations/{id}")).JsonAsync();
        var lines = pending.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToList();
        lines[0].GetProperty("toolName").GetString().Should().Be("get_usage");
        lines[0].GetProperty("toolLabel").GetString().Should().Be("بررسی مصرف و سقف‌های پلن", "the panel speaks Persian unless told otherwise");
        lines[0].GetProperty("toolCategory").GetString().Should().Be("usage");
        lines[0].GetProperty("durationMs").GetInt32().Should().Be(usage[1].GetProperty("durationMs").GetInt32());
        lines[0].GetProperty("planId").ValueKind.Should().Be(JsonValueKind.Null);
        lines[0].GetProperty("toolState").GetString().Should().Be("done");
        lines[1].GetProperty("durationMs").ValueKind.Should().Be(JsonValueKind.Null, "a proposal ran nothing");
        lines[1].GetProperty("planId").GetGuid().Should().Be(planId);
        lines[1].GetProperty("toolState").ValueKind.Should().Be(JsonValueKind.Null, "a proposal has no outcome yet");
        pending.GetProperty("pendingPlan").GetProperty("id").GetGuid().Should().Be(planId);
        pending.GetProperty("plans").GetArrayLength().Should().Be(0, "the pending plan is only in pendingPlan");

        var approve = await PostAsync(client, $"/assistant/plans/{planId}/approve", csrf, new { });
        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        var approved = await approve.JsonAsync();

        // Reopened after the decision: the plan moved to plans, with the result the approval returned.
        var decided = await (await client.GetAsync($"/assistant/conversations/{id}")).JsonAsync();
        decided.GetProperty("pendingPlan").ValueKind.Should().Be(JsonValueKind.Null);
        var history = decided.GetProperty("plans").EnumerateArray().Single();
        history.GetProperty("plan").GetProperty("id").GetGuid().Should().Be(planId);
        history.GetProperty("plan").GetProperty("status").GetString().Should().Be("executed");
        history.GetProperty("result").GetRawText().Should().Be(approved.GetRawText());

        var closing = decided.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        history.GetProperty("afterMessageId").GetGuid().Should().Be(closing.GetProperty("id").GetGuid());
        decided.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("planId").ValueKind != JsonValueKind.Null).Should().HaveCount(2,
                "the proposal line and the line the execution wrote");

        // The outcome of each tool line: the read worked, the proposal has none, the step the approval ran worked.
        decided.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool")
            .Select(m => m.GetProperty("toolState").ValueKind == JsonValueKind.Null ? null : m.GetProperty("toolState").GetString())
            .Should().Equal("done", null, "done");
    }
}
