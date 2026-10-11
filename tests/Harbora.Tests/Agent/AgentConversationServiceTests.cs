using FluentAssertions;
using Harbora.Domain.Agent;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Agent.Tools;
using Harbora.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The tool-calling loop, driven by a scripted model: every case "How one message flows" in the
/// 2026-10-11 assistant-agent design names — reads run, writes become a plan, refusals go back to the
/// model, the consent rules, the cap and the ceiling — and the honesty rule underneath all of them:
/// nothing is reported as done unless it ran.
/// </summary>
public class AgentConversationServiceTests
{
    private static FakeTool Reader(string name = "peek", string result = """{"answer":42}""") =>
        new(name, AgentToolKind.Read)
        {
            OnExecute = (_, _) => new AgentToolResult(true, result, $"{name} looked", $"{name} دید")
        };

    // ---- reads ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_tool_runs_and_its_result_is_in_the_next_request()
    {
        var peek = Reader();
        using var h = new AgentHarness([peek]);
        var call = h.Chat.Call("peek");
        h.Chat.ThenCalls(call).ThenText("It is 42.");

        var (outcome, events) = await h.SendAsync("what is the answer?");

        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        events.Names().Should().Equal("started", "tool", "tool", "message", "done");
        events.Where(e => e.Name == "tool").Select(e => ((AgentToolEventData)e.Data).State).Should().Equal("running", "done");
        events.Single<AgentMessageView>("message").Text.Should().Be("It is 42.");
        peek.Executions.Should().Be(1);

        var second = h.Chat.Requests[1].Messages;
        var answer = second.Single(m => m.Role == AgentRole.Tool);
        answer.ToolCallId.Should().Be(call.Id);
        answer.Content.Should().Contain("42");
        second.Should().Contain(m => m.Role == AgentRole.Assistant && m.ToolCalls != null && m.ToolCalls[0].Id == call.Id,
            "a tool answer is only valid behind the call it answers");

        // Every message is kept so the next turn has the context.
        var stored = await h.Db.AgentMessages.OrderBy(m => m.Sequence).Select(m => m.Role).ToListAsync();
        stored.Should().Equal(AgentMessageRole.User, AgentMessageRole.Assistant, AgentMessageRole.Tool, AgentMessageRole.Assistant);
    }

    [Fact]
    public async Task An_unknown_tool_is_fed_back_to_the_model_and_the_turn_continues()
    {
        using var h = new AgentHarness([Reader()]);
        h.Chat.ThenCall("make_coffee").ThenText("I cannot do that.");

        var (_, events) = await h.SendAsync("make coffee");

        events.Names().Should().Equal("started", "message", "done");
        var fedBack = h.Chat.Requests[1].Messages.Single(m => m.Role == AgentRole.Tool).Content;
        fedBack.Should().Contain("Unknown tool 'make_coffee'").And.Contain("peek");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("\"a string\"")]
    public async Task Arguments_that_are_not_a_json_object_are_fed_back_and_nothing_runs(string arguments)
    {
        var peek = Reader();
        using var h = new AgentHarness([peek]);
        h.Chat.ThenCall("peek", arguments).ThenText("Sorry.");

        await h.SendAsync("peek");

        peek.Executions.Should().Be(0);
        h.Chat.Requests[1].Messages.Single(m => m.Role == AgentRole.Tool).Content
            .Should().Contain("not a valid JSON object");
    }

    [Fact]
    public async Task Blank_or_repeated_call_ids_are_replaced_so_every_answer_names_exactly_one_call()
    {
        var peek = Reader();
        using var h = new AgentHarness([peek]);
        h.Chat.ThenCalls(new AgentToolCall("", "peek", "{}"), new AgentToolCall("dup", "peek", "{}"), new AgentToolCall("dup", "peek", "{}"))
            .ThenText("Done looking.");

        await h.SendAsync("peek three times");

        var second = h.Chat.Requests[1].Messages;
        var ids = second.Single(m => m.Role == AgentRole.Assistant).ToolCalls!.Select(c => c.Id).ToList();
        ids.Should().OnlyHaveUniqueItems().And.NotContain("");
        second.Where(m => m.Role == AgentRole.Tool).Select(m => m.ToolCallId).Should().Equal(ids);
        peek.Executions.Should().Be(3);
    }

