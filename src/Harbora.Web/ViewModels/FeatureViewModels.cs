using Harbora.Domain.Features;

namespace Harbora.Web.ViewModels;

/// <summary>What the customer is shown instead of the feature, and why.</summary>
public sealed record FeatureLockedViewModel(PlatformFeature Feature, FeatureVerdict Verdict);

/// <summary>One plan's column in the grid, with its decision per feature.</summary>
public sealed record FeaturePlanRow(
    Guid PlanId, string Name, string NameFa, bool IsDefault, IReadOnlyDictionary<string, FeatureState> States)
{
    public string Label(bool isFa) => isFa && !string.IsNullOrWhiteSpace(NameFa) ? NameFa : Name;
}

/// <summary>One workspace-level exception to its plan.</summary>
public sealed record FeatureOverrideRow(
    Guid GrantId, string FeatureKey, Guid WorkspaceId, string WorkspaceName, FeatureState State, string? Note);

public sealed record FeatureWorkspaceOption(Guid Id, string Name, string Slug);

public sealed record FeatureAdminViewModel(
    IReadOnlyList<PlatformFeature> Features,
    IReadOnlyList<FeaturePlanRow> Plans,
    IReadOnlyList<FeatureOverrideRow> Overrides,
    IReadOnlyList<FeatureWorkspaceOption> Workspaces);
