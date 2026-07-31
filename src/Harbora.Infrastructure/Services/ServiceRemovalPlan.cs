namespace Harbora.Infrastructure.Services;

/// <summary>What removing a managed database will actually do.</summary>
/// <param name="DeletesData">True when the volume goes too — the irreversible case.</param>
/// <param name="OrphanedVolume">
/// The volume left on the node when the data is kept. Named because "your data is safe" is not true
/// in any useful sense if nothing tells you where it went: the row that knew about it is gone.
/// </param>
/// <param name="BreaksApps">Apps whose environment points at this database and will stop working.</param>
public readonly record struct ServiceRemoval(
    bool DeletesData,
    string? OrphanedVolume,
    IReadOnlyList<string> BreaksApps);

/// <summary>
/// The decision behind the delete button.
///
/// It replaced a single browser <c>confirm()</c> that asked "Remove service?" and then removed a
/// database without checking who was using it, while leaving its volume behind untracked. Every
/// clause here is one thing that button did not say.
/// </summary>
public static class ServiceRemovalPlan
{
    public static ServiceRemoval Describe(bool deleteData, string volumeName, IReadOnlyList<string> appsUsing) =>
        new(deleteData, deleteData ? null : NullIfBlank(volumeName), appsUsing);

    /// <summary>
    /// Whether the typed confirmation matches. Required only when the data goes with it: asking
    /// someone to type a name for a reversible action trains them to type it without reading.
    /// </summary>
    public static bool IsConfirmed(bool deleteData, string? typedName, string serviceName) =>
        !deleteData || string.Equals(typedName?.Trim(), serviceName?.Trim(), StringComparison.Ordinal);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
