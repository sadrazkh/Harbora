using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Logging;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Logging;

/// <summary>
/// <see cref="ILogIngestionEngine"/>'s only implementation. See that interface for who calls this and
/// why, and <see cref="Harbora.Domain.Logging.AppLogLine"/> for what it writes.
/// </summary>
public sealed class LogIngestionEngine(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    IOptions<LogIngestionOptions> options,
    ISystemClock clock,
    ILogger<LogIngestionEngine> logger) : ILogIngestionEngine
{
    public Task<LogIngestionOutcome> IngestAsync(Guid appId, CancellationToken ct) =>
        IngestCoreAsync(appId, null, ct);

    public Task<LogIngestionOutcome> IngestContainerAsync(Guid appId, string containerId, CancellationToken ct) =>
        IngestCoreAsync(appId, containerId, ct);

    /// <param name="onlyContainerId">
    /// When given, the container to read instead of resolving the app's current one — see
    /// <see cref="ILogIngestionEngine.IngestContainerAsync"/> for why a cutover cannot ask "which
    /// container is the app's" and get a useful answer.
    /// </param>
    private async Task<LogIngestionOutcome> IngestCoreAsync(
        Guid appId, string? onlyContainerId, CancellationToken ct)
    {
        // Unfiltered and sessionless, the same reasoning AppOperationsService.ResolveAsync gives for
        // its own read: both callers of this method — the periodic tick and a pre-removal flush —
        // have no session, or one bound to a workspace other than the app's.
        var app = await db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null || app.LogRetentionDays <= 0)
            return new LogIngestionOutcome(LogIngestionStatus.Disabled, 0, null);

        IDockerEngine docker;
        try
        {
            docker = await engineFactory.ResolveAsync(app.ServerId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Log ingestion could not reach the server engine for app {AppId}.", appId);
            return new LogIngestionOutcome(LogIngestionStatus.EngineUnreachable, 0, ex.Message);
        }

        var containerId = onlyContainerId
            ?? await FindContainerIdAsync(docker, app.WorkspaceId, app.Slug, ct);
        if (containerId is null)
            return new LogIngestionOutcome(LogIngestionStatus.NoContainer, 0, "No container is running for this app.");

        var opt = options.Value;
        var now = clock.UtcNow;

        // The cursor: the newest line already stored for THIS container specifically — see
        // AppLogLine.ContainerId's own doc for why a different container id (redeploy, a
        // crash-restart that replaced rather than restarted in place) must start fresh at its own
        // configured window rather than inherit a cursor that could sit ahead of its own earliest
        // lines and skip them outright.
        var cursor = await db.AppLogLines.IgnoreQueryFilters()
            .Where(l => l.AppId == appId && l.ContainerId == containerId)
            .OrderByDescending(l => l.Timestamp)
            .Select(l => (DateTimeOffset?)l.Timestamp)
            .FirstOrDefaultAsync(ct);

        var since = cursor ?? now.AddDays(-Math.Min(app.LogRetentionDays, opt.MaxRetentionDays));

        IReadOnlyList<TimedLogLine> fetched;
        try
        {
            fetched = await docker.GetLogsSinceAsync(containerId, since, opt.MaxLinesPerIngest, ct);
        }
        catch (NotSupportedException ex)
        {
            // This app's host cannot attach real per-line timestamps at all — not merely "nothing new
            // right now". Logged once per call rather than suppressed: an operator who turned retention
            // on for an app whose host can never honor it needs to be told why nothing is accumulating,
            // not left assuming ingestion is simply idle.
            logger.LogWarning(
                "App {AppId}'s host cannot attach real timestamps to its log lines, so persisted " +
                "retention cannot ingest for it: {Message}", appId, ex.Message);
            return new LogIngestionOutcome(LogIngestionStatus.Unsupported, 0, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Log ingestion failed for app {AppId}.", appId);
            return new LogIngestionOutcome(LogIngestionStatus.Failed, 0, ex.Message);
        }

        // Strictly newer than the cursor: GetLogsSinceAsync's own contract ("no older than since") is
        // inclusive at the boundary, and the boundary line is exactly what the previous pass already
        // stored — without this a stalled container (nothing new, cursor sits still) would re-ingest
        // its own last line forever.
        var fresh = cursor is { } c ? fetched.Where(l => l.Timestamp > c).ToList() : fetched;

        if (fresh.Count > 0)
        {
            db.AppLogLines.AddRange(fresh.Select(l => new AppLogLine
            {
                Id = Guid.NewGuid(),
                WorkspaceId = app.WorkspaceId,
                AppId = app.Id,
                ContainerId = containerId,
                Timestamp = l.Timestamp,
                Text = l.Text,
                SizeBytes = System.Text.Encoding.UTF8.GetByteCount(l.Text)
            }));
            await db.SaveChangesAsync(ct);
        }

        var trimmed = await LogBudgetEnforcer.EnforcePerAppAsync(db, appId, opt.MaxBytesPerApp, ct);
        await LogBudgetEnforcer.RecomputeBudgetCappedAsync(db, app, now, trimmed, ct);
        await db.SaveChangesAsync(ct);

        return new LogIngestionOutcome(LogIngestionStatus.Ingested, fresh.Count, null);
    }

    /// <summary>
    /// The same match <c>AppOperationsService.FindContainerIdAsync</c> makes, duplicated rather than
    /// shared: that method is private to a different class, and the match itself — by the app label,
    /// preferring the running container, workspace-exclusive when the slug is — is
    /// <see cref="DeploymentPlanning"/>'s own public contract, so there is exactly one place either
    /// copy could drift from.
    /// </summary>
    private async Task<string?> FindContainerIdAsync(IDockerEngine docker, Guid workspaceId, string slug, CancellationToken ct)
    {
        var containers = await docker.ListContainersAsync(DeploymentPlanning.AppLabel, ct);
        var slugExclusive = !await db.Apps.IgnoreQueryFilters()
            .AnyAsync(a => a.Slug == slug && a.WorkspaceId != workspaceId, ct);
        return DeploymentPlanning.CurrentContainerId(containers, workspaceId, slug, slugExclusive);
    }
}
