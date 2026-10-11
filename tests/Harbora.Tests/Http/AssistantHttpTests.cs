using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Agent;
using Harbora.Tests.Agent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// One booted panel for the assistant's endpoints: the real pipeline (authentication, the entitlement
/// filter, antiforgery, the capability policies) with only the model scripted and two test-double
/// write tools added. Its own host rather than the shared one, because the shared host has no seam for
/// replacing <see cref="IAgentChatClient"/>; it is built through <c>WithWebHostBuilder</c> on a
/// <see cref="HarboraWebFactory"/>, so every substitution that factory documents still applies.
/// </summary>
public sealed class AssistantPanelFixture : IDisposable
{
    private readonly HarboraWebFactory _root = new();

    public WebApplicationFactory<Program> Panel { get; }
    public ScriptedAgentChatClient Chat { get; } = new();
    public FakeTool SafeTool { get; } = new("test_safe_write", AgentToolKind.SafeWrite);
    public FakeTool DeleteTool { get; } = new("test_delete", AgentToolKind.Destructive);

    public Guid WorkspaceId { get; } = Guid.CreateVersion7();
    public Guid LockedWorkspaceId { get; } = Guid.CreateVersion7();

    public AssistantPanelFixture()
    {
        Panel = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentChatClient>();
            services.AddSingleton<IAgentChatClient>(Chat);
            services.AddSingleton<IAgentTool>(SafeTool);
            services.AddSingleton<IAgentTool>(DeleteTool);
        }));

        Seed(db =>
        {
            db.Settings.Add(new Setting { Key = SettingKeys.SetupCompleted, Value = "true" });

            // A workspace with no plan is on the provider's default plan, which the seeder entitles to
            // every feature — so the locked workspace is put on a customer plan, where the assistant
            // ships Locked, exactly as a customer's would be.
            var customerPlan = db.Plans.Where(p => !p.IsDefault).Select(p => (Guid?)p.Id).First();
            foreach (var (id, slug, plan) in new[] { (WorkspaceId, "assistant-on", (Guid?)null), (LockedWorkspaceId, "assistant-off", customerPlan) })
            {
                db.Workspaces.Add(new Workspace { Id = id, Name = slug, Slug = slug, PlanId = plan });
                var projectId = Guid.CreateVersion7();
                db.Projects.Add(new Harbora.Domain.Projects.Project { Id = projectId, WorkspaceId = id, Name = "Shop", Slug = slug + "-shop" });
                db.Environments.Add(new Harbora.Domain.Projects.Environment
                {
                    WorkspaceId = id, ProjectId = projectId, Name = "Production", Slug = "production", IsDefault = true
                });
            }
            db.FeatureGrants.Add(new FeatureGrant
            {
                Scope = FeatureScope.Workspace, TargetId = WorkspaceId, FeatureKey = PlatformFeatures.Assistant, State = FeatureState.Enabled
            });
        });

        Read(db => db.Workspaces.Single(w => w.Id == LockedWorkspaceId).PlanId).Should().NotBeNull(
            "the locked workspace must not fall back to the provider's plan");
    }

    public void Seed(Action<HarboraDbContext> seed)
    {
        using var scope = Panel.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        seed(db);
        db.SaveChanges();
    }

    public T Read<T>(Func<HarboraDbContext, T> read)
    {
        using var scope = Panel.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<HarboraDbContext>());
    }

    public User GivenUser(Guid workspaceId, string email, SystemRole role = SystemRole.Owner)
    {
        using var scope = Panel.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var user = new User
        {
            Email = email, DisplayName = email, PasswordHash = hasher.Hash(HarboraWebFactory.TestPassword),
            Role = role, IsActive = true, EmailVerifiedAt = DateTimeOffset.UtcNow
        };
        Seed(db =>
        {
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = workspaceId, UserId = user.Id,
                Role = role is SystemRole.Owner or SystemRole.Admin ? WorkspaceRole.Admin : WorkspaceRole.Member
            });
        });
        return user;
    }

    /// <summary>A client signed in over the real login form, holding an antiforgery token for its session.</summary>
    public async Task<(HttpClient Client, string Csrf)> SignedInAs(string remoteIp, string email)
    {
        var client = Panel.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"), HandleCookies = true
        });
        client.DefaultRequestHeaders.Add(HarboraWebFactory.RemoteIpHeader, remoteIp);

        var loginToken = await client.AntiforgeryTokenFrom("/account/login");
        var login = await client.PostFormAsync("/account/login", loginToken,
            ("Email", email), ("Password", HarboraWebFactory.TestPassword));
        login.StatusCode.Should().Be(HttpStatusCode.Found, "the sign-in should succeed");

        // The token the island is handed is bound to the signed-in session, so it is taken after sign-in.
        return (client, await client.AntiforgeryTokenFrom("/apps"));
    }

    public void Dispose()
    {
        Panel.Dispose();
        _root.Dispose();
    }
}

