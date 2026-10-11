using System.Text;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Harbora.Domain.Authorization;
using Harbora.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The orchestrator: one person's message in, a stream of events out — the loop "How one message flows"
/// in the 2026-10-11 assistant-agent design describes.
///
/// <para>
/// The order of the gates is the design's: entitlement, then whether a model can be reached at all,
/// then the workspace's daily cap. Then the model is called with the system prompt, the snapshot of
/// what this person can see, where they are in the panel, the conversation's last
/// <see cref="HistoryMessages"/> messages and the tools their role allows — at most
/// <see cref="MaxModelCallsPerMessage"/> times. Reads run as soon as they are asked for. A write is
/// only ever validated: it becomes a step of a draft plan, and the model is told so in the reply to
/// its call, because a model that believed its call did something would say "done" to the person.
/// </para>
///
/// <para>
/// Everything runs inside the person's own request, so the workspace filter, <c>ICurrentUser</c> and
/// project access behave exactly as they do for a click in the panel.
/// </para>
///
/// <para>
/// The client's failures and availability reasons are shown verbatim only to somebody who manages the
/// platform (<see cref="Capabilities.PlatformManage"/>). They can carry the provider's own error text,
/// model ids and a host name from a connection failure — the owner needs exactly that to fix it, and a
/// customer's member should neither read nor be able to act on it. Everyone else is told plainly that
/// the assistant cannot answer right now, and the detail goes to the server log.
/// </para>
/// </summary>
public sealed class AgentConversationService(
    HarboraDbContext db,
    IAgentChatClient chat,
    AgentToolRegistry registry,
    AgentPlanService plans,
    AgentUsageMeter meter,
    AgentWorkspaceSnapshot snapshot,
    IFeatureGate features,
    IAgentCapabilityCheck capabilities,
    ISystemClock clock,
    ILogger<AgentConversationService> logger)
{
    /// <summary>The most model calls one message may cause — reads included.</summary>
    public const int MaxModelCallsPerMessage = 8;

    /// <summary>How much of the conversation is sent back to the model each turn.</summary>
    public const int HistoryMessages = 20;

    /// <summary>The longest message a person may send.</summary>
    public const int MaxMessageLength = 4000;

    /// <summary>How many conversations the pane's list shows.</summary>
    public const int ConversationListSize = 30;

    /// <summary>How many messages the conversation view returns (the newest).</summary>
    public const int ConversationViewMessages = 200;

    private const int MaxTitleLength = 60;

    /// <summary>
    /// The longest one turn may run — model calls, tools and all — once it no longer depends on the
    /// request that started it. Past this the turn is stopped, what was done so far stays saved, and the
    /// person is told so.
    /// </summary>
    public static readonly TimeSpan DefaultTurnDeadline = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long a turn's mark is believed. A little past the deadline: a turn that reached its deadline
    /// still needs a moment to write its end, and only a mark older than that can have been left by a
    /// process that died mid-turn.
    /// </summary>
    public static readonly TimeSpan TurnMarkGrace = TimeSpan.FromSeconds(30);

    /// <summary>The deadline in force; tests shorten it.</summary>
    internal TimeSpan TurnDeadline { get; init; } = DefaultTurnDeadline;

    private TimeSpan TurnMarkLifetime => TurnDeadline + TurnMarkGrace;

    // ---- reading -------------------------------------------------------------------------------

    /// <summary>What the pane shows on opening: can the assistant be used now, which model, how much is left today.</summary>
    public async Task<AgentStatusView> StatusAsync(AgentActor actor, CancellationToken ct)
    {
        var model = await chat.GetModelAsync(ct);
        var remaining = await meter.RemainingAsync(actor.WorkspaceId, ct);

        if (!(await features.EvaluateAsync(actor.WorkspaceId, PlatformFeatures.Assistant, ct)).IsEnabled)
            return new(false, NotEntitledEn, NotEntitledFa, model, remaining);

        if (await chat.GetAvailabilityAsync(ct) is { } unavailable)
        {
            var reason = await UnavailableAsync(actor, unavailable, ct);
            return new(false, reason.MessageEn, reason.MessageFa, model, remaining);
        }

        if (remaining == 0)
            return new(false, CapReachedEn, CapReachedFa, model, 0);

        return new(true, null, null, model, remaining);
    }

    /// <summary>
    /// This person's latest conversations in this workspace, most recently active first. A conversation
    /// created ahead of its first message (<see cref="CreateAsync"/>) and never used has no title yet and
    /// is left out — it is a place the pane reserved, not something the person said.
    /// </summary>
    public async Task<IReadOnlyList<AgentConversationSummaryView>> ListAsync(AgentActor actor, CancellationToken ct) =>
        await db.AgentConversations.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.WorkspaceId == actor.WorkspaceId && c.UserId == actor.UserId && c.Title != "")
            .OrderByDescending(c => c.UpdatedAt)
            .Take(ConversationListSize)
            .Select(c => new AgentConversationSummaryView(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync(ct);

    /// <summary>
    /// One conversation as the pane renders it, with the plan still waiting for a decision. Null when it
    /// is not this person's in this workspace. Only what a person should read is returned: their
    /// messages, the model's text, and the tool messages that carry a summary — not the raw replies
    /// the model got to a malformed call.
    /// </summary>
    public async Task<AgentConversationView?> GetAsync(AgentActor actor, Guid conversationId, bool persian, CancellationToken ct)
    {
        var conversation = await db.AgentConversations.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.WorkspaceId == actor.WorkspaceId && c.UserId == actor.UserId, ct);
        if (conversation is null) return null;

        var rows = await db.AgentMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.WorkspaceId == actor.WorkspaceId)
            .OrderByDescending(m => m.Sequence)
            .Take(ConversationViewMessages)
            .ToListAsync(ct);
        rows.Reverse();

        var messages = new List<AgentMessageView>();
        foreach (var row in rows)
        {
            switch (row.Role)
            {
                case AgentMessageRole.User:
                    messages.Add(new(row.Id, "user", row.Text, null, row.CreatedAt));
                    break;
                case AgentMessageRole.Assistant when !string.IsNullOrWhiteSpace(row.Text):
                    messages.Add(new(row.Id, "assistant", row.Text, null, row.CreatedAt));
                    break;
                case AgentMessageRole.Tool when row.SummaryEn is not null:
                    messages.Add(new(row.Id, "tool", persian ? row.SummaryFa ?? row.SummaryEn : row.SummaryEn,
                        row.ToolName, row.CreatedAt));
                    break;
            }
        }

        var pending = await plans.PendingPlanAsync(actor, conversationId, ct);
        return new AgentConversationView(conversation.Id, conversation.Title, messages,
            pending is null ? null : plans.ViewOf(pending),
            InProgress: IsTurnLive(conversation.TurnStartedAt));
    }

    /// <summary>Whether a turn mark still means a turn is being answered — see <see cref="TurnMarkGrace"/>.</summary>
    private bool IsTurnLive(DateTimeOffset? turnStartedAt) =>
        turnStartedAt is { } started && clock.UtcNow - started < TurnMarkLifetime;

    /// <summary>
    /// A new, empty conversation — created before its first message, so the pane can remember the id at
    /// once and a reload in the middle of that first answer still knows where it was. Null when the
    /// workspace is not entitled to the assistant.
    /// </summary>
    public async Task<AgentConversationSummaryView?> CreateAsync(AgentActor actor, CancellationToken ct)
    {
        if (!(await features.EvaluateAsync(actor.WorkspaceId, PlatformFeatures.Assistant, ct)).IsEnabled)
            return null;

        var now = clock.UtcNow;
        var conversation = new AgentConversation
        {
            WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, Title = "", CreatedAt = now, UpdatedAt = now
        };
        db.AgentConversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        return new AgentConversationSummaryView(conversation.Id, conversation.Title, conversation.UpdatedAt);
    }

    // ---- one message ---------------------------------------------------------------------------


    /// <summary>
    /// Handles one message. Refusals that need no stream — an unusable message, a workspace without the
    /// entitlement, a conversation that is not this person's, one still answering the previous message —
    /// come back as the outcome with nothing emitted, so the caller can still answer with a status code.
    /// Otherwise <c>started</c> is emitted first and <c>done</c> last, whatever happens in between; a
    /// failure after <c>started</c> becomes an <c>error</c> event, never an exception.
    ///
    /// <para>
    /// The turn does not belong to the request. <paramref name="ct"/> — the request's own token, which a
    /// reload or a closed tab cancels — only guards the checks before the turn is taken on. From then on
    /// the turn runs on a token of its own with a hard deadline (<see cref="DefaultTurnDeadline"/>), and a
    /// listener that has gone away is simply no longer written to: the answer is still produced and saved
    /// as it goes, so the person who comes back finds it. While it runs, the conversation carries
    /// <see cref="AgentConversation.TurnStartedAt"/>, which is what <c>InProgress</c> reports and what
    /// refuses a second message over the first.
    /// </para>
    /// </summary>
    /// <param name="conversationId">Null starts a new conversation.</param>
    /// <param name="persianInterface">The panel's language, a hint for messages whose own language is unclear.</param>
    public async Task<AgentSendOutcome> SendAsync(
        AgentActor actor, Guid? conversationId, AgentSendMessageRequest? request, bool persianInterface,
        Func<AgentStreamEvent, CancellationToken, Task> emit, CancellationToken ct)
    {
        var text = request?.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return new(AgentSendStatus.Invalid, "Type a message first.", "اول پیامی بنویسید.");
        if (text.Length > MaxMessageLength)
            return new(AgentSendStatus.Invalid,
                $"A message can be at most {MaxMessageLength} characters.",
                $"پیام حداکثر می‌تواند {MaxMessageLength} نویسه باشد.");

        if (!(await features.EvaluateAsync(actor.WorkspaceId, PlatformFeatures.Assistant, ct)).IsEnabled)
            return new(AgentSendStatus.NotEntitled, NotEntitledEn, NotEntitledFa);

        // Stored to the microsecond, which is all Postgres keeps: the mark is later compared for
        // equality, and a stamp with finer ticks than the column would never match its own row.
        var stamp = ToMicroseconds(clock.UtcNow);
        AgentConversation conversation;
        if (conversationId is { } id)
        {
            var existing = await db.AgentConversations.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == actor.WorkspaceId && c.UserId == actor.UserId, ct);
            if (existing is null)
                return new(AgentSendStatus.NotFound, "There is no such conversation.", "چنین گفت‌وگویی وجود ندارد.");
            conversation = existing;

            // On no token: a claim that committed and was then reported cancelled would leave a mark that
            // no finally clears, and the conversation would refuse every message until it lapsed.
            if (!await TryClaimTurnAsync(conversation, stamp, CancellationToken.None))
                return new(AgentSendStatus.Busy,
                    "Still answering your previous message — wait for it to finish, then send this one.",
                    "هنوز در حال پاسخ به پیام قبلی شماست — صبر کنید تمام شود، بعد این یکی را بفرستید.");

            // A conversation created ahead of its first message takes its title from that message.
            if (conversation.Title.Length == 0) conversation.Title = TitleFrom(text);
        }
        else
        {
            conversation = new AgentConversation
            {
                WorkspaceId = actor.WorkspaceId,
                UserId = actor.UserId,
                Title = TitleFrom(text),
                TurnStartedAt = stamp,
                CreatedAt = stamp,
                UpdatedAt = stamp
            };
            db.AgentConversations.Add(conversation);
        }

        // From here the turn is taken on: it runs to its end on its own token, whoever is listening.
        using var deadline = new CancellationTokenSource(TurnDeadline);
        var turn = deadline.Token;
        var listener = new Listener(emit, logger);
        emit = listener.EmitAsync;

        try
        {
            // The design's gates after the entitlement: can a model be reached, and is any of today's
            // allowance left. Asked before anything else changes, so a message that cannot be answered
            // does not withdraw a card that could still have been approved.
            var refusal = await GateAsync(actor, turn);

            // A card left open while the person moved on is withdrawn, and the turn is told so — only for
            // a message that will actually be answered, and before it, so the record reads in order.
            if (refusal is null && conversationId is not null)
                await plans.SupersedePendingAsync(actor, conversation.Id, turn);

            // The person's words are kept whatever happens next, so the conversation they reopen shows
            // what they asked — including when the answer was an error.
            await AppendAsync(conversation, new AgentMessage { Role = AgentMessageRole.User, Text = text }, turn);

            await emit(new AgentStreamEvent(AgentStreamEvents.Started, new AgentStartedData(conversation.Id)), turn);

            try
            {
                if (refusal is not null)
                    await emit(new AgentStreamEvent(AgentStreamEvents.Error, refusal), turn);
                else
                    await RunTurnAsync(actor, conversation, request!.Page, persianInterface, emit, turn);
            }
            catch (OperationCanceledException) when (turn.IsCancellationRequested)
            {
                logger.LogWarning("The assistant turn in conversation {Conversation} reached its {Deadline} deadline.",
                    conversation.Id, TurnDeadline);
                await emit(Error(
                    $"This answer took longer than {TurnDeadline.TotalMinutes:0} minutes and was stopped. What was done so far is saved.",
                    $"این پاسخ بیش از {TurnDeadline.TotalMinutes:0} دقیقه طول کشید و متوقف شد. آنچه تا اینجا انجام شده ذخیره شده است."),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The assistant turn failed in conversation {Conversation}.", conversation.Id);
                await emit(Error("Something went wrong on the server while answering; nothing further was done.",
                    "هنگام پاسخ‌دادن خطایی در سرور رخ داد؛ کار دیگری انجام نشد."), CancellationToken.None);
            }

            var remaining = await meter.RemainingAsync(actor.WorkspaceId, CancellationToken.None);
            await emit(new AgentStreamEvent(AgentStreamEvents.Done, new AgentDoneData(conversation.Id, remaining)),
                CancellationToken.None);
        }
        finally
        {
            await EndTurnAsync(conversation.Id, stamp);
        }

        return AgentSendOutcome.Streamed;
    }

    /// <summary>
    /// The person listening to a turn — or not, any more. A write to a closed connection fails, and that
    /// failure must not end the turn: the answer is still being produced and saved for when the person
    /// comes back. So the first failed write marks the listener gone, and nothing is written after it.
    /// </summary>
    private sealed class Listener(Func<AgentStreamEvent, CancellationToken, Task> sink, ILogger logger)
    {
        private bool _gone;

        public async Task EmitAsync(AgentStreamEvent e, CancellationToken ct)
        {
            if (_gone) return;
            try
            {
                await sink(e, ct);
            }
            catch (Exception ex)
            {
                _gone = true;
                logger.LogInformation("The listener left during an assistant turn ({Error}); the turn carries on and is saved.",
                    ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Takes the conversation's turn, if no live one holds it: one conditional update whose <c>WHERE</c>
    /// is the whole check (no mark, or a mark old enough to have been left by a process that died), so
    /// two messages sent at once cannot both start a turn. EF InMemory has no <c>ExecuteUpdateAsync</c>;
    /// there it is a plain read-then-write, as in <c>AgentPlanService.TryMoveAsync</c>.
    /// </summary>
    private async Task<bool> TryClaimTurnAsync(AgentConversation conversation, DateTimeOffset stamp, CancellationToken ct)
    {
        var staleBefore = stamp - TurnMarkLifetime;

        if (db.Database.IsRelational())
        {
            var claimed = await db.AgentConversations.IgnoreQueryFilters()
                .Where(c => c.Id == conversation.Id && (c.TurnStartedAt == null || c.TurnStartedAt <= staleBefore))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.TurnStartedAt, stamp), ct);
            // The tracked copy is brought in line, so later saves of this conversation never write the
            // stale mark back (the same reason as AgentPlanService.RefreshTrackedAsync).
            if (claimed > 0) await db.Entry(conversation).ReloadAsync(CancellationToken.None);
            return claimed > 0;
        }

        if (conversation.TurnStartedAt is { } started && started > staleBefore) return false;
        conversation.TurnStartedAt = stamp;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Clears the turn's mark — only this turn's own, so a turn that overran its deadline cannot clear
    /// the mark of a newer one that took over. Never throws: a mark that could not be cleared goes stale
    /// on its own after <see cref="TurnMarkGrace"/>, and the answer is saved either way.
    /// </summary>
    private async Task EndTurnAsync(Guid conversationId, DateTimeOffset stamp)
    {
        try
        {
            if (db.Database.IsRelational())
            {
                await db.AgentConversations.IgnoreQueryFilters()
                    .Where(c => c.Id == conversationId && c.TurnStartedAt == stamp)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.TurnStartedAt, (DateTimeOffset?)null), CancellationToken.None);
                var tracked = db.ChangeTracker.Entries<AgentConversation>().FirstOrDefault(e => e.Entity.Id == conversationId);
                if (tracked is not null) await tracked.ReloadAsync(CancellationToken.None);
                return;
            }

            // Re-read rather than trusting a reference: a broken plan run may have cleared the tracker.
            var row = await db.AgentConversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversationId);
            if (row is null || row.TurnStartedAt != stamp) return;
            row.TurnStartedAt = null;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The turn mark of conversation {Conversation} could not be cleared; it lapses by itself.", conversationId);
        }
    }

    private static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, value.Offset);

    private async Task RunTurnAsync(
        AgentActor actor, AgentConversation conversation, AgentPageContext? page, bool persianInterface,
        Func<AgentStreamEvent, CancellationToken, Task> emit, CancellationToken ct)
    {
        var context = new AgentToolContext(actor.WorkspaceId, actor.UserId, actor.User, conversation.Id, page);
        var system = AgentSystemPrompt.Build(
            await snapshot.DescribeAsync(context, ct),
            await snapshot.DescribePageAsync(context, ct),
            persianInterface);

        var messages = new List<AgentChatMessage> { new(AgentRole.System, system) };
        messages.AddRange(await HistoryAsync(actor, conversation.Id, ct));

        var allowed = await registry.AllowedForAsync(actor.User, ct);
        var specs = allowed.Select(AgentToolRegistry.SpecOf).ToList();
        var turn = new Turn(actor, context, await plans.RememberedToolsAsync(actor, ct), allowed);

        var finished = false;
        var stoppedEarly = false;

        for (var call = 1; call <= MaxModelCallsPerMessage; call++)
        {
            // Reserved before the call, so the cap holds under concurrent turns — see AgentUsageMeter.
            if (!await meter.TryReserveAsync(actor.WorkspaceId, ct))
            {
                await emit(Error(CapReachedEn, CapReachedFa), ct);
                stoppedEarly = true;
                break;
            }

            // A copy: the list keeps growing after the call, and a request must stay what was sent. Streamed:
            // each piece of the model's text goes to the pane as a delta while it is written — display
            // only; the message event below carries the whole text and is the one that counts. The client
            // passes text only, never tool-call fragments.
            //
            // Every run of deltas is finalized by a message event — after a call that ends in text, in
            // tool calls with text before them, or in nothing but whitespace, and also after a call that
            // failed or ran out of time half-way, whose partial text is saved and sent as the message.
            // The pane's live bubble is never left without its final word. The callback cannot throw for
            // a browser that went away: emit is the listener, which swallows that.
            var streamed = new StringBuilder();
            AgentChatResponse response;
            try
            {
                response = await chat.CompleteAsync(new AgentChatRequest(messages.ToList(), specs),
                    piece =>
                    {
                        if (string.IsNullOrEmpty(piece)) return Task.CompletedTask;
                        streamed.Append(piece);
                        return emit(new AgentStreamEvent(AgentStreamEvents.Delta, new AgentDeltaData(piece)), ct);
                    },
                    ct);
            }
            catch (Exception) when (streamed.Length > 0)
            {
                await FinalizePartialAsync(conversation, streamed.ToString(), emit);
                throw;
            }

            if (!response.Ok || response.Message is null)
            {
                if (streamed.Length > 0) await FinalizePartialAsync(conversation, streamed.ToString(), emit);
                await emit(new AgentStreamEvent(AgentStreamEvents.Error, await ProviderFailureAsync(actor, response, ct)), ct);
                stoppedEarly = true;
                break;
            }

            var reply = response.Message;
            var toolCalls = NormalizeCalls(reply.ToolCalls, call);

            var assistant = await AppendAsync(conversation, new AgentMessage
            {
                Role = AgentMessageRole.Assistant,
                Text = string.IsNullOrWhiteSpace(reply.Content) ? null : reply.Content,
                ToolCallsJson = toolCalls.Count > 0 ? AgentJson.Serialize(toolCalls) : null,
                Model = Truncate(response.Model, AgentMessage.MaxModelLength),
                InputTokens = response.Usage?.InputTokens ?? 0,
                OutputTokens = response.Usage?.OutputTokens ?? 0
            }, ct);
            messages.Add(new AgentChatMessage(AgentRole.Assistant, reply.Content, toolCalls.Count > 0 ? toolCalls : null));

            if (assistant.Text is not null || streamed.Length > 0)
                await emit(new AgentStreamEvent(AgentStreamEvents.Message,
                    new AgentMessageView(assistant.Id, "assistant", assistant.Text, null, assistant.CreatedAt)), ct);

            if (toolCalls.Count == 0)
            {
                finished = true;
                break;
            }

            foreach (var toolCall in toolCalls)
            {
                var answer = await AnswerToolCallAsync(toolCall, turn, emit, ct);
                await AppendAsync(conversation, new AgentMessage
                {
                    Role = AgentMessageRole.Tool,
                    Text = answer.Content,
                    ToolCallId = toolCall.Id,
                    ToolName = Truncate(toolCall.Name, AgentMessage.MaxToolNameLength),
                    SummaryEn = AgentConversationLog.Bound(answer.SummaryEn),
                    SummaryFa = AgentConversationLog.Bound(answer.SummaryFa)
                }, ct);
                messages.Add(new AgentChatMessage(AgentRole.Tool, answer.Content, ToolCallId: toolCall.Id));
            }
        }

        if (!finished && !stoppedEarly)
            await emit(Error(
                $"I stopped after {MaxModelCallsPerMessage} steps without finishing. Try a smaller request.",
                $"پس از {MaxModelCallsPerMessage} مرحله بدون تمام‌کردن کار متوقف شدم. درخواست کوچک‌تری بدهید."), ct);

        if (turn.Draft.Count == 0) return;

        var plan = await plans.CreatePendingAsync(actor, conversation.Id, turn.Draft, ct);

        // Only a finished turn whose every step is a remembered safe tool runs without a card. A turn the
        // model never finished — the cap, the ceiling, a provider error — may have meant more steps
        // than it got to propose, so its steps always wait for the person.
        if (finished && turn.Draft.All(s => s.AutoApproved))
        {
            var result = await plans.ExecuteAutoApprovedAsync(actor, plan, ct);
            await emit(new AgentStreamEvent(AgentStreamEvents.Executed, result), ct);
        }
        else
        {
            await emit(new AgentStreamEvent(AgentStreamEvents.Plan, plans.ViewOf(plan)), ct);
        }
    }

    /// <summary>
    /// What a person already watched being written, when the call that was writing it never finished:
    /// saved as the model's message, so a reload shows what was shown, and sent as the message event
    /// that finalizes the bubble. Runs on no token — the turn's own may be the one that just ran out.
    /// </summary>
    private async Task FinalizePartialAsync(
        AgentConversation conversation, string text, Func<AgentStreamEvent, CancellationToken, Task> emit)
    {
        var partial = await AppendAsync(conversation, new AgentMessage
        {
            Role = AgentMessageRole.Assistant,
            Text = string.IsNullOrWhiteSpace(text) ? null : text
        }, CancellationToken.None);
        await emit(new AgentStreamEvent(AgentStreamEvents.Message,
            new AgentMessageView(partial.Id, "assistant", partial.Text, null, partial.CreatedAt)), CancellationToken.None);
    }

    /// <summary>Why this message cannot be answered at all right now, or null: no reachable model, or
    /// none of today's allowance left. (Each model call still reserves its own share as it is made.)</summary>
    private async Task<AgentErrorData?> GateAsync(AgentActor actor, CancellationToken ct)
    {
        if (await chat.GetAvailabilityAsync(ct) is { } unavailable)
            return await UnavailableAsync(actor, unavailable, ct);

        if (await meter.RemainingAsync(actor.WorkspaceId, ct) == 0)
            return new AgentErrorData(CapReachedEn, CapReachedFa);

        return null;
    }

    /// <summary>The state one turn accumulates across its tool calls.</summary>
    private sealed class Turn(
        AgentActor actor, AgentToolContext context, IReadOnlySet<string> remembered, IReadOnlyList<IAgentTool> allowed)
    {
        public AgentActor Actor { get; } = actor;
        public AgentToolContext Context { get; } = context;
        public IReadOnlySet<string> Remembered { get; } = remembered;
        public IReadOnlyList<IAgentTool> Allowed { get; } = allowed;
        public AgentPlanScope Scope { get; } = new();
        public List<AgentPlanStep> Draft { get; } = [];
    }

    /// <summary>What goes back to the model for one call, and what the person may be shown instead.</summary>
    private sealed record ToolAnswer(string Content, string? SummaryEn = null, string? SummaryFa = null);

    private async Task<ToolAnswer> AnswerToolCallAsync(
        AgentToolCall call, Turn turn, Func<AgentStreamEvent, CancellationToken, Task> emit, CancellationToken ct)
    {
        var tool = registry.Find(call.Name);
        if (tool is null)
            return new(AgentJson.ForModel(
                $"Unknown tool '{call.Name}'. The tools you can use are: {string.Join(", ", turn.Allowed.Select(t => t.Name))}."));

        if (!await registry.AllowsAsync(turn.Actor.User, tool, ct))
            return new(AgentJson.ForModel(
                $"The user's role does not allow '{tool.Name}' (it needs the '{tool.Capability}' permission). Nothing was done; tell the user they cannot do this."));

        if (ParseArguments(call) is not { } args)
            return new(AgentJson.ForModel(
                $"The arguments for '{tool.Name}' are not a valid JSON object. Call it again with a JSON object that matches its parameters."));

        return AgentConsentPolicy.RunsImmediately(tool.Kind)
            ? await RunReadAsync(tool, args, turn, emit, ct)
            : await ProposeWriteAsync(tool, call, args, turn, ct);
    }

    private async Task<ToolAnswer> RunReadAsync(
        IAgentTool tool, JsonElement args, Turn turn, Func<AgentStreamEvent, CancellationToken, Task> emit, CancellationToken ct)
    {
        var kind = AgentConsentPolicy.KindLabel(tool.Kind);
        await emit(new AgentStreamEvent(AgentStreamEvents.Tool, new AgentToolEventData(tool.Name, kind, "running", null, null)), ct);

        var before = AgentTracker.ForeignChanges(db);
        AgentToolResult result;
        try
        {
            result = await tool.ExecuteAsync(args, turn.Context, ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            // Includes the tool's own timeout: that is a failed read for the model to hear about, not
            // the end of the turn.
            logger.LogError(ex, "Agent read tool {Tool} threw.", tool.Name);
            result = AgentToolResult.Failed($"The tool '{tool.Name}' failed on the server.", $"ابزار «{tool.Name}» در سرور ناموفق بود.");
        }

        // A read must not leave anything to save; whatever it did leave would be written by the next
        // message this loop stores.
        var dropped = AgentTracker.DetachForeignChanges(db, before);
        if (dropped.Count > 0)
            logger.LogWarning("Agent read tool {Tool} left unsaved changes ({Types}); they were dropped.", tool.Name, string.Join(", ", dropped));

        var summaryEn = result.Ok ? result.SummaryEn ?? $"Looked up: {tool.Name}" : result.Error ?? $"{tool.Name} failed";
        var summaryFa = result.Ok ? result.SummaryFa ?? $"بررسی شد: {tool.Name}" : result.ErrorFa ?? $"{tool.Name} ناموفق بود";
        await emit(new AgentStreamEvent(AgentStreamEvents.Tool,
            new AgentToolEventData(tool.Name, kind, result.Ok ? "done" : "failed", summaryEn, summaryFa)), ct);

        return new(AgentJson.ForModel(result.ResultJson), summaryEn, summaryFa);
    }

    private async Task<ToolAnswer> ProposeWriteAsync(
        IAgentTool tool, AgentToolCall call, JsonElement args, Turn turn, CancellationToken ct)
    {
        var before = AgentTracker.ForeignChanges(db);
        AgentValidation validation;
        try
        {
            validation = await tool.ValidateAsync(args, turn.Context, turn.Scope, ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            logger.LogError(ex, "Agent tool {Tool} threw while validating.", tool.Name);
            validation = AgentValidation.Refused("The tool could not check this request on the server.",
                "ابزار نتوانست این درخواست را در سرور بررسی کند.");
        }

        // Validation must change nothing: this loop saves after every tool call, before anybody has
        // consented, so a change a validation left behind would be written by that save.
        if (plans.ValidationLeftChanges(tool.Name, before) is { } leaked)
            validation = AgentValidation.Refused(leaked.En, leaked.Fa);

        // A delete with no phrase to type would run on a click. That is a tool bug, and it fails closed.
        if (validation.Ok && AgentConsentPolicy.NeedsTypedConfirmation(tool.Kind)
                          && string.IsNullOrWhiteSpace(validation.ConfirmationPhrase))
        {
            logger.LogWarning("Destructive agent tool {Tool} validated a call without a confirmation phrase.", tool.Name);
            validation = AgentValidation.Refused("This step cannot be offered: the tool did not say what to confirm.",
                "این مرحله قابل ارائه نیست: ابزار مشخص نکرد چه چیزی باید تأیید شود.");
        }

        if (!validation.Ok)
            return new(AgentJson.ForModel(
                $"Refused: {validation.Error} Nothing was added to the plan; correct the arguments and call it again, or ask the user."));

        if (validation.Creates is not null) turn.Scope.Add(validation.Creates);

        // Index is the step's 0-based position — what the card keys typed confirmations by; people,
        // and the model, read it as "step Index + 1".
        var index = turn.Draft.Count;
        var number = index + 1;
        var titleEn = string.IsNullOrWhiteSpace(validation.TitleEn) ? tool.Name : validation.TitleEn;
        var titleFa = string.IsNullOrWhiteSpace(validation.TitleFa) ? titleEn : validation.TitleFa;

        turn.Draft.Add(new AgentPlanStep(
            index, tool.Name, tool.Kind, args.GetRawText(), call.Id,
            titleEn, titleFa, validation.DetailEn, validation.DetailFa,
            validation.WarningsEn ?? [], validation.WarningsFa ?? [],
            AgentConsentPolicy.NeedsTypedConfirmation(tool.Kind) ? validation.ConfirmationPhrase!.Trim() : null,
            AgentConsentPolicy.IsAutoApproved(tool.Kind, turn.Remembered.Contains(tool.Name))));

        return new(
            AgentJson.ForModel($"Added as step {number} — nothing has run yet; the user will approve it. (Step {number}: {titleEn})"),
            $"Proposed as step {number}: {titleEn}",
            $"پیشنهاد به‌عنوان مرحلهٔ {number}: {titleFa}");
    }

    /// <summary>
    /// Whether an exception is this request being cancelled — the browser went away, so the turn ends.
    /// Any other cancellation (a tool's own timeout, an <c>HttpClient.Timeout</c>) is a tool failure.
    /// </summary>
    private static bool IsCallerCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>
    /// The model's calls with ids that can be answered: a weak model sometimes sends a blank id, or the
    /// same id twice in one reply, and every answer has to name exactly one call or the provider refuses
    /// the next request outright. Such an id is replaced by one of ours, chosen so it can collide with no
    /// id the reply really carries — the provider only ever sees it again in the history this
    /// conversation sends back, where it is consistent.
    /// </summary>
    private static IReadOnlyList<AgentToolCall> NormalizeCalls(IReadOnlyList<AgentToolCall>? calls, int round)
    {
        if (calls is null || calls.Count == 0) return [];

        static bool Usable(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= AgentMessage.MaxToolCallIdLength;

        // First every id that will be kept as sent (the first use of each usable one), so a generated id
        // can be checked against all of them, including ones later in the reply.
        var used = new HashSet<string>(StringComparer.Ordinal);
        var keep = new bool[calls.Count];
        for (var i = 0; i < calls.Count; i++)
            keep[i] = Usable(calls[i].Id) && used.Add(calls[i].Id);

        var normalized = new List<AgentToolCall>(calls.Count);
        for (var i = 0; i < calls.Count; i++)
        {
            if (keep[i])
            {
                normalized.Add(calls[i]);
                continue;
            }

            var id = $"call_r{round}_{i + 1}";
            for (var n = 2; !used.Add(id); n++) id = $"call_r{round}_{i + 1}_{n}";
            normalized.Add(calls[i] with { Id = id });
        }
        return normalized;
    }

    /// <summary>The call's arguments as a JSON object, or null. Blank counts as "no arguments" — many
    /// models send an empty string for a tool that takes none.</summary>
    private static JsonElement? ParseArguments(AgentToolCall call)
    {
        var raw = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- history -------------------------------------------------------------------------------

    /// <summary>
    /// The conversation's tail as chat messages. A window can cut through the middle of a tool exchange,
    /// and a tool message whose call is not in front of it is refused by every chat-completions
    /// provider — so an assistant message is only sent together with an answer to each of its calls,
    /// and an answer only behind its call; anything else in the window is left out.
    /// </summary>
    private async Task<IReadOnlyList<AgentChatMessage>> HistoryAsync(AgentActor actor, Guid conversationId, CancellationToken ct)
    {
        var rows = await db.AgentMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.WorkspaceId == actor.WorkspaceId)
            .OrderByDescending(m => m.Sequence)
            .Take(HistoryMessages)
            .ToListAsync(ct);
        rows.Reverse();
        return Sanitize(rows);
    }

    internal static IReadOnlyList<AgentChatMessage> Sanitize(IReadOnlyList<AgentMessage> rows)
    {
        var history = new List<AgentChatMessage>();
        var i = 0;
        while (i < rows.Count)
        {
            var row = rows[i];
            if (row.Role == AgentMessageRole.User)
            {
                if (!string.IsNullOrWhiteSpace(row.Text)) history.Add(new(AgentRole.User, row.Text));
                i++;
                continue;
            }

            if (row.Role == AgentMessageRole.Tool)
            {
                i++; // an answer with no call in front of it
                continue;
            }

            var calls = AgentJson.Deserialize<List<AgentToolCall>>(row.ToolCallsJson) ?? [];
            if (calls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(row.Text)) history.Add(new(AgentRole.Assistant, row.Text));
                i++;
                continue;
            }

            var answers = new List<AgentMessage>();
            var j = i + 1;
            while (j < rows.Count && rows[j].Role == AgentMessageRole.Tool) answers.Add(rows[j++]);

            var answered = calls.All(c => answers.Any(a => a.ToolCallId == c.Id));
            if (answered)
            {
                history.Add(new(AgentRole.Assistant, row.Text, calls));
                foreach (var c in calls)
                    history.Add(new(AgentRole.Tool, answers.First(a => a.ToolCallId == c.Id).Text ?? "", ToolCallId: c.Id));
            }

            i = j;
        }

        return history;
    }

    // ---- writing -------------------------------------------------------------------------------

    private async Task<AgentMessage> AppendAsync(AgentConversation conversation, AgentMessage message, CancellationToken ct)
    {
        var now = clock.UtcNow;
        message.WorkspaceId = conversation.WorkspaceId;
        message.ConversationId = conversation.Id;
        message.Sequence = await AgentConversationLog.NextSequenceAsync(db, conversation.Id, ct);
        message.CreatedAt = now;
        message.UpdatedAt = now;
        db.AgentMessages.Add(message);
        conversation.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>The conversation's title: its first message on one line, cut short.</summary>
    internal static string TitleFrom(string text)
    {
        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= MaxTitleLength ? oneLine : oneLine[..(MaxTitleLength - 1)].TrimEnd() + "…";
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static AgentStreamEvent Error(string en, string fa) =>
        new(AgentStreamEvents.Error, new AgentErrorData(en, fa));

    /// <summary>Whether this person may read the client's own words — see the class doc.</summary>
    private Task<bool> SeesProviderDetailAsync(AgentActor actor, CancellationToken ct) =>
        capabilities.AllowsAsync(actor.User, Capabilities.PlatformManage, ct);

    private async Task<AgentErrorData> ProviderFailureAsync(AgentActor actor, AgentChatResponse response, CancellationToken ct)
    {
        if (await SeesProviderDetailAsync(actor, ct))
            return new(response.Error ?? "The model did not answer.", response.ErrorFa ?? "مدل پاسخی نداد.");

        logger.LogWarning("The AI provider could not answer for workspace {Workspace}: {Error}", actor.WorkspaceId, response.Error);
        return new(ProviderFailedEn, ProviderFailedFa);
    }

    private async Task<AgentErrorData> UnavailableAsync(AgentActor actor, Assistant.AssistantUnavailable unavailable, CancellationToken ct)
    {
        if (await SeesProviderDetailAsync(actor, ct)) return new(unavailable.Reason, unavailable.ReasonFa);

        logger.LogInformation("The assistant is unavailable for workspace {Workspace}: {Reason}", actor.WorkspaceId, unavailable.Reason);
        return new(UnavailableEn, UnavailableFa);
    }

    private const string ProviderFailedEn = "The AI provider could not answer right now. Try again in a moment.";
    private const string ProviderFailedFa = "ارائه‌دهندهٔ هوش مصنوعی الان نتوانست پاسخ دهد. کمی بعد دوباره امتحان کنید.";
    private const string UnavailableEn = "The assistant is not available right now. The platform's owner can check its settings.";
    private const string UnavailableFa = "دستیار الان در دسترس نیست. مالک پلتفرم می‌تواند تنظیمات آن را بررسی کند.";

    private const string NotEntitledEn = "The assistant is not enabled for this workspace.";
    private const string NotEntitledFa = "دستیار برای این فضای کاری فعال نیست.";
    private const string CapReachedEn = "This workspace has used all of today's assistant requests. The limit resets at 00:00 UTC.";
    private const string CapReachedFa = "این فضای کاری همهٔ درخواست‌های امروز دستیار را مصرف کرده است. سقف در ساعت ۰۰:۰۰ UTC از نو شروع می‌شود.";
}
