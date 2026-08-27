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

    /// <summary>
    /// Turn maintenance mode on or off (P5, 2026-08-20 platform-options plan). The app's containers
    /// are never touched — only the router for its hosts changes, through the exact same
    /// <c>IProxyEngine.ApplyAllAsync</c> path <c>RoutesController.Save</c> uses. Stopping the app is a
    /// separate, pre-existing action.
    ///
    /// <para>
    /// <b>Honesty on failure:</b> <c>App.MaintenanceMode</c> is written only after the apply is known
    /// to have succeeded. When it has not, every route this touched is put back exactly as it found
    /// it and the platform's proxy config is re-published from the reverted rows — the same
    /// "revert, then re-apply" shape <c>DeploymentPipeline.WireProxyAsync</c>'s own failure path
    /// uses — so a failed toggle leaves both the flag and the live routing exactly where they were.
    /// </para>
    /// </summary>
    /// <param name="messageEn">Optional message shown on the maintenance page (English/default).
    /// Ignored when <paramref name="enabled"/> is false.</param>
    /// <param name="messageFa">The Persian counterpart, independently optional. Ignored when
    /// <paramref name="enabled"/> is false.</param>
    Task<MaintenanceToggleResult> SetMaintenanceModeAsync(
        Guid appId, bool enabled, string? messageEn, string? messageFa, CancellationToken ct);

    /// <summary>
    /// Turn a per-app request-rate limit on, off, or reconfigure it while it is already on (C3,
    /// 2026-08-27 what's-left plan) — applied as Traefik middleware on every route this app owns,
    /// through the exact same <c>IProxyEngine.ApplyAllAsync</c> path <c>RoutesController.Save</c> and
    /// <see cref="SetMaintenanceModeAsync"/> both use. There is exactly one proxy-config writer in
    /// this codebase; this does not add a second one.
    ///
    /// <para>
    /// <b>Honesty on failure:</b> mirrors <see cref="SetMaintenanceModeAsync"/>'s own rule exactly.
    /// <c>App.RateLimitEnabled</c>/<c>Average</c>/<c>Burst</c> are written only after the apply is
    /// known to have succeeded. When it has not, every route this touched is put back exactly as it
    /// found it and the platform's proxy config is re-published from the reverted rows — the same
    /// "revert, then re-apply" shape <c>DeploymentPipeline.WireProxyAsync</c>'s own failure path uses
    /// — so a failed toggle leaves both the flag and the live routing exactly where they were. A panel
    /// reading "rate limiting: enabled" over a router nobody updated is the one outcome this exists to
    /// rule out.
    /// </para>
    /// </summary>
    /// <param name="average">Requests allowed per minute from one client. Ignored (but still validated
    /// against <see cref="Harbora.Domain.Apps.AppRateLimitPolicy"/>'s bounds) when
    /// <paramref name="enabled"/> is false.</param>
    /// <param name="burst">Extra requests allowed to arrive at once, above the steady per-minute rate.
    /// Same validation rule as <paramref name="average"/>.</param>
    Task<RateLimitToggleResult> SetRateLimitAsync(
        Guid appId, bool enabled, int average, int burst, CancellationToken ct);
}

/// <summary>
/// What happened when the per-app rate limit was toggled or reconfigured. Mirrors
/// <see cref="MaintenanceToggleResult"/>'s own honesty shape for the same reason: a caller that only
/// checks "did it throw" is exactly how a failed apply once read as a success.
/// </summary>
public sealed record RateLimitToggleResult(bool Success, string? Error)
{
    public static readonly RateLimitToggleResult Ok = new(true, null);
    public static RateLimitToggleResult Failed(string error) => new(false, error);
}

/// <summary>
/// What happened when maintenance mode was toggled. Mirrors <see cref="ProxyApplyResult"/>'s own
/// honesty shape — a caller that only checks "did it throw" is exactly how a failed apply once read
/// as a success.
/// </summary>
public sealed record MaintenanceToggleResult(bool Success, string? Error)
{
    public static readonly MaintenanceToggleResult Ok = new(true, null);
    public static MaintenanceToggleResult Failed(string error) => new(false, error);
}
