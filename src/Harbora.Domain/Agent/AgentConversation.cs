using Harbora.Domain.Common;

namespace Harbora.Domain.Agent;

/// <summary>
/// One conversation between a person and the in-panel agent (round six, 2026-10-11 assistant-agent
/// design). It belongs to the person who started it, inside one workspace: a colleague in the same
/// workspace never sees it, because what somebody asked the assistant is closer to their own notes
/// than to shared workspace data.
/// </summary>
public class AgentConversation : BaseEntity
{
    public const int MaxTitleLength = 120;

    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The first thing the person asked, trimmed — what the conversation list shows.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// When the turn being answered right now started; null when none is. Set as a message is taken on
    /// and cleared when its turn ends, so a person who reloads mid-answer is told it is still coming, and
    /// a second message cannot start a second turn over the first. A process that dies mid-turn leaves it
    /// set, so it is only believed while younger than the turn's own deadline.
    /// </summary>
    public DateTimeOffset? TurnStartedAt { get; set; }

    public ICollection<AgentMessage> Messages { get; set; } = new List<AgentMessage>();
}

/// <summary>Who wrote a stored message. The system prompt is rebuilt on every turn and never stored.</summary>
public enum AgentMessageRole
{
    User = 0,
    Assistant = 1,
    Tool = 2
}

/// <summary>
/// One message of a conversation, stored so the next turn has the context: what the person said,
/// what the model answered (including the tool calls it made), and what each tool call returned.
///
/// <para>
/// Carries its own <see cref="WorkspaceId"/> rather than being filtered through its conversation, for
/// the reason <c>Deployment</c> does: a navigation filter over a required key becomes an inner join
/// that hides rows whose parent is momentarily absent.
/// </para>
/// </summary>
public class AgentMessage : BaseEntity
{
    public const int MaxToolNameLength = 64;
    public const int MaxToolCallIdLength = 128;
    public const int MaxModelLength = 128;
    public const int MaxSummaryLength = 500;

    public Guid WorkspaceId { get; set; }
    public Guid ConversationId { get; set; }
    public AgentConversation? Conversation { get; set; }

    /// <summary>
    /// Order within the conversation. A counter rather than <see cref="BaseEntity.CreatedAt"/>: one
    /// turn writes several messages within the same millisecond, and a version-7 Guid is not
    /// monotonic inside one millisecond either.
    /// </summary>
    public int Sequence { get; set; }

    public AgentMessageRole Role { get; set; }

    /// <summary>
    /// The text — the person's words, the model's answer, or what a tool returned to the model (already
    /// truncated and redacted). Null for an assistant message that only called tools.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>For an assistant message: the tool calls it made, as JSON. Never contains a secret
    /// value — arguments are what the model wrote, and the model has never been shown one.</summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>For a tool message: which call it answers.</summary>
    public string? ToolCallId { get; set; }

    /// <summary>For a tool message: which tool ran (or was proposed).</summary>
    public string? ToolName { get; set; }

    /// <summary>For a tool message: what the panel shows a person instead of the raw result.</summary>
    public string? SummaryEn { get; set; }
    public string? SummaryFa { get; set; }

    /// <summary>For a tool message written by plan execution: the plan it reports on.</summary>
    public Guid? PlanId { get; set; }

    /// <summary>For an assistant message: the model that actually answered (fallbacks can differ).</summary>
    public string? Model { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}
