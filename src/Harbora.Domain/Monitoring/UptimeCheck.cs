namespace Harbora.Domain.Monitoring;

/// <summary>
/// What the last probe of one <see cref="UptimeCheck"/> found. Deliberately three members, not two —
/// see the type's own doc for why "could not run" must never collapse into either neighbour.
/// </summary>
public enum UptimeCheckOutcome
{
    /// <summary>The probe ran and the target answered what it was configured to expect.</summary>
    Up = 0,

    /// <summary>
    /// The probe ran and the target did not answer what it was configured to expect — wrong status,
    /// a body that did not contain the required text, a refused connection, or a timeout. All four are
    /// the same fact from a visitor's chair: whatever they asked for did not come back in time or in
    /// the shape promised.
    /// </summary>
    Down = 1,

    /// <summary>
    /// The check itself never got to ask the question — the app has no public domain to probe, or the
    /// probe threw for a reason that says nothing about whether the target is up (a bug in the checker,
    /// a DNS resolver error distinct from a real connection attempt). 2.1's own honesty rule: this is
    /// never rendered as <see cref="Up"/> (that would be a green dot for a probe that never fired) and
    /// never as <see cref="Down"/> either (that would blame the target for a question nobody managed to
    /// ask it) — it is stored and shown as its own, third thing.
    /// </summary>
    CouldNotRun = 2
}

/// <summary>
/// Outside-in configuration for one app: hit this path on an interval, expect this status (and
/// optionally this text in the body), from the panel. 2.1 (2026-09 market-gaps round two) —
/// <c>HealthDiagnosis</c> is the only HTTP probe that existed before this, and it only ever runs once,
/// during a deploy; nothing afterwards ever asks the app whether it is still answering.
///
/// <para>
/// One row per app (unique <see cref="AppId"/>) rather than a list — an app either has an outside-in
/// check or it does not, the same one-config-per-subject shape <c>MaintenanceMode</c> and
/// <c>RateLimitEnabled</c> already use directly on <c>App</c> itself; this is not on <c>App</c> only
/// because it is optional and owns its own history table, unlike a plain flag.
/// </para>
///
/// <para>
/// <b>Runs from the panel, not from each node.</b> <c>CertificateWatcher</c> already performs a real
/// outside-in TLS handshake against every app domain from this same process, so adding an HTTP GET to
/// the same trust boundary is not a new one. <b>What that choice cannot detect:</b> a network path that
/// is broken only between a visitor and the app's node, but happens to be fine between the panel and
/// that node (or the reverse) reads identically to a healthy app — this is one vantage point, not every
/// visitor's. Running a second probe from each node was priced into 2.1 as later work, not dropped
/// silently; the panel-only probe is what ships today, and nothing in this class or its rendering may
/// claim to see more than that one vantage point actually saw.
/// </para>
///
/// <para>
/// The latest outcome is cached here (<see cref="LastOutcome"/>/<see cref="LastCheckedAt"/>/
/// <see cref="LastDetail"/>) the same way <c>Alert.LastAttemptAt</c>/<c>LastError</c> already denormalise
/// a channel's own latest fact onto the row that owns it — the public status page and the app page read
/// this row for "right now" without a join into <see cref="UptimeCheckResult"/>'s full history.
/// </para>
/// </summary>
public class UptimeCheck : Common.BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid AppId { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Path appended to the app's own primary public domain. Defaults to "/" — the same
    /// default <c>App.HealthCheckPath</c> ships with — so a check created without being told anything
    /// still asks a reasonable question.</summary>
    public string Path { get; set; } = "/";

    /// <summary>The HTTP status the probe must see for the check to pass.</summary>
    public int ExpectedStatus { get; set; } = 200;

    /// <summary>Optional substring the response body must contain. Null/blank means the status code
    /// alone decides the check — most apps never need more than that.</summary>
    public string? BodyContains { get; set; }

    /// <summary>How often this app is probed.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>How long the probe waits for an answer before the target counts as not responding —
    /// see <see cref="UptimeCheckOutcome.Down"/>'s own doc: a timeout is a real, reportable failure,
    /// never a hang that stalls the next app's check.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>When this check is next due. Null means "due now" — the state a freshly-created or
    /// freshly-re-enabled check starts in, so it is not left waiting out an interval it never ran
    /// against.</summary>
    public DateTimeOffset? NextCheckAt { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Null only for a check that has never once run — every other combination (including a
    /// disabled check's last-known fact) keeps whatever this last found, the same "leave the fact
    /// alone, flip only the flag" shape <c>Alert.IsEnabled</c> next to <c>Alert.LastError</c> uses.</summary>
    public UptimeCheckOutcome? LastOutcome { get; set; }

    public int? LastHttpStatus { get; set; }
    public long? LastLatencyMs { get; set; }

    /// <summary>Why the last probe passed, failed, or could not run — in words an operator can act on,
    /// never "operation failed". See <see cref="UptimeCheckResult.Detail"/> for the same text kept per
    /// row in the history this row's cache summarises.</summary>
    public string? LastDetail { get; set; }
}
