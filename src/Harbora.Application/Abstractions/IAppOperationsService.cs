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
}
