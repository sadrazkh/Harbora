namespace Harbora.Domain.Monitoring;

/// <summary>
/// One probe attempt of one <see cref="UptimeCheck"/>, kept so the app page and the public status page
/// can show a real history rather than only "right now" — see <see cref="UptimeCheck.LastOutcome"/> for
/// the cached "right now" half of the same fact.
/// </summary>
public class UptimeCheckResult : Common.BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid AppId { get; set; }
    public Guid UptimeCheckId { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    public UptimeCheckOutcome Outcome { get; set; }

    /// <summary>Null when the probe never got an HTTP response at all — a timeout, a refused
    /// connection, or a check that could not run.</summary>
    public int? HttpStatus { get; set; }

    /// <summary>Null under the same conditions as <see cref="HttpStatus"/>.</summary>
    public long? LatencyMs { get; set; }

    /// <summary>Why this outcome is what it is, in words an operator can act on — "expected 200, got
    /// 503", "timed out after 10s", "no public domain configured for this app" — never "operation
    /// failed".</summary>
    public string Detail { get; set; } = string.Empty;
}
