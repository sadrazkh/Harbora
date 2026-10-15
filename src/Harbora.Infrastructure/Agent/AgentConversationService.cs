using System.Diagnostics;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Harbora.Domain.Authorization;
using Harbora.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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
    ILogger<AgentConversationService> logger,
    IAgentSecretValues secretValues,
    IAuditLogger audit,
    IHostApplicationLifetime? lifetime = null,
    AgentTurnRegistry? turns = null)
{
    /// <summary>
    /// Where Stop finds the turns being answered. The container hands every request the one singleton; a
    /// service built without one (older tests) gets a registry of its own, which only that instance can stop.
    /// </summary>
    private readonly AgentTurnRegistry _turns = turns ?? new AgentTurnRegistry();

    public const string ConversationDeletedAction = "assistant.conversation_deleted";
    public const string ConversationsDeletedAction = "assistant.conversations_deleted";

    /// <summary>The most model calls one message may cause — reads included.</summary>
    public const int MaxModelCallsPerMessage = 8;

    /// <summary>How much of the conversation is sent back to the model each turn.</summary>
    public const int HistoryMessages = 20;

    /// <summary>The longest message a person may send.</summary>
    public const int MaxMessageLength = 4000;

    /// <summary>How many conversations one page of the pane's list holds.</summary>
    public const int ConversationListSize = 30;

    /// <summary>The longest search the list accepts — a title is at most 60 characters anyway.</summary>
    public const int MaxSearchLength = 100;

    /// <summary>How many messages the conversation view returns (the newest).</summary>
    public const int ConversationViewMessages = 200;

    /// <summary>The most decided plans the conversation view returns (the newest of those it can place).</summary>
    public const int PlanHistorySize = 50;

    /// <summary>How much earlier than the oldest message of the view a plan may be dated and still be read
    /// for it — see <see cref="GetAsync"/>. A plan can only be younger than its lines, so this is slack for
    /// servers whose clocks differ, not part of the rule.</summary>
    private static readonly TimeSpan PlanClockMargin = TimeSpan.FromMinutes(1);

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

    /// <summary>The first page of <see cref="ListAsync(AgentActor, string?, int, CancellationToken)"/>, unsearched.</summary>
    public async Task<IReadOnlyList<AgentConversationSummaryView>> ListAsync(AgentActor actor, CancellationToken ct) =>
        (await ListAsync(actor, q: null, skip: 0, ct)).Value!;

    /// <summary>
    /// One page of this person's conversations in this workspace, most recently active first, at most
    /// <see cref="ConversationListSize"/> rows — a shorter page is the last. A conversation created ahead of
    /// its first message (<see cref="CreateAsync"/>) and never used has no title yet and is left out — it is a
    /// place the pane reserved, not something the person said.
    ///
    /// <para>
    /// <paramref name="q"/>, trimmed, is looked for inside the title, ignoring case: the title is lowered in
    /// the query (<c>ToLower</c>, which Npgsql sends as <c>lower()</c>) and the search before it, the way
    /// <c>AgentResourceResolver</c> matches names. Persian has no case, so for it this is a plain substring
    /// match. Refused — nothing read — when <paramref name="q"/> is
    /// longer than <see cref="MaxSearchLength"/> or <paramref name="skip"/> is negative.
    /// </para>
    /// </summary>
    public async Task<ServiceOutcome<IReadOnlyList<AgentConversationSummaryView>>> ListAsync(
        AgentActor actor, string? q, int skip, CancellationToken ct)
    {
        var term = (q ?? "").Trim();
        if (term.Length > MaxSearchLength)
            return ServiceOutcome<IReadOnlyList<AgentConversationSummaryView>>.Refused(new ServiceRefusal(
                $"A search can be at most {MaxSearchLength} characters.",
                $"جست‌وجو حداکثر می‌تواند {MaxSearchLength} نویسه باشد.", nameof(q)));
        if (skip < 0)
            return ServiceOutcome<IReadOnlyList<AgentConversationSummaryView>>.Refused(new ServiceRefusal(
                "skip must be 0 or more.", "skip باید ۰ یا بیشتر باشد.", nameof(skip)));

        var page = await Page(db.AgentConversations.IgnoreQueryFilters().AsNoTracking(), actor.WorkspaceId, actor.UserId, term, skip)
            .Select(c => new AgentConversationSummaryView(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync(ct);
        return ServiceOutcome<IReadOnlyList<AgentConversationSummaryView>>.Success(page);
    }

    /// <summary>The list's query, on its own so the SQL Npgsql makes of it can be checked without a database.</summary>
    internal static IQueryable<AgentConversation> Page(
        IQueryable<AgentConversation> conversations, Guid workspaceId, Guid userId, string term, int skip)
    {
        var mine = conversations.Where(c => c.WorkspaceId == workspaceId && c.UserId == userId && c.Title != "");
        if (term.Length > 0)
        {
            var lowered = term.ToLowerInvariant();
            mine = mine.Where(c => c.Title.ToLower().Contains(lowered));
        }

        // The id breaks ties, so a page boundary between two conversations touched in the same instant
        // neither repeats one nor drops the other.
        return mine
            .OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id)
            .Skip(skip)
            .Take(ConversationListSize);
    }

    /// <summary>
    /// One conversation as the pane renders it, with the plan still waiting for a decision. Null when it
    /// is not this person's in this workspace. Only what a person should read is returned: their
    /// messages, the model's text, and the tool messages that carry a summary — not the raw replies
    /// the model got to a malformed call.
    ///
    /// <para>
    /// A tool message carries the tool's name for people, its icon group, how long a read took and the
    /// plan it belongs to. The conversation's other plans — decided, executing or expired, whose proposal
    /// is among the messages — come back in <see cref="AgentConversationView.Plans"/> with what they did
    /// and where they are drawn, so a conversation opened later shows what the live one did.
    /// </para>
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

        // Only the plans made since the window began can have a proposal line in it (a plan is made after its
        // lines are written, by the same clock); the margin is for clocks that are not quite the same. The
        // pending plan is added by the query whatever its age.
        var since = rows.Count == 0 ? (DateTimeOffset?)null : rows.Min(r => r.CreatedAt) - PlanClockMargin;
        var storedPlans = await plans.PlansOfAsync(actor, conversationId, since, ct);
        var pending = plans.PendingAmong(storedPlans);
        var proposals = ProposalLines(rows, storedPlans);

        var messages = new List<AgentMessageView>();
        // The visible messages a turn wrote itself — not the lines a plan's execution wrote after it was
        // decided — in order: the places a plan's card can be drawn after.
        var turnMessages = new List<(int Sequence, Guid Id)>();
        foreach (var row in rows)
        {
            AgentMessageView? view = null;
            switch (row.Role)
            {
                case AgentMessageRole.User:
                    view = new(row.Id, "user", row.Text, null, row.CreatedAt);
                    break;
                case AgentMessageRole.Assistant when !string.IsNullOrWhiteSpace(row.Text):
                    view = new(row.Id, "assistant", row.Text, null, row.CreatedAt);
                    break;
                case AgentMessageRole.Tool when row.SummaryEn is not null:
                    var label = row.ToolName is null ? null : AgentToolLabels.For(row.ToolName);
                    view = new(row.Id, "tool", persian ? row.SummaryFa ?? row.SummaryEn : row.SummaryEn,
                        row.ToolName, row.CreatedAt,
                        ToolLabel: persian ? label?.Fa : label?.En,
                        ToolCategory: label?.Category,
                        DurationMs: row.DurationMs,
                        ToolState: row.ToolSucceeded switch { true => "done", false => "failed", null => null },
                        PlanId: row.PlanId ?? (proposals.PlanOfLine.TryGetValue(row.Id, out var proposed) ? proposed.Id : null));
                    break;
            }

            if (view is null) continue;
            messages.Add(view);
            if (row.PlanId is null) turnMessages.Add((row.Sequence, row.Id));
        }

        // A plan with no proposal line in this window was proposed before it began and has nowhere to be drawn.
        var history = storedPlans
            .Where(p => p.Id != pending?.Id && proposals.LastLineOfPlan.ContainsKey(p.Id))
            .OrderBy(p => proposals.LastLineOfPlan[p.Id].Sequence)
            .TakeLast(PlanHistorySize)
            .Select(p => new AgentPlanHistoryView(plans.ViewOf(p), AgentPlanService.ResultOf(p),
                AfterTurnOf(proposals.LastLineOfPlan[p.Id], rows, turnMessages)))
            .ToList();

        return new AgentConversationView(conversation.Id, conversation.Title, messages,
            pending is null ? null : plans.ViewOf(pending),
            InProgress: IsTurnLive(conversation.TurnStartedAt),
            Plans: history);
    }

    /// <summary>
    /// Which plan each proposal line ("Proposed as step 1: …") belongs to. The row carries no plan — it was
    /// written during the turn, before the plan existed — but the plan's steps keep the model's call id and
    /// the tool, and that is what the line was written for.
    ///
    /// <para>
    /// A call id is only unique within one reply: a provider that numbers its calls from zero, or the
    /// <c>call_r1_1</c> that <see cref="NormalizeCalls"/> gives a blank one, repeats in the next turn. So
    /// lines and plans sharing an id and a tool are paired in order, newest to newest — the window drops the
    /// oldest lines, never the newest — and a line with no plan left over is simply not claimed.
    /// </para>
    /// </summary>
    private static ProposalIndex ProposalLines(IReadOnlyList<AgentMessage> rows, IReadOnlyList<AgentPlan> storedPlans)
    {
        // One entry per step, oldest plan first, so a plan whose steps share a key is listed once for each.
        var plansByCall = new Dictionary<(string Call, string Tool), List<AgentPlan>>();
        foreach (var plan in storedPlans)
            foreach (var step in AgentPlanService.StepsOf(plan))
            {
                if (step.ToolCallId is null) continue;
                var key = (step.ToolCallId, step.Tool);
                if (!plansByCall.TryGetValue(key, out var list)) plansByCall[key] = list = [];
                list.Add(plan);
            }

        var lines = rows
            .Where(r => r.Role == AgentMessageRole.Tool && r.PlanId is null && r.SummaryEn is not null
                        && r.ToolCallId is not null && r.ToolName is not null)
            .GroupBy(r => (Call: r.ToolCallId!, Tool: r.ToolName!));

        var index = new ProposalIndex([], []);
        foreach (var group in lines)
        {
            if (!plansByCall.TryGetValue(group.Key, out var candidates)) continue;
            var ofCall = group.ToList();
            for (var i = 1; i <= Math.Min(ofCall.Count, candidates.Count); i++)
            {
                var line = ofCall[^i];
                var plan = candidates[^i];
                index.PlanOfLine[line.Id] = plan;
                if (!index.LastLineOfPlan.TryGetValue(plan.Id, out var last) || line.Sequence > last.Sequence)
                    index.LastLineOfPlan[plan.Id] = (line.Sequence, line.Id);
            }
        }
        return index;
    }

    /// <summary>The proposal lines of a window: the plan each belongs to, and each plan's last one.</summary>
    private sealed record ProposalIndex(
        Dictionary<Guid, AgentPlan> PlanOfLine, Dictionary<Guid, (int Sequence, Guid Id)> LastLineOfPlan);

    /// <summary>
    /// The message a plan's card is drawn after: the last one the proposing turn itself wrote — which
    /// ends where the person's next message begins. The lines a plan's execution wrote come after the
    /// turn's closing words and say what the card says, so they never anchor it. Never empty: the plan's
    /// own last proposal line is one of the turn's messages, and the search starts from it.
    /// </summary>
    private static Guid AfterTurnOf(
        (int Sequence, Guid Id) lastProposalLine, IReadOnlyList<AgentMessage> rows,
        IReadOnlyList<(int Sequence, Guid Id)> turnMessages)
    {
        var nextUser = rows.Where(r => r.Role == AgentMessageRole.User && r.Sequence > lastProposalLine.Sequence)
            .Select(r => (int?)r.Sequence).Min();
        var after = lastProposalLine.Id;
        foreach (var (sequence, id) in turnMessages)
            if (sequence > lastProposalLine.Sequence && (nextUser is null || sequence < nextUser)) after = id;
        return after;
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

    // ---- deleting and stopping -----------------------------------------------------------------

    /// <summary>
    /// Deletes one of this person's conversations, with its messages and plans. Refused while it is busy — a
    /// turn being answered in it, or one of its plans executing — with the check and the delete made one
    /// operation (<see cref="AgentConversationDeletion"/>), so a plan claimed for execution never loses its
    /// conversation underneath it. What the assistant did stays in the audit log, and the day's usage stays
    /// spent; the delete itself is audited by the conversation's id alone — never its title or its text,
    /// which are exactly what the person asked to be rid of.
    /// </summary>
    public async Task<AgentDeleteOutcome> DeleteAsync(AgentActor actor, Guid conversationId, CancellationToken ct)
    {
        var cut = BusyCut();

        // The reason is read before the delete is tried, so a refusal names what was in the way when the
        // person asked — not whatever is true a moment later, once a turn has ended or a plan has finished.
        var why = await BusyReasonAsync(actor, conversationId, cut, ct);
        if (why is not null) return why;

        var deleted = await AgentConversationDeletion.DeleteIdleAsync(db, [conversationId],
            c => c.WorkspaceId == actor.WorkspaceId && c.UserId == actor.UserId, cut, tally: null, ct);
        if (deleted == 0)
            // Idle when read and busy at the delete: something started in between, and the same read names it.
            return await BusyReasonAsync(actor, conversationId, cut, CancellationToken.None)
                   ?? new AgentDeleteOutcome(AgentDeleteStatus.Busy,
                       "This conversation became busy just now — try again in a moment.",
                       "این گفت‌وگو همین الان مشغول شد — کمی بعد دوباره امتحان کنید.");

        await AuditAsync(actor, ConversationDeletedAction, conversationId.ToString(), metadataJson: null);
        return AgentDeleteOutcome.Deleted;
    }

    /// <summary>
    /// Deletes every conversation of this person in this workspace that is not busy; the busy ones are kept
    /// and counted. "Every" is every one the list shows: a conversation the pane reserved and never used has
    /// nothing in it to delete, and it may be the one the pane has open right now. One audit row, with the
    /// counts — written for the batches that were deleted even when a later one fails.
    /// </summary>
    public async Task<AgentDeleteAllView> DeleteAllAsync(AgentActor actor, CancellationToken ct)
    {
        Expression<Func<AgentConversation, bool>> listed =
            c => c.WorkspaceId == actor.WorkspaceId && c.UserId == actor.UserId && c.Title != "";

        var ids = await db.AgentConversations.IgnoreQueryFilters().AsNoTracking().Where(listed).Select(c => c.Id).ToListAsync(ct);
        var tally = new AgentDeletionTally();
        try
        {
            if (ids.Count > 0)
                await AgentConversationDeletion.DeleteIdleAsync(db, ids, listed, BusyCut(), tally, ct);
        }
        catch (Exception ex)
        {
            // The batches before the one that failed are deleted and stay deleted: they are recorded as such,
            // and the failure goes on to the request.
            logger.LogError(ex, "Deleting every assistant conversation stopped part-way; {Deleted} had been deleted.", tally.Deleted);
            try
            {
                await AuditAsync(actor, ConversationsDeletedAction, targetId: null,
                    JsonSerializer.Serialize(new { deleted = tally.Deleted, interrupted = true }));
            }
            catch (Exception auditFailure)
            {
                // The failure that stopped the delete is the one the request must see; this one is only logged
                // (the line above already says how many were deleted).
                logger.LogError(auditFailure, "The audit row for a part-way delete of every assistant conversation could not be written.");
            }
            throw;
        }

        // Those of them still here are the ones that were busy. (One a concurrent delete took is neither.)
        var skipped = tally.Deleted == ids.Count ? 0
            : await db.AgentConversations.IgnoreQueryFilters().CountAsync(c => ids.Contains(c.Id), CancellationToken.None);

        await AuditAsync(actor, ConversationsDeletedAction, targetId: null,
            JsonSerializer.Serialize(new { deleted = tally.Deleted, skipped }));
        return new AgentDeleteAllView(tally.Deleted, skipped);
    }

    /// <summary>
    /// Asks the turn running in this conversation to stop (<see cref="AgentTurnRegistry"/>). Null when the
    /// conversation is not this person's in this workspace; <c>Stopped</c> false when there is nothing left to
    /// stop — the answer has ended, or what remains of it (storing and running its plan) cannot be stopped.
    /// What the turn does on being stopped is <see cref="SendAsync"/>'s.
    /// </summary>
    public async Task<AgentStopView?> StopAsync(AgentActor actor, Guid conversationId, CancellationToken ct)
    {
        if (!await db.AgentConversations.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(c => c.Id == conversationId && c.WorkspaceId == actor.WorkspaceId && c.UserId == actor.UserId, ct))
            return null;

        return new AgentStopView(_turns.Stop(conversationId, actor.WorkspaceId, actor.UserId));
    }

    /// <summary>What counts as busy right now, by this service's turn-mark lifetime.</summary>
    private AgentBusyCut BusyCut() => AgentBusyCut.At(clock.UtcNow, TurnMarkLifetime);

    /// <summary>
    /// Why this conversation cannot be deleted right now, or null when it can: not this person's (or gone), a
    /// live turn, or a plan executing under a live claim — the same cut <see cref="AgentConversationDeletion"/>
    /// applies.
    /// </summary>
    private async Task<AgentDeleteOutcome?> BusyReasonAsync(AgentActor actor, Guid conversationId, AgentBusyCut cut, CancellationToken ct)
    {
        var current = await db.AgentConversations.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == conversationId && c.WorkspaceId == actor.WorkspaceId && c.UserId == actor.UserId)
            .Select(c => new { c.TurnStartedAt })
            .FirstOrDefaultAsync(ct);
        if (current is null) return NoSuchConversation;

        if (current.TurnStartedAt is { } started && started > cut.TurnMarkBefore)
            return new(AgentDeleteStatus.Busy,
                "This conversation is still answering a message — stop it or wait for it to finish, then delete it.",
                "این گفت‌وگو هنوز در حال پاسخ به پیامی است — آن را متوقف کنید یا صبر کنید تمام شود، بعد حذفش کنید.");

        if (await db.AgentPlans.IgnoreQueryFilters().AnyAsync(AgentConversationDeletion.Running(conversationId, cut), ct))
            return new(AgentDeleteStatus.Busy,
                "A plan in this conversation is still running — wait for it to finish, then delete the conversation.",
                "برنامه‌ای در این گفت‌وگو هنوز در حال اجراست — صبر کنید تمام شود، بعد گفت‌وگو را حذف کنید.");

        return null;
    }

    private static readonly AgentDeleteOutcome NoSuchConversation =
        new(AgentDeleteStatus.NotFound, "There is no such conversation.", "چنین گفت‌وگویی وجود ندارد.");

    /// <summary>On no token: what it records has already happened.</summary>
    private Task AuditAsync(AgentActor actor, string action, string? targetId, string? metadataJson) =>
        audit.LogAsync(action, targetType: "agent_conversation", targetId: targetId, ipAddress: actor.IpAddress,
            metadataJson: metadataJson, workspaceId: actor.WorkspaceId, ct: CancellationToken.None);

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
    ///
    /// <para>
    /// The person can end it early with <see cref="StopAsync"/>. That is its own ending, not the deadline's
    /// and not a shutdown's: what the interrupted model call had written so far is saved and sent as the
    /// <c>message</c>, then <c>stopped</c>, then <c>done</c> as always, and the mark is cleared.
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
            {
                // A delete that got there first leaves no row to claim, and that is not a turn in progress.
                if (!await db.AgentConversations.IgnoreQueryFilters().AsNoTracking()
                        .AnyAsync(c => c.Id == id, CancellationToken.None))
                    return new(AgentSendStatus.NotFound, "There is no such conversation.", "چنین گفت‌وگویی وجود ندارد.");

                return new(AgentSendStatus.Busy,
                    "Still answering your previous message — wait for it to finish, then send this one.",
                    "هنوز در حال پاسخ به پیام قبلی شماست — صبر کنید تمام شود، بعد این یکی را بفرستید.");
            }
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

        // From here the turn is taken on: it runs to its end on its own token, whoever is listening —
        // bounded by its deadline, and by the server shutting down (a self-deploy), so a restart ends the
        // turn cleanly instead of leaving the conversation marked as answering until the mark lapses.
        var stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(TurnDeadline);
        var turn = deadline.Token;

        // And by the person: Stop (StopAsync, from a request of its own) reaches the turn through this
        // registration — in this process's memory, which is enough because the panel is one process (see
        // AgentTurnRegistry). The answer itself listens on `answering`; the few steps before `started`
        // stay on `turn`, so a Stop pressed in that instant still leaves the person's message stored and a
        // stream that opens and closes, with the stop taking effect at the answer's first step.
        var stop = _turns.Register(conversation.Id, actor.WorkspaceId, actor.UserId);
        using var stoppable = CancellationTokenSource.CreateLinkedTokenSource(turn, stop.Token);
        var answering = stoppable.Token;

        var listener = new Listener(emit, logger);
        emit = listener.EmitAsync;

        // The try starts the moment the turn is held, so nothing after the claim — not even bringing the
        // tracked copy in line with it — can leave a mark that the finally does not clear.
        try
        {
            if (conversationId is not null)
            {
                await RefreshAfterClaimAsync(conversation);

                // A conversation created ahead of its first message takes its title from that message.
                if (conversation.Title.Length == 0) conversation.Title = TitleFrom(text);
            }

            // The design's gates after the entitlement: can a model be reached, and is any of today's
            // allowance left. Asked before anything else changes, so a message that cannot be answered
            // does not withdraw a card that could still have been approved.
            var refusal = await GateAsync(actor, turn);

            // A message that cannot be answered ends the turn here, whatever happens on the way out: it is
            // released at once, so a Stop from now on answers false and cannot relabel that ending. (A Stop
            // that came before it is the ending instead — see below.)
            var stoppedBeforeRefusal = refusal is not null && _turns.Release(stop);

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
                if (refusal is not null && !stoppedBeforeRefusal)
                    await emit(new AgentStreamEvent(AgentStreamEvents.Error, refusal), turn);
                else if (refusal is null)
                    await RunTurnAsync(actor, conversation, request!.Page, persianInterface, emit, stop, answering);
            }
            catch (Exception ex)
            {
                // First thing on the way out: the turn is released, and what the release reports decides how
                // it ended. Only a Stop that reached the turn before this moment is a stop; one pressed while
                // a deadline, a shutdown or a failure is already ending it finds nothing and answers false,
                // and cannot relabel that ending. (RunTurnAsync released already, before any clean-up of its
                // own; a release is idempotent and keeps reporting what it first found.)
                if (_turns.Release(stop))
                {
                    // The person's own Stop — not a failure, so no error, whatever form the interruption took on
                    // its way out: a cancellation, or the client's "dropped" when the stream it was reading was
                    // cut. What the interrupted call had written was saved and sent as its message; a read cut
                    // off leaves no answer row, and the history builder drops a call without one; steps already
                    // proposed were recorded as not run. `stopped` follows below.
                    if (ex is OperationCanceledException)
                        logger.LogInformation("The assistant turn in conversation {Conversation} was stopped by the person.", conversation.Id);
                    else
                        // Not the stop's own doing, most likely — a real failure that happened to coincide with
                        // it. The person is told they stopped the answer; the failure must still be seen here.
                        logger.LogWarning(ex, "The assistant turn in conversation {Conversation} was stopped by the person, and failed " +
                                              "on the way out ({Error}).", conversation.Id, ex.GetType().Name);
                }
                else if (ex is OperationCanceledException && turn.IsCancellationRequested && stopping.IsCancellationRequested)
                {
                    logger.LogWarning("The assistant turn in conversation {Conversation} was stopped by the server shutting down.",
                        conversation.Id);
                    await emit(Error(
                        "The server is restarting, so this answer was stopped. What was done so far is saved — ask again in a moment.",
                        "سرور در حال راه‌اندازی دوباره است و این پاسخ متوقف شد. آنچه تا اینجا انجام شده ذخیره شده است — کمی بعد دوباره بپرسید."),
                        CancellationToken.None);
                }
                else if (ex is OperationCanceledException && turn.IsCancellationRequested)
                {
                    logger.LogWarning("The assistant turn in conversation {Conversation} reached its {Deadline} deadline.",
                        conversation.Id, TurnDeadline);
                    await emit(Error(
                        $"This answer took longer than {TurnDeadline.TotalMinutes:0} minutes and was stopped. What was done so far is saved.",
                        $"این پاسخ بیش از {TurnDeadline.TotalMinutes:0} دقیقه طول کشید و متوقف شد. آنچه تا اینجا انجام شده ذخیره شده است."),
                        CancellationToken.None);
                }
                else
                {
                    logger.LogError(ex, "The assistant turn failed in conversation {Conversation}.", conversation.Id);
                    await emit(Error("Something went wrong on the server while answering; nothing further was done.",
                        "هنگام پاسخ‌دادن خطایی در سرور رخ داد؛ کار دیگری انجام نشد."), CancellationToken.None);
                }
            }

            // The turn released itself as soon as nothing it had left could be stopped — at the model's last
            // word, before storing its plan (RunTurnAsync), or the moment it began to end any other way — so a
            // Stop after that answered false, and nothing here says "stopped" under an answer, a plan that ran
            // to its end, or a deadline, a shutdown, a refusal or a failure. A Stop that came
            // before is the one reported, exactly when the Stop request answered true. The model calls already
            // made stay counted: each was reserved before it was made.
            if (_turns.Release(stop))
                await emit(new AgentStreamEvent(AgentStreamEvents.Stopped, new AgentErrorData(StoppedEn, StoppedFa)),
                    CancellationToken.None);

            var remaining = await meter.RemainingAsync(actor.WorkspaceId, CancellationToken.None);
            await emit(new AgentStreamEvent(AgentStreamEvents.Done, new AgentDoneData(conversation.Id, remaining)),
                CancellationToken.None);
        }
        finally
        {
            _turns.Release(stop);
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
                if (ex is OperationCanceledException or IOException or ObjectDisposedException)
                    logger.LogInformation("The listener left during an assistant turn ({Error}); the turn carries on and is saved.",
                        ex.GetType().Name);
                else
                    logger.LogWarning(ex, "Writing to the listener of an assistant turn failed unexpectedly; the turn carries on, unheard.");
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
            // The tracked copy is brought in line by RefreshAfterClaimAsync, inside the turn's try, so a
            // reload that fails after the update committed still ends in the finally that clears the mark.
            return claimed > 0;
        }

        if (conversation.TurnStartedAt is { } started && started > staleBefore) return false;
        conversation.TurnStartedAt = stamp;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row was deleted since it was read — a delete won the race. Nothing was claimed.
            db.Entry(conversation).State = EntityState.Detached;
            return false;
        }
        return true;
    }

    /// <summary>
    /// After a relational claim, the tracked copy of the conversation is reloaded, so later saves of it
    /// never write the stale mark back (the reason AgentPlanService.RefreshTrackedAsync gives). The
    /// InMemory claim already wrote through the tracked copy.
    /// </summary>
    private async Task RefreshAfterClaimAsync(AgentConversation conversation)
    {
        if (db.Database.IsRelational()) await db.Entry(conversation).ReloadAsync(CancellationToken.None);
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
        Func<AgentStreamEvent, CancellationToken, Task> emit, AgentTurnHandle stop, CancellationToken ct)
    {
        var context = new AgentToolContext(actor.WorkspaceId, actor.UserId, actor.User, conversation.Id, page);
        var system = AgentSystemPrompt.Build(
            await snapshot.DescribeAsync(context, ct),
            await snapshot.DescribePageAsync(context, ct),
            persianInterface);

        var messages = new List<AgentChatMessage> { new(AgentRole.System, system) };
        // The values this person holds in the lock-button vault. The stored history is scrubbed of them by
        // value only before it goes back to the model — someone who locked a value and also typed it plainly
        // must not send it to the provider — and only by value, so a handle in the same message survives
        // intact for the model to pass on.
        var known = secretValues.LiveValues(actor.WorkspaceId, actor.UserId);
        messages.AddRange((await HistoryAsync(actor, conversation.Id, ct))
            .Select(m => m.Role == AgentRole.User ? m with { Content = AgentSecretScrub.ByValue(m.Content, known) } : m));

        var allowed = await registry.AllowedForAsync(actor.User, ct);
        var specs = allowed.Select(AgentToolRegistry.SpecOf).ToList();
        var turn = new Turn(actor, context, await plans.RememberedToolsAsync(actor, ct), allowed,
            known);

        try
        {
            await AnswerAsync(actor, conversation, turn, messages, specs, emit, stop, ct);
        }
        catch (Exception) when (turn.Draft.Count > 0 && !turn.DraftStored)
        {
            // Released first: the turn is ending, and a Stop pressed while its steps are being recorded must
            // not turn a deadline or a failure into a stop (SendAsync reads what this release found).
            _turns.Release(stop);

            // A turn that ended before its steps became a plan — stopped, out of time, shut down, or broken —
            // had already told the model "added as step N … the user will approve it". Left like that, the
            // next turn would speak of a card that does not exist, so the steps are recorded as not run — and
            // shown so, before the turn's ending (`stopped` or `error`) is.
            await RecordDraftNotRunAsync(actor, conversation, turn, emit);
            throw;
        }
    }

    /// <summary>
    /// The model loop, then the plan the turn proposed. A Stop can interrupt the loop and nothing after it:
    /// the turn is released (<see cref="AgentTurnRegistry.Release"/>) at the model's last word, before that
    /// answer is saved or shown, and again as the loop ends, before the closing error, storing the plan and
    /// running it — and on every other way out (a failed call, the cap, an exception) before anything is
    /// cleaned up. A Stop that reached the turn before such a point is honoured there, by ending the turn as
    /// a stop; one after it finds nothing and answers false.
    /// </summary>
    private async Task AnswerAsync(
        AgentActor actor, AgentConversation conversation, Turn turn, List<AgentChatMessage> messages,
        IReadOnlyList<AgentToolSpec> specs, Func<AgentStreamEvent, CancellationToken, Task> emit, AgentTurnHandle stop,
        CancellationToken ct)
    {
        var finished = false;
        var stoppedEarly = false;

        for (var call = 1; call <= MaxModelCallsPerMessage; call++)
        {
            // Reserved before the call, so the cap holds under concurrent turns — see AgentUsageMeter.
            if (!await meter.TryReserveAsync(actor.WorkspaceId, ct))
            {
                // The cap ends the turn: settled before its error is written, so a Stop pressed over it answers
                // false. One that came first is the ending instead.
                if (_turns.Release(stop)) throw new OperationCanceledException(StoppedEn);
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
            // failed, ran out of time or was stopped half-way, whose partial text is saved and sent as the
            // message (unless it was only whitespace, which nothing stores).
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
                // Released before the partial is saved — the turn is ending, and how is already decided.
                _turns.Release(stop);
                await FinalizePartialAsync(conversation, streamed.ToString(), emit);
                throw;
            }

            if (!response.Ok || response.Message is null)
            {
                // The call failed, so the turn is ending: settled first, before the partial is saved.
                var stoppedFirst = _turns.Release(stop);
                if (streamed.Length > 0) await FinalizePartialAsync(conversation, streamed.ToString(), emit);

                // A call cut off by the turn's own ending — Stop, the deadline, a shutdown — can come back as
                // the client's "dropped" or "unreachable" rather than as a cancellation. Then the ending is
                // what the person is told about, never a provider failure under it.
                ThrowIfCutShort(stoppedFirst, ct);
                await emit(new AgentStreamEvent(AgentStreamEvents.Error, await ProviderFailureAsync(actor, response, ct)), ct);
                stoppedEarly = true;
                break;
            }

            var reply = response.Message;
            var toolCalls = NormalizeCalls(reply.ToolCalls, call);

            // A reply without calls is the model's last word, and nothing the turn does after it can be
            // stopped: it is released before the answer is saved or shown, so a Stop pressed over a complete
            // answer answers false. A Stop that came while the model was writing and that the call did not
            // notice is honoured — what was shown is kept, and the turn ends as stopped.
            if (toolCalls.Count == 0 && _turns.Release(stop))
            {
                if (streamed.Length > 0) await FinalizePartialAsync(conversation, streamed.ToString(), emit);
                throw new OperationCanceledException(StoppedEn);
            }

            // An answer the person watched being written is saved on no token: the turn's own may run out
            // at this very moment, and losing text that was already shown is the one outcome the deltas
            // must never cause. If the save itself fails, the bubble is still closed with the text before
            // the failure goes on to become the turn's error.
            var assistant = new AgentMessage
            {
                Role = AgentMessageRole.Assistant,
                Text = string.IsNullOrWhiteSpace(reply.Content) ? null : reply.Content,
                ToolCallsJson = toolCalls.Count > 0 ? AgentJson.Serialize(toolCalls) : null,
                Model = Truncate(response.Model, AgentMessage.MaxModelLength),
                InputTokens = response.Usage?.InputTokens ?? 0,
                OutputTokens = response.Usage?.OutputTokens ?? 0
            };
            try
            {
                await AppendAsync(conversation, assistant, streamed.Length > 0 ? CancellationToken.None : ct);
            }
            catch (Exception) when (streamed.Length > 0)
            {
                // The save failed, so the turn is ending in that failure: released before the bubble is closed.
                _turns.Release(stop);
                Forget(assistant);
                await emit(new AgentStreamEvent(AgentStreamEvents.Message, new AgentMessageView(
                    assistant.Id, "assistant", assistant.Text, null, assistant.CreatedAt)), CancellationToken.None);
                throw;
            }
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
                    SummaryFa = AgentConversationLog.Bound(answer.SummaryFa),
                    DurationMs = answer.DurationMs,
                    ToolSucceeded = answer.Succeeded
                }, ct);
                messages.Add(new AgentChatMessage(AgentRole.Tool, answer.Content, ToolCallId: toolCall.Id));
            }
        }

        // Everything a Stop can interrupt is behind us; what is left — the closing error, storing the plan and
        // running it — runs to its end. A finished turn was released at its last word already.
        if (_turns.Release(stop)) throw new OperationCanceledException(StoppedEn);

        if (!finished && !stoppedEarly)
            await emit(Error(
                $"I stopped after {MaxModelCallsPerMessage} steps without finishing. Try a smaller request.",
                $"پس از {MaxModelCallsPerMessage} مرحله بدون تمام‌کردن کار متوقف شدم. درخواست کوچک‌تری بدهید."), ct);

        if (turn.Draft.Count == 0) return;

        // The turn's token guards this line and nothing after it: the deadline or a shutdown that came
        // before ends the turn here, and its steps are recorded as not run (RunTurnAsync). Past it the plan is
        // stored, and claimed, on no token — a store or a claim that committed and was then reported
        // cancelled would leave a plan nobody was shown, or one executing with nothing running it.
        ct.ThrowIfCancellationRequested();
        var plan = await plans.CreatePendingAsync(actor, conversation.Id, turn.Draft, CancellationToken.None);
        turn.DraftStored = true;

        // Only a finished turn whose every step is a remembered safe tool runs without a card. A turn the
        // model never finished — the cap, the ceiling, a provider error — may have meant more steps
        // than it got to propose, so its steps always wait for the person.
        if (finished && turn.Draft.All(s => s.AutoApproved))
        {
            // Once claimed, the plan runs to its end on no token (AgentPlanService.RunClaimedAsync) — it is
            // not bound by the turn's deadline or a shutdown, and a Stop no longer reaches it, because a plan
            // abandoned half-way would be worse than one that overruns.
            var result = await plans.ExecuteAutoApprovedAsync(actor, plan, CancellationToken.None);
            await emit(new AgentStreamEvent(AgentStreamEvents.Executed, result), CancellationToken.None);
        }
        else
        {
            await emit(new AgentStreamEvent(AgentStreamEvents.Plan, plans.ViewOf(plan)), CancellationToken.None);
        }
    }

    /// <summary>
    /// What a person already watched being written, when the call that was writing it never finished —
    /// it failed, ran out of time, or the person pressed Stop: saved as the model's message, through the
    /// same <see cref="AppendAsync"/> as every other, so a reload shows what was shown, and sent as the
    /// message event that finalizes the bubble. Runs on no token — the turn's own may be the one that just
    /// ran out. Text that is only whitespace was never anything to read: no row is written and no message
    /// sent — there is no stored id to give one — and the event that ends the turn closes the bubble.
    ///
    /// <para>
    /// Never throws. It runs while another failure is on its way out — the provider's, the deadline's, a
    /// shutdown's — and that failure is the one the person must be told; a save that fails here is logged,
    /// its row dropped from the tracker so nothing retries it, and the bubble is closed all the same.
    /// </para>
    /// </summary>
    private async Task FinalizePartialAsync(
        AgentConversation conversation, string text, Func<AgentStreamEvent, CancellationToken, Task> emit)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var partial = new AgentMessage { Role = AgentMessageRole.Assistant, Text = text };
        try
        {
            await AppendAsync(conversation, partial, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The partial answer in conversation {Conversation} could not be saved.", conversation.Id);
            Forget(partial);
        }

        await emit(new AgentStreamEvent(AgentStreamEvents.Message,
            new AgentMessageView(partial.Id, "assistant", partial.Text, null, partial.CreatedAt)), CancellationToken.None);
    }

    /// <summary>Drops a message whose save failed from the tracker, so no later save retries it.</summary>
    private void Forget(AgentMessage message)
    {
        var entry = db.Entry(message);
        if (entry.State != EntityState.Detached) entry.State = EntityState.Detached;
    }

    /// <summary>Ends the turn as the cancellation it is, when it is one: the person's Stop (one that reached the
    /// turn before it was released), or the turn's deadline or a shutdown (<paramref name="ct"/>).</summary>
    private static void ThrowIfCutShort(bool stoppedFirst, CancellationToken ct)
    {
        if (stoppedFirst) throw new OperationCanceledException(StoppedEn);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The steps a turn proposed before it ended, stored as a plan and expired at once — the same path a card
    /// takes when the person sends a new message instead of deciding (<see cref="AgentPlanService.SupersedePendingAsync"/>).
    /// The conversation then records, beside the "added as step N" answers the model was given, that the plan
    /// expired and nothing ran, and the reopened conversation shows the plan as expired. The live stream says
    /// the same: the plan goes out as a <c>plan</c> event, expired, before the <c>stopped</c> or <c>error</c>
    /// that ends the turn — so the person watching sees the card a reload would show. Runs on no token and
    /// never throws: it runs while the turn's real ending is on its way out.
    /// </summary>
    private async Task RecordDraftNotRunAsync(
        AgentActor actor, AgentConversation conversation, Turn turn, Func<AgentStreamEvent, CancellationToken, Task> emit)
    {
        try
        {
            var plan = await plans.CreatePendingAsync(actor, conversation.Id, turn.Draft, CancellationToken.None);
            turn.DraftStored = true;
            await plans.SupersedePendingAsync(actor, conversation.Id, CancellationToken.None);
            // Only once it is expired: a card offering Run for steps whose turn has ended would be false.
            if (plan.Status == AgentPlanStatus.Expired)
                await emit(new AgentStreamEvent(AgentStreamEvents.Plan, plans.ViewOf(plan)), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The steps proposed in conversation {Conversation} before its turn ended could not be recorded as not run.",
                conversation.Id);
        }
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
        AgentActor actor, AgentToolContext context, IReadOnlySet<string> remembered, IReadOnlyList<IAgentTool> allowed,
        IReadOnlyList<string> known)
    {
        public AgentActor Actor { get; } = actor;
        public AgentToolContext Context { get; } = context;
        public IReadOnlySet<string> Remembered { get; } = remembered;
        public IReadOnlyList<IAgentTool> Allowed { get; } = allowed;

        /// <summary>The values this person holds in the lock-button vault: scrubbed, by value, from everything
        /// the turn sends the model or stores, so a tool that echoed one it applied cannot pass it on.</summary>
        public IReadOnlyList<string> Known { get; } = known;
        public AgentPlanScope Scope { get; } = new();
        public List<AgentPlanStep> Draft { get; } = [];

        /// <summary>Whether <see cref="Draft"/> has become a plan — after which it is the plan's to report.</summary>
        public bool DraftStored { get; set; }
    }

    /// <summary>What goes back to the model for one call, and what the person may be shown instead.</summary>
    /// <param name="DurationMs">How long the tool took, for a read that ran; null for a proposal or a refused call.</param>
    /// <param name="Succeeded">Whether a read that ran worked; null for a proposal or a refused call.</param>
    private sealed record ToolAnswer(
        string Content, string? SummaryEn = null, string? SummaryFa = null, int? DurationMs = null, bool? Succeeded = null);

    /// <summary>A tool answer for the model: redacted by the person's held values and by shape, then cut
    /// to size — <see cref="AgentJson.ForModel"/> with the turn's <see cref="Turn.Known"/>.</summary>
    private static string ForModel(Turn turn, string? text) => AgentJson.ForModel(text, turn.Known);

    /// <summary>A line for the person, scrubbed of held values — by value only, never by shape, so a handle
    /// in it stays a handle. It is stored with the conversation.</summary>
    private static string? Scrub(Turn turn, string? text) =>
        AgentSecretScrub.ByValue(text, turn.Known);

    private async Task<ToolAnswer> AnswerToolCallAsync(
        AgentToolCall call, Turn turn, Func<AgentStreamEvent, CancellationToken, Task> emit, CancellationToken ct)
    {
        var tool = registry.Find(call.Name);
        if (tool is null)
            return new(ForModel(turn,
                $"Unknown tool '{call.Name}'. The tools you can use are: {string.Join(", ", turn.Allowed.Select(t => t.Name))}."));

        if (!await registry.AllowsAsync(turn.Actor.User, tool, ct))
            return new(ForModel(turn,
                $"The user's role does not allow '{tool.Name}' (it needs the '{tool.Capability}' permission). Nothing was done; tell the user they cannot do this."));

        if (ParseArguments(call) is not { } args)
            return new(ForModel(turn,
                $"The arguments for '{tool.Name}' are not a valid JSON object. Call it again with a JSON object that matches its parameters."));

        return AgentConsentPolicy.RunsImmediately(tool.Kind)
            ? await RunReadAsync(tool, args, turn, emit, ct)
            : await ProposeWriteAsync(tool, call, args, turn, ct);
    }

    private async Task<ToolAnswer> RunReadAsync(
        IAgentTool tool, JsonElement args, Turn turn, Func<AgentStreamEvent, CancellationToken, Task> emit, CancellationToken ct)
    {
        var kind = AgentConsentPolicy.KindLabel(tool.Kind);
        // The person reads what the tool is doing, not its identifier — on the running line, the finished
        // one, and every sentence below that names the tool.
        var label = AgentToolLabels.For(tool.Name);
        await emit(new AgentStreamEvent(AgentStreamEvents.Tool, new AgentToolEventData(
            tool.Name, kind, "running", null, null, label.En, label.Fa, label.Category)), ct);

        var before = AgentTracker.ForeignChanges(db);
        AgentToolResult result;
        var started = Stopwatch.GetTimestamp();
        try
        {
            result = await tool.ExecuteAsync(args, turn.Context, ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            // Includes the tool's own timeout: that is a failed read for the model to hear about, not
            // the end of the turn.
            logger.LogError(ex, "Agent read tool {Tool} threw.", tool.Name);
            result = AgentToolResult.Failed($"{label.En} failed on the server.", $"{label.Fa} در سرور ناموفق بود.");
        }
        // Stopped before the clean-up below: the time is the tool's own, whether it answered or threw.
        var durationMs = (int)Math.Min(int.MaxValue, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds));

        // A read must not leave anything to save; whatever it did leave would be written by the next
        // message this loop stores.
        var dropped = AgentTracker.DetachForeignChanges(db, before);
        if (dropped.Count > 0)
            logger.LogWarning("Agent read tool {Tool} left unsaved changes ({Types}); they were dropped.", tool.Name, string.Join(", ", dropped));

        var summaryEn = Scrub(turn, result.Ok ? result.SummaryEn ?? $"Looked up: {label.En}" : result.Error ?? $"{label.En} failed");
        var summaryFa = Scrub(turn, result.Ok ? result.SummaryFa ?? $"بررسی شد: {label.Fa}" : result.ErrorFa ?? $"{label.Fa} ناموفق بود");
        await emit(new AgentStreamEvent(AgentStreamEvents.Tool, new AgentToolEventData(
            tool.Name, kind, result.Ok ? "done" : "failed", summaryEn, summaryFa,
            label.En, label.Fa, label.Category, durationMs)), ct);

        return new(ForModel(turn, result.ResultJson), summaryEn, summaryFa, durationMs, result.Ok);
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
            return new(ForModel(turn,
                $"Refused: {validation.Error} Nothing was added to the plan; correct the arguments and call it again, or ask the user."));

        if (validation.Creates is not null) turn.Scope.Add(validation.Creates);

        // Index is the step's 0-based position — what the card keys typed confirmations by; people,
        // and the model, read it as "step Index + 1".
        var index = turn.Draft.Count;
        var number = index + 1;
        // The card's words pass the held-value scrub too: a tool is not meant to put a value it checked
        // into its title or warnings, and if one does, the card, the stored plan and the summary must
        // still not show it. By value only, like every line a person reads.
        var titleEn = Scrub(turn, string.IsNullOrWhiteSpace(validation.TitleEn) ? tool.Name : validation.TitleEn)!;
        var titleFa = Scrub(turn, string.IsNullOrWhiteSpace(validation.TitleFa) ? titleEn : validation.TitleFa)!;

        turn.Draft.Add(new AgentPlanStep(
            index, tool.Name, tool.Kind, args.GetRawText(), call.Id,
            titleEn, titleFa, Scrub(turn, validation.DetailEn), Scrub(turn, validation.DetailFa),
            (validation.WarningsEn ?? []).Select(w => Scrub(turn, w)!).ToList(),
            (validation.WarningsFa ?? []).Select(w => Scrub(turn, w)!).ToList(),
            AgentConsentPolicy.NeedsTypedConfirmation(tool.Kind) ? validation.ConfirmationPhrase!.Trim() : null,
            AgentConsentPolicy.IsAutoApproved(tool.Kind, turn.Remembered.Contains(tool.Name))));

        return new(
            ForModel(turn, $"Added as step {number} — nothing has run yet; the user will approve it. (Step {number}: {titleEn})"),
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

    // The `stopped` event's data: { messageEn, messageFa }, the same shape as an error's.
    private const string StoppedEn = "You stopped this answer.";
    private const string StoppedFa = "این پاسخ را متوقف کردید.";

    private const string NotEntitledEn = "The assistant is not enabled for this workspace.";
    private const string NotEntitledFa = "دستیار برای این فضای کاری فعال نیست.";
    private const string CapReachedEn = "This workspace has used all of today's assistant requests. The limit resets at 00:00 UTC.";
    private const string CapReachedFa = "این فضای کاری همهٔ درخواست‌های امروز دستیار را مصرف کرده است. سقف در ساعت ۰۰:۰۰ UTC از نو شروع می‌شود.";
}
