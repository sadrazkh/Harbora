using Harbora.Domain.Common;

namespace Harbora.Domain.Apps;

/// <summary>
/// One execution of a scheduled job.
///
/// Kept as history rather than a log line because the questions people ask about a cron job are "did
/// it run?", "did it work?" and "what did it say?" — and none of those can be answered by a service
/// that merely exists. An exit code and the tail of its output are the whole point.
/// </summary>
public class CronRun : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid AppId { get; set; }
    public App? App { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Null while running. 0 is success; anything else is not.</summary>
    public int? ExitCode { get; set; }

    /// <summary>The job's own last output — where the reason for a failure actually lives.</summary>
    public string? Output { get; set; }

    /// <summary>Set when the run could not be started at all, as opposed to running and failing.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// True when someone pressed "run now". Recorded because "why did this run at 14:32 when it is
    /// scheduled for 03:00?" cannot be answered otherwise.
    /// </summary>
    public bool IsManual { get; set; }

    public bool Succeeded => ExitCode == 0;
}
