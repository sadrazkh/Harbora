namespace Harbora.Web.ViewModels;

/// <summary>One resource plan an operator can make the default.</summary>
public sealed record SizeChoiceViewModel(string Key, string Name, double CpuCores, long MemoryBytes, long DiskBytes);

/// <summary>One ready-made app an operator can choose to put in front of people.</summary>
public sealed record TemplateChoiceViewModel(
    string Key, string Name, string? NameFa, string Category, string? IconUrl);

/// <summary>
/// The settings that change how the platform behaves for everybody, as opposed to the preferences
/// on /settings that change it for one person.
/// </summary>
public sealed class AdminSettingsViewModel
{
    public IReadOnlyList<TemplateChoiceViewModel> Templates { get; init; } = [];

    /// <summary>Chosen keys, in the order they will appear. Empty means the fallback order.</summary>
    public IReadOnlyList<string> Featured { get; init; } = [];

    public int FeaturedSlots { get; init; }

    /// <summary>Null when no platform default is set, and each person's role decides.</summary>
    public string? DefaultPanelMode { get; init; }
    public string? DefaultCulture { get; init; }

    /// <summary>
    /// Whether the side panels start open for people who have not chosen. Null means the shipped
    /// answer, which is not the same for both — and is why these are nullable rather than bool.
    /// </summary>
    public bool? QuickStartDefault { get; init; }
    public bool? OverviewDefault { get; init; }

    /// <summary>
    /// Whether the optional modules are switched on in configuration. Reported rather than
    /// toggled: each needs an engine running on the host before its pages can do anything.
    /// </summary>
    public bool SyncEnabled { get; init; }
    public bool BackupEnabled { get; init; }
    public string? PlatformName { get; init; }

    public IReadOnlyList<SizeChoiceViewModel> Sizes { get; init; } = [];

    /// <summary>Preselected on every create form. Empty means nothing is preselected.</summary>
    public string? DefaultInstanceSize { get; init; }

    /// <summary>Whether branch previews start on for a new Git-backed application.</summary>
    public bool PreviewsDefault { get; init; }

    /// <summary>Shown here because it is a platform decision, though it is changed on its own page.</summary>
    public bool RegistryDiscoveryEnabled { get; init; }
}
