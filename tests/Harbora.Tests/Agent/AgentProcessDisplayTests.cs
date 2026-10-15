using FluentAssertions;
using Harbora.Domain.Agent;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// What the pane needs to show the process of a turn — and to show it again when the conversation is
/// reopened: the tools' names for people and how long each read took on the stream and in the stored
/// line, and the decided plans, with what they did, where they appeared.
/// </summary>
public class AgentProcessDisplayTests
{
    private static FakeTool Reader(string name) =>
        new(name, AgentToolKind.Read)
        {
            OnExecute = (_, _) => new AgentToolResult(true, """{"ok":true}""", $"{name} looked", $"{name} دید")
        };

    private static AgentHarness Harness(out FakeTool create)
    {
        create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        return new AgentHarness([Reader("get_app"), Reader("get_usage"), create]);
    }

    private static List<AgentToolEventData> ToolEvents(IEnumerable<AgentStreamEvent> events) =>
        events.Where(e => e.Name == "tool").Select(e => (AgentToolEventData)e.Data).ToList();

    /// <summary>One turn: two reads and a proposal in a single reply, then the model's closing words.</summary>
    private static async Task<(Guid ConversationId, Guid PlanId, Guid ClosingMessageId)> ProposeAsync(
        AgentHarness h, string closing = "Approve the plan to create it.", Guid? conversationId = null, string? callId = null)
    {
        if (callId is null)
            h.Chat.ThenCalls(h.Chat.Call("get_app"), h.Chat.Call("get_usage"), h.Chat.Call("create_database"));
        else
            h.Chat.ThenCalls(h.Chat.Call("get_app"), h.Chat.Call("get_usage"), new AgentToolCall(callId, "create_database", "{}"));
        h.Chat.ThenText(closing);

        var (_, events) = await h.SendAsync("create it", conversationId);

        return (events.Single<AgentStartedData>("started").ConversationId,
            events.Single<AgentPlanView>("plan").Id,
            events.Single<AgentMessageView>("message").Id);
    }

    // ---- the stream ----------------------------------------------------------------------------

    [Fact]
    public async Task A_running_read_has_its_label_and_no_duration_and_a_finished_one_has_both()
    {
        var slow = new FakeTool("get_deployment", AgentToolKind.Read)
        {
            OnExecuteAsync = async (_, _) =>
            {
                await Task.Delay(60);
                return new AgentToolResult(true, """{"status":"succeeded"}""", "Read deployment 3", "استقرار ۳ خوانده شد");
            }
        };
        using var h = new AgentHarness([slow]);
        h.Chat.ThenCall("get_deployment").ThenText("It succeeded.");

        var (_, events) = await h.SendAsync("how did the deployment go?");

        var tools = ToolEvents(events);
        tools.Select(t => t.State).Should().Equal("running", "done");
        tools.Should().OnlyContain(t => t.Name == "get_deployment" && t.Kind == "read");
        tools.Should().OnlyContain(t => t.LabelEn == "Reading the deployment's logs"
                                        && t.LabelFa == "خواندن لاگ‌های استقرار" && t.Category == "deployment");

        tools[0].DurationMs.Should().BeNull("a running tool has not taken any time yet");
        tools[1].DurationMs.Should().BeGreaterThanOrEqualTo(40, "the time is measured around the tool, not made up");
        tools[1].SummaryEn.Should().Be("Read deployment 3");

        // The stored line keeps the very number the stream reported.
        var stored = await h.Db.AgentMessages.AsNoTracking().SingleAsync(m => m.Role == AgentMessageRole.Tool);
        stored.DurationMs.Should().Be(tools[1].DurationMs);
    }