    [Fact]
    public async Task Blank_arguments_count_as_an_empty_object()
    {
        var peek = Reader();
        using var h = new AgentHarness([peek]);
        h.Chat.ThenCall("peek", "").ThenText("ok");

        await h.SendAsync("peek");

        peek.Executions.Should().Be(1);
    }

    [Fact]
    public async Task A_tool_the_role_does_not_allow_is_not_offered_and_is_refused_when_called_anyway()
    {
        var create = new FakeTool("create_app", AgentToolKind.SafeWrite, Capabilities.AppsCreate);
        using var h = new AgentHarness([Reader(), create], role: SystemRole.Viewer);
        h.Chat.ThenCall("create_app", """{"name":"shop"}""").ThenText("You cannot.");

        var (_, events) = await h.SendAsync("create an app");

        h.Chat.Requests[0].Tools.Select(t => t.Name).Should().Equal(["peek"], "a viewer is offered reads only");
        h.Chat.Requests[1].Messages.Single(m => m.Role == AgentRole.Tool).Content
            .Should().Contain("does not allow 'create_app'");
        create.Validations.Should().Be(0);
        events.Names().Should().NotContain("plan");
        (await h.Db.AgentPlans.CountAsync()).Should().Be(0);
    }

    // ---- writes become plans -------------------------------------------------------------------

