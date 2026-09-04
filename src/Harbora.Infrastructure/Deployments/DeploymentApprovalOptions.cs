namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// How long a protected-environment deploy waits for a second person before the expiry sweep closes
/// it (5.2, 2026-09 market-gaps round two).
/// </summary>
public sealed class DeploymentApprovalOptions
{
    public const string SectionName = "DeploymentApproval";

    /// <summary>
    /// 24 hours: long enough to cross one working day and a night, short enough that a forgotten
    /// request does not sit for a week pointing at a commit nobody remembers requesting.
    /// </summary>
    public TimeSpan ExpiryWindow { get; set; } = TimeSpan.FromHours(24);
}
