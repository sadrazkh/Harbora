namespace Harbora.Application.Abstractions;

/// <summary>What one ingest pass for one app did — never thrown for an ordinary reason, so a
/// best-effort flush right before a container is removed can never be the thing that fails a
/// deploy or a delete. See <see cref="ILogIngestionEngine.IngestAsync"/>.</summary>
public enum LogIngestionStatus
{
    /// <summary><c>App.LogRetentionDays</c> is 0 — nothing is written for an app that never opted in.</summary>
    Disabled,
    /// <summary>New lines (zero or more) were pulled and stored.</summary>
    Ingested,
    /// <summary>No container is currently running for this app.</summary>
    NoContainer,
    /// <summary>This app's host cannot attach real per-line timestamps
    /// (<see cref="IDockerEngine.GetLogsSinceAsync"/> throws <see cref="NotSupportedException"/>), so
    /// nothing can be told apart from what was already stored — persisted retention is unavailable
    /// for this app, not merely empty.</summary>
    Unsupported,
    /// <summary>The app's server engine could not be reached.</summary>
    EngineUnreachable,
    /// <summary>Something else went wrong; <see cref="LogIngestionOutcome.Detail"/> says what.</summary>
    Failed
}

/// <param name="Status">What happened.</param>
/// <param name="LinesIngested">How many new rows were written — 0 is a normal, honest answer (nothing
/// new since the last pass), not a stand-in for "did not run".</param>
/// <param name="Detail">Why, when <paramref name="Status"/> is not <see cref="LogIngestionStatus.Ingested"/>.</param>
public sealed record LogIngestionOutcome(LogIngestionStatus Status, int LinesIngested, string? Detail);

/// <summary>
/// Pulls whatever is new since the last pass for one app's currently-running container and persists
/// it (2.2, 2026-09 log-retention plan) — the seam that gives <c>LogsController</c>'s search a history
/// that survives the container, where before there was only ever a fetched tail.
///
/// <para>
/// One method, called from two places for two different reasons. <c>LogIngestionHostedService</c>
/// calls it on a timer for every app with retention configured — the ordinary path, and the one that
/// answers "why did it crash" for an in-place restart, since Docker's own <c>unless-stopped</c> policy
/// keeps a crashed container's log buffer alive under the same id until something removes it (see
/// <c>DockerEngine</c>'s <c>RestartPolicy</c>). <c>AppOperationsService.DeleteAsync</c> and
/// <c>DeploymentPipeline.RetireOldContainersAsync</c> call it once more, best-effort, in the instant
/// before a container is actually removed — the one moment a crash's last lines really are about to be
/// destroyed, because removal (not a crash-restart) is what makes <c>docker logs</c> stop answering for
/// that id at all.
/// </para>
/// </summary>
public interface ILogIngestionEngine
{
    Task<LogIngestionOutcome> IngestAsync(Guid appId, CancellationToken ct);
}
