using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harbora.Domain.Agent;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Agent;
using Harbora.Tests.Agent;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Round seven's conversation endpoints through the real pipeline — authentication, the entitlement filter,
/// antiforgery, the controller's status codes and JSON shapes: deleting one conversation or all of them,
/// Stop, and the searchable, paged list. The shapes are the contracts the pane codes against
/// (<c>AgentViews.cs</c>: <c>AgentDeleteAllView</c>, <c>AgentStopView</c>, the <c>stopped</c> event).
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AssistantConversationHttpTests(AssistantPanelFixture fixture) : IClassFixture<AssistantPanelFixture>
{
    private ScriptedAgentChatClient Chat => fixture.Chat;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? csrf, object? body = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
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

    /// <summary>One message answered in full; the conversation's id.</summary>
    private async Task<Guid> ConversationAsync(HttpClient client, string csrf, string text, string answer = "Sure.")
    {
        Chat.ThenText(answer);
        var response = await SendAsync(client, HttpMethod.Post, "/assistant/conversations/new/messages", csrf, new { text });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return Frames(await response.Content.ReadAsStringAsync())[0].Data.GetProperty("conversationId").GetGuid();
    }

    private Guid SeedConversation(Guid userId, string title, DateTimeOffset? turnStartedAt = null, AgentPlanStatus? plan = null)
    {
        var id = Guid.CreateVersion7();
        fixture.Seed(db =>
        {
            db.AgentConversations.Add(new AgentConversation
            {
                Id = id, WorkspaceId = fixture.WorkspaceId, UserId = userId, Title = title, TurnStartedAt = turnStartedAt
            });
            if (plan is { } status)
                db.AgentPlans.Add(new AgentPlan
                {
                    WorkspaceId = fixture.WorkspaceId, UserId = userId, ConversationId = id, Status = status, StepsJson = "[]",
                    ExpiresAt = DateTimeOffset.UtcNow + AgentPlan.Lifetime
                });
        });
        return id;
    }

    // ---- deleting one --------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_conversation_is_204_and_it_is_gone_while_the_audit_of_what_the_assistant_did_stays()
    {
        fixture.GivenUser(fixture.WorkspaceId, "conv-delete-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("192.0.2.241", "conv-delete-owner@example.com");
        Chat.Clear();
        Chat.ThenCall("test_safe_write", """{"name":"shop-db"}""").ThenText("Approve it?");
        var first = Frames(await (await SendAsync(client, HttpMethod.Post, "/assistant/conversations/new/messages", csrf,
            new { text = "my private question about shop-db" })).Content.ReadAsStringAsync());
        var id = first[0].Data.GetProperty("conversationId").GetGuid();
        var planId = first.Single(f => f.Event == "plan").Data.GetProperty("id").GetGuid();
        (await SendAsync(client, HttpMethod.Post, $"/assistant/plans/{planId}/approve", csrf, new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        var deleted = await SendAsync(client, HttpMethod.Delete, $"/assistant/conversations/{id}", csrf);

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/assistant/conversations/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await client.GetAsync("/assistant/conversations")).JsonAsync()).EnumerateArray()
            .Should().NotContain(c => c.GetProperty("id").GetGuid() == id);
        fixture.Read(db => db.AgentMessages.Count(m => m.ConversationId == id)).Should().Be(0);
        fixture.Read(db => db.AgentPlans.Count(p => p.ConversationId == id)).Should().Be(0);

        fixture.Read(db => db.AuditLogs.Count(a => a.Action == AgentPlanService.StepExecutedAction && a.TargetId == planId.ToString()))
            .Should().Be(1, "what the assistant did stays on the record after its conversation is gone");
        var audit = fixture.Read(db => db.AuditLogs.Single(a => a.Action == AgentConversationService.ConversationDeletedAction && a.TargetId == id.ToString()));
        (audit.MetadataJson ?? "").Should().NotContain("private question").And.NotContain("shop-db");

        (await SendAsync(client, HttpMethod.Delete, $"/assistant/conversations/{id}", csrf)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "a second delete finds nothing");
    }

    [Fact]
    public async Task A_delete_without_the_antiforgery_header_is_refused_and_deletes_nothing()
    {
        var owner = fixture.GivenUser(fixture.WorkspaceId, "conv-csrf-owner@example.com");
        var (client, _) = await fixture.SignedInAs("192.0.2.242", "conv-csrf-owner@example.com");
        var id = SeedConversation(owner.Id, "keep me");

        (await SendAsync(client, HttpMethod.Delete, $"/assistant/conversations/{id}", csrf: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(client, HttpMethod.Delete, "/assistant/conversations", csrf: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(client, HttpMethod.Post, $"/assistant/conversations/{id}/stop", csrf: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        fixture.Read(db => db.AgentConversations.Count(c => c.Id == id)).Should().Be(1);
    }

    [Fact]
    public async Task Somebody_elses_conversation_is_a_404_with_the_bilingual_body_and_is_kept()
    {
        var owner = fixture.GivenUser(fixture.WorkspaceId, "conv-theirs-owner@example.com");
        fixture.GivenUser(fixture.WorkspaceId, "conv-theirs-colleague@example.com", SystemRole.Member);
        var (colleague, csrf) = await fixture.SignedInAs("192.0.2.243", "conv-theirs-colleague@example.com");
        var id = SeedConversation(owner.Id, "not yours");

        var refused = await SendAsync(colleague, HttpMethod.Delete, $"/assistant/conversations/{id}", csrf);

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await refused.JsonAsync();
        body.GetProperty("error").GetString().Should().Be("not_found");
        body.GetProperty("messageEn").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("messageFa").GetString().Should().NotBeNullOrEmpty();
        (await SendAsync(colleague, HttpMethod.Post, $"/assistant/conversations/{id}/stop", csrf)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        fixture.Read(db => db.AgentConversations.Count(c => c.Id == id)).Should().Be(1);
    }

    [Fact]
    public async Task A_conversation_answering_or_running_a_plan_is_a_409_busy_with_the_reason()
    {
        var owner = fixture.GivenUser(fixture.WorkspaceId, "conv-busy-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("192.0.2.244", "conv-busy-owner@example.com");
        var answering = SeedConversation(owner.Id, "answering", turnStartedAt: DateTimeOffset.UtcNow);
        var executing = SeedConversation(owner.Id, "executing", plan: AgentPlanStatus.Executing);

        foreach (var id in new[] { answering, executing })
        {
            var refused = await SendAsync(client, HttpMethod.Delete, $"/assistant/conversations/{id}", csrf);

            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await refused.JsonAsync();
            body.GetProperty("error").GetString().Should().Be("busy");
            body.GetProperty("messageEn").GetString().Should().NotBeNullOrEmpty();
            body.GetProperty("messageFa").GetString().Should().NotBeNullOrEmpty();
            fixture.Read(db => db.AgentConversations.Count(c => c.Id == id)).Should().Be(1);
        }
    }

    // ---- deleting all --------------------------------------------------------------------------

    [Fact]
    public async Task Delete_all_answers_how_many_were_deleted_and_how_many_were_kept_busy()
    {
        var owner = fixture.GivenUser(fixture.WorkspaceId, "conv-all-owner@example.com");
        var colleague = fixture.GivenUser(fixture.WorkspaceId, "conv-all-colleague@example.com", SystemRole.Member);
        var (client, csrf) = await fixture.SignedInAs("192.0.2.245", "conv-all-owner@example.com");
        SeedConversation(owner.Id, "one");
        SeedConversation(owner.Id, "two");
        var busy = SeedConversation(owner.Id, "busy", turnStartedAt: DateTimeOffset.UtcNow);
        var theirs = SeedConversation(colleague.Id, "theirs");

        var response = await SendAsync(client, HttpMethod.Delete, "/assistant/conversations", csrf);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.JsonAsync();
        body.GetProperty("deleted").GetInt32().Should().Be(2);
        body.GetProperty("skipped").GetInt32().Should().Be(1);
        fixture.Read(db => db.AgentConversations.Where(c => c.UserId == owner.Id).Select(c => c.Id).ToList()).Should().Equal(busy);
        fixture.Read(db => db.AgentConversations.Count(c => c.Id == theirs)).Should().Be(1);
        fixture.Read(db => db.AuditLogs.Count(a => a.Action == AgentConversationService.ConversationsDeletedAction
                                                   && a.MetadataJson!.Contains("\"deleted\":2"))).Should().Be(1);
    }

    // ---- the list ------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_searches_and_pages_and_refuses_an_overlong_search_or_a_bad_skip()
    {
        var owner = fixture.GivenUser(fixture.WorkspaceId, "conv-list-owner@example.com");
        var (client, _) = await fixture.SignedInAs("192.0.2.246", "conv-list-owner@example.com");
        for (var i = 0; i < 32; i++) SeedConversation(owner.Id, i % 4 == 0 ? $"Deploy the shop {i}" : $"something else {i}");

        var found = await (await client.GetAsync("/assistant/conversations?q=" + Uri.EscapeDataString("  DEPLOY "))).JsonAsync();
        found.EnumerateArray().Select(c => c.GetProperty("title").GetString()).Should().HaveCount(8).And.OnlyContain(t => t!.StartsWith("Deploy"));

        var firstPage = await (await client.GetAsync("/assistant/conversations")).JsonAsync();
        var secondPage = await (await client.GetAsync("/assistant/conversations?skip=30")).JsonAsync();
        firstPage.GetArrayLength().Should().Be(30);
        secondPage.GetArrayLength().Should().Be(2);
        firstPage.EnumerateArray().Concat(secondPage.EnumerateArray()).Select(c => c.GetProperty("id").GetGuid())
            .Should().OnlyHaveUniqueItems().And.HaveCount(32);
        var row = firstPage[0];
        row.TryGetProperty("id", out _).Should().BeTrue();
        row.TryGetProperty("title", out _).Should().BeTrue();
        row.TryGetProperty("updatedAt", out _).Should().BeTrue();

        foreach (var bad in new[] { "q=" + new string('x', 101), "skip=-1", "skip=abc" })
        {
            var refused = await client.GetAsync("/assistant/conversations?" + bad);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, bad);
            var body = await refused.JsonAsync();
            body.GetProperty("error").GetString().Should().Be("invalid");
            body.GetProperty("messageEn").GetString().Should().NotBeNullOrEmpty();
            body.GetProperty("messageFa").GetString().Should().NotBeNullOrEmpty();
        }
        (await client.GetAsync("/assistant/conversations?q=" + new string('x', 100))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- stop ----------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_with_nothing_running_is_200_and_false()
    {
        fixture.GivenUser(fixture.WorkspaceId, "conv-stop-idle@example.com");
        var (client, csrf) = await fixture.SignedInAs("192.0.2.247", "conv-stop-idle@example.com");
        Chat.Clear();
        var id = await ConversationAsync(client, csrf, "hello");

        var response = await SendAsync(client, HttpMethod.Post, $"/assistant/conversations/{id}/stop", csrf);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("stopped").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Stop_in_the_middle_of_an_answer_is_200_true_and_the_stream_ends_with_message_stopped_done()
    {
        fixture.GivenUser(fixture.WorkspaceId, "conv-stop-live@example.com");
        var (client, csrf) = await fixture.SignedInAs("192.0.2.248", "conv-stop-live@example.com");
        Chat.Clear();
        Chat.ThenStreamed(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "never"), "m", null, "stop", null, null),
            ["Half an ", "answer"], after: ct => Task.Delay(Timeout.Infinite, ct));

        var request = new HttpRequestMessage(HttpMethod.Post, "/assistant/conversations/new/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text = "write me something long" }), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var stream = await response.Content.ReadAsStreamAsync();

        // Read until both deltas are in: the answer is now being written and waits for nothing but Stop.
        var received = new StringBuilder();
        var buffer = new byte[4096];
        bool BothDeltasIn()
        {
            var text = received.ToString();
            return text.EndsWith("\n\n", StringComparison.Ordinal) && Frames(text).Count(f => f.Event == "delta") >= 2;
        }
        using (var reading = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            while (!BothDeltasIn())
            {
                var read = await stream.ReadAsync(buffer, reading.Token);
                if (read == 0) break;
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        var id = Frames(received.ToString())[0].Data.GetProperty("conversationId").GetGuid();

        var stop = await SendAsync(client, HttpMethod.Post, $"/assistant/conversations/{id}/stop", csrf);
        stop.StatusCode.Should().Be(HttpStatusCode.OK);
        (await stop.JsonAsync()).GetProperty("stopped").GetBoolean().Should().BeTrue();

        using (var rest = new StreamReader(stream))
            received.Append(await rest.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30)));

        var frames = Frames(received.ToString());
        frames.Select(f => f.Event).Should().Equal("started", "delta", "delta", "message", "stopped", "done");
        frames[3].Data.GetProperty("text").GetString().Should().Be("Half an answer");
        frames[4].Data.GetProperty("messageEn").GetString().Should().Be("You stopped this answer.");
        frames[4].Data.GetProperty("messageFa").GetString().Should().Be("این پاسخ را متوقف کردید.");

        var view = await (await client.GetAsync($"/assistant/conversations/{id}")).JsonAsync();
        view.GetProperty("inProgress").GetBoolean().Should().BeFalse();
        view.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("role").GetString(), m.GetProperty("text").GetString()))
            .Should().Equal(("user", "write me something long"), ("assistant", "Half an answer"));
    }
}
