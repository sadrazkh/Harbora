using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// A turn the person can leave and come back to (round six, owner feedback after the first deploy): the
/// answer is written out as it arrives, it is produced and saved whether or not anybody is still
/// listening, a reload mid-answer is told it is still coming, and a second message cannot start a second
/// turn over the first.
/// </summary>
public class AgentTurnTests
{
    private static FakeTool Reader() =>
        new("peek", AgentToolKind.Read) { OnExecute = (_, _) => new AgentToolResult(true, """{"answer":42}""", "peek looked", "peek دید") };

    private static async Task<(AgentSendOutcome Outcome, List<AgentStreamEvent> Events)> Send(
        AgentConversationService service, AgentHarness h, string text, Guid? conversationId = null,
        Func<AgentStreamEvent, CancellationToken, Task>? sink = null, CancellationToken requestToken = default)
    {
        var events = new List<AgentStreamEvent>();
        var outcome = await service.SendAsync(h.Actor, conversationId, new AgentSendMessageRequest(text, null), false,
            sink ?? ((e, _) => { events.Add(e); return Task.CompletedTask; }), requestToken);
        return (outcome, events);
    }

    // ---- streamed text -------------------------------------------------------------------------

    [Fact]
    public async Task Text_is_streamed_as_deltas_in_order_and_the_whole_message_follows()
    {
        using var h = new AgentHarness();
        h.Chat.ThenStreamedText("سلام", "، چه ", "کمکی از دستم برمی‌آید؟");

        var (_, events) = await h.SendAsync("سلام");

        events.Names().Should().Equal("started", "delta", "delta", "delta", "message", "done");
        events.Where(e => e.Name == "delta").Select(e => ((AgentDeltaData)e.Data).Text)
            .Should().Equal("سلام", "، چه ", "کمکی از دستم برمی‌آید؟");
        events.Single<AgentMessageView>("message").Text.Should().Be("سلام، چه کمکی از دستم برمی‌آید؟");
        h.Chat.StreamedCalls.Should().Be(1, "the loop asks the streaming overload");
    }

    [Fact]
    public async Task A_client_that_cannot_stream_still_answers_without_deltas()
    {
        using var h = new AgentHarness();

        var (_, events) = await Send(h.ServiceOver(chat: new PlainChatClient("Hello there.")), h, "hi");

        events.Names().Should().Equal("started", "message", "done");
        events.Single<AgentMessageView>("message").Text.Should().Be("Hello there.");
    }

    [Fact]
    public async Task Text_written_before_a_tool_call_is_finalized_by_a_message_before_the_tool_runs()
    {
        using var h = new AgentHarness([Reader()]);
        h.Chat.ThenStreamedCalls(["Let me ", "check."], h.Chat.Call("peek"))
            .ThenStreamedText("It is ", "42.");

        var (_, events) = await h.SendAsync("what is it?");

        events.Names().Should().Equal("started", "delta", "delta", "message", "tool", "tool", "delta", "delta", "message", "done");
        events.Where(e => e.Name == "message").Select(e => ((AgentMessageView)e.Data).Text).Should().Equal("Let me check.", "It is 42.");
    }

    [Fact]
    public async Task A_streamed_answer_of_nothing_but_whitespace_is_still_finalized()
    {
        using var h = new AgentHarness();
        h.Chat.ThenStreamedText("\n", "  ");

        var (_, events) = await h.SendAsync("say nothing");

        events.Names().Should().Equal("started", "delta", "delta", "message", "done");
        events.Single<AgentMessageView>("message").Text.Should().BeNull("there was no text, and the bubble is closed with that");
    }

    [Fact]
    public async Task A_call_that_fails_after_writing_part_of_its_answer_finalizes_and_keeps_what_was_shown()
    {
        using var h = new AgentHarness();
        h.Chat.ThenStreamed(AgentChatResponse.Failed("The stream was cut off.", "جریان قطع شد."), ["The first half "]);

        var (_, events) = await h.SendAsync("tell me");

        events.Names().Should().Equal("started", "delta", "message", "error", "done");
        events.Single<AgentMessageView>("message").Text.Should().Be("The first half ");
        (await h.Db.AgentMessages.CountAsync(m => m.Role == AgentMessageRole.Assistant && m.Text == "The first half "))
            .Should().Be(1, "a reload shows what was shown");
    }

