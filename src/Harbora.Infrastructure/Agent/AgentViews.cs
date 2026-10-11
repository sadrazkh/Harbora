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

/// <summary>One row of <c>GET /assistant/conversations</c>.</summary>
public sealed record AgentConversationSummaryView(Guid Id, string Title, DateTimeOffset UpdatedAt);

/// <summary>A message as the pane renders it.</summary>
/// <param name="Role">"user", "assistant" or "tool".</param>
/// <param name="ToolName">For "tool" messages: which tool ran.</param>
public sealed record AgentMessageView(
    Guid Id,
    string Role,
    string? Text,
    string? ToolName,
    DateTimeOffset CreatedAt);

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
public sealed record AgentConversationView(
    Guid Id,
    string Title,
    IReadOnlyList<AgentMessageView> Messages,
    AgentPlanView? PendingPlan,
    bool InProgress = false);

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

    /// <summary><c>{ name, kind, state: "running"|"done"|"failed", summaryEn, summaryFa }</c>.</summary>
    public const string Tool = "tool";

    /// <summary>
    /// <c>{ text }</c> — the next piece of the model's text as it is written, appended to the bubble being
    /// typed. Purely for display: the <see cref="Message"/> event that follows carries the whole text and
    /// is the one that counts (it replaces what the deltas built).
    /// </summary>
    public const string Delta = "delta";

    /// <summary>An <see cref="AgentMessageView"/> — the model's text.</summary>
    public const string Message = "message";

    /// <summary>An <see cref="AgentPlanView"/> waiting for approval.</summary>
    public const string Plan = "plan";

    /// <summary>An <see cref="AgentPlanResultView"/> — a plan that ran without a card (all steps were
    /// remembered safe tools).</summary>
    public const string Executed = "executed";

    /// <summary><c>{ messageEn, messageFa }</c>.</summary>
    public const string Error = "error";

    /// <summary><c>{ conversationId, remainingToday }</c> — always last.</summary>
    public const string Done = "done";
}
