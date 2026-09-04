using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Logging;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Logging;

/// <summary>
/// Enforces the two disk budgets <see cref="LogIngestionOptions"/> sets — a cap per app and a cap
/// across every app and workspace — by dropping the oldest lines first, and keeps
/// <see cref="App.LogRetentionBudgetCapped"/> an honest, fully-recomputed answer to "is that budget
/// the reason this app's history does not reach as far back as it should" (2.2, 2026-09 log-retention
/// plan).
///
/// <para>
/// <b>Two caps, two reasons.</b> The per-app cap stops one verbose app from starving every other
/// retention-enabled app of its own share of the shared budget below it. The global cap is the actual
/// backstop against "disk is finite and this is the feature most likely to fill it" — many apps each
/// comfortably under their own cap can still add up to a full disk, and only a cap that looks across
/// all of them can catch that.
/// </para>
/// <para>
/// <b>Deletes only, never reads for a decision elsewhere.</b> Like <c>RetentionRule</c> and
/// <c>RetentionCalculator</c>, nothing here has an opinion about correctness beyond "which rows are
/// still under budget" — the honesty work is entirely in
/// <see cref="RecomputeBudgetCappedAsync"/>, kept deliberately separate from the trim itself so a
/// caller can recompute the signal for an app the global trim touched without re-running that trim.
/// </para>
/// <para>
/// <b>Bounded, not exhaustive, per call.</b> Each trim examines at most
/// <see cref="MaxRowsExaminedPerPass"/> of the oldest rows in one pass. An install so far over budget
/// that one pass cannot fully recover needs another pass — the ingestion loop provides one every
/// <see cref="LogIngestionOptions.PollInterval"/> — rather than a single call blocking on an
/// unbounded scan. See this sub-project's report for the honest limits of this approach at real
/// volume.
/// </para>
/// </summary>
public static class LogBudgetEnforcer
{
    private const int MaxRowsExaminedPerPass = 20_000;

    /// <summary>
    /// Trims <paramref name="appId"/>'s own rows down to <paramref name="maxBytesPerApp"/>, oldest
    /// first. Returns whether anything was deleted, purely for logging — the persisted signal an
    /// operator actually sees is <see cref="RecomputeBudgetCappedAsync"/>'s, called separately so the
    /// same recompute also covers apps this method never touched (the global trim's own victims).
    /// </summary>
    public static async Task<bool> EnforcePerAppAsync(
        HarboraDbContext db, Guid appId, long maxBytesPerApp, CancellationToken ct)
    {
        var total = await db.AppLogLines.IgnoreQueryFilters()
            .Where(l => l.AppId == appId)
            .SumAsync(l => (long)l.SizeBytes, ct);

        if (total <= maxBytesPerApp) return false;

        return await TrimOldestAsync(
            db,
            db.AppLogLines.IgnoreQueryFilters().Where(l => l.AppId == appId),
            total - maxBytesPerApp, ct) > 0;
    }

