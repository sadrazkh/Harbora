namespace Harbora.Web.ViewModels;

/// <summary>Backs <c>/config-groups</c> (Sub-project 9, 2026-08-20 platform-options plan).</summary>
public sealed class ConfigGroupsPageViewModel
{
    public List<ConfigGroupRow> Groups { get; set; } = [];
    public List<ConfigGroupEntryRow> Entries { get; set; } = [];

    /// <summary>Apps attached to each group, keyed by group id — what the group page shows so a
    /// person editing it can see exactly who will pick the change up on their next deploy.</summary>
    public Dictionary<Guid, IReadOnlyList<AttachedAppRow>> AttachedApps { get; set; } = [];
}

public sealed record ConfigGroupRow(Guid Id, string Name, int EntryCount, int SecretCount, int AttachedAppCount);

/// <summary>One entry's row. <c>Value</c> is null for a secret — never sent to the page, same as
/// <see cref="Harbora.Domain.Apps.EnvironmentVariable"/>'s own masking.</summary>
public sealed record ConfigGroupEntryRow(Guid Id, Guid ConfigGroupId, string Key, bool IsSecret, string? Value);

public sealed record AttachedAppRow(string AppName, bool HasUnpublishedChanges);