    [Fact]
    public async Task A_read_that_reports_failure_and_one_that_throws_are_both_timed_and_named()
    {
        var refusing = new FakeTool("get_app", AgentToolKind.Read)
        {
            OnExecute = (_, _) => AgentToolResult.Failed("No such app.", "چنین اپی نیست.")
        };
        var broken = new FakeTool("get_usage", AgentToolKind.Read) { OnExecute = (_, _) => throw new InvalidOperationException("boom") };
        using var h = new AgentHarness([refusing, broken]);
        h.Chat.ThenCalls(h.Chat.Call("get_app"), h.Chat.Call("get_usage")).ThenText("Neither worked.");

        var (_, events) = await h.SendAsync("look at both");

        var tools = ToolEvents(events);
        tools.Select(t => (t.Name, t.State)).Should().Equal(
            ("get_app", "running"), ("get_app", "failed"), ("get_usage", "running"), ("get_usage", "failed"));

        var failed = tools.Where(t => t.State == "failed").ToList();
        failed.Should().OnlyContain(t => t.DurationMs >= 0 && t.LabelEn != null && t.LabelFa != null && t.Category != null);
        failed[0].SummaryEn.Should().Be("No such app.");
        failed[1].SummaryEn.Should().Be("Checking your plan's usage and limits failed on the server.",
            "the sentence names the activity, not the identifier, and never the exception");
        failed[1].SummaryFa.Should().Be("بررسی مصرف و سقف‌های پلن در سرور ناموفق بود.");
        h.Chat.Requests[1].Messages.Last().Content.Should().NotContain("boom");

        (await h.Db.AgentMessages.AsNoTracking().Where(m => m.Role == AgentMessageRole.Tool).ToListAsync())
            .Should().OnlyContain(m => m.DurationMs != null && m.DurationMs >= 0);
    }

    [Fact]
    public async Task A_read_with_no_summary_of_its_own_is_described_by_its_label_not_its_identifier()
    {
        var silent = new FakeTool("get_usage", AgentToolKind.Read) { OnExecute = (_, _) => new AgentToolResult(true, "{}") };
        var failing = new FakeTool("get_catalog", AgentToolKind.Read) { OnExecute = (_, _) => new AgentToolResult(false, "{}") };
        using var h = new AgentHarness([silent, failing]);
        h.Chat.ThenCalls(h.Chat.Call("get_usage"), h.Chat.Call("get_catalog")).ThenText("ok");

        var (_, events) = await h.SendAsync("peek");

        var done = ToolEvents(events).Where(t => t.State != "running").ToList();
        done[0].SummaryEn.Should().Be("Looked up: Checking your plan's usage and limits");
        done[0].SummaryFa.Should().Be("بررسی شد: بررسی مصرف و سقف‌های پلن");
        done[1].SummaryEn.Should().Be("Browsing what can be created failed");
        done[1].SummaryFa.Should().Be("مرور آنچه می‌توان ساخت ناموفق بود");
        events.Where(e => e.Name == "tool").Select(e => ((AgentToolEventData)e.Data).SummaryEn)
            .Should().NotContain(s => s != null && (s.Contains("get_usage") || s.Contains("get_catalog")));
    }

    [Fact]
    public async Task A_tool_with_no_label_still_streams_under_its_identifier_and_the_other_category()
    {
        using var h = new AgentHarness([Reader("peek")]);
        h.Chat.ThenCall("peek").ThenText("ok");

        var (_, events) = await h.SendAsync("peek");

        ToolEvents(events).Should().OnlyContain(t => t.LabelEn == "peek" && t.LabelFa == "peek" && t.Category == "other");
    }

    // ---- the stored conversation: tool lines ---------------------------------------------------

    [Fact]
    public async Task Tool_lines_carry_their_label_category_and_duration_in_the_panels_language()
    {
        using var h = Harness(out _);
        var (id, _, _) = await ProposeAsync(h);

        var english = await h.Service.GetAsync(h.Actor, id, persian: false, default);
        var persian = await h.Service.GetAsync(h.Actor, id, persian: true, default);

        var tools = english!.Messages.Where(m => m.Role == "tool").ToList();
        tools.Select(m => (m.ToolName, m.ToolLabel, m.ToolCategory)).Should().Equal(
            ("get_app", "Reading an app's details", "app"),
            ("get_usage", "Checking your plan's usage and limits", "usage"),
            ("create_database", "Creating a database", "database"));
        tools[0].DurationMs.Should().BeGreaterThanOrEqualTo(0);
        tools[1].DurationMs.Should().BeGreaterThanOrEqualTo(0);
        tools[2].DurationMs.Should().BeNull("a proposal ran nothing");

        persian!.Messages.Where(m => m.Role == "tool").Select(m => m.ToolLabel).Should().Equal(
            "خواندن جزئیات اپ", "بررسی مصرف و سقف‌های پلن", "ساخت دیتابیس");

        // Nothing but tool lines has any of this.
        english.Messages.Where(m => m.Role != "tool").Should().OnlyContain(m =>
            m.ToolLabel == null && m.ToolCategory == null && m.DurationMs == null && m.PlanId == null);
    }

