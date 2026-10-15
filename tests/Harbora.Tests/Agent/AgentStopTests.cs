using System.Text.Json;
using FluentAssertions;
using Harbora.Domain.Agent;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// Stop (round seven): the person ends the answer being written. It is its own ending — not the deadline's,
/// not a shutdown's — with what was written kept, a <c>stopped</c> event, and <c>done</c> as always. A plan
/// already claimed is never interrupted. Every Stop here comes from a second service over a second context,
/// as the pane's <c>POST …/stop</c> is a request of its own; the two meet in the harness's one registry, as
/// two requests meet in the container's singleton.
/// </summary>
public class AgentStopTests
{
    /// <summary>A deadline short enough that a Stop which never lands fails the test instead of hanging it.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static Task<AgentStopView?> StopFromAnotherRequest(AgentHarness h, Guid conversationId) =>
        h.ServiceOver(h.NewContext()).StopAsync(h.Actor, conversationId, default);

    private static Guid ConversationOf(IEnumerable<AgentStreamEvent> events) => events.Single<AgentStartedData>("started").ConversationId;

    /// <summary>
    /// What every chat-completions provider insists on: each assistant message's calls are answered, each by
    /// exactly one tool message right behind it, and no tool message answers a call that is not in front of it.
    /// </summary>
    private static void ShouldBeAValidRequest(IReadOnlyList<AgentChatMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role == AgentRole.Tool)
            {
                var owner = messages.Take(i).LastOrDefault(m => m.Role != AgentRole.Tool);
                owner.Should().NotBeNull();
                owner!.Role.Should().Be(AgentRole.Assistant, "a tool answer only ever follows the call it answers");
                owner.ToolCalls!.Select(c => c.Id).Should().Contain(message.ToolCallId);
            }