    /// <summary>
    /// Trims across every app and workspace at once, oldest line first regardless of which app wrote
    /// it — the requirement's own words ("an overall cap, with the oldest dropped first"). Returns the
    /// set of apps that lost at least one row, so the caller can refresh their
    /// <see cref="App.LogRetentionBudgetCapped"/> immediately rather than waiting for that app's own
    /// next ingest tick.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> EnforceGlobalAsync(
        HarboraDbContext db, long maxBytesTotal, CancellationToken ct)
    {
        var total = await db.AppLogLines.IgnoreQueryFilters().SumAsync(l => (long)l.SizeBytes, ct);
        if (total <= maxBytesTotal) return new HashSet<Guid>();

        var excess = total - maxBytesTotal;
        var candidates = await db.AppLogLines.IgnoreQueryFilters()
            .OrderBy(l => l.Timestamp)
            .Select(l => new { l.Id, l.AppId, l.SizeBytes })
            .Take(MaxRowsExaminedPerPass)
            .ToListAsync(ct);

        var toDelete = new List<Guid>();
        var touched = new HashSet<Guid>();
        long freed = 0;
        foreach (var c in candidates)
        {
            if (freed >= excess) break;
            toDelete.Add(c.Id);
            touched.Add(c.AppId);
            freed += c.SizeBytes;
        }

        if (toDelete.Count > 0)
            await DeleteByIdAsync(db, toDelete, ct);

        return touched;
    }

    /// <summary>
    /// The oldest rows matching <paramref name="scope"/>, deleted until at least
    /// <paramref name="bytesToFree"/> worth of <see cref="AppLogLine.SizeBytes"/> is gone (or
    /// <see cref="MaxRowsExaminedPerPass"/> rows have been examined). Returns how many rows were
    /// actually removed.
    /// </summary>
    private static async Task<int> TrimOldestAsync(
        HarboraDbContext db, IQueryable<AppLogLine> scope, long bytesToFree, CancellationToken ct)
    {
        var candidates = await scope
            .OrderBy(l => l.Timestamp)
            .Select(l => new { l.Id, l.SizeBytes })
            .Take(MaxRowsExaminedPerPass)
            .ToListAsync(ct);

        var toDelete = new List<Guid>();
        long freed = 0;
        foreach (var c in candidates)
        {
            if (freed >= bytesToFree) break;
            toDelete.Add(c.Id);
            freed += c.SizeBytes;
        }

        if (toDelete.Count == 0) return 0;

        await DeleteByIdAsync(db, toDelete, ct);
        return toDelete.Count;
    }

    /// <summary>
    /// The same relational/in-memory split <c>DataRetentionSweeper.DeleteAsync</c> uses and explains:
    /// <c>ExecuteDeleteAsync</c> is the one shape that can bound a table this large without ever
    /// loading a row's own text, and the tests' <c>InMemory</c> provider does not implement it.
    /// </summary>
    private static async Task DeleteByIdAsync(HarboraDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var doomed = db.AppLogLines.IgnoreQueryFilters().Where(l => ids.Contains(l.Id));

        if (db.Database.IsRelational())
        {
            await doomed.ExecuteDeleteAsync(ct);
            return;
        }

        var rows = await doomed.ToListAsync(ct);
        db.AppLogLines.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A little slack past the exact expected boundary, so an ordinary ingestion tick's own latency —
    /// never instantaneous — does not itself read as budget capping. Twice the poll interval is
    /// generous next to how far a real budget shortfall actually reaches.
    /// </summary>
    private static readonly TimeSpan Slack = LogIngestionOptions.PollInterval * 2;

    /// <summary>
    /// Overwrites <see cref="App.LogRetentionBudgetCapped"/> with the honest current answer: does the
    /// oldest row this app actually has predate what it should have, given how long retention has been
    /// on for it and what it is configured to keep.
    ///
    /// <para>
    /// <b>The comparison.</b> <c>expectedEarliest</c> is the later of <c>now − LogRetentionDays</c>
    /// (the configured window) and <c>App.LogRetentionEnabledAt</c> (retention cannot have kept rows
    /// from before it was turned on). If the oldest row actually on hand is newer than that — past a
    /// small <see cref="Slack"/> — something that should still be here was removed early, and that can
    /// only have been the budget: age-based retention in <c>DataRetentionSweeper</c> only ever removes
    /// rows already past the same cutoff. No rows at all is never "capped": an app with nothing
    /// persisted yet has nothing budget could have cut short either.
    /// </para>
    /// </summary>
    public static async Task RecomputeBudgetCappedAsync(
        HarboraDbContext db, App app, DateTimeOffset now, CancellationToken ct)
    {
        if (app.LogRetentionDays <= 0)
        {
            app.LogRetentionBudgetCapped = false;
            return;
        }

        var earliest = await db.AppLogLines.IgnoreQueryFilters()
            .Where(l => l.AppId == app.Id)
            .OrderBy(l => l.Timestamp)
            .Select(l => (DateTimeOffset?)l.Timestamp)
            .FirstOrDefaultAsync(ct);

        if (earliest is null)
        {
            app.LogRetentionBudgetCapped = false;
            return;
        }

        var configuredCutoff = now.AddDays(-app.LogRetentionDays);
        var expectedEarliest = app.LogRetentionEnabledAt is { } enabledAt && enabledAt > configuredCutoff
            ? enabledAt
            : configuredCutoff;

        app.LogRetentionBudgetCapped = earliest.Value > expectedEarliest + Slack;
    }
}
