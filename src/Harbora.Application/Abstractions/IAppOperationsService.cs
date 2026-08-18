namespace Harbora.Application.Abstractions;

/// <summary>
/// Lifecycle operations for a deployed app (start/stop/restart/delete) and a log snapshot.
/// Resolves the app's server engine, so it works for local and remote nodes alike.
/// </summary>
public interface IAppOperationsService
{
    Task RestartAsync(Guid appId, CancellationToken ct);
    Task StopAsync(Guid appId, CancellationToken ct);
    Task StartAsync(Guid appId, CancellationToken ct);
    /// <summary>Remove the container (+ optionally its volumes), drop its routes, re-apply the proxy, and delete the app.</summary>
    Task DeleteAsync(Guid appId, bool removeVolumes, CancellationToken ct);
    Task<string> GetLogsAsync(Guid appId, int tail, CancellationToken ct);

    /// <summary>
    /// Searches the fetched tail of one or more apps' logs — never a stored history, since none
    /// exists (see <see cref="GetLogsAsync"/>'s doc comment: every call reaches the container fresh).
    ///
    /// <para>
    /// <paramref name="appIds"/> is the caller's to vet, exactly as <see cref="GetLogsAsync"/> already
    /// documents for one app: this resolves each app's server engine unfiltered by workspace, so a
    /// caller spanning several apps — a project- or environment-wide search — must have already
    /// checked that every id in the list belongs to a workspace and project it may see. A failure to
    /// reach one app's engine does not abort the others; it is reported in that app's
    /// <see cref="AppLogCoverage"/> instead.
    /// </para>
    /// </summary>
    Task<LogSearchResult> SearchLogsAsync(
        IReadOnlyList<Guid> appIds, string? text, bool problemsOnly, TimeSpan? window, int maxLinesPerApp,
        CancellationToken ct);
}
