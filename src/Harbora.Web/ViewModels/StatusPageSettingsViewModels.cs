namespace Harbora.Web.ViewModels;

/// <summary>One app currently chosen for the workspace's status page.</summary>
public sealed record StatusPageComponentRow(Guid Id, Guid AppId, string AppName, string DisplayName, int SortOrder);

/// <summary>One of the workspace's apps not yet chosen — the settings page's "add" picker.</summary>
public sealed record StatusPageAvailableAppRow(Guid Id, string Name);

/// <summary>One manual incident note, both languages, as authored — the settings screen shows both,
/// unlike the public page which shows only the reader's own.</summary>
public sealed record StatusPageIncidentRow(
    Guid Id, string TitleEn, string TitleFa, string? BodyEn, string? BodyFa,
    DateTimeOffset StartedAt, DateTimeOffset? ResolvedAt);

/// <summary>The workspace status-page settings screen: opt-in state, the address it would answer on,
/// which apps are shown, which are available to add, and the incident log.</summary>
public sealed class StatusPageSettingsViewModel
{
    public required bool IsEnabled { get; init; }

    /// <summary>The host the page answers on once enabled, or null when no platform root domain is
    /// configured yet — the same "nothing to build a name under" state <c>AppAddress</c> already
    /// carries for ordinary apps.</summary>
    public required string? PublicHost { get; init; }

    public required IReadOnlyList<StatusPageComponentRow> Components { get; init; }
    public required IReadOnlyList<StatusPageAvailableAppRow> AvailableApps { get; init; }
    public required IReadOnlyList<StatusPageIncidentRow> Incidents { get; init; }
}
