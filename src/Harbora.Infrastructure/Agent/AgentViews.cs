namespace Harbora.Infrastructure.Agent;

// The JSON the chat pane reads. Serialized camelCase by ASP.NET's defaults; the Vue island mirrors
// these shapes in TypeScript, so a renamed property here is a broken pane there.

/// <summary><c>GET /assistant/status</c>.</summary>
/// <param name="RemainingToday">Model calls left for this workspace today; null when unlimited.</param>
public sealed record AgentStatusView(
    bool Available,
    string? ReasonEn,
    string? ReasonFa,
    string? Model,
    int? RemainingToday);

/// <summary>
/// One row of <c>GET /assistant/conversations?q=&amp;skip=</c> — at most
/// <c>AgentConversationService.ConversationListSize</c> rows a page, most recently active first; a page
/// shorter than that is the last. <c>q</c> matches inside the title, ignoring case.
/// </summary>
public sealed record AgentConversationSummaryView(Guid Id, string Title, DateTimeOffset UpdatedAt);

/// <summary><c>DELETE /assistant/conversations</c>: how many of this person's conversations in this
/// workspace were deleted, and how many were kept because a turn or a plan was still running in them.</summary>
public sealed record AgentDeleteAllView(int Deleted, int Skipped);

/// <summary><c>POST /assistant/conversations/{id}/stop</c>: whether a turn was running here and has been
/// told to stop. False when there was nothing to stop: the turn had already ended — or it is finishing and
/// can no longer be stopped (from the model's last word on, and while it stores and runs its plan, the turn
/// has let go of its Stop and ends as it would have anyway).</summary>
public sealed record AgentStopView(bool Stopped);

/// <summary>A message as the pane renders it.</summary>
/// <param name="Role">"user", "assistant" or "tool".</param>
/// <param name="ToolName">For "tool" messages: which tool ran.</param>
/// <param name="ToolLabel">For "tool" messages: the tool's name for people, in the panel's language
/// (<see cref="AgentToolLabels"/>) — "Reading the deployment's logs", never <c>get_deployment</c>.</param>
/// <param name="ToolCategory">For "tool" messages: one of <see cref="AgentToolLabels.Categories"/>, for the icon.</param>
/// <param name="DurationMs">For "tool" messages of a read that ran: how long it took. Null otherwise.</param>
/// <param name="PlanId">For messages written by a plan's execution: the plan they report on. The pane
/// hides those lines when it draws that plan's result card, which says the same thing.</param>
/// <param name="ToolState">For "tool" messages: "done" or "failed" when the outcome was recorded — a read
/// that ran, or a step a plan's execution ran or could not run — otherwise null (a proposal, a refused
/// call, a skipped, rejected or expired step, and lines stored before outcomes were kept).</param>
public sealed record AgentMessageView(
    Guid Id,
    string Role,
    string? Text,
    string? ToolName,
    DateTimeOffset CreatedAt,
    string? ToolLabel = null,
    string? ToolCategory = null,
    int? DurationMs = null,
    Guid? PlanId = null,
    string? ToolState = null);

/// <summary>One step of a plan card.</summary>
/// <param name="Kind">"safe", "dangerous" or "destructive".</param>
/// <param name="ConfirmationPhrase">Destructive steps: the text the user must type.</param>
/// <param name="AutoApproved">The user already said "don't ask again" for this tool.</param>
/// <param name="Rememberable">Whether the card may offer "don't ask again" (safe steps only).</param>
public sealed record AgentPlanStepView(
    int Index,
    string Tool,
    string Kind,
    string TitleEn,
    string TitleFa,
    string? DetailEn,
    string? DetailFa,
    IReadOnlyList<string> WarningsEn,
    IReadOnlyList<string> WarningsFa,
    string? ConfirmationPhrase,
    bool AutoApproved,
    bool Rememberable);

/// <param name="Status">"pending", "executing", "executed", "failed", "rejected" or "expired".</param>
public sealed record AgentPlanView(
    Guid Id,
    string Status,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<AgentPlanStepView> Steps);

/// <param name="State">"done", "failed" or "skipped" (a later step after a failure).</param>
public sealed record AgentPlanStepResultView(
    int Index,
    string State,
    string? SummaryEn,
    string? SummaryFa,
    string? ErrorEn,
    string? ErrorFa,
    IReadOnlyList<AgentResourceLink> Links);

public sealed record AgentPlanResultView(
    Guid PlanId,
    string Status,
    IReadOnlyList<AgentPlanStepResultView> Steps);