[Collection(HarboraHttpCollection.Name)]
public class AssistantHttpTests(AssistantPanelFixture fixture) : IClassFixture<AssistantPanelFixture>
{
    private ScriptedAgentChatClient Chat => fixture.Chat;

    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? csrf, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : Body(body) };
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    /// <summary>The frames of a server-sent event stream: each an event name and its one JSON object.</summary>
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

    private async Task<List<(string Event, JsonElement Data)>> SendAsync(HttpClient client, string csrf, string conversation, string text)
    {
        var response = await PostAsync(client, $"/assistant/conversations/{conversation}/messages", csrf,
            new { text, page = new { path = "/apps" } });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        return Frames(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Status_reports_the_model_and_the_requests_left_today()
    {
        fixture.GivenUser(fixture.WorkspaceId, "status-owner@example.com");
        var (client, _) = await fixture.SignedInAs("203.0.113.180", "status-owner@example.com");

        var response = await client.GetAsync("/assistant/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.JsonAsync();
        json.GetProperty("available").GetBoolean().Should().BeTrue();
        json.GetProperty("model").GetString().Should().Be("test/agent-model:free");
        json.GetProperty("remainingToday").GetInt32().Should().BeLessThanOrEqualTo(AgentSettingKeys.DefaultDailyRequestLimit);
    }

    [Fact]
    public async Task A_workspace_without_the_entitlement_is_refused_by_the_filter()
    {
        fixture.GivenUser(fixture.LockedWorkspaceId, "locked-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.181", "locked-owner@example.com");
        Chat.Clear();

        var status = new HttpRequestMessage(HttpMethod.Get, "/assistant/status");
        status.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var refused = await client.SendAsync(status);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.JsonAsync()).GetProperty("error").GetString().Should().Be("feature_not_enabled");

        var message = await PostAsync(client, "/assistant/conversations/new/messages", csrf, new { text = "hello" });
        message.StatusCode.Should().NotBe(HttpStatusCode.OK);
        Chat.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_post_without_the_antiforgery_header_is_refused()
    {
        fixture.GivenUser(fixture.WorkspaceId, "csrf-owner@example.com");
        var (client, _) = await fixture.SignedInAs("203.0.113.182", "csrf-owner@example.com");
        Chat.Clear();

        var response = await PostAsync(client, "/assistant/conversations/new/messages", csrf: null, new { text = "hello" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Chat.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Approve_reject_and_revoke_refuse_a_post_without_the_antiforgery_header()
    {
        fixture.GivenUser(fixture.WorkspaceId, "csrf-plan-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.190", "csrf-plan-owner@example.com");
        Chat.Clear();
        Chat.ThenCall("test_safe_write", """{"name":"shop-db"}""").ThenText("Approve it?");
        var before = fixture.SafeTool.Executions;
        var frames = await SendAsync(client, csrf, "new", "create shop-db");
        var planId = frames.Single(f => f.Event == "plan").Data.GetProperty("id").GetGuid();

        (await PostAsync(client, $"/assistant/plans/{planId}/approve", csrf: null, new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(client, $"/assistant/plans/{planId}/reject", csrf: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(client, "/assistant/approvals/test_safe_write/revoke", csrf: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        fixture.SafeTool.Executions.Should().Be(before);
        fixture.Read(db => db.AgentPlans.Single(p => p.Id == planId).Status).Should().Be(Harbora.Domain.Agent.AgentPlanStatus.Pending,
            "neither the approval nor the rejection got past the antiforgery check");
    }

    [Fact]
    public async Task A_message_streams_started_first_and_done_last_and_the_conversation_can_be_reopened()
    {
        fixture.GivenUser(fixture.WorkspaceId, "stream-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.183", "stream-owner@example.com");
        Chat.Clear();
        Chat.ThenText("سلام! چه کمکی از دستم برمی‌آید؟");

        var frames = await SendAsync(client, csrf, "new", "سلام");

        frames.Select(f => f.Event).Should().Equal("started", "message", "done");
        var id = frames[0].Data.GetProperty("conversationId").GetGuid();
        frames[1].Data.GetProperty("role").GetString().Should().Be("assistant");
        frames[1].Data.GetProperty("text").GetString().Should().Be("سلام! چه کمکی از دستم برمی‌آید؟");
        frames[2].Data.GetProperty("conversationId").GetGuid().Should().Be(id);
        frames[2].Data.TryGetProperty("remainingToday", out _).Should().BeTrue();

        var list = await (await client.GetAsync("/assistant/conversations")).JsonAsync();
        list.EnumerateArray().Should().Contain(c => c.GetProperty("id").GetGuid() == id && c.GetProperty("title").GetString() == "سلام");

        var view = await (await client.GetAsync($"/assistant/conversations/{id}")).JsonAsync();
        view.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "assistant");
        view.GetProperty("pendingPlan").ValueKind.Should().Be(JsonValueKind.Null);

        // Somebody else in the same workspace cannot open it.
        fixture.GivenUser(fixture.WorkspaceId, "stream-colleague@example.com", SystemRole.Member);
        var (colleague, _) = await fixture.SignedInAs("203.0.113.184", "stream-colleague@example.com");
        (await colleague.GetAsync($"/assistant/conversations/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_proposed_write_is_a_plan_and_approving_it_runs_it_exactly_once()
    {
        fixture.GivenUser(fixture.WorkspaceId, "plan-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.185", "plan-owner@example.com");
        Chat.Clear();
        Chat.ThenCall("test_safe_write", """{"name":"shop-db"}""").ThenText("Approve the plan to create shop-db.");
        var before = fixture.SafeTool.Executions;

        var frames = await SendAsync(client, csrf, "new", "create shop-db");

        frames.Select(f => f.Event).Should().Equal("started", "message", "plan", "done");
        var plan = frames[2].Data;
        plan.GetProperty("status").GetString().Should().Be("pending");
        var step = plan.GetProperty("steps")[0];
        step.GetProperty("kind").GetString().Should().Be("safe");
        step.GetProperty("rememberable").GetBoolean().Should().BeTrue();
        fixture.SafeTool.Executions.Should().Be(before, "nothing runs before the person approves");
        var planId = plan.GetProperty("id").GetGuid();

        var approve = await PostAsync(client, $"/assistant/plans/{planId}/approve", csrf,
            new { confirmations = new Dictionary<string, string>(), rememberTools = new[] { "test_safe_write" } });
        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await approve.JsonAsync();
        result.GetProperty("planId").GetGuid().Should().Be(planId);
        result.GetProperty("status").GetString().Should().Be("executed");
        result.GetProperty("steps")[0].GetProperty("state").GetString().Should().Be("done");
        fixture.SafeTool.Executions.Should().Be(before + 1);

        var again = await PostAsync(client, $"/assistant/plans/{planId}/approve", csrf, new { });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.JsonAsync()).GetProperty("error").GetString().Should().Be("not_pending");
        fixture.SafeTool.Executions.Should().Be(before + 1);

        var approvals = await (await client.GetAsync("/assistant/approvals")).JsonAsync();
        approvals.EnumerateArray().Select(a => a.GetProperty("tool").GetString()).Should().Equal("test_safe_write");
        (await PostAsync(client, "/assistant/approvals/test_safe_write/revoke", csrf)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await client.GetAsync("/assistant/approvals")).JsonAsync()).GetArrayLength().Should().Be(0);

        fixture.Read(db => db.AuditLogs.Count(a => a.Action == AgentPlanService.StepExecutedAction && a.TargetId == planId.ToString()))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_delete_needs_its_name_typed_and_a_plan_can_be_rejected()
    {
        fixture.GivenUser(fixture.WorkspaceId, "delete-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.186", "delete-owner@example.com");
        Chat.Clear();
        Chat.ThenCall("test_delete").ThenText("Type the name to confirm.")
            .ThenCall("test_delete").ThenText("Type the name to confirm.");
        var before = fixture.DeleteTool.Executions;

        var first = await SendAsync(client, csrf, "new", "delete shop-db");
        var planId = first.Single(f => f.Event == "plan").Data.GetProperty("id").GetGuid();
        first.Single(f => f.Event == "plan").Data.GetProperty("steps")[0].GetProperty("confirmationPhrase").GetString().Should().Be("shop-db");

        var plan = first.Single(f => f.Event == "plan").Data;
        var index = plan.GetProperty("steps")[0].GetProperty("index").GetInt32();
        index.Should().Be(0, "a step's index is its 0-based position, and the card keys confirmations by it");

        var wrong = await PostAsync(client, $"/assistant/plans/{planId}/approve", csrf,
            new { confirmations = new Dictionary<string, string> { [index.ToString()] = "Shop-DB" } });
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var refusal = await wrong.JsonAsync();
        refusal.GetProperty("error").GetString().Should().Be("confirmation_mismatch");
        refusal.GetProperty("stepIndex").GetInt32().Should().Be(index);
        refusal.GetProperty("messageEn").GetString().Should().NotBeNullOrEmpty();
        refusal.GetProperty("messageFa").GetString().Should().NotBeNullOrEmpty();

        var right = await PostAsync(client, $"/assistant/plans/{planId}/approve", csrf,
            new { confirmations = new Dictionary<string, string> { [index.ToString()] = "shop-db" } });
        right.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.DeleteTool.Executions.Should().Be(before + 1);

        var conversation = first[0].Data.GetProperty("conversationId").GetGuid();
        var second = await SendAsync(client, csrf, conversation.ToString(), "and delete it again");
        var secondPlan = second.Single(f => f.Event == "plan").Data.GetProperty("id").GetGuid();

        (await PostAsync(client, $"/assistant/plans/{secondPlan}/reject", csrf)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var late = await PostAsync(client, $"/assistant/plans/{secondPlan}/approve", csrf,
            new { confirmations = new Dictionary<string, string> { ["0"] = "shop-db" } });
        late.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await late.JsonAsync()).GetProperty("messageEn").GetString().Should().Contain("rejected");
        fixture.DeleteTool.Executions.Should().Be(before + 1);
    }

    [Fact]
    public async Task A_step_refused_when_rechecked_at_approval_is_a_409_with_the_reason_and_nothing_runs()
    {
        fixture.GivenUser(fixture.WorkspaceId, "recheck-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.189", "recheck-owner@example.com");
        Chat.Clear();
        Chat.ThenCall("test_safe_write", """{"name":"shop-db"}""").ThenText("Approve it?");
        var before = fixture.SafeTool.Executions;

        var frames = await SendAsync(client, csrf, "new", "create shop-db");
        var planId = frames.Single(f => f.Event == "plan").Data.GetProperty("id").GetGuid();

        fixture.SafeTool.OnValidate = (_, _, _) => AgentValidation.Refused("The workspace's quota is full now.", "سهمیهٔ فضای کاری اکنون پر است.");
        try
        {
            var refused = await PostAsync(client, $"/assistant/plans/{planId}/approve", csrf, new { });

            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await refused.JsonAsync();
            body.GetProperty("error").GetString().Should().Be("revalidation_refused");
            body.GetProperty("messageEn").GetString().Should().EndWith("The workspace's quota is full now.");
            body.GetProperty("messageFa").GetString().Should().Contain("سهمیهٔ فضای کاری اکنون پر است.");
            body.GetProperty("stepIndex").GetInt32().Should().Be(0);
            fixture.SafeTool.Executions.Should().Be(before);
        }
        finally
        {
            fixture.SafeTool.OnValidate = null;
        }
    }

    // ---- a turn the person can leave and come back to -----------------------------------------

    [Fact]
    public async Task A_conversation_can_be_created_before_its_first_message_and_then_used()
    {
        fixture.GivenUser(fixture.WorkspaceId, "create-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.191", "create-owner@example.com");
        Chat.Clear();

        (await PostAsync(client, "/assistant/conversations", csrf: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "creating one is a POST like any other and needs the antiforgery header");

        var created = await PostAsync(client, "/assistant/conversations", csrf);
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await created.JsonAsync();
        var id = summary.GetProperty("id").GetGuid();
        summary.GetProperty("title").GetString().Should().BeEmpty();
        summary.TryGetProperty("updatedAt", out _).Should().BeTrue();

        var view = await (await client.GetAsync($"/assistant/conversations/{id}")).JsonAsync();
        view.GetProperty("messages").GetArrayLength().Should().Be(0);
        view.GetProperty("inProgress").GetBoolean().Should().BeFalse();

        Chat.ThenStreamedText("Hello", ", world");
        var frames = await SendAsync(client, csrf, id.ToString(), "hi");
        frames.Select(f => f.Event).Should().Equal("started", "delta", "delta", "message", "done");
        frames[0].Data.GetProperty("conversationId").GetGuid().Should().Be(id);
        frames.Where(f => f.Event == "delta").Select(f => f.Data.GetProperty("text").GetString()).Should().Equal("Hello", ", world");
        frames[3].Data.GetProperty("text").GetString().Should().Be("Hello, world");
    }

    [Fact]
    public async Task A_second_message_while_the_first_is_still_being_answered_is_a_409_with_the_reason()
    {
        var owner = fixture.GivenUser(fixture.WorkspaceId, "busy-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.192", "busy-owner@example.com");
        Chat.Clear();
        var conversationId = Guid.CreateVersion7();
        fixture.Seed(db => db.AgentConversations.Add(new Harbora.Domain.Agent.AgentConversation
        {
            Id = conversationId, WorkspaceId = fixture.WorkspaceId, UserId = owner.Id, Title = "busy",
            TurnStartedAt = DateTimeOffset.UtcNow
        }));

        var view = await (await client.GetAsync($"/assistant/conversations/{conversationId}")).JsonAsync();
        view.GetProperty("inProgress").GetBoolean().Should().BeTrue();

        var refused = await PostAsync(client, $"/assistant/conversations/{conversationId}/messages", csrf, new { text = "again?" });

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await refused.JsonAsync();
        body.GetProperty("error").GetString().Should().Be("turn_in_progress");
        body.GetProperty("messageEn").GetString().Should().StartWith("Still answering your previous message");
        body.GetProperty("messageFa").GetString().Should().NotBeNullOrEmpty();
        Chat.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Leaving_in_the_middle_of_an_answer_does_not_lose_it()
    {
        fixture.GivenUser(fixture.WorkspaceId, "leave-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.193", "leave-owner@example.com");
        Chat.Clear();
        var left = new TaskCompletionSource();
        Chat.ThenText("Saved for when you come back.", before: ct => left.Task.WaitAsync(ct));

        var request = new HttpRequestMessage(HttpMethod.Post, "/assistant/conversations/new/messages")
        {
            Content = Body(new { text = "answer slowly" })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Read the started frame, then leave — the reload the owner did.
        var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];
        var first = new StringBuilder();
        while (!first.ToString().Contains("\n\n"))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            first.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        var conversationId = Frames(first.ToString().Split("\n\n")[0] + "\n\n")[0].Data.GetProperty("conversationId").GetGuid();
        response.Dispose();
        left.SetResult();

        // The answer arrives in the database although nobody was listening for it.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        (bool Saved, bool Marked) state = default;
        while (DateTime.UtcNow < deadline)
        {
            state = fixture.Read(db => (
                db.AgentMessages.Any(m => m.ConversationId == conversationId && m.Text == "Saved for when you come back."),
                db.AgentConversations.Single(c => c.Id == conversationId).TurnStartedAt != null));
            if (state.Saved && !state.Marked) break;
            await Task.Delay(100);
        }

        state.Saved.Should().BeTrue("the turn runs to its end whoever is listening");
        state.Marked.Should().BeFalse("and clears its mark when it ends");
        var view = await (await client.GetAsync($"/assistant/conversations/{conversationId}")).JsonAsync();
        view.GetProperty("inProgress").GetBoolean().Should().BeFalse();
        view.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "assistant");
    }

    [Fact]
    public async Task Refusals_before_the_stream_are_ordinary_status_codes()
    {
        fixture.GivenUser(fixture.WorkspaceId, "refusal-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.187", "refusal-owner@example.com");
        Chat.Clear();

        var empty = await PostAsync(client, "/assistant/conversations/new/messages", csrf, new { text = "   " });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await empty.JsonAsync()).GetProperty("error").GetString().Should().Be("invalid");

        var unknown = await PostAsync(client, $"/assistant/conversations/{Guid.NewGuid()}/messages", csrf, new { text = "hi" });
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await PostAsync(client, "/assistant/conversations/not-a-guid/messages", csrf, new { text = "hi" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PostAsync(client, $"/assistant/plans/{Guid.NewGuid()}/approve", csrf, new { }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/assistant/conversations/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Chat.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_failure_after_the_stream_began_is_an_error_event_not_a_500()
    {
        fixture.GivenUser(fixture.WorkspaceId, "failure-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.188", "failure-owner@example.com");
        Chat.Clear();
        Chat.Then(AgentChatResponse.Failed("The provider timed out after 90 seconds.", "ارائه‌دهنده پس از ۹۰ ثانیه پاسخ نداد."));

        var frames = await SendAsync(client, csrf, "new", "hello");

        frames.Select(f => f.Event).Should().Equal("started", "error", "done");
        frames[1].Data.GetProperty("messageEn").GetString().Should().Be("The provider timed out after 90 seconds.",
            "the owner manages the platform and may read the provider's own words");
        frames[1].Data.GetProperty("messageFa").GetString().Should().NotBeNullOrEmpty();
    }
}
