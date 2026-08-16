using Harbora.Domain.Features;

namespace Harbora.Application.Abstractions;

/// <summary>
/// The one place anything asks whether a workspace may use a feature.
///
/// <para>
/// Read by controllers, by the navigation map's filter and by background work, so it must answer
/// with or without a session — a verdict that depended on the caller having signed in would leave
/// the cron scheduler and the event bus deciding that nobody is entitled to anything.
/// </para>
/// </summary>
public interface IFeatureGate
{
    Task<FeatureVerdict> EvaluateAsync(Guid workspaceId, string featureKey, CancellationToken ct);

    /// <summary>
    /// Every feature for one workspace, in one round trip. The sidebar asks about all of them on
    /// every page render; asking one at a time would be one query per feature per request.
    /// </summary>
    Task<IReadOnlyDictionary<string, FeatureVerdict>> EvaluateAllAsync(Guid workspaceId, CancellationToken ct);
}
