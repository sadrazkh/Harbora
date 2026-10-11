using Harbora.Domain.Common;

namespace Harbora.Domain.Agent;

/// <summary>
/// "Don't ask again" for one tool, from one person, in one workspace. Only ever written for a
/// <c>SafeWrite</c> tool — a dangerous or destructive one asks every time whatever the request says.
/// A remembered step still validates, still audits and is still reported; it only skips the card.
/// </summary>
public class AgentToolApproval : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The tool's stable snake_case name.</summary>
    public string ToolName { get; set; } = string.Empty;
}

/// <summary>
/// How many model calls one workspace made on one UTC day — the counter behind the daily cap. One row
/// per workspace and date; every chat-completions call counts, including the ones that call tools.
/// </summary>
public class AgentUsageDay : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>The UTC calendar day. The cap resets at 00:00 UTC for everybody.</summary>
    public DateOnly Day { get; set; }

    public int ModelCalls { get; set; }
}
