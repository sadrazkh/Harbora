using System.Text.Json;
using FluentAssertions;
using Harbora.Domain.Agent;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The generic plan engine, proven with test-double tools of every kind: approve, reject, expire and
/// execute — once, in order, re-validated, stopping at the first failure — and what it writes down
/// about each. The two-approvals race itself runs in the Postgres lane (EF InMemory cannot run the
/// conditional update); everything else is here.
/// </summary>
public class AgentPlanServiceTests
{
    private static AgentPlanStep Step(int index, IAgentTool tool, string args = "{}", string? phrase = null) => new(
        index, tool.Name, tool.Kind, args, $"call_{index}", $"Step {index}", $"مرحلهٔ {index}", null, null, [], [],
        tool.Kind == AgentToolKind.Destructive ? phrase ?? "shop-db" : null, false);

    private static async Task<(AgentHarness H, AgentPlan Plan)> PlanWith(params FakeTool[] tools)
    {
        var h = new AgentHarness(tools);
        var conversation = new AgentConversation { WorkspaceId = h.WorkspaceId, UserId = h.UserId, Title = "t" };
        h.Db.AgentConversations.Add(conversation);
        await h.Db.SaveChangesAsync();
        var plan = await h.Plans.CreatePendingAsync(h.Actor, conversation.Id,
            tools.Select((t, i) => Step(i, t, $$"""{"n":{{i}}}""")).ToList(), default);
        return (h, plan);
    }

    private static AgentApproveRequest Approve(Dictionary<int, string>? confirmations = null, params string[] remember) =>
        new(confirmations, remember);

    [Fact]
    public async Task Approving_runs_every_step_in_order_and_writes_it_down_three_times()
    {
        var order = new List<string>();
        FakeTool Tracked(string name) => new(name, AgentToolKind.SafeWrite)
        {
            OnExecute = (_, _) => { order.Add(name); return new AgentToolResult(true, $$"""{"made":"{{name}}"}""", $"{name} made", $"{name} ساخته شد"); }
        };
        var (h, plan) = await PlanWith(Tracked("first"), Tracked("second"), Tracked("third"));
        using var _ = h;

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Ok.Should().BeTrue();
        order.Should().Equal("first", "second", "third");
        outcome.Result!.Status.Should().Be("executed");
        outcome.Result.Steps.Select(s => (s.Index, s.State, s.SummaryEn)).Should().Equal(
            (0, "done", "first made"), (1, "done", "second made"), (2, "done", "third made"));

        // 1. the plan
        var stored = await h.Db.AgentPlans.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(AgentPlanStatus.Executed);
        stored.ExecutedAt.Should().NotBeNull();
        stored.DecidedAt.Should().Be(AgentHarness.Start);
        AgentJson.Deserialize<List<AgentPlanStepResultView>>(stored.ResultsJson)!.Should().HaveCount(3);

        // 2. the conversation: one assistant message calling each step's tool, one answer per step
        var messages = await h.Db.AgentMessages.OrderBy(m => m.Sequence).ToListAsync();
        messages.Select(m => m.Role).Should().Equal(
            AgentMessageRole.Assistant, AgentMessageRole.Tool, AgentMessageRole.Tool, AgentMessageRole.Tool);
        var calls = AgentJson.Deserialize<List<AgentToolCall>>(messages[0].ToolCallsJson)!;
        calls.Select(c => c.Id).Should().Equal(messages.Skip(1).Select(m => m.ToolCallId));
        messages[1].Text.Should().Contain("\"state\":\"done\"").And.Contain("\"made\":\"first\"");
        AgentConversationService.Sanitize(messages).Should().HaveCount(4, "the record is a well-formed tool exchange");

        // 3. the audit trail, one entry per step that ran
        var audit = h.Audit.Entries.Where(e => e.Action == AgentPlanService.StepExecutedAction).ToList();
        audit.Should().HaveCount(3);
        audit.Should().OnlyContain(e => e.WorkspaceId == h.WorkspaceId && e.TargetId == plan.Id.ToString());
        using var meta = JsonDocument.Parse(audit[1].MetadataJson!);
        meta.RootElement.GetProperty("conversationId").GetGuid().Should().Be(plan.ConversationId);
        meta.RootElement.GetProperty("planId").GetGuid().Should().Be(plan.Id);
        meta.RootElement.GetProperty("step").GetInt32().Should().Be(2, "the ordinal a person reads");
        meta.RootElement.GetProperty("stepIndex").GetInt32().Should().Be(1, "the position the card keys by");
        meta.RootElement.GetProperty("tool").GetString().Should().Be("second");
        meta.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_second_approval_is_refused_and_nothing_runs_twice()
    {
        var tool = new FakeTool("create", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(tool);
        using var _ = h;

        (await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default)).Ok.Should().BeTrue();
        var second = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        second.Ok.Should().BeFalse();
        second.Refusal!.Code.Should().Be(AgentPlanRefusal.NotPendingCode);
        second.Refusal.Status.Should().Be("executed");
        tool.Executions.Should().Be(1);
    }

    [Fact]
    public async Task A_typed_name_must_match_exactly_and_a_mismatch_leaves_the_plan_pending()
    {
        var delete = new FakeTool("delete_database", AgentToolKind.Destructive);
        var (h, plan) = await PlanWith(delete);
        using var _ = h;

        foreach (var typed in new[] { "Shop-DB", "shop", "", "shop-db!" })
        {
            var refused = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(new() { [0] = typed }), default);
            refused.Refusal!.Code.Should().Be(AgentPlanRefusal.ConfirmationMismatchCode, $"'{typed}' is not the name");
            refused.Refusal.StepIndex.Should().Be(0);
            refused.Refusal.MessageEn.Should().StartWith("Step 1: type \"shop-db\"");
        }
        (await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default)).Refusal!.Code
            .Should().Be(AgentPlanRefusal.ConfirmationMismatchCode, "a destructive step without any typed name does not run");