    [Fact]
    public async Task A_line_stored_before_durations_were_kept_has_none_and_a_tool_nobody_labelled_keeps_its_identifier()
    {
        using var h = Harness(out _);
        h.Chat.ThenText("hi");
        var (_, events) = await h.SendAsync("hello");
        var id = events.Single<AgentStartedData>("started").ConversationId;

        // A row as the last release wrote it: no DurationMs column value at all.
        h.Db.AgentMessages.Add(new AgentMessage
        {
            WorkspaceId = h.WorkspaceId, ConversationId = id, Sequence = 10, Role = AgentMessageRole.Tool,
            ToolCallId = "old", ToolName = "get_app", SummaryEn = "Read app web", SummaryFa = "اپ web بررسی شد"
        });
        h.Db.AgentMessages.Add(new AgentMessage
        {
            WorkspaceId = h.WorkspaceId, ConversationId = id, Sequence = 11, Role = AgentMessageRole.Tool,
            ToolCallId = "gone", ToolName = "retired_tool", SummaryEn = "Did something", SummaryFa = "کاری کرد"
        });
        await h.Db.SaveChangesAsync();

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        var old = view!.Messages.Single(m => m.ToolName == "get_app");
        old.DurationMs.Should().BeNull("unknown is not zero");
        old.ToolLabel.Should().Be("Reading an app's details");
        var gone = view.Messages.Single(m => m.ToolName == "retired_tool");
        gone.ToolLabel.Should().Be("retired_tool");
        gone.ToolCategory.Should().Be("other");
    }

    // ---- the stored conversation: plans --------------------------------------------------------

    [Fact]
    public async Task An_approved_plan_is_in_plans_once_with_the_approve_responses_own_result_after_its_turn()
    {
        using var h = Harness(out var create);
        var (id, planId, closingId) = await ProposeAsync(h);

        var approved = await h.Plans.ApproveAsync(h.Actor, planId, new AgentApproveRequest(null, null), default);
        approved.Ok.Should().BeTrue();
        create.Executions.Should().Be(1);

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        view!.PendingPlan.Should().BeNull();
        var history = view.Plans.Should().ContainSingle().Subject;
        history.Plan.Id.Should().Be(planId);
        history.Plan.Status.Should().Be("executed");
        history.Plan.Steps.Single().Tool.Should().Be("create_database");

        // Not a second mapping that can drift: what the pane got on approval is what the stored plan says.
        history.Result.Should().NotBeNull();
        AgentJson.Serialize(history.Result).Should().Be(AgentJson.Serialize(approved.Result));
        history.Result!.Steps.Single().State.Should().Be("done");
        history.Result.Steps.Single().SummaryEn.Should().Be("create_database done");

        // The card goes after the turn's closing words, not after the lines the execution wrote since.
        view.Messages.Select(m => m.Role).Should().Equal("user", "tool", "tool", "tool", "assistant", "tool");
        history.AfterMessageId.Should().Be(closingId);
        view.Messages[^1].PlanId.Should().Be(planId);
    }

    [Fact]
    public async Task The_proposal_line_and_the_execution_line_both_carry_their_plan_and_the_reads_carry_none()
    {
        using var h = Harness(out _);
        var (id, planId, _) = await ProposeAsync(h);
        await h.Plans.ApproveAsync(h.Actor, planId, new AgentApproveRequest(null, null), default);

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        view!.Messages.Where(m => m.Role == "tool").Select(m => (m.ToolName, m.PlanId)).Should().Equal(
            ("get_app", (Guid?)null), ("get_usage", null), ("create_database", planId), ("create_database", planId));
        view.Messages.Single(m => m.Text!.StartsWith("Proposed as step 1")).PlanId.Should().Be(planId,
            "written during the turn with no plan on its row, it is found by the call it was written for");
    }