    [Fact]
    public async Task A_refused_write_is_fed_back_and_the_corrected_call_is_accepted_into_a_plan()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite)
        {
            OnValidate = (args, _, _) =>
            {
                var name = AgentArgs.String(args, "name") ?? "";
                return name == name.ToLowerInvariant() && !name.Contains(' ')
                    ? new AgentValidation(true, TitleEn: $"Create database {name}", TitleFa: $"ساخت دیتابیس {name}")
                    : AgentValidation.Refused("Names are lowercase with hyphens.", "نام باید با حروف کوچک باشد.");
            }
        };
        using var h = new AgentHarness([create]);
        h.Chat.ThenCall("create_database", """{"name":"Shop DB"}""")
            .ThenCall("create_database", """{"name":"shop-db"}""")
            .ThenText("I prepared a plan; approve it to create shop-db.");

        var (_, events) = await h.SendAsync("make a database called Shop DB");

        h.Chat.Requests[1].Messages.Last().Content.Should().StartWith("Refused: Names are lowercase with hyphens.");
        h.Chat.Requests[2].Messages.Last().Content
            .Should().Be("Added as step 1 — nothing has run yet; the user will approve it. (Step 1: Create database shop-db)");

        create.Validations.Should().Be(2);
        create.Executions.Should().Be(0, "a write is only ever validated while the model is talking");

        events.Names().Should().Equal("started", "message", "plan", "done");
        var plan = events.Single<AgentPlanView>("plan");
        plan.Status.Should().Be("pending");
        plan.ExpiresAt.Should().Be(AgentHarness.Start + TimeSpan.FromMinutes(15));
        plan.Steps.Should().ContainSingle();
        plan.Steps[0].Should().BeEquivalentTo(new
        {
            Index = 0, Tool = "create_database", Kind = "safe", TitleEn = "Create database shop-db",
            AutoApproved = false, Rememberable = true, ConfirmationPhrase = (string?)null
        });
        (await h.Db.AgentPlans.SingleAsync()).Status.Should().Be(AgentPlanStatus.Pending);
    }

    [Fact]
    public async Task Steps_keep_their_order_and_a_later_step_may_name_what_an_earlier_one_creates()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite)
        {
            OnValidate = (args, _, _) => new AgentValidation(true, TitleEn: "Create " + AgentArgs.String(args, "name"),
                Creates: new AgentPlannedResource(AgentResourceKinds.Database, AgentArgs.String(args, "name")!))
        };
        var attach = new FakeTool("attach_database", AgentToolKind.SafeWrite)
        {
            OnValidate = (args, _, scope) => scope.Plans(AgentResourceKinds.Database, AgentArgs.String(args, "name")!)
                ? new AgentValidation(true, TitleEn: "Attach " + AgentArgs.String(args, "name"))
                : AgentValidation.Refused("No such database.", "چنین دیتابیسی نیست.")
        };
        using var h = new AgentHarness([create, attach]);
        h.Chat.ThenCall("attach_database", """{"name":"shop-db"}""")
            .ThenCalls(h.Chat.Call("create_database", """{"name":"shop-db"}"""), h.Chat.Call("attach_database", """{"name":"shop-db"}"""))
            .ThenText("Plan ready.");

        var (_, events) = await h.SendAsync("create shop-db and attach it");

        h.Chat.Requests[1].Messages.Last().Content.Should().StartWith("Refused: No such database.",
            "attaching something nothing creates is refused");
        var plan = events.Single<AgentPlanView>("plan");
        plan.Steps.Select(s => (s.Index, s.Tool)).Should().Equal((0, "create_database"), (1, "attach_database"));
    }

    [Fact]
    public async Task A_finished_turn_of_remembered_safe_steps_runs_at_once_and_says_so()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([create]);
        h.Remember("create_database");
        h.Chat.ThenCall("create_database", """{"name":"shop-db"}""").ThenText("Creating shop-db.");

        var (_, events) = await h.SendAsync("create shop-db");

        events.Names().Should().Equal("started", "message", "executed", "done");
        var result = events.Single<AgentPlanResultView>("executed");
        result.Status.Should().Be("executed");
        result.Steps.Should().ContainSingle(s => s.State == "done" && s.SummaryEn == "create_database done");
        create.Executions.Should().Be(1);
        create.Validations.Should().Be(2, "an auto-approved step is still validated again before it runs");

        h.Audit.Entries.Should().ContainSingle(e => e.Action == AgentPlanService.StepExecutedAction);
        (await h.Db.AgentPlans.SingleAsync()).Status.Should().Be(AgentPlanStatus.Executed);
    }

    [Theory]
    [InlineData(AgentToolKind.DangerousWrite)]
    [InlineData(AgentToolKind.Destructive)]
    public async Task Dangerous_and_destructive_steps_always_get_a_card_even_with_an_approval_row(AgentToolKind kind)
    {
        var tool = new FakeTool("risky", kind);
        using var h = new AgentHarness([tool]);
        h.Remember("risky"); // a row nothing legitimate writes — the policy must still ignore it
        h.Chat.ThenCall("risky", """{"name":"shop-db"}""").ThenText("Waiting for your approval.");

        var (_, events) = await h.SendAsync("do the risky thing");

        events.Names().Should().Contain("plan").And.NotContain("executed");
        var step = events.Single<AgentPlanView>("plan").Steps.Single();
        step.AutoApproved.Should().BeFalse();
        step.Rememberable.Should().BeFalse();
        step.Kind.Should().Be(kind == AgentToolKind.Destructive ? "destructive" : "dangerous");
        step.ConfirmationPhrase.Should().Be(kind == AgentToolKind.Destructive ? "shop-db" : null);
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task One_unremembered_step_puts_the_whole_plan_on_a_card()
    {
        var remembered = new FakeTool("set_env", AgentToolKind.SafeWrite);
        var fresh = new FakeTool("add_domain", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([remembered, fresh]);
        h.Remember("set_env");
        h.Chat.ThenCalls(h.Chat.Call("set_env"), h.Chat.Call("add_domain")).ThenText("Plan ready.");

        var (_, events) = await h.SendAsync("set env and add a domain");

        var plan = events.Single<AgentPlanView>("plan");
        plan.Steps.Select(s => s.AutoApproved).Should().Equal(true, false);
        remembered.Executions.Should().Be(0);
        fresh.Executions.Should().Be(0);
    }

    [Fact]
    public async Task A_destructive_tool_that_names_no_confirmation_phrase_is_refused_rather_than_offered()
    {
        var delete = new FakeTool("delete_app", AgentToolKind.Destructive)
        {
            OnValidate = (_, _, _) => new AgentValidation(true, TitleEn: "Delete app")
        };
        using var h = new AgentHarness([delete]);
        h.Chat.ThenCall("delete_app").ThenText("I could not.");

        var (_, events) = await h.SendAsync("delete it");

        events.Names().Should().NotContain("plan");
        h.Chat.Requests[1].Messages.Last().Content.Should().StartWith("Refused:");
    }

    [Fact]
    public async Task A_turn_the_model_never_finished_never_auto_runs_its_steps()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([create]);
        h.Remember("create_database");
        h.Chat.ThenCall("create_database").Then(AgentChatResponse.Failed("Provider timed out.", "مهلت تمام شد."));

        var (_, events) = await h.SendAsync("create it");

        events.Names().Should().Equal("started", "error", "plan", "done");
        create.Executions.Should().Be(0, "the model may have meant more steps than it got to propose");
    }

    // ---- gates ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_without_the_entitlement_is_refused_before_anything_is_stored_or_sent()
    {
        using var h = new AgentHarness([Reader()], entitled: false);
        h.Chat.ThenText("never");

        var (outcome, events) = await h.SendAsync("hello");

        outcome.Status.Should().Be(AgentSendStatus.NotEntitled);
        events.Should().BeEmpty();
        h.Chat.Requests.Should().BeEmpty();
        (await h.Db.AgentConversations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unavailable_model_is_an_error_event_and_no_call_is_made()
    {
        using var h = new AgentHarness([Reader()]);
        h.Chat.Unavailable = new AssistantUnavailable("The assistant is turned off.", "دستیار خاموش است.");

        var (_, events) = await h.SendAsync("hello");

        events.Names().Should().Equal("started", "error", "done");
        events.Single<AgentErrorData>("error").MessageFa.Should().Be("دستیار خاموش است.");
        h.Chat.Requests.Should().BeEmpty();
        (await h.Db.AgentMessages.CountAsync()).Should().Be(1, "the person's message is kept even when nobody answered it");
    }

    [Fact]
    public async Task A_provider_failure_is_an_error_event_in_both_languages()
    {
        using var h = new AgentHarness([Reader()]);
        h.Chat.Then(AgentChatResponse.Failed("Rate limited by the provider.", "ارائه‌دهنده محدود کرد."));

        var (_, events) = await h.SendAsync("hello");

        events.Names().Should().Equal("started", "error", "done");
        events.Single<AgentErrorData>("error").Should().Be(new AgentErrorData("Rate limited by the provider.", "ارائه‌دهنده محدود کرد."));
    }

    [Fact]
    public async Task Somebody_who_does_not_manage_the_platform_never_reads_the_providers_own_words()
    {
        using var h = new AgentHarness([Reader()], role: SystemRole.Member);
        h.Chat.Then(AgentChatResponse.Failed(
            "OpenRouter answered 402: insufficient credits for model vendor/secret-model at api.example.internal",
            "OpenRouter با ۴۰۲ پاسخ داد"));

        var (_, events) = await h.SendAsync("hello");

        var error = events.Single<AgentErrorData>("error");
        error.MessageEn.Should().Be("The AI provider could not answer right now. Try again in a moment.");
        error.MessageFa.Should().NotContain("OpenRouter");

        h.Chat.Unavailable = new AssistantUnavailable("No API key is configured for the AI provider.", "کلید API تنظیم نشده است.");
        var (_, again) = await h.SendAsync("hello?");
        again.Single<AgentErrorData>("error").MessageEn.Should().StartWith("The assistant is not available right now.");
        (await h.Service.StatusAsync(h.Actor, default)).ReasonEn.Should().StartWith("The assistant is not available right now.");
    }

    [Fact]
    public async Task The_daily_cap_counts_every_model_call_and_stops_the_turn_when_it_is_reached()
    {
        using var h = new AgentHarness([Reader()]);
        h.SetDailyLimit("2");
        h.Chat.ThenCall("peek").ThenCall("peek").ThenText("never reached");

        var (_, events) = await h.SendAsync("peek twice");

        h.Chat.Requests.Should().HaveCount(2);
        events.Names().Should().Contain("error");
        events.Last().Data.Should().Be(new AgentDoneData(events.Single<AgentStartedData>("started").ConversationId, 0));

        // And the next message is refused before any call.
        h.Chat.Clear();
        var (_, again) = await h.SendAsync("hello?");
        again.Names().Should().Equal("started", "error", "done");
        h.Chat.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cap_of_zero_means_unlimited()
    {
        using var h = new AgentHarness([Reader()]);
        h.SetDailyLimit("0");
        h.Chat.ThenText("hi");

        var (_, events) = await h.SendAsync("hello");

        ((AgentDoneData)events.Last().Data).RemainingToday.Should().BeNull();
    }

    [Fact]
    public async Task One_message_makes_at_most_eight_model_calls()
    {
        var peek = Reader();
        using var h = new AgentHarness([peek]);
        for (var i = 0; i < 12; i++) h.Chat.ThenCall("peek");

        var (_, events) = await h.SendAsync("loop forever");

        h.Chat.Requests.Should().HaveCount(AgentConversationService.MaxModelCallsPerMessage);
        peek.Executions.Should().Be(8);
        events.Single<AgentErrorData>("error").MessageEn.Should().Contain("stopped after 8 steps");
        events.Last().Name.Should().Be("done");
        ((AgentDoneData)events.Last().Data).RemainingToday.Should().Be(AgentSettingKeys.DefaultDailyRequestLimit - 8);
    }

    // ---- secrets -------------------------------------------------------------------------------

    [Fact]
    public async Task A_secret_value_never_reaches_the_model_through_any_read_tool()
    {
        using var h = new AgentHarness();
        const string secret = "s3cr3t-Value-9f1c2a77";
        var appId = Guid.CreateVersion7();
        h.Db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = h.WorkspaceId, EnvironmentId = h.EnvironmentId, Name = "Shop", Slug = "shop-web",
            Status = AppStatus.Failed
        });
        h.Db.EnvironmentVariables.AddRange(
            new EnvironmentVariable { AppId = appId, Key = "STRIPE_KEY", Value = h.Protector.Protect(secret), IsSecret = true },
            new EnvironmentVariable { AppId = appId, Key = "PORT", Value = "8080" });
        var deploymentId = Guid.CreateVersion7();
        h.Db.Deployments.Add(new Deployment
        {
            Id = deploymentId, AppId = appId, WorkspaceId = h.WorkspaceId, Number = 1, Status = DeploymentStatus.Failed,
            ErrorMessage = $"startup failed: key {secret} rejected"
        });
        h.Db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = deploymentId, Sequence = 1, Message = $"using {secret} for payments" });
        await h.Db.SaveChangesAsync();

        h.Tools.Add(new GetAppTool(h.Db, h.Resolver, h.Protector));
        h.Tools.Add(new GetDeploymentTool(h.Db, h.Resolver, h.Protector));
        h.Chat.ThenCall("get_app", """{"app":"shop-web"}""")
            .ThenCall("get_deployment", """{"app":"shop-web"}""")
            .ThenText("The payment key was rejected.");

        await h.SendAsync("why did shop-web fail?");

        var sent = h.Chat.EverythingSent();
        sent.Should().Contain("STRIPE_KEY").And.Contain("8080").And.Contain("payments");
        sent.Should().NotContain(secret);
        (await h.Db.AgentMessages.AnyAsync(m => m.Text != null && m.Text.Contains(secret))).Should().BeFalse();
    }

    // ---- context and history -------------------------------------------------------------------

    [Fact]
    public async Task The_system_prompt_carries_the_rules_the_snapshot_and_the_page_resolved_to_names()
    {
        using var h = new AgentHarness();
        var appId = Guid.CreateVersion7();
        h.Db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = h.WorkspaceId, EnvironmentId = h.EnvironmentId, Name = "Shop", Slug = "shop-web",
            Status = AppStatus.Running
        });
        await h.Db.SaveChangesAsync();
        h.Chat.ThenText("ok");

        await h.SendAsync("restart this app", page: new AgentPageContext($"/apps/{appId}", appId, null, null, null), persian: true);

        var system = h.Chat.Requests[0].Messages[0];
        system.Role.Should().Be(AgentRole.System);
        system.Content.Should().Contain("Write tools do not perform anything when you call them")
            .And.Contain("transliterate")
            .And.Contain("answer in Persian")
            .And.Contain($"- project \"{h.ProjectSlug}\" (Shop)")
            .And.Contain("- app \"shop-web\": running, web")
            .And.Contain($"The user is currently looking at the page /apps/{appId} — app \"shop-web\" ({h.ProjectSlug}/production)");
        h.Chat.Requests[0].Messages[1].Should().Be(new AgentChatMessage(AgentRole.User, "restart this app"));
    }

    [Fact]
    public async Task The_next_turn_sends_the_conversation_back_and_titles_come_from_the_first_message()
    {
        using var h = new AgentHarness([Reader()]);
        h.Chat.ThenCall("peek").ThenText("It is 42.");
        var (_, first) = await h.SendAsync("  what   is\nthe answer?  ");
        var id = first.Single<AgentStartedData>("started").ConversationId;

        h.Chat.Clear();
        h.Chat.ThenText("Still 42.");
        await h.SendAsync("and now?", id);

        var history = h.Chat.Requests[0].Messages.Skip(1).Select(m => (m.Role, m.Content)).ToList();
        history.Should().HaveCount(5);
        history[0].Should().Be((AgentRole.User, "what   is\nthe answer?"));
        history[3].Should().Be((AgentRole.Assistant, "It is 42."));
        history[4].Should().Be((AgentRole.User, "and now?"));

        (await h.Db.AgentConversations.SingleAsync()).Title.Should().Be("what is the answer?");
    }

    [Fact]
    public async Task A_new_message_withdraws_the_pending_plan_and_the_model_is_told_it_did_not_run()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([create]);
        h.Chat.ThenCall("create_database").ThenText("Approve it?");
        var (_, first) = await h.SendAsync("create a database");
        var id = first.Single<AgentStartedData>("started").ConversationId;

        h.Chat.Clear();
        h.Chat.ThenText("Okay, never mind.");
        await h.SendAsync("actually, forget it", id);

        (await h.Db.AgentPlans.SingleAsync()).Status.Should().Be(AgentPlanStatus.Expired);
        h.Chat.Requests[0].Messages.Should().Contain(m => m.Role == AgentRole.Tool && m.Content!.Contains("\"state\":\"expired\""));
        create.Executions.Should().Be(0);
    }

    [Fact]
    public async Task Somebody_elses_conversation_is_not_found_and_nothing_is_written_to_it()
    {
        using var h = new AgentHarness([Reader()]);
        h.Chat.ThenText("hi");
        var (_, first) = await h.SendAsync("hello");
        var id = first.Single<AgentStartedData>("started").ConversationId;

        using var colleague = h.Another();
        var (outcome, events) = await colleague.SendAsync("let me in", id);

        outcome.Status.Should().Be(AgentSendStatus.NotFound);
        events.Should().BeEmpty();
        (await colleague.Service.GetAsync(colleague.Actor, id, persian: false, default)).Should().BeNull();
        (await colleague.Service.ListAsync(colleague.Actor, default)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_message_is_refused_without_a_stream(string text)
    {
        using var h = new AgentHarness();
        var (outcome, events) = await h.SendAsync(text);
        outcome.Status.Should().Be(AgentSendStatus.Invalid);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tool_that_throws_becomes_a_failed_read_and_the_turn_still_ends_with_done()
    {
        var broken = new FakeTool("peek", AgentToolKind.Read) { OnExecute = (_, _) => throw new InvalidOperationException("boom") };
        using var h = new AgentHarness([broken]);
        h.Chat.ThenCall("peek").ThenText("That failed.");

        var (_, events) = await h.SendAsync("peek");

        events.Where(e => e.Name == "tool").Select(e => ((AgentToolEventData)e.Data).State).Should().Equal("running", "failed");
        h.Chat.Requests[1].Messages.Last().Content.Should().Contain("failed on the server").And.NotContain("boom");
        events.Last().Name.Should().Be("done");
    }

    [Fact]
    public async Task The_conversation_view_shows_what_a_person_should_read_and_the_pending_plan()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([Reader(), create]);
        h.Chat.ThenCall("make_coffee").ThenCall("peek").ThenCall("create_database").ThenText("Approve the plan.");
        var (_, events) = await h.SendAsync("go");
        var id = events.Single<AgentStartedData>("started").ConversationId;

        var view = await h.Service.GetAsync(h.Actor, id, persian: true, default);

        view!.Messages.Select(m => (m.Role, m.ToolName)).Should().Equal(
            ("user", null), ("tool", "peek"), ("tool", "create_database"), ("assistant", null));
        view.Messages[1].Text.Should().Be("peek دید", "a Persian reader gets the Persian summary");
        view.PendingPlan!.Steps.Single().Tool.Should().Be("create_database");

        var list = await h.Service.ListAsync(h.Actor, default);
        list.Should().ContainSingle(c => c.Id == id && c.Title == "go");
    }

    [Fact]
    public async Task History_never_sends_an_answer_without_its_call_or_a_call_without_its_answer()
    {
        var rows = new List<AgentMessage>
        {
            new() { Role = AgentMessageRole.Tool, ToolCallId = "orphan", Text = "cut off by the window" },
            new() { Role = AgentMessageRole.User, Text = "hi" },
            new() { Role = AgentMessageRole.Assistant, ToolCallsJson = AgentJson.Serialize(new[] { new AgentToolCall("a", "peek", "{}"), new AgentToolCall("b", "peek", "{}") }) },
            new() { Role = AgentMessageRole.Tool, ToolCallId = "a", Text = "only one answer" },
            new() { Role = AgentMessageRole.Assistant, ToolCallsJson = AgentJson.Serialize(new[] { new AgentToolCall("c", "peek", "{}") }) },
            new() { Role = AgentMessageRole.Tool, ToolCallId = "c", Text = "answered" },
            new() { Role = AgentMessageRole.Assistant, Text = "done" }
        };

        var history = AgentConversationService.Sanitize(rows);

        history.Select(m => (m.Role, m.Content ?? "")).Should().Equal(
            (AgentRole.User, "hi"), (AgentRole.Assistant, ""), (AgentRole.Tool, "answered"), (AgentRole.Assistant, "done"));
        history[1].ToolCalls!.Single().Id.Should().Be("c");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Status_reports_the_model_and_what_is_left_today()
    {
        using var h = new AgentHarness();
        h.SetDailyLimit("10");

        var status = await h.Service.StatusAsync(h.Actor, default);
        status.Should().Be(new AgentStatusView(true, null, null, "test/agent-model:free", 10));

        h.Chat.Unavailable = new AssistantUnavailable("No model is configured.", "مدلی تنظیم نشده است.");
        (await h.Service.StatusAsync(h.Actor, default)).Should().Be(
            new AgentStatusView(false, "No model is configured.", "مدلی تنظیم نشده است.", "test/agent-model:free", 10));
    }
}