        delete.Executions.Should().Be(0);
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync()).Status.Should().Be(AgentPlanStatus.Pending);

        (await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(new() { [1] = "shop-db" }), default)).Refusal!.Code
            .Should().Be(AgentPlanRefusal.ConfirmationMismatchCode, "confirmations are keyed by the step's 0-based Index");

        var accepted = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(new() { [0] = "  shop-db \n" }), default);
        accepted.Ok.Should().BeTrue("surrounding whitespace is trimmed; the name itself is compared exactly");
        delete.Executions.Should().Be(1);
    }

    [Fact]
    public async Task A_plan_past_its_fifteen_minutes_cannot_be_approved_and_is_written_down_as_expired()
    {
        var tool = new FakeTool("create", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(tool);
        using var _ = h;

        h.Clock.UtcNow = AgentHarness.Start + AgentPlan.Lifetime - TimeSpan.FromSeconds(1);
        h.Plans.ViewOf(plan).Status.Should().Be("pending");

        h.Clock.UtcNow = AgentHarness.Start + AgentPlan.Lifetime;
        h.Plans.ViewOf(plan).Status.Should().Be("expired", "expiry is computed when read, with no sweeper");

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);
        outcome.Refusal!.Code.Should().Be(AgentPlanRefusal.ExpiredCode);
        tool.Executions.Should().Be(0);
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync()).Status.Should().Be(AgentPlanStatus.Expired);
        (await h.Plans.PendingPlanAsync(h.Actor, plan.ConversationId, default)).Should().BeNull();
    }

    [Fact]
    public async Task If_any_step_is_refused_when_rechecked_nothing_runs_at_all()
    {
        var first = new FakeTool("first", AgentToolKind.SafeWrite);
        var second = new FakeTool("second", AgentToolKind.SafeWrite);
        var third = new FakeTool("third", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(first, second, third);
        using var _ = h;

        // The world changed while the card was open.
        second.OnValidate = (_, _, _) => AgentValidation.Refused("The quota is full now.", "سهمیه اکنون پر است.");

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Ok.Should().BeFalse("nothing ran, and the person is told why");
        outcome.Refusal!.Code.Should().Be(AgentPlanRefusal.RevalidationRefusedCode);
        outcome.Refusal.StepIndex.Should().Be(1);
        outcome.Refusal.Status.Should().Be("failed");
        outcome.Refusal.MessageEn.Should().Be("Nothing ran: step 2 was refused when the plan was checked again. The quota is full now.");
        outcome.Refusal.MessageFa.Should().Contain("سهمیه اکنون پر است.");
        (first.Executions + second.Executions + third.Executions).Should().Be(0);
        h.Audit.Entries.Should().NotContain(e => e.Action == AgentPlanService.StepExecutedAction);

        var stored = await h.Db.AgentPlans.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(AgentPlanStatus.Failed, "the plan cannot be approved again into a world that refused it");
        stored.ExecutedAt.Should().BeNull("nothing executed");
        AgentJson.Deserialize<List<AgentPlanStepResultView>>(stored.ResultsJson)!.Select(s => s.State)
            .Should().Equal("skipped", "failed", "skipped");
    }

    [Fact]
    public async Task Re_validation_uses_a_fresh_scope_so_later_steps_can_still_name_what_earlier_ones_create()
    {
        var create = new FakeTool("create", AgentToolKind.SafeWrite)
        {
            OnValidate = (_, _, _) => new AgentValidation(true, Creates: new AgentPlannedResource(AgentResourceKinds.Database, "shop-db"))
        };
        var attach = new FakeTool("attach", AgentToolKind.SafeWrite)
        {
            OnValidate = (_, _, scope) => scope.Plans(AgentResourceKinds.Database, "shop-db")
                ? new AgentValidation(true) : AgentValidation.Refused("No such database.", "نیست.")
        };
        var (h, plan) = await PlanWith(create, attach);
        using var _ = h;

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Result!.Status.Should().Be("executed");
        attach.Executions.Should().Be(1);
    }

    [Fact]
    public async Task The_first_failure_stops_the_rest()
    {
        var first = new FakeTool("first", AgentToolKind.SafeWrite);
        var second = new FakeTool("second", AgentToolKind.SafeWrite)
        {
            OnExecute = (_, _) => AgentToolResult.Failed("The node is unreachable.", "نود در دسترس نیست.")
        };
        var third = new FakeTool("third", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(first, second, third);
        using var _ = h;

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Result!.Status.Should().Be("failed");
        outcome.Result.Steps.Select(s => s.State).Should().Equal("done", "failed", "skipped");
        outcome.Result.Steps[1].ErrorEn.Should().Be("The node is unreachable.");
        third.Executions.Should().Be(0);
        h.Audit.Entries.Where(e => e.Action == AgentPlanService.StepExecutedAction)
            .Select(e => JsonDocument.Parse(e.MetadataJson!).RootElement.GetProperty("ok").GetBoolean())
            .Should().Equal(true, false);

        var answers = await h.Db.AgentMessages.Where(m => m.Role == AgentMessageRole.Tool).OrderBy(m => m.Sequence).ToListAsync();
        answers.Select(m => m.SummaryEn).Should().Equal("first done", "The node is unreachable.", "Skipped: an earlier step failed, so this did not run.");
    }

    [Fact]
    public async Task A_step_that_throws_fails_without_leaking_the_exception_and_stops_the_rest()
    {
        var first = new FakeTool("first", AgentToolKind.SafeWrite) { OnExecute = (_, _) => throw new InvalidOperationException("db password is hunter2") };
        var second = new FakeTool("second", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(first, second);
        using var _ = h;

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Result!.Steps.Select(s => s.State).Should().Equal("failed", "skipped");
        outcome.Result.Steps[0].ErrorEn.Should().NotContain("hunter2");
        (await h.Db.AgentMessages.AnyAsync(m => m.Text != null && m.Text.Contains("hunter2"))).Should().BeFalse();
    }

    [Fact]
    public async Task Another_persons_plan_and_another_workspaces_plan_are_not_found_and_do_not_run()
    {
        var tool = new FakeTool("create", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(tool);
        using var _ = h;
        using var colleague = h.Another();
        using var stranger = h.Another(sameWorkspace: false);

        (await colleague.Plans.ApproveAsync(colleague.Actor, plan.Id, Approve(), default)).Refusal!.Code.Should().Be(AgentPlanRefusal.NotFoundCode);
        (await stranger.Plans.ApproveAsync(stranger.Actor, plan.Id, Approve(), default)).Refusal!.Code.Should().Be(AgentPlanRefusal.NotFoundCode);
        (await colleague.Plans.RejectAsync(colleague.Actor, plan.Id, default))!.Code.Should().Be(AgentPlanRefusal.NotFoundCode);

        tool.Executions.Should().Be(0);
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync()).Status.Should().Be(AgentPlanStatus.Pending);
    }

    [Fact]
    public async Task Rejecting_is_audited_and_final_and_a_second_rejection_changes_nothing()
    {
        var tool = new FakeTool("create", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(tool);
        using var _ = h;

        (await h.Plans.RejectAsync(h.Actor, plan.Id, default)).Should().BeNull();
        (await h.Plans.RejectAsync(h.Actor, plan.Id, default)).Should().BeNull();
        (await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default)).Refusal!.Code.Should().Be(AgentPlanRefusal.NotPendingCode);

        tool.Executions.Should().Be(0);
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync()).Status.Should().Be(AgentPlanStatus.Rejected);
        h.Audit.Entries.Should().ContainSingle(e => e.Action == AgentPlanService.PlanRejectedAction && e.TargetId == plan.Id.ToString());
        (await h.Db.AgentMessages.Where(m => m.Role == AgentMessageRole.Tool).SingleAsync()).Text.Should().Contain("\"state\":\"rejected\"");
    }

    [Fact]
    public async Task Only_safe_tools_used_by_the_plan_are_ever_remembered_whatever_the_request_says()
    {
        var safe = new FakeTool("set_env", AgentToolKind.SafeWrite);
        var dangerous = new FakeTool("stop_app", AgentToolKind.DangerousWrite);
        var notInPlan = new FakeTool("add_domain", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(safe, dangerous);
        using var _ = h;
        h.Tools.Add(notInPlan);

        await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(null, "set_env", "stop_app", "add_domain", "no_such_tool", "set_env"), default);

        (await h.Plans.RememberedToolsAsync(h.Actor, default)).Should().BeEquivalentTo(new[] { "set_env" });
        (await h.Plans.ApprovalsAsync(h.Actor, default)).Select(a => a.Tool).Should().Equal("set_env");

        await h.Plans.RevokeApprovalAsync(h.Actor, "set_env", default);
        await h.Plans.RevokeApprovalAsync(h.Actor, "set_env", default);
        (await h.Plans.ApprovalsAsync(h.Actor, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_approval_remembers_nothing()
    {
        var safe = new FakeTool("set_env", AgentToolKind.SafeWrite);
        var delete = new FakeTool("delete_app", AgentToolKind.Destructive);
        var (h, plan) = await PlanWith(safe, delete);
        using var _ = h;

        await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(new() { [1] = "wrong" }, "set_env"), default);

        (await h.Plans.ApprovalsAsync(h.Actor, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tool_whose_consent_level_changed_since_the_card_was_drawn_is_refused()
    {
        var tool = new FakeTool("wipe", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(tool);
        using var _ = h;
        tool.Kind = AgentToolKind.Destructive; // a later release made it a delete; nobody typed a name

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Refusal!.Code.Should().Be(AgentPlanRefusal.RevalidationRefusedCode);
        outcome.Refusal.MessageEn.Should().Contain("changed since this plan was made");
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task A_destructive_step_whose_resource_now_has_a_different_name_is_refused()
    {
        var delete = new FakeTool("delete_database", AgentToolKind.Destructive);
        var (h, plan) = await PlanWith(delete);
        using var _ = h;
        delete.OnValidate = (_, _, _) => new AgentValidation(true, ConfirmationPhrase: "billing-db");

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(new() { [0] = "shop-db" }), default);

        outcome.Refusal!.Code.Should().Be(AgentPlanRefusal.RevalidationRefusedCode);
        outcome.Refusal.StepIndex.Should().Be(0);
        delete.Executions.Should().Be(0);
    }

    [Fact]
    public async Task A_person_whose_role_no_longer_allows_a_step_cannot_run_it()
    {
        var tool = new FakeTool("create_app", AgentToolKind.SafeWrite, Capabilities.AppsCreate);
        using var h = new AgentHarness([tool]);
        var conversation = new AgentConversation { WorkspaceId = h.WorkspaceId, UserId = h.UserId, Title = "t" };
        h.Db.AgentConversations.Add(conversation);
        await h.Db.SaveChangesAsync();
        var plan = await h.Plans.CreatePendingAsync(h.Actor, conversation.Id, [Step(0, tool)], default);

        // The same person, now signed in as a viewer.
        var viewer = new AgentHarness([tool], role: SystemRole.Viewer, databaseName: h.DatabaseName, workspaceId: h.WorkspaceId, clock: h.Clock);
        using var __ = viewer;
        var demoted = h.Actor with { User = viewer.Principal };

        var outcome = await h.Plans.ApproveAsync(demoted, plan.Id, Approve(), default);

        outcome.Refusal!.MessageEn.Should().EndWith("Your role no longer allows this step.");
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task The_plan_view_tells_the_card_everything_it_draws()
    {
        var safe = new FakeTool("set_env", AgentToolKind.SafeWrite);
        var dangerous = new FakeTool("stop_app", AgentToolKind.DangerousWrite);
        var delete = new FakeTool("delete_app", AgentToolKind.Destructive);
        var (h, plan) = await PlanWith(safe, dangerous, delete);
        using var _ = h;

        var view = h.Plans.ViewOf(plan);

        view.Status.Should().Be("pending");
        view.Steps.Select(s => (s.Kind, s.Rememberable, s.ConfirmationPhrase)).Should().Equal(
            ("safe", true, null), ("dangerous", false, null), ("destructive", false, "shop-db"));
    }

    // ---- every claimed plan ends executed or failed ---------------------------------------------

    private static async Task AssertFinishedAsFailed(AgentHarness h, AgentPlan plan, params string[] states)
    {
        var stored = await h.Db.AgentPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id);
        stored.Status.Should().Be(AgentPlanStatus.Failed, "a claimed plan never stays executing");
        AgentJson.Deserialize<List<AgentPlanStepResultView>>(stored.ResultsJson)!.Select(s => s.State).Should().Equal(states);

        var answers = await h.Db.AgentMessages.AsNoTracking()
            .Where(m => m.PlanId == plan.Id && m.Role == AgentMessageRole.Tool).OrderBy(m => m.Sequence).ToListAsync();
        answers.Select(m => JsonDocument.Parse(m.Text!).RootElement.GetProperty("state").GetString())
            .Should().Equal(states, "the next turn must hear what really happened");
        h.Db.ChangeTracker.Entries().Should().OnlyContain(e => e.State == EntityState.Unchanged, "nothing half-written is left to be saved");
    }

    [Fact]
    public async Task A_step_whose_tool_times_out_fails_the_plan_with_every_result_recorded()
    {
        var first = new FakeTool("first", AgentToolKind.SafeWrite);
        var second = new FakeTool("second", AgentToolKind.SafeWrite)
        {
            // What HttpClient.Timeout throws — a cancellation that is not the caller's.
            OnExecute = (_, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")
        };
        var third = new FakeTool("third", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(first, second, third);
        using var _ = h;

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Result!.Status.Should().Be("failed");
        outcome.Result.Steps.Select(s => s.State).Should().Equal("done", "failed", "skipped");
        outcome.Result.Steps[1].ErrorEn.Should().Be("The step failed unexpectedly; see the server log.");
        third.Executions.Should().Be(0);
        h.Audit.Entries.Where(e => e.Action == AgentPlanService.StepExecutedAction)
            .Select(e => JsonDocument.Parse(e.MetadataJson!).RootElement.GetProperty("ok").GetBoolean())
            .Should().Equal(true, false);
        await AssertFinishedAsFailed(h, plan, "done", "failed", "skipped");
    }

    [Fact]
    public async Task A_step_whose_save_throws_leaves_nothing_half_written_and_the_plan_still_finishes()
    {
        Exception? thrown = null;
        AgentHarness? harness = null;
        var create = new FakeTool("create_project", AgentToolKind.SafeWrite)
        {
            // The shape of a unique-slug race: the tool's own save throws, and its entity stays Added in
            // the context it shares with the agent.
            OnExecuteAsync = async (_, context) =>
            {
                harness!.Db.Projects.Add(new Harbora.Domain.Projects.Project { WorkspaceId = context.WorkspaceId, Name = null!, Slug = "half-made" });
                try { await harness!.Db.SaveChangesAsync(); }
                catch (Exception e) { thrown = e; throw; }
                return new AgentToolResult(true, "{}");
            }
        };
        var after = new FakeTool("after", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(create, after);
        harness = h;
        using var _ = h;

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        thrown.Should().BeOfType<DbUpdateException>("this is the failure the guard exists for");
        outcome.Result!.Steps.Select(s => s.State).Should().Equal("failed", "skipped");
        h.Audit.Entries.Should().ContainSingle(e => e.Action == AgentPlanService.StepExecutedAction);
        await AssertFinishedAsFailed(h, plan, "failed", "skipped");
        (await h.Db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Slug == "half-made")).Should().BeFalse();
    }

    [Fact]
    public async Task A_step_that_leaves_the_tracker_inconsistent_still_ends_in_a_failed_audited_plan()
    {
        AgentHarness? harness = null;
        var poisoner = new FakeTool("poison", AgentToolKind.SafeWrite)
        {
            // A navigation pointed at a second instance of an already-tracked key: from here on every
            // DetectChanges — ChangeTracker.Entries() included — throws.
            OnExecute = (_, _) =>
            {
                var environment = harness!.Db.Environments.Local.Single(e => e.Id == harness.EnvironmentId);
                environment.Project = new Harbora.Domain.Projects.Project
                {
                    Id = harness.ProjectId, WorkspaceId = harness.WorkspaceId, Name = "Twin", Slug = "twin"
                };
                throw new InvalidOperationException("the tool broke after confusing the tracker");
            }
        };
        var after = new FakeTool("after", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(poisoner, after);
        harness = h;
        using var _ = h;

        var act = () => h.Db.ChangeTracker.Entries().ToList();
        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Result!.Steps.Select(s => s.State).Should().Equal("failed", "skipped");
        h.Audit.Entries.Where(e => e.Action == AgentPlanService.StepExecutedAction)
            .Select(e => JsonDocument.Parse(e.MetadataJson!).RootElement.GetProperty("ok").GetBoolean())
            .Should().Equal([false], "the step that broke the run is still audited");
        await AssertFinishedAsFailed(h, plan, "failed", "skipped");
        act.Should().NotThrow("the fallback clears the tracker instead of enumerating it");
    }

    [Fact]
    public async Task A_failing_step_drops_only_what_it_left_not_what_was_already_pending()
    {
        AgentHarness? harness = null;
        var first = new FakeTool("first", AgentToolKind.SafeWrite)
        {
            // Leaves a change for the agent's next save to write, the way an audit row left Added by an
            // earlier swallowed save would be.
            OnExecute = (_, context) =>
            {
                harness!.Db.Projects.Add(new Harbora.Domain.Projects.Project { WorkspaceId = context.WorkspaceId, Name = "One", Slug = "left-by-first" });
                return new AgentToolResult(true, "{}", "first done", "اول انجام شد");
            }
        };
        var second = new FakeTool("second", AgentToolKind.SafeWrite)
        {
            OnExecute = (_, context) =>
            {
                harness!.Db.Projects.Add(new Harbora.Domain.Projects.Project { WorkspaceId = context.WorkspaceId, Name = "Two", Slug = "left-by-second" });
                return AgentToolResult.Failed("No.", "نه.");
            }
        };
        var (h, plan) = await PlanWith(first, second);
        harness = h;
        using var _ = h;

        await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        (await h.Db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Slug == "left-by-first")).Should().BeTrue(
            "it was pending before the failing step ran, so it was not that step's to drop");
        (await h.Db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Slug == "left-by-second")).Should().BeFalse();
    }

    [Fact]
    public async Task A_recheck_refusal_is_still_the_answer_when_its_first_save_breaks()
    {
        var tool = new FakeTool("create", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(tool);
        using var _ = h;
        tool.OnValidate = (_, context, _) =>
        {
            // An agent row the change guard does not drop, and that the first save cannot write.
            h.Db.AgentConversations.Add(new AgentConversation { WorkspaceId = context.WorkspaceId, UserId = context.UserId, Title = null! });
            return AgentValidation.Refused("Gone.", "رفته.");
        };

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Ok.Should().BeFalse();
        outcome.Refusal!.Code.Should().Be(AgentPlanRefusal.RevalidationRefusedCode, "a 409, not a 200 with a failed result");
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id)).Status.Should().Be(AgentPlanStatus.Failed);
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task An_auto_run_whose_tool_times_out_reports_the_failure_instead_of_saying_nothing_ran()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite)
        {
            OnExecute = (_, _) => throw new OperationCanceledException("the tool's own deadline")
        };
        using var h = new AgentHarness([create]);
        h.Remember("create_database");
        h.Chat.ThenCall("create_database").ThenText("Creating it.");

        var (_, events) = await h.SendAsync("create a database");

        events.Names().Should().Equal("started", "message", "executed", "done");
        var result = events.Single<AgentPlanResultView>("executed");
        result.Status.Should().Be("failed");
        result.Steps.Single().State.Should().Be("failed");
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync()).Status.Should().Be(AgentPlanStatus.Failed);
    }

    [Fact]
    public async Task A_validation_that_changes_data_is_refused_at_the_recheck_and_its_change_is_never_saved()
    {
        var tool = new FakeTool("create_project", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(tool);
        using var _ = h;
        tool.OnValidate = (_, context, _) =>
        {
            h.Db.Projects.Add(new Harbora.Domain.Projects.Project { WorkspaceId = context.WorkspaceId, Name = "Leak", Slug = "leaked" });
            return new AgentValidation(true);
        };

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default);

        outcome.Refusal!.Code.Should().Be(AgentPlanRefusal.RevalidationRefusedCode);
        outcome.Refusal.MessageEn.Should().Contain("checking it tried to change data");
        tool.Executions.Should().Be(0);
        (await h.Db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Slug == "leaked")).Should().BeFalse();
    }

    [Fact]
    public async Task A_plan_found_expired_at_approval_is_told_to_the_conversation()
    {
        var tool = new FakeTool("create", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(tool);
        using var _ = h;
        h.Clock.UtcNow = AgentHarness.Start + AgentPlan.Lifetime + TimeSpan.FromMinutes(1);

        (await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(), default)).Refusal!.Code.Should().Be(AgentPlanRefusal.ExpiredCode);

        (await h.Db.AgentMessages.Where(m => m.PlanId == plan.Id && m.Role == AgentMessageRole.Tool).SingleAsync())
            .Text.Should().Contain("\"state\":\"expired\"");
    }

    [Fact]
    public async Task A_plan_refused_at_the_recheck_remembers_nothing_because_the_approval_did_not_go_ahead()
    {
        var safe = new FakeTool("set_env", AgentToolKind.SafeWrite);
        var (h, plan) = await PlanWith(safe);
        using var _ = h;
        safe.OnValidate = (_, _, _) => AgentValidation.Refused("Gone.", "رفته.");

        var outcome = await h.Plans.ApproveAsync(h.Actor, plan.Id, Approve(null, "set_env"), default);

        outcome.Refusal!.Code.Should().Be(AgentPlanRefusal.RevalidationRefusedCode);
        (await h.Plans.ApprovalsAsync(h.Actor, default)).Should().BeEmpty();
    }
}