    [Fact]
    public async Task The_pending_plan_is_only_the_pending_plan_though_its_proposal_line_already_names_it()
    {
        using var h = Harness(out _);
        var (id, planId, _) = await ProposeAsync(h);

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        view!.PendingPlan!.Id.Should().Be(planId);
        view.Plans.Should().BeEmpty();
        view.Messages.Single(m => m.ToolName == "create_database").PlanId.Should().Be(planId);
    }

    [Fact]
    public async Task A_rejected_a_superseded_and_a_lapsed_plan_are_in_plans_in_order_with_no_result()
    {
        using var h = Harness(out var create);
        var (id, first, firstClosing) = await ProposeAsync(h, "First card.");
        (await h.Plans.RejectAsync(h.Actor, first, default)).Should().BeNull();

        // The next message withdraws the card left open: that plan is stored as expired.
        h.Clock.UtcNow += TimeSpan.FromMinutes(1);
        var (_, second, secondClosing) = await ProposeAsync(h, "Second card.", id);
        h.Clock.UtcNow += TimeSpan.FromMinutes(1);
        var (_, third, thirdClosing) = await ProposeAsync(h, "Third card.", id);

        // The third is left until it lapses: stored as pending, but nobody can approve it any more.
        h.Clock.UtcNow += AgentPlan.Lifetime + TimeSpan.FromMinutes(1);
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync(p => p.Id == third)).Status.Should().Be(AgentPlanStatus.Pending);

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        view!.PendingPlan.Should().BeNull("a plan past its time cannot be decided");
        view.Plans!.Select(p => (p.Plan.Id, p.Plan.Status, HasResult: p.Result != null, p.AfterMessageId)).Should().Equal(
            (first, "rejected", false, (Guid?)firstClosing),
            (second, "expired", false, (Guid?)secondClosing),
            (third, "expired", false, (Guid?)thirdClosing));
        create.Executions.Should().Be(0, "none of them was ever approved");
    }

    [Fact]
    public async Task A_plan_whose_check_refused_a_step_at_approval_ran_nothing_and_has_no_result()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([Reader("get_app"), create]);
        h.Chat.ThenCalls(h.Chat.Call("create_database")).ThenText("Approve it?");
        var (_, events) = await h.SendAsync("create it");
        var id = events.Single<AgentStartedData>("started").ConversationId;
        var planId = events.Single<AgentPlanView>("plan").Id;

        // The world changed while the card was open.
        create.OnValidate = (_, _, _) => new AgentValidation(false, Error: "The database name is taken now.", ErrorFa: "نام دیتابیس اکنون گرفته شده است.");
        var outcome = await h.Plans.ApproveAsync(h.Actor, planId, new AgentApproveRequest(null, null), default);
        outcome.Refusal!.Code.Should().Be(AgentPlanRefusal.RevalidationRefusedCode);

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        var history = view!.Plans.Should().ContainSingle().Subject;
        history.Plan.Status.Should().Be("failed");
        history.Result.Should().BeNull("the approval was answered with a refusal, and nothing ran");
        create.Executions.Should().Be(0);
    }

    [Fact]
    public async Task A_plan_that_failed_midway_keeps_the_result_it_reported()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite)
        {
            OnExecute = (_, _) => AgentToolResult.Failed("Out of quota.", "سهمیه تمام شد.")
        };
        using var h = new AgentHarness([Reader("get_app"), Reader("get_usage"), create]);
        var (id, planId, _) = await ProposeAsync(h);

        var approved = await h.Plans.ApproveAsync(h.Actor, planId, new AgentApproveRequest(null, null), default);
        approved.Result!.Status.Should().Be("failed");

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        var history = view!.Plans.Should().ContainSingle().Subject;
        history.Plan.Status.Should().Be("failed");
        AgentJson.Serialize(history.Result).Should().Be(AgentJson.Serialize(approved.Result));
        history.Result!.Steps.Single().ErrorEn.Should().Be("Out of quota.");
    }

    [Fact]
    public async Task A_plan_that_ran_without_a_card_is_in_plans_after_the_closing_words()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([Reader("get_app"), Reader("get_usage"), create]);
        h.Remember("create_database");
        h.Chat.ThenCalls(h.Chat.Call("create_database")).ThenText("Created.");

        var (_, events) = await h.SendAsync("create it");
        var id = events.Single<AgentStartedData>("started").ConversationId;
        var executed = events.Single<AgentPlanResultView>("executed");
        var closing = events.Single<AgentMessageView>("message").Id;

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        view!.PendingPlan.Should().BeNull();
        var history = view.Plans.Should().ContainSingle().Subject;
        history.Plan.Status.Should().Be("executed");
        AgentJson.Serialize(history.Result).Should().Be(AgentJson.Serialize(executed));
        history.AfterMessageId.Should().Be(closing);
    }

    [Fact]
    public async Task Call_ids_that_repeat_from_one_turn_to_the_next_still_pair_each_line_with_its_own_plan()
    {
        // A provider that numbers its calls from zero, or a blank id the loop replaced: the same id turn after turn.
        using var h = Harness(out _);
        var (id, first, firstClosing) = await ProposeAsync(h, "Card one.", callId: "call_0");
        (await h.Plans.ApproveAsync(h.Actor, first, new AgentApproveRequest(null, null), default)).Ok.Should().BeTrue();
        h.Clock.UtcNow += TimeSpan.FromMinutes(1);
        var (_, second, secondClosing) = await ProposeAsync(h, "Card two.", id, callId: "call_0");
        (await h.Plans.RejectAsync(h.Actor, second, default)).Should().BeNull();

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        view!.Plans!.Select(p => (p.Plan.Id, p.Plan.Status, p.AfterMessageId)).Should().Equal(
            (first, "executed", (Guid?)firstClosing), (second, "rejected", (Guid?)secondClosing));
        var proposals = view.Messages.Where(m => m.Text!.StartsWith("Proposed as step 1")).ToList();
        proposals.Select(m => m.PlanId).Should().Equal((Guid?)first, second);
    }

    [Fact]
    public async Task A_plan_proposed_before_the_window_is_left_out_and_one_inside_it_is_not()
    {
        using var h = Harness(out _);
        var (id, first, _) = await ProposeAsync(h, "Card one.");
        (await h.Plans.ApproveAsync(h.Actor, first, new AgentApproveRequest(null, null), default)).Ok.Should().BeTrue();

        // Two hundred more messages push that whole turn out of the newest-200 window.
        var next = await h.Db.AgentMessages.IgnoreQueryFilters().Where(m => m.ConversationId == id).MaxAsync(m => m.Sequence) + 1;
        for (var i = 0; i < AgentConversationService.ConversationViewMessages; i++)
            h.Db.AgentMessages.Add(new AgentMessage
            {
                WorkspaceId = h.WorkspaceId, ConversationId = id, Sequence = next + i,
                Role = i % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant, Text = $"filler {i}"
            });
        await h.Db.SaveChangesAsync();

        var outside = await h.Service.GetAsync(h.Actor, id, persian: false, default);
        outside!.Messages.Should().HaveCount(AgentConversationService.ConversationViewMessages);
        outside.Plans.Should().BeEmpty("its proposal line is not among the messages, so there is nowhere to draw it");

        h.Clock.UtcNow += TimeSpan.FromMinutes(1);
        var (_, second, secondClosing) = await ProposeAsync(h, "Card two.", id);
        (await h.Plans.RejectAsync(h.Actor, second, default)).Should().BeNull();

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);
        view!.Plans!.Select(p => (p.Plan.Id, p.AfterMessageId)).Should().Equal((second, (Guid?)secondClosing));
    }

    [Fact]
    public async Task At_most_fifty_plans_come_back_the_newest_ones_oldest_first()
    {
        using var h = Harness(out _);
        var conversation = new AgentConversation { WorkspaceId = h.WorkspaceId, UserId = h.UserId, Title = "many" };
        h.Db.AgentConversations.Add(conversation);
        var at = AgentHarness.Start;
        var sequence = 1;
        const int Turns = 60; // three rows each, 180 in all: inside the 200-message window
        for (var n = 0; n < Turns; n++)
        {
            at += TimeSpan.FromMinutes(1);
            var callId = $"seed_{n}";
            var step = new AgentPlanStep(0, "create_database", AgentToolKind.SafeWrite, "{}", callId,
                $"Create {n}", $"ساخت {n}", null, null, [], [], null, false);
            h.Db.AgentMessages.Add(new AgentMessage
            {
                WorkspaceId = h.WorkspaceId, ConversationId = conversation.Id, Sequence = sequence++,
                Role = AgentMessageRole.User, Text = $"make {n}", CreatedAt = at
            });
            h.Db.AgentMessages.Add(new AgentMessage
            {
                WorkspaceId = h.WorkspaceId, ConversationId = conversation.Id, Sequence = sequence++,
                Role = AgentMessageRole.Assistant, ToolCallsJson = AgentJson.Serialize(new[] { new AgentToolCall(callId, "create_database", "{}") }),
                CreatedAt = at
            });
            h.Db.AgentMessages.Add(new AgentMessage
            {
                WorkspaceId = h.WorkspaceId, ConversationId = conversation.Id, Sequence = sequence++,
                Role = AgentMessageRole.Tool, ToolCallId = callId, ToolName = "create_database",
                SummaryEn = $"Proposed as step 1: Create {n}", SummaryFa = $"پیشنهاد: ساخت {n}", CreatedAt = at
            });
            h.Db.AgentPlans.Add(new AgentPlan
            {
                WorkspaceId = h.WorkspaceId, UserId = h.UserId, ConversationId = conversation.Id,
                Status = AgentPlanStatus.Rejected, StepsJson = AgentJson.Serialize(new[] { step }),
                ExpiresAt = at + AgentPlan.Lifetime, CreatedAt = at, UpdatedAt = at
            });
        }
        await h.Db.SaveChangesAsync();
        h.Clock.UtcNow = at + TimeSpan.FromMinutes(1);

        var view = await h.Service.GetAsync(h.Actor, conversation.Id, persian: false, default);

        view!.Messages.Should().HaveCount(Turns * 2, "the model's tool-calling reply has no words and is not shown");
        view.Plans.Should().HaveCount(AgentConversationService.PlanHistorySize);
        view.Plans!.Select(p => p.Plan.Steps.Single().TitleEn).Should().Equal(
            Enumerable.Range(Turns - AgentConversationService.PlanHistorySize, AgentConversationService.PlanHistorySize).Select(n => $"Create {n}"));
    }

    [Fact]
    public async Task Another_persons_plan_never_appears_and_never_lends_its_id_to_a_line()
    {
        using var h = Harness(out _);
        var (id, planId, _) = await ProposeAsync(h);
        (await h.Plans.RejectAsync(h.Actor, planId, default)).Should().BeNull();

        // A plan of somebody else's, in this conversation and in this workspace, made for the same call.
        var callId = (await h.Db.AgentMessages.AsNoTracking().SingleAsync(m => m.ToolName == "create_database" && m.PlanId == null && m.SummaryEn != null)).ToolCallId;
        var intruder = new AgentPlan
        {
            WorkspaceId = h.WorkspaceId, UserId = Guid.CreateVersion7(), ConversationId = id,
            Status = AgentPlanStatus.Executed, ExecutedAt = h.Clock.UtcNow, ResultsJson = "[]",
            StepsJson = AgentJson.Serialize(new[]
            {
                new AgentPlanStep(0, "create_database", AgentToolKind.SafeWrite, "{}", callId, "Intruder", "مزاحم", null, null, [], [], null, false)
            }),
            ExpiresAt = h.Clock.UtcNow + AgentPlan.Lifetime, CreatedAt = h.Clock.UtcNow + TimeSpan.FromSeconds(1)
        };
        // And one from another workspace entirely.
        var foreign = new AgentPlan
        {
            WorkspaceId = Guid.CreateVersion7(), UserId = h.UserId, ConversationId = id,
            Status = AgentPlanStatus.Executed, ExecutedAt = h.Clock.UtcNow, ResultsJson = "[]",
            StepsJson = intruder.StepsJson, ExpiresAt = intruder.ExpiresAt, CreatedAt = intruder.CreatedAt
        };
        h.Db.AgentPlans.AddRange(intruder, foreign);
        await h.Db.SaveChangesAsync();

        var view = await h.Service.GetAsync(h.Actor, id, persian: false, default);

        view!.Plans!.Select(p => p.Plan.Id).Should().Equal(planId);
        view.Messages.Single(m => m.Text!.StartsWith("Proposed as step 1")).PlanId.Should().Be(planId);

        using var colleague = h.Another();
        (await colleague.Service.GetAsync(colleague.Actor, id, persian: false, default)).Should().BeNull();
    }
}
