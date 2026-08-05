namespace Harbora.Web.ViewModels;

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
    public string? PlatformName { get; init; }

    /// <summary>Shown here because it is a platform decision, though it is changed on its own page.</summary>
    public bool RegistryDiscoveryEnabled { get; init; }
}
