namespace Harbora.Infrastructure.Projects;

/// <summary>One app or database a project delete would destroy, named and placed.</summary>
public readonly record struct ProjectRemovalItem(Guid Id, string Name, string EnvironmentName);

/// <summary>
/// Everything a project delete would destroy — read once by <see cref="ProjectDeletionService.PlanAsync"/>
/// for the confirm screen, and read again the exact same way inside
/// <see cref="ProjectDeletionService.DeleteAsync"/>, so the two can never end up looking at different
/// sets. <c>ServiceRemovalPlan</c> is the single-database version of the same idea, one class over.
/// </summary>
public readonly record struct ProjectRemovalPlan(
    Guid ProjectId,
    string ProjectName,
    IReadOnlyList<ProjectRemovalItem> Apps,
    IReadOnlyList<ProjectRemovalItem> Databases,
    IReadOnlyList<string> DomainHosts,
    int ScheduledFunctionCount)
{
    public int TotalWorkloads => Apps.Count + Databases.Count;

    /// <summary>Nothing lives in any of this project's environments — the trivial, no-confirmation case.</summary>
    public bool IsEmpty => TotalWorkloads == 0;

    /// <summary>
    /// Whether the typed confirmation matches. Required only when the delete would actually destroy
    /// an app or a database — the same rule <c>ServiceRemovalPlan.IsConfirmed</c> applies to a single
    /// database: asking someone to type a name for a delete that removes nothing trains them to type
    /// it without reading.
    /// </summary>
    public bool IsConfirmed(string? typedName) =>
        IsEmpty || string.Equals(typedName?.Trim(), ProjectName.Trim(), StringComparison.Ordinal);
}

/// <summary>
/// What actually happened when <see cref="ProjectDeletionService.DeleteAsync"/> ran the plan.
///
/// <c>FullyDeleted</c> is false whenever anything the plan named is still there afterwards — whether
/// because removing it threw, or because it returned without removing anything. Either way this is
/// what a caller reports instead of "Deleted": which apps and databases are still there, so the
/// person asking can see exactly what is left rather than being told the project is gone when it
/// is not.
/// </summary>
public readonly record struct ProjectRemovalOutcome(
    bool FullyDeleted,
    string ProjectName,
    IReadOnlyList<string> RemainingApps,
    IReadOnlyList<string> RemainingDatabases);