/// <summary><c>GET /assistant/conversations/{id}</c>.</summary>
/// <param name="InProgress">A turn is still being answered for this conversation — the person reloaded
/// or came back mid-answer. The pane shows the typing indicator and reads the conversation again until
/// this is false; the answer is saved by the server whether or not anybody is still listening.</param>
/// <param name="Plans">Every other plan of this conversation whose proposal is among
/// <paramref name="Messages"/> — executing, executed, failed, rejected or expired — oldest first, so a
/// conversation reopened later still shows what was proposed and what it did. The pending plan is only
/// in <paramref name="PendingPlan"/>, never here as well.</param>
/// <remarks>
/// What a Stop leaves is stored as an ordinary assistant message: the text written so far, with nothing to
/// say it was cut short. So a reopened conversation shows that text, and the steps the turn had proposed as
/// an expired plan (<paramref name="Plans"/>), but not the "You stopped this answer." line the live stream
/// drew from its <see cref="AgentStreamEvents.Stopped"/> event — no column records that a turn was stopped.
/// </remarks>
public sealed record AgentConversationView(
    Guid Id,
    string Title,
    IReadOnlyList<AgentMessageView> Messages,
    AgentPlanView? PendingPlan,
    bool InProgress = false,
    IReadOnlyList<AgentPlanHistoryView>? Plans = null);

/// <summary>A decided (or executing) plan in a stored conversation, and where it goes.</summary>
/// <param name="Result">What it did — null while it is executing, and for a plan that never ran
/// (rejected, expired, or refused when checked again: then <see cref="AgentPlanView.Status"/> says which).</param>
/// <param name="AfterMessageId">The id of the message in <see cref="AgentConversationView.Messages"/> the
/// card is drawn after: the last visible message of the turn that proposed it. Never null from this server: a
/// plan is listed only when one of its proposal lines is among the messages, and that line — or a later
/// message of the same turn — is the anchor. Null would mean "before the first message": the case of a plan
/// with nothing in the view to anchor it, which this server leaves out of the list instead. A client that
/// gets one anyway draws the card first.</param>
public sealed record AgentPlanHistoryView(
    AgentPlanView Plan,
    AgentPlanResultView? Result,
    Guid? AfterMessageId);

/// <summary>Body of <c>POST /assistant/conversations/{id}/messages</c>.</summary>
public sealed record AgentSendMessageRequest(string Text, AgentPageContext? Page);

/// <summary>Body of <c>POST /assistant/plans/{id}/approve</c>.</summary>
/// <param name="Confirmations">Step index → the text the user typed, for destructive steps.</param>
/// <param name="RememberTools">Safe tools the user ticked "don't ask again" for.</param>
public sealed record AgentApproveRequest(
    IReadOnlyDictionary<int, string>? Confirmations,
    IReadOnlyList<string>? RememberTools);

/// <summary>Server-sent event names on the message stream. The data of each is one JSON object.</summary>
public static class AgentStreamEvents
{
    /// <summary><c>{ conversationId }</c> — first event; the id is new when the conversation was.</summary>
    public const string Started = "started";

    /// <summary><c>{ name, kind, state: "running"|"done"|"failed", summaryEn, summaryFa, labelEn, labelFa,
    /// category, durationMs }</c> — see <see cref="AgentToolEventData"/>.</summary>
    public const string Tool = "tool";

    /// <summary>
    /// <c>{ text }</c> — the next piece of the model's text as it is written, appended to the bubble being
    /// typed. Purely for display: the <see cref="Message"/> event that follows carries the whole text and
    /// is the one that counts (it replaces what the deltas built).
    /// </summary>
    public const string Delta = "delta";

    /// <summary>An <see cref="AgentMessageView"/> — the model's text.</summary>
    public const string Message = "message";

    /// <summary>
    /// An <see cref="AgentPlanView"/> waiting for approval — or, when a Stop, the deadline, a shutdown or a
    /// failure ended the turn after it had proposed steps but before they became a card, those steps as a plan
    /// already expired (nothing ran), sent before the <see cref="Stopped"/> or <see cref="Error"/> that ends the
    /// turn: the card a reopened conversation shows for them.
    /// </summary>
    public const string Plan = "plan";

    /// <summary>An <see cref="AgentPlanResultView"/> — a plan that ran without a card (all steps were
    /// remembered safe tools).</summary>
    public const string Executed = "executed";

    /// <summary><c>{ messageEn, messageFa }</c>.</summary>
    public const string Error = "error";

    /// <summary>
    /// <c>{ messageEn, messageFa }</c> — the person pressed Stop (<c>POST …/stop</c>) and the turn ended
    /// there. Not a failure: what was written so far was saved (as a <see cref="Message"/> sent just
    /// before this). Never follows a claimed plan: the turn lets go of its Stop before it stores its plan, so a
    /// Stop after that answers false and the plan runs (or waits) as if none had been pressed. Steps proposed
    /// before the Stop come just before this as an expired <see cref="Plan"/>. <see cref="Done"/> still follows.
    /// </summary>
    public const string Stopped = "stopped";

    /// <summary><c>{ conversationId, remainingToday }</c> — always last.</summary>
    public const string Done = "done";
}