            if (message.Role == AgentRole.Assistant && message.ToolCalls is { Count: > 0 } calls)
            {
                var answers = messages.Skip(i + 1).TakeWhile(m => m.Role == AgentRole.Tool).Select(m => m.ToolCallId).ToList();
                answers.Should().BeEquivalentTo(calls.Select(c => c.Id), "every call is answered, once");
            }
        }
    }

    [Fact]
    public async Task Stop_in_the_middle_of_an_answer_keeps_what_was_written_and_ends_with_message_stopped_and_done()
    {
        using var h = new AgentHarness();
        h.Chat.ThenStreamed(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "never all of it"), "m", null, "stop", null, null),
            ["Writing the ", "first half"], after: ct => Task.Delay(Timeout.Infinite, ct));
        AgentStopView? stop = null;
        var events = new List<AgentStreamEvent>();

        async Task Sink(AgentStreamEvent e, CancellationToken _)
        {
            events.Add(e);
            if (e.Name == AgentStreamEvents.Delta && events.Count(x => x.Name == AgentStreamEvents.Delta) == 2)
                stop = await StopFromAnotherRequest(h, ConversationOf(events));
        }

        var outcome = await h.ServiceOver(turnDeadline: Guard)
            .SendAsync(h.Actor, null, new AgentSendMessageRequest("tell me everything", null), false, Sink, default);

        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        stop!.Stopped.Should().BeTrue();
        events.Names().Should().Equal("started", "delta", "delta", "message", "stopped", "done");
        events.Single<AgentMessageView>("message").Text.Should().Be("Writing the first half");
        var stopped = events.Single<AgentErrorData>("stopped");
        stopped.MessageEn.Should().Be("You stopped this answer.");
        stopped.MessageFa.Should().Be("این پاسخ را متوقف کردید.");

        var id = ConversationOf(events);
        using (var other = h.NewContext())
        {
            (await other.AgentMessages.CountAsync(m => m.ConversationId == id && m.Role == AgentMessageRole.Assistant
                                                       && m.Text == "Writing the first half"))
                .Should().Be(1, "what the person watched being written is what a reload shows");
            (await other.AgentConversations.SingleAsync(c => c.Id == id)).TurnStartedAt.Should().BeNull("the mark is cleared");
            (await other.AgentUsageDays.SingleAsync()).ModelCalls.Should().Be(1, "the call that was stopped was still made");
        }

        (await StopFromAnotherRequest(h, id))!.Stopped.Should().BeFalse("the answer has ended; there is nothing left to stop");

        // The next turn's request carries the stopped answer as an ordinary one, and is valid.
        h.Chat.ThenText("Here is the rest.");
        var (next, nextEvents) = await h.SendAsync("go on", id);
        next.Status.Should().Be(AgentSendStatus.Streamed);
        nextEvents.Names().Should().NotContain("stopped", "a Stop belongs to the turn it stopped, not to the next one");
        var request = h.Chat.Requests.Last().Messages;
        request.Where(m => m.Role != AgentRole.System).Select(m => (m.Role, m.Content)).Should().Equal(
            (AgentRole.User, "tell me everything"), (AgentRole.Assistant, "Writing the first half"), (AgentRole.User, "go on"));
        ShouldBeAValidRequest(request);
    }

    [Fact]
    public async Task Stop_after_nothing_but_whitespace_stores_nothing_and_sends_no_message_with_an_unstored_id()
    {
        using var h = new AgentHarness();
        h.Chat.ThenStreamed(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "never"), "m", null, "stop", null, null),
            ["\n", "  "], after: ct => Task.Delay(Timeout.Infinite, ct));
        var events = new List<AgentStreamEvent>();

        async Task Sink(AgentStreamEvent e, CancellationToken _)
        {
            events.Add(e);
            if (e.Name == AgentStreamEvents.Delta && events.Count(x => x.Name == AgentStreamEvents.Delta) == 2)
                await StopFromAnotherRequest(h, ConversationOf(events));
        }

        await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("hm?", null), false, Sink, default);

        events.Names().Should().Equal(["started", "delta", "delta", "stopped", "done"],
            "a message would carry an id nothing stores; `stopped` closes the bubble");
        using var other = h.NewContext();
        (await other.AgentMessages.CountAsync(m => m.Role == AgentMessageRole.Assistant))
            .Should().Be(0, "whitespace was never anything to read, so no row is written for it");
    }

    [Fact]
    public async Task Stop_while_a_read_runs_leaves_no_half_written_answer_and_the_next_request_is_valid()
    {
        Guid conversationId = default;
        AgentStopView? stop = null;
        var peek = new FakeTool("peek", AgentToolKind.Read)
        {
            OnExecute = (_, _) => new AgentToolResult(true, """{"answer":42}""", "peek looked", "peek دید")
        };
        using var h = new AgentHarness([peek]);
        h.Tools.Add(new WaitingRead("slow_read", async () => stop = await StopFromAnotherRequest(h, conversationId)));
        var slowCall = h.Chat.Call("slow_read");
        h.Chat.ThenCalls(h.Chat.Call("peek"), slowCall);
        var events = new List<AgentStreamEvent>();

        var outcome = await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("look at both", null), false,
            (e, _) =>
            {
                events.Add(e);
                if (e.Name == AgentStreamEvents.Started) conversationId = ((AgentStartedData)e.Data).ConversationId;
                return Task.CompletedTask;
            }, default);

        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        stop!.Stopped.Should().BeTrue();
        events.Names().Should().Equal("started", "tool", "tool", "tool", "stopped", "done");
        events.Names().Should().NotContain("error", "a stop is not a failure");

        using (var other = h.NewContext())
        {
            var rows = await other.AgentMessages.Where(m => m.ConversationId == conversationId).OrderBy(m => m.Sequence).ToListAsync();
            rows.Select(r => r.Role).Should().Equal(AgentMessageRole.User, AgentMessageRole.Assistant, AgentMessageRole.Tool);
            rows.Should().NotContain(r => r.ToolCallId == slowCall.Id, "the read that was cut off wrote no answer at all");
        }

        // The cut exchange — a call with no answer — is left out of the next request rather than sent broken.
        h.Chat.ThenText("Fine.");
        await h.SendAsync("and now?", conversationId);
        var request = h.Chat.Requests.Last().Messages;
        ShouldBeAValidRequest(request);
        request.Where(m => m.Role != AgentRole.System).Select(m => (m.Role, m.Content))
            .Should().Equal((AgentRole.User, "look at both"), (AgentRole.User, "and now?"));
    }

    /// <summary>A read that tells the test it started and then runs until it is cancelled.</summary>
    private sealed class WaitingRead(string name, Func<Task> started) : IAgentTool
    {
        public string Name => name;
        public string Description => "Takes its time.";
        public string ParametersSchema => """{ "type": "object" }""";
        public AgentToolKind Kind => AgentToolKind.Read;
        public string? Capability => null;

        public Task<AgentValidation> ValidateAsync(JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct) =>
            Task.FromResult(new AgentValidation(true));

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
        {
            await started();
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentToolResult(true, "{}", "never", "هرگز");
        }
    }

    [Fact]
    public async Task Stop_pressed_the_moment_the_stream_opens_still_ends_with_stopped_and_done_and_keeps_the_message()
    {
        using var h = new AgentHarness();
        // If the call is made before the stop lands, it waits for the stop; either way nothing is answered.
        h.Chat.ThenText("never", before: ct => Task.Delay(Timeout.Infinite, ct));
        AgentStopView? stop = null;
        var events = new List<AgentStreamEvent>();

        async Task Sink(AgentStreamEvent e, CancellationToken _)
        {
            events.Add(e);
            if (e.Name == AgentStreamEvents.Started) stop = await StopFromAnotherRequest(h, ConversationOf(events));
        }

        await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("hello?", null), false, Sink, default);

        stop!.Stopped.Should().BeTrue();
        events.Names().Should().Equal("started", "stopped", "done");
        using var other = h.NewContext();
        (await other.AgentMessages.SingleAsync()).Text.Should().Be("hello?", "the person's words are kept whatever happens next");
        (await other.AgentConversations.SingleAsync()).TurnStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task Stop_with_nothing_running_is_false_and_somebody_elses_conversation_is_not_found()
    {
        using var h = new AgentHarness();
        h.Chat.ThenText("Done.");
        var (_, events) = await h.SendAsync("hi");
        var id = ConversationOf(events);
        using var colleague = h.Another();

        (await h.Service.StopAsync(h.Actor, id, default))!.Stopped.Should().BeFalse();
        (await colleague.Service.StopAsync(colleague.Actor, id, default)).Should().BeNull();
        (await h.Service.StopAsync(new AgentActor(Guid.CreateVersion7(), h.UserId, h.Principal), id, default)).Should().BeNull();
        (await h.Service.StopAsync(h.Actor, Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task Stop_during_an_auto_approved_plan_is_false_and_the_plan_finishes_with_its_result_saved()
    {
        var tag = new FakeTool("tag_app", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([tag]);
        h.Remember("tag_app");
        h.Chat.ThenCall("tag_app", """{"name":"shop"}""").ThenText("Tagging it now.");
        Guid conversationId = default;
        AgentStopView? stop = null;
        tag.OnExecuteAsync = async (_, _) =>
        {
            // Pressed while the step runs: the turn released itself before its plan was stored and claimed,
            // so there is nothing left that a Stop could stop.
            stop = await StopFromAnotherRequest(h, conversationId);
            await Task.Delay(100);
            return new AgentToolResult(true, """{"tagged":"shop"}""", "Tagged shop", "shop برچسب خورد");
        };
        var events = new List<AgentStreamEvent>();

        await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("tag the shop", null), false,
            (e, _) =>
            {
                events.Add(e);
                if (e.Name == AgentStreamEvents.Started) conversationId = ((AgentStartedData)e.Data).ConversationId;
                return Task.CompletedTask;
            }, default);

        stop!.Stopped.Should().BeFalse("nothing stopped: the plan was already running, and runs to its end");
        events.Names().Should().Equal(["started", "message", "executed", "done"], "no \"You stopped this answer\" under a plan that ran");
        var result = events.Single<AgentPlanResultView>("executed");
        result.Status.Should().Be("executed");
        result.Steps.Single().State.Should().Be("done");
        tag.Executions.Should().Be(1);

        using var other = h.NewContext();
        var plan = await other.AgentPlans.SingleAsync();
        plan.Status.Should().Be(AgentPlanStatus.Executed, "a claimed plan runs to its end whatever the person presses");
        plan.ResultsJson.Should().Contain("Tagged shop");
        (await other.AgentMessages.CountAsync(m => m.PlanId == plan.Id && m.Role == AgentMessageRole.Tool))
            .Should().Be(1, "the conversation is told what the plan did");
        (await other.AgentConversations.SingleAsync()).TurnStartedAt.Should().BeNull();
        h.Audit.Entries.Should().ContainSingle(e => e.Action == AgentPlanService.StepExecutedAction);
    }

    // ---- fixes after review --------------------------------------------------------------------

    [Fact]
    public async Task Stop_pressed_over_a_complete_answer_is_false_and_nothing_says_stopped()
    {
        using var h = new AgentHarness();
        h.Chat.ThenStreamedText("All ", "of it.");
        AgentStopView? stop = null;
        var events = new List<AgentStreamEvent>();

        async Task Sink(AgentStreamEvent e, CancellationToken _)
        {
            events.Add(e);
            // The answer is complete and shown: the person presses Stop before `done` arrives.
            if (e.Name == AgentStreamEvents.Message) stop = await StopFromAnotherRequest(h, ConversationOf(events));
        }

        await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("everything", null), false, Sink, default);

        stop!.Stopped.Should().BeFalse("the model had already given its last word; there was nothing to stop");
        events.Names().Should().Equal("started", "delta", "delta", "message", "done");
        events.Single<AgentMessageView>("message").Text.Should().Be("All of it.");
    }

    [Fact]
    public async Task A_stop_the_model_call_did_not_notice_is_still_honoured_and_keeps_what_was_shown()
    {
        using var h = new AgentHarness();
        Guid conversationId = default;
        AgentStopView? stop = null;
        // A client that writes its whole answer and returns normally although the Stop landed meanwhile.
        h.Chat.ThenStreamed(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "Half and the rest."), "m",
                new AgentChatUsage(1, 1), "stop", null, null),
            ["Half ", "and the rest."], after: async _ => stop = await StopFromAnotherRequest(h, conversationId));
        var events = new List<AgentStreamEvent>();

        await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("go", null), false,
            (e, _) =>
            {
                events.Add(e);
                if (e.Name == AgentStreamEvents.Started) conversationId = ((AgentStartedData)e.Data).ConversationId;
                return Task.CompletedTask;
            }, default);

        stop!.Stopped.Should().BeTrue();
        events.Names().Should().Equal(["started", "delta", "delta", "message", "stopped", "done"],
            "a Stop that answered true is always followed by `stopped`");
        events.Single<AgentMessageView>("message").Text.Should().Be("Half and the rest.", "what was shown is kept");
        using var other = h.NewContext();
        (await other.AgentMessages.CountAsync(m => m.Role == AgentMessageRole.Assistant && m.Text == "Half and the rest.")).Should().Be(1);
    }

    [Fact]
    public async Task Stop_after_steps_were_proposed_records_them_as_expired_so_the_next_turn_hears_they_did_not_run()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([create]);
        h.Chat.ThenCall("create_database", """{"name":"shop-db"}""")
            .ThenStreamed(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "never"), "m", null, "stop", null, null),
                ["I have prepared "], after: ct => Task.Delay(Timeout.Infinite, ct));
        var events = new List<AgentStreamEvent>();

        async Task Sink(AgentStreamEvent e, CancellationToken _)
        {
            events.Add(e);
            if (e.Name == AgentStreamEvents.Delta) await StopFromAnotherRequest(h, ConversationOf(events));
        }

        await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("make a database", null), false, Sink, default);

        // The steps go out as the plan they became — expired, so no card offers Run for an answer the person
        // stopped — before the `stopped` that ends the turn.
        events.Names().Should().Equal("started", "delta", "message", "plan", "stopped", "done");
        var shown = events.Single<AgentPlanView>("plan");
        shown.Status.Should().Be("expired");
        var id = ConversationOf(events);
        using (var other = h.NewContext())
        {
            var plan = await other.AgentPlans.SingleAsync();
            plan.Status.Should().Be(AgentPlanStatus.Expired, "the steps the model was told were \"added\" are recorded as not run");
            create.Executions.Should().Be(0);

            var view = await h.ServiceOver(other).GetAsync(h.Actor, id, persian: false, default);
            view!.PendingPlan.Should().BeNull();
            view.Plans.Should().ContainSingle().Which.Plan.Should().BeEquivalentTo(shown,
                "live and reopened tell the same story: the same plan, expired, with the same steps");
        }

        h.Chat.ThenText("Understood.");
        await h.SendAsync("so, what now?", id);
        var request = h.Chat.Requests.Last().Messages;
        request.Should().Contain(m => m.Role == AgentRole.Tool && m.Content!.Contains("\"state\":\"expired\"") && m.Content.Contains("nothing ran"),
            "the model hears that the step it proposed did not run, rather than that the user will approve it");
        ShouldBeAValidRequest(request);
    }

    [Fact]
    public async Task A_turn_out_of_time_after_proposing_steps_also_records_them_as_expired()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([create]);
        h.Chat.ThenCall("create_database", """{"name":"shop-db"}""")
            .ThenText("never", before: ct => Task.Delay(Timeout.Infinite, ct));

        var (outcome, events) = await SendWith(h.ServiceOver(turnDeadline: TimeSpan.FromSeconds(2)), h, "make a database");

        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        events.Names().Should().Equal("started", "plan", "error", "done");
        events.Single<AgentPlanView>("plan").Status.Should().Be("expired", "shown as not run, before the deadline's error");
        using var other = h.NewContext();
        (await other.AgentPlans.SingleAsync()).Status.Should().Be(AgentPlanStatus.Expired);
        (await other.AgentMessages.CountAsync(m => m.Role == AgentMessageRole.Tool && m.PlanId != null && m.Text!.Contains("\"state\":\"expired\"")))
            .Should().Be(1);
        create.Executions.Should().Be(0);
    }

    private static async Task<(AgentSendOutcome Outcome, List<AgentStreamEvent> Events)> SendWith(
        AgentConversationService service, AgentHarness h, string text)
    {
        var events = new List<AgentStreamEvent>();
        var outcome = await service.SendAsync(h.Actor, null, new AgentSendMessageRequest(text, null), false,
            (e, _) => { events.Add(e); return Task.CompletedTask; }, default);
        return (outcome, events);
    }

    // ---- a turn already ending is never relabelled as stopped -------------------------------------

    [Fact]
    public async Task A_stop_pressed_while_a_turn_out_of_time_records_its_steps_is_false_and_the_ending_stays_the_deadline()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([create]);
        h.Chat.ThenCall("create_database", """{"name":"shop-db"}""")
            .ThenText("never", before: ct => Task.Delay(Timeout.Infinite, ct));
        // The Stop lands exactly while the deadline-ended turn is storing its steps as not run.
        var interceptor = new StopWhileRecordingDraft(planConversation => StopFromAnotherRequest(h, planConversation));
        using var db = new Harbora.Data.HarboraDbContext(
            new DbContextOptionsBuilder<Harbora.Data.HarboraDbContext>().UseInMemoryDatabase(h.DatabaseName).AddInterceptors(interceptor).Options,
            new Harbora.Application.Abstractions.FixedWorkspaceScope(h.WorkspaceId));

        var (_, events) = await SendWith(h.ServiceOver(db: db, turnDeadline: TimeSpan.FromSeconds(2)), h, "make a database");

        interceptor.Stop.Should().NotBeNull("the Stop was pressed in the window");
        interceptor.Stop!.Stopped.Should().BeFalse("the turn was already ending by its deadline");
        events.Names().Should().Equal("started", "plan", "error", "done");
        events.Single<AgentErrorData>("error").MessageEn.Should().StartWith("This answer took longer than");
        using var other = h.NewContext();
        (await other.AgentPlans.SingleAsync()).Status.Should().Be(AgentPlanStatus.Expired, "the steps are still recorded as not run");
    }

    /// <summary>Presses Stop, from another request, the first time this context stores a plan.</summary>
    private sealed class StopWhileRecordingDraft(Func<Guid, Task<AgentStopView?>> stop)
        : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public AgentStopView? Stop { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var plan = eventData.Context!.ChangeTracker.Entries<AgentPlan>().FirstOrDefault(e => e.State == EntityState.Added);
            if (plan is not null && Stop is null) Stop = await stop(plan.Entity.ConversationId);
            return result;
        }
    }

    [Fact]
    public async Task A_stop_pressed_over_the_cap_error_is_false_and_nothing_says_stopped()
    {
        var peek = new FakeTool("peek", AgentToolKind.Read)
        {
            OnExecute = (_, _) => new AgentToolResult(true, """{"answer":42}""", "peek looked", "peek دید")
        };
        using var h = new AgentHarness([peek]);
        h.SetDailyLimit("1");
        h.Chat.ThenCall("peek").ThenText("never asked");
        AgentStopView? stop = null;
        var events = new List<AgentStreamEvent>();

        async Task Sink(AgentStreamEvent e, CancellationToken _)
        {
            events.Add(e);
            if (e.Name == AgentStreamEvents.Error) stop = await StopFromAnotherRequest(h, ConversationOf(events));
        }

        await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("look", null), false, Sink, default);

        stop!.Stopped.Should().BeFalse("the cap had already ended the turn");
        events.Names().Should().Equal("started", "tool", "tool", "error", "done");
    }

    [Fact]
    public async Task A_stop_pressed_over_a_refusal_is_false_and_nothing_says_stopped()
    {
        using var h = new AgentHarness();
        h.SetDailyLimit("1");
        h.Db.AgentUsageDays.Add(new AgentUsageDay
        {
            WorkspaceId = h.WorkspaceId, Day = DateOnly.FromDateTime(AgentHarness.Start.UtcDateTime), ModelCalls = 1
        });
        await h.Db.SaveChangesAsync();
        AgentStopView? stop = null;
        var events = new List<AgentStreamEvent>();

        async Task Sink(AgentStreamEvent e, CancellationToken _)
        {
            events.Add(e);
            if (e.Name == AgentStreamEvents.Started) stop = await StopFromAnotherRequest(h, ConversationOf(events));
        }

        await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("hello", null), false, Sink, default);

        stop!.Stopped.Should().BeFalse("today's allowance was gone; the message was never going to be answered");
        events.Names().Should().Equal("started", "error", "done");
        h.Chat.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stop_pressed_while_a_failed_call_closes_its_bubble_is_false_and_the_failure_is_what_is_shown()
    {
        using var h = new AgentHarness();
        h.Chat.ThenStreamed(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "never"), "m", null, "stop", null, null),
            ["Half "], after: _ => throw new InvalidOperationException("The client broke."));
        AgentStopView? stop = null;
        var events = new List<AgentStreamEvent>();

        async Task Sink(AgentStreamEvent e, CancellationToken _)
        {
            events.Add(e);
            // The partial is being closed because the call broke: the turn is already ending in that failure.
            if (e.Name == AgentStreamEvents.Message) stop = await StopFromAnotherRequest(h, ConversationOf(events));
        }

        await h.ServiceOver(turnDeadline: Guard).SendAsync(h.Actor, null, new AgentSendMessageRequest("go", null), false, Sink, default);

        stop!.Stopped.Should().BeFalse();
        events.Names().Should().Equal("started", "delta", "message", "error", "done");
        events.Single<AgentErrorData>("error").MessageEn.Should().StartWith("Something went wrong on the server");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stream_the_stop_cut_off_is_never_reported_as_a_provider_failure(bool clientThrows)
    {
        // The real client turns a cut connection into a "dropped" failure; a client may also throw the
        // IOException itself. Either way, after a Stop the person is told it stopped — not that the provider failed.
        using var h = new AgentHarness();
        Guid conversationId = default;
        AgentStopView? stop = null;
        var dropped = AgentChatResponse.Failed("Dropped: the provider closed the stream.", "قطع شد: ارائه‌دهنده جریان را بست.");
        h.Chat.ThenStreamed(dropped, ["Half "], after: async _ =>
        {
            stop = await StopFromAnotherRequest(h, conversationId);
            if (clientThrows) throw new IOException("The response ended prematurely.");
        });
        var events = new List<AgentStreamEvent>();
        var logger = new ListLogger<AgentConversationService>();

        await h.ServiceOver(turnDeadline: Guard, logger: logger).SendAsync(h.Actor, null, new AgentSendMessageRequest("go", null), false,
            (e, _) =>
            {
                events.Add(e);
                if (e.Name == AgentStreamEvents.Started) conversationId = ((AgentStartedData)e.Data).ConversationId;
                return Task.CompletedTask;
            }, default);

        stop!.Stopped.Should().BeTrue();
        events.Names().Should().Equal("started", "delta", "message", "stopped", "done");
        events.Names().Should().NotContain("error");
        events.Single<AgentMessageView>("message").Text.Should().Be("Half ");

        // A failure that is not the cancellation itself is still seen in the log, at a level someone reads.
        var line = logger.Entries.Single(e => e.Text.Contains("stopped by the person"));
        line.Level.Should().Be(clientThrows ? Microsoft.Extensions.Logging.LogLevel.Warning : Microsoft.Extensions.Logging.LogLevel.Information);
        if (clientThrows) line.Text.Should().Contain("IOException");
    }
}
