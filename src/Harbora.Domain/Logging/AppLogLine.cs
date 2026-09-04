namespace Harbora.Domain.Logging;

/// <summary>
/// One persisted line of a running container's own stdout/stderr (2.2, 2026-09 log-retention plan).
///
/// <para>
/// Everything <c>LogsController</c>'s search reaches today is a fetched container tail — see
/// <c>AppOperationsService.GetLogsAsync</c>'s own doc — which is gone the moment the container that
/// wrote it is replaced: every deploy, every crash-restart that removes and recreates rather than
/// restarts in place. An app opts in with <c>App.LogRetentionDays</c> (0 = off, the default: this
/// table costs disk, so nothing is written for an app that never asked); once it has, each row here
/// is one more line the same search can still find after the container is gone.
/// </para>
/// <para>
/// <b>Not append-only forever.</b> Two independent sweeps prune this table: the nightly age-based
/// pass in <c>DataRetentionSweeper</c> (each app's own <c>LogRetentionDays</c> — there is no single
/// shared cutoff the way every other table this sweeper owns has one, see its own remarks), and the
/// disk-budget trim in <c>LogIngestionEngine</c>/<c>LogBudgetEnforcer</c>, which runs far more often
/// because a verbose app can blow through its own byte cap in minutes, long before the next night.
/// </para>
/// </summary>
public sealed class AppLogLine
{
    public Guid Id { get; set; }

    /// <summary>
    /// Carried directly, not resolved through <see cref="AppId"/>, for the same reason every other
    /// sessionless-readable table in this codebase carries its own: the ingestion loop and both
    /// sweeps have no session, and read this with <c>IgnoreQueryFilters()</c> plus an explicit
    /// <c>WorkspaceId ==</c> — never a bare unfiltered scan — so a background pass can never answer
    /// "everyone's rows" when it meant one workspace's.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    public Guid AppId { get; set; }

    /// <summary>
    /// The container that wrote this line — the reason the ingestion cursor
    /// (<c>LogIngestionEngine.IngestAsync</c>) is scoped per container rather than per app. A
    /// replaced container (every deploy, every crash-restart that recreates rather than restarts in
    /// place) is not the same stream as the one before it: reusing a cursor built from the old
    /// container's last timestamp could sit ahead of the new container's own earliest lines and skip
    /// them outright, which is exactly the loss this table exists to prevent.
    /// </summary>
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>
    /// The moment the container produced this line — Docker's own per-line timestamp
    /// (<see cref="Harbora.Application.Abstractions.TimedLogLine.Timestamp"/>), not when ingestion
    /// happened to observe it. A time-windowed search means what it says only if age is measured from
    /// the line's own birth, not from whenever the poller got around to it.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// UTF-8 byte length of <see cref="Text"/>, captured once at ingestion so the budget sweep can
    /// <c>SUM</c> an indexed column instead of loading and re-measuring every row it is deciding
    /// about — the same reason <c>ImageInfo.SizeBytes</c> is stored rather than recomputed.
    /// </summary>
    public int SizeBytes { get; set; }
}