    [Fact]
    public async Task A_turn_that_runs_out_of_time_mid_sentence_finalizes_the_sentence_it_was_writing()
    {
        using var h = new AgentHarness();
        h.Chat.ThenStreamed(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "never"), "m", null, "stop", null, null),
            ["Working on "], after: ct => Task.Delay(Timeout.Infinite, ct));

        var (_, events) = await Send(h.ServiceOver(turnDeadline: TimeSpan.FromSeconds(2)), h, "slow");

        events.Names().Should().Equal("started", "delta", "message", "error", "done");
        events.Single<AgentMessageView>("message").Text.Should().Be("Working on ");
        using var other = h.NewContext();
        (await other.AgentConversations.SingleAsync()).TurnStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_delta_callback_never_throws_when_the_browser_has_gone()
    {
        using var h = new AgentHarness();
        var client = new DeltaProbeClient(["Hel", "lo"]);

        // A sink like a closed connection: every write after the first fails.
        var writes = 0;
        Task Sink(AgentStreamEvent e, CancellationToken _) =>
            ++writes == 1 ? Task.CompletedTask : throw new IOException("The client disconnected.");

        var (outcome, _) = await Send(h.ServiceOver(chat: client), h, "hi", sink: Sink);

        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        client.CallbackThrew.Should().BeFalse("the client must never see the browser's departure as its own failure");
        client.DeltasDelivered.Should().Be(2);
        (await h.Db.AgentMessages.CountAsync(m => m.Role == AgentMessageRole.Assistant && m.Text == "Hello")).Should().Be(1);
    }

    /// <summary>A streaming client that records whether the delta callback ever threw at it.</summary>
    private sealed class DeltaProbeClient(string[] pieces) : IAgentChatClient
    {
        public bool CallbackThrew { get; private set; }
        public int DeltasDelivered { get; private set; }

        public Task<Harbora.Infrastructure.Assistant.AssistantUnavailable?> GetAvailabilityAsync(CancellationToken ct) =>
            Task.FromResult<Harbora.Infrastructure.Assistant.AssistantUnavailable?>(null);

        public Task<string?> GetModelAsync(CancellationToken ct) => Task.FromResult<string?>("probe/model");

        public Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, CancellationToken ct) =>
            CompleteAsync(request, null, ct);

        public async Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, Func<string, Task>? onTextDelta, CancellationToken ct)
        {
            foreach (var piece in pieces)
            {
                try
                {
                    if (onTextDelta is not null) await onTextDelta(piece);
                    DeltasDelivered++;
                }
                catch
                {
                    CallbackThrew = true;
                }
            }
            return new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, string.Concat(pieces)), "probe/model",
                new AgentChatUsage(1, 1), "stop", null, null);
        }
    }

    // ---- fixes after review --------------------------------------------------------------------

    [Fact]
    public async Task A_fully_streamed_answer_is_saved_even_when_the_turn_runs_out_of_time_as_it_finishes()
    {
        using var h = new AgentHarness();
        // The answer is complete, but the turn's deadline passes before it can be saved.
        h.Chat.ThenStreamed(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "All of it."), "m", null, "stop", null, null),
            ["All ", "of it."], after: async ct =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
            });

        var (_, events) = await Send(h.ServiceOver(turnDeadline: TimeSpan.FromSeconds(1)), h, "everything");

        events.Names().Should().ContainInOrder("delta", "delta", "message").And.EndWith("done");
        events.Single<AgentMessageView>("message").Text.Should().Be("All of it.");
        using var other = h.NewContext();
        (await other.AgentMessages.CountAsync(m => m.Role == AgentMessageRole.Assistant && m.Text == "All of it."))
            .Should().Be(1, "text the person watched being written is never lost to the deadline");
    }

    [Fact]
    public async Task A_streamed_answer_whose_save_fails_still_closes_its_bubble_before_the_error()
    {
        using var h = new AgentHarness();
        using var failing = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(h.DatabaseName)
                .AddInterceptors(new FailAssistantSave("Answered.")).Options,
            new Harbora.Application.Abstractions.FixedWorkspaceScope(h.WorkspaceId));
        h.Chat.ThenStreamedText("Answ", "ered.");

        var (_, events) = await Send(h.ServiceOver(db: failing), h, "go");

        events.Names().Should().Equal("started", "delta", "delta", "message", "error", "done");
        events.Single<AgentMessageView>("message").Text.Should().Be("Answered.");
    }

    [Fact]
    public async Task A_server_shutting_down_mid_turn_ends_it_cleanly_and_releases_its_mark()
    {
        using var h = new AgentHarness([Reader()]);
        var lifetime = new FakeLifetime();
        h.Chat.ThenCall("peek").ThenText("never", before: ct =>
        {
            lifetime.StopApplication();
            return Task.Delay(Timeout.Infinite, ct);
        });

        var (outcome, events) = await Send(h.ServiceOver(lifetime: lifetime), h, "work");

        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        events.Names().Should().Equal("started", "tool", "tool", "error", "done");
        events.Single<AgentErrorData>("error").MessageEn.Should().StartWith("The server is restarting");
        using var other = h.NewContext();
        (await other.AgentConversations.SingleAsync()).TurnStartedAt.Should().BeNull("no 409s for 3½ minutes after a self-deploy");
    }

    [Theory]
    [InlineData(typeof(IOException), Microsoft.Extensions.Logging.LogLevel.Information)]
    [InlineData(typeof(OperationCanceledException), Microsoft.Extensions.Logging.LogLevel.Information)]
    [InlineData(typeof(InvalidOperationException), Microsoft.Extensions.Logging.LogLevel.Warning)]
    public async Task A_listener_that_fails_is_logged_quietly_only_when_the_failure_is_a_departure(Type failure, Microsoft.Extensions.Logging.LogLevel level)
    {
        using var h = new AgentHarness();
        var logger = new ListLogger<AgentConversationService>();
        h.Chat.ThenText("hi");
        var writes = 0;
        Task Sink(AgentStreamEvent e, CancellationToken _) =>
            ++writes == 1 ? Task.CompletedTask : throw (Exception)Activator.CreateInstance(failure, "boom")!;

        await Send(h.ServiceOver(logger: logger), h, "hello", sink: Sink);

        logger.Entries.Should().ContainSingle(e => e.Text.Contains("listener")).Which.Level.Should().Be(level);
    }

    [Fact]
    public async Task A_partial_answer_whose_save_fails_still_closes_its_bubble_and_the_deadline_is_what_the_person_is_told()
    {
        using var h = new AgentHarness();
        using var failing = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(h.DatabaseName)
                .AddInterceptors(new FailAssistantSave("Working on ")).Options,
            new Harbora.Application.Abstractions.FixedWorkspaceScope(h.WorkspaceId));
        h.Chat.ThenStreamed(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "never"), "m", null, "stop", null, null),
            ["Working on "], after: ct => Task.Delay(Timeout.Infinite, ct));

        var (_, events) = await Send(h.ServiceOver(db: failing, turnDeadline: TimeSpan.FromSeconds(2)), h, "slow");

        events.Names().Should().Equal("started", "delta", "message", "error", "done");
        events.Single<AgentMessageView>("message").Text.Should().Be("Working on ");
        events.Single<AgentErrorData>("error").MessageEn.Should().StartWith("This answer took longer than",
            "the deadline is the failure the person must hear about, not the save that failed on the way out");
        using var other = h.NewContext();
        (await other.AgentConversations.SingleAsync()).TurnStartedAt.Should().BeNull("the failed row was dropped, so the mark could be cleared");
    }

    private sealed class FailAssistantSave(string text) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AgentMessage>()
                .Any(e => e.State == EntityState.Added && e.Entity.Text == text))
                throw new DbUpdateException("The database refused the write.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // ---- the turn outlives its listener --------------------------------------------------------

    [Fact]
    public async Task A_listener_that_leaves_mid_turn_does_not_stop_the_answer_being_produced_and_saved()
    {
        using var h = new AgentHarness([Reader()]);
        using var request = new CancellationTokenSource();
        var heard = new List<string>();

        // A sink like a real connection: once the request is gone, every write fails.
        Task Sink(AgentStreamEvent e, CancellationToken _)
        {
            request.Token.ThrowIfCancellationRequested();
            heard.Add(e.Name);
            return Task.CompletedTask;
        }

        // The reload happens while the model is working on the first call.
        h.Chat.ThenCallsAfter(_ => { request.Cancel(); return Task.CompletedTask; }, h.Chat.Call("peek"))
            .ThenText("The answer is 42.");

        var (outcome, _) = await Send(h.Service, h, "what is the answer?", sink: Sink, requestToken: request.Token);

        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        heard.Should().Equal(["started"], "nothing is written after the listener left");
        h.Chat.Requests.Should().HaveCount(2, "the turn carried on to its second model call");

        using var other = h.NewContext();
        var conversationId = await other.AgentConversations.Select(c => c.Id).SingleAsync();
        var view = await h.ServiceOver(other).GetAsync(h.Actor, conversationId, persian: false, default);
        view!.Messages.Select(m => (m.Role, m.Text)).Should().Equal(
            ("user", "what is the answer?"), ("tool", "peek looked"), ("assistant", "The answer is 42."));
        view.InProgress.Should().BeFalse("the turn's mark is cleared when it ends");
    }

    [Fact]
    public async Task A_conversation_mid_turn_shows_what_is_saved_so_far_reports_in_progress_and_refuses_a_second_message()
    {
        using var h = new AgentHarness([Reader()]);
        AgentConversationView? midTurn = null;
        AgentSendOutcome? second = null;
        Guid conversationId = default;

        h.Chat.ThenCall("peek")
            .ThenText("Done.", before: async cancellation =>
            {
                // A second request — the reloaded pane, then the person typing again — on its own context.
                using var other = h.NewContext();
                var service = h.ServiceOver(other);
                conversationId = await other.AgentConversations.Select(c => c.Id).SingleAsync();
                midTurn = await service.GetAsync(h.Actor, conversationId, persian: false, default);
                second = (await Send(service, h, "hello again?", conversationId)).Outcome;
            });

        await h.SendAsync("first question");

        midTurn!.InProgress.Should().BeTrue();
        midTurn.Messages.Select(m => m.Role).Should().Equal(["user", "tool"], "what was produced so far is already saved");
        second!.Status.Should().Be(AgentSendStatus.Busy);
        second.MessageEn.Should().StartWith("Still answering your previous message");
        second.MessageFa.Should().NotBeNullOrEmpty();

        using var after = h.NewContext();
        (await h.ServiceOver(after).GetAsync(h.Actor, conversationId, persian: false, default))!.InProgress.Should().BeFalse();
        (await after.AgentMessages.CountAsync(m => m.Text == "hello again?")).Should().Be(0, "the refused message was not stored");
    }

    [Fact]
    public async Task A_stale_turn_mark_left_by_a_dead_process_is_neither_in_progress_nor_in_the_way()
    {
        using var h = new AgentHarness();
        var conversation = new AgentConversation
        {
            WorkspaceId = h.WorkspaceId, UserId = h.UserId, Title = "old",
            TurnStartedAt = AgentHarness.Start - AgentConversationService.DefaultTurnDeadline - AgentConversationService.TurnMarkGrace
        };
        h.Db.AgentConversations.Add(conversation);
        await h.Db.SaveChangesAsync();

        (await h.Service.GetAsync(h.Actor, conversation.Id, persian: false, default))!.InProgress.Should().BeFalse();

        h.Chat.ThenText("Back again.");
        var (outcome, events) = await h.SendAsync("anyone there?", conversation.Id);
        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        events.Names().Should().Contain("message");
        (await h.Db.AgentConversations.AsNoTracking().SingleAsync()).TurnStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_fresh_turn_mark_refuses_the_message_before_anything_is_stored_or_sent()
    {
        using var h = new AgentHarness();
        var conversation = new AgentConversation
        {
            WorkspaceId = h.WorkspaceId, UserId = h.UserId, Title = "busy", TurnStartedAt = AgentHarness.Start - TimeSpan.FromSeconds(20)
        };
        h.Db.AgentConversations.Add(conversation);
        await h.Db.SaveChangesAsync();
        h.Chat.ThenText("never");

        var (outcome, events) = await h.SendAsync("again", conversation.Id);

        outcome.Status.Should().Be(AgentSendStatus.Busy);
        events.Should().BeEmpty();
        h.Chat.Requests.Should().BeEmpty();
        (await h.Db.AgentMessages.CountAsync()).Should().Be(0);
        (await h.Db.AgentConversations.AsNoTracking().SingleAsync()).TurnStartedAt
            .Should().Be(AgentHarness.Start - TimeSpan.FromSeconds(20), "a refused message does not touch the running turn's mark");
    }

    [Fact]
    public async Task A_turn_past_its_deadline_is_stopped_with_what_was_done_saved_and_its_mark_released()
    {
        using var h = new AgentHarness([Reader()]);
        h.Chat.ThenCall("peek").ThenText("too late", before: ct => Task.Delay(Timeout.Infinite, ct));

        var (outcome, events) = await Send(h.ServiceOver(turnDeadline: TimeSpan.FromSeconds(2)), h, "slow one");

        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        events.Names().Should().Equal("started", "tool", "tool", "error", "done");
        events.Single<AgentErrorData>("error").MessageEn.Should().StartWith("This answer took longer than");
        using var other = h.NewContext();
        (await other.AgentConversations.SingleAsync()).TurnStartedAt.Should().BeNull();
        (await other.AgentMessages.CountAsync(m => m.Role == AgentMessageRole.Tool)).Should().Be(1, "the read that finished is kept");
    }

    // ---- a conversation before its first message -----------------------------------------------

    [Fact]
    public async Task A_conversation_created_ahead_is_empty_unlisted_and_takes_its_title_from_the_first_message()
    {
        using var h = new AgentHarness();

        var created = await h.Service.CreateAsync(h.Actor, default);

        created!.Title.Should().BeEmpty();
        (await h.Service.ListAsync(h.Actor, default)).Should().BeEmpty("a reserved conversation is not something the person said");
        var view = await h.Service.GetAsync(h.Actor, created.Id, persian: false, default);
        view!.Messages.Should().BeEmpty();
        view.InProgress.Should().BeFalse();

        h.Chat.ThenText("Hi!");
        var (outcome, events) = await h.SendAsync("  hello   there ", created.Id);

        outcome.Status.Should().Be(AgentSendStatus.Streamed);
        events.Single<AgentStartedData>("started").ConversationId.Should().Be(created.Id);
        (await h.Service.ListAsync(h.Actor, default)).Should().ContainSingle(c => c.Id == created.Id && c.Title == "hello there");
    }

    [Fact]
    public async Task Creating_a_conversation_is_refused_without_the_entitlement()
    {
        using var h = new AgentHarness(entitled: false);

        (await h.Service.CreateAsync(h.Actor, default)).Should().BeNull();
        (await h.Db.AgentConversations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_new_conversation_carries_its_turn_mark_while_it_is_answered()
    {
        using var h = new AgentHarness();
        bool? inProgress = null;
        h.Chat.ThenText("ok", before: async _ =>
        {
            using var other = h.NewContext();
            var id = await other.AgentConversations.Select(c => c.Id).SingleAsync();
            inProgress = (await h.ServiceOver(other).GetAsync(h.Actor, id, persian: false, default))!.InProgress;
        });

        await h.SendAsync("first ever message");

        inProgress.Should().BeTrue();
        (await h.Db.AgentConversations.AsNoTracking().SingleAsync()).TurnStartedAt.Should().BeNull();
    }
}
