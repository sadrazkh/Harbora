namespace Harbora.Web.ViewModels;

/// <param name="Dsn">Non-null only for the one provider somebody clicked to reveal — the same rule
/// <c>EmailProviderViewModel.Password</c> and <c>StorageBucketViewModel.SecretKey</c> follow.</param>
/// <param name="AttachedApps">Apps this provider is currently attached to.</param>
/// <param name="AvailableApps">Apps in this workspace not yet attached to this provider, for the
/// attach form.</param>
public sealed record ErrorTrackingProviderViewModel(
    Guid Id,
    string Name,
    string? Dsn,
    IReadOnlyList<ErrorTrackingProviderAttachedAppViewModel> AttachedApps,
    IReadOnlyList<ErrorTrackingProviderAttachableAppViewModel> AvailableApps);

/// <summary>One app an error-tracking provider could be attached to next (1.8, 2026-09 market-gaps
/// round two).</summary>
public sealed record ErrorTrackingProviderAttachableAppViewModel(Guid Id, string Name);

/// <summary>One app an error-tracking provider is currently attached to (1.8, 2026-09 market-gaps
/// round two).</summary>
public sealed record ErrorTrackingProviderAttachedAppViewModel(Guid AppId, string AppName, bool HasUnpublishedChanges);

public sealed record ErrorTrackingProvidersPageViewModel
{
    public IReadOnlyList<ErrorTrackingProviderViewModel> Providers { get; init; } = [];
    public Guid? RevealedProviderId { get; init; }
}
