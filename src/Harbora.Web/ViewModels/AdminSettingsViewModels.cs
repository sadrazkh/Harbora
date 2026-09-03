namespace Harbora.Web.ViewModels;

/// <summary>One resource plan an operator can make the default.</summary>
public sealed record SizeChoiceViewModel(string Key, string Name, double CpuCores, long MemoryBytes, long DiskBytes);

/// <summary>One ready-made app an operator can choose to put in front of people.</summary>
public sealed record TemplateChoiceViewModel(
    string Key, string Name, string? NameFa, string Category, string? IconUrl);

/// <summary>
/// One external sign-in provider as the admin form sees it.
/// </summary>
/// <param name="HasSecret">Whether a client secret is stored. The secret itself never travels to the
/// page — a settings screen that renders one leaks it into every browser cache and screen recording,
/// which is why the SMTP password is reported the same way.</param>
/// <param name="RedirectUri">The address to paste into the provider's own console. Derived from the
/// request rather than typed, because a mistyped one fails at the worst possible moment: on somebody
/// else's site, after consent.</param>
/// <param name="IsConfigured">Whether this provider would actually show a button. Switched on with no
/// client id is not configured, and saying so here is the difference between a switch that works and
/// one that reads as though it does.</param>
public sealed record SsoProviderViewModel(
    string Provider,
    string Name,
    bool Enabled,
    string? ClientId,
    bool HasSecret,
    string? Authority,
    string? DisplayName,
    string RedirectUri,
    bool IsConfigured);

/// <summary>
/// The settings that change how the platform behaves for everybody, as opposed to the preferences
/// on /settings that change it for one person.
/// </summary>
public sealed class AdminSettingsViewModel
{
    /// <summary>Google, GitHub and the generic OpenID Connect provider, in that order.</summary>
    public IReadOnlyList<SsoProviderViewModel> Sso { get; init; } = [];

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

    // Outgoing mail. The password itself never travels to the page — only whether one is stored.
    public string? SmtpHost { get; init; }
    public string? SmtpPort { get; init; }
    public string? SmtpUser { get; init; }
    public string? SmtpFrom { get; init; }
    public bool SmtpUseSsl { get; init; }
    public bool SmtpHasPassword { get; init; }

    /// <summary>The daily release check. Off by default — it is an outbound request to a third party.</summary>
    public bool UpdateCheckEnabled { get; init; }
    /// <summary>What the last check saw, whatever it was. Null until one has run.</summary>
    public string? LatestReleaseTag { get; init; }
    public string? RunningVersion { get; init; }

    /// <summary>
    /// Sub-project 12's "last drill" surface — read-only here; written only by
    /// <c>harbora record-drill-result</c>, which <c>deploy/restore-drill.sh</c> calls.
    /// </summary>
    public required Harbora.Infrastructure.DisasterRecovery.RestoreDrillStatus DrillStatus { get; init; }

    /// <summary>
    /// Sub-project 1.9 — the signup trial credit an administrator turns on. Zero is the shipped
    /// default and grants nothing; see <c>Harbora.Infrastructure.Billing.SignupTrialCreditService</c>.
    /// </summary>
    public long SignupCreditAmountMinor { get; init; }

    /// <summary>What has actually been granted so far — proof the switch above has done something.</summary>
    public long SignupCreditIssuedTotalMinor { get; init; }
    public int SignupCreditIssuedCount { get; init; }
}
