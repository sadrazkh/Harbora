using Harbora.Domain.Common;

namespace Harbora.Domain.Agent;

/// <summary>Where a plan is in its one-way life. Never renumber: the values are stored.</summary>
public enum AgentPlanStatus
{
    /// <summary>Waiting for the person to approve or reject it.</summary>
    Pending = 0,

    /// <summary>Approved and claimed by exactly one execution, which is running its steps.</summary>
    Executing = 1,

    /// <summary>Every step ran and reported success.</summary>
    Executed = 2,

    /// <summary>A step failed (the rest were skipped), or re-validation refused a step and nothing ran.</summary>
    Failed = 3,

    Rejected = 4,

    /// <summary>Not approved within its lifetime, or superseded by a newer message in its conversation.</summary>
    Expired = 5
}

/// <summary>
/// The write steps the model proposed during one turn, waiting for the person's consent — or already
/// decided. Harbora executes an approved plan itself, without the model in the loop, and only once:
/// the move out of <see cref="AgentPlanStatus.Pending"/> is a conditional update, so two approvals
/// racing each other cannot both run it.
/// </summary>
public class AgentPlan : BaseEntity
{
    /// <summary>How long a plan waits. The world it was validated against goes stale quickly.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public AgentConversation? Conversation { get; set; }

    public AgentPlanStatus Status { get; set; } = AgentPlanStatus.Pending;

    /// <summary>The validated steps, in order, as JSON — tool name, arguments and the card's words.</summary>
    public string StepsJson { get; set; } = "[]";

    /// <summary>What each step did, once decided, as JSON. Null while pending.</summary>
    public string? ResultsJson { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When execution finished. Null when nothing ran — including a refused re-validation.</summary>
    public DateTimeOffset? ExecutedAt { get; set; }

    /// <summary>When the person approved or rejected it.</summary>
    public DateTimeOffset? DecidedAt { get; set; }
}
