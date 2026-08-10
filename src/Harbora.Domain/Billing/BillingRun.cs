using Harbora.Domain.Common;

namespace Harbora.Domain.Billing;

/// <summary>The durable state of one ended UTC hour's billing pass.</summary>
public enum BillingRunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Incomplete = 3
}

/// <summary>
/// One durable, retryable billing hour. The job queue points at this row rather than encoding a
/// timestamp into a transient timer callback, so a panel restart cannot forget an hour it owed.
/// </summary>
public sealed class BillingRun : BaseEntity
{
    public DateTimeOffset BillingHour { get; set; }
    public BillingRunStatus Status { get; set; } = BillingRunStatus.Queued;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public int WorkspacesCharged { get; set; }
    public int LinesWritten { get; set; }
    public int WorkspacesSuspended { get; set; }
    public string FailureSummary { get; set; } = string.Empty;
}
