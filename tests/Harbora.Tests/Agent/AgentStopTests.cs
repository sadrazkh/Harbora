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
    public async Task Stop_during_an_auto_approved_plan_lets_the_plan_finish_and_its_result_is_saved()
    {
        var tag = new FakeTool("tag_app", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([tag]);
        h.Remember("tag_app");
        h.Chat.ThenCall("tag_app", """{"name":"shop"}""").ThenText("Tagging it now.");
        Guid conversationId = default;
        AgentStopView? stop = null;
        tag.OnExecuteAsync = async (_, _) =>
        {
            // Pressed while the step runs; by the time it returns the stop has long landed on the turn.
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

        stop!.Stopped.Should().BeTrue();
        events.Names().Should().Equal("started", "message", "executed", "stopped", "done");
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
}
