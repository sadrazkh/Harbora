namespace Harbora.Application.Abstractions;

/// <summary>One matching line from one app's log, produced by a search that may span several apps.</summary>
public record LogSearchHit(Guid AppId, string AppName, string Line, DateTimeOffset? Timestamp);

/// <summary>
/// What a search actually did for one app it was asked about — named whether or not it found
/// anything there, so a search that silently reached only part of what it was asked to search never
/// looks the same on screen as one that covered everything and found nothing. That silent gap is
/// this codebase's own defining defect, restated for logs: a result that claims coverage it does not
/// have.
/// </summary>
/// <param name="Reached">Whether a running container answered for this app at all.</param>
/// <param name="UnavailableReason">Why not, when <see cref="Reached"/> is false — shown, not swallowed.</param>
/// <param name="LinesScanned">How many lines of this app's fetched tail the search actually looked at.</param>
/// <param name="TimeWindowRequested">Whether the caller asked for a time window at all.</param>
/// <param name="TimeWindowHonored">
/// Whether this app's host could actually attach real timestamps and restrict to the window —
/// false whenever <see cref="TimeWindowRequested"/> is true but the app's engine cannot honor one
/// (see <see cref="IDockerEngine.GetLogsSinceAsync"/>), in which case <see cref="LinesScanned"/>
/// still reflects a full, un-windowed tail rather than a silently narrower one.
/// </param>
/// <param name="ReachedBackTo">
/// 2.2 (2026-09 log-retention plan). The earliest moment this search actually looked at for this
/// app — the live tail's oldest timestamped line, or the persisted store's oldest row within the
/// window, whichever reaches further back. Null when nothing here carries a real timestamp to
/// report one from (an untimed live tail with no persisted history), which is "not measured", never
/// stood in for by a fabricated "now". This is the field that answers "how far back did this really
/// reach" honestly instead of leaving a search that silently covered the last hour looking the same
/// as one that covered the whole configured retention window.
/// </param>
/// <param name="RetentionEnabled">Whether this app has persisted log retention turned on at all
/// (<c>App.LogRetentionDays &gt; 0</c>) — false means everything here came from the live tail alone,
/// exactly as it always did before 2.2.</param>
/// <param name="BudgetCapped">
/// True when <see cref="RetentionEnabled"/> and the disk budget — not this app's own configured day
/// count — is why the persisted history does not reach as far back as it was asked to keep. Mirrors
/// <c>App.LogRetentionBudgetCapped</c>; see that field's own doc for why this cannot be inferred from
/// <see cref="ReachedBackTo"/> alone.
/// </param>
public record AppLogCoverage(
    Guid AppId,
    string AppName,
    bool Reached,
    string? UnavailableReason,
    int LinesScanned,
    bool TimeWindowRequested,
    bool TimeWindowHonored,
    DateTimeOffset? ReachedBackTo = null,
    bool RetentionEnabled = false,
    bool BudgetCapped = false);

/// <summary>
/// A log search's full answer: every matching line, tagged with the app it came from, and a coverage
/// entry for every app that was asked about — whether or not it matched anything.
/// </summary>
public record LogSearchResult(IReadOnlyList<LogSearchHit> Hits, IReadOnlyList<AppLogCoverage> Coverage);
