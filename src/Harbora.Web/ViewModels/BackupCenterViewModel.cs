using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;

namespace Harbora.Web.Controllers;

/// <summary>
/// Everything the Backup Center page shows.
///
/// <para>
/// Deliberately holds only rows the module already stores. There is no success-rate figure and no
/// deduplication saving on this page for the built-in engine, because that engine records neither —
/// and a number nobody measured is worse than a blank.
/// </para>
/// </summary>
public sealed class BackupCenterViewModel
{
    public IReadOnlyList<BackupRepository> Repositories { get; init; } = [];
    public IReadOnlyList<BackupPolicy> Policies { get; init; } = [];
    public IReadOnlyList<BackupSnapshot> Snapshots { get; init; } = [];
    public IReadOnlyList<RestoreJob> Restores { get; init; } = [];

    /// <summary>Shown so the destination field explains what it will accept.</summary>
    public string RestoreRoot { get; init; } = "";

    /// <summary>Empty means no directory may be backed up yet — the page says so rather than failing later.</summary>
    public IReadOnlyList<string> AllowedSourceRoots { get; init; } = [];

    public BackupSnapshot? LastSuccessful => Snapshots
        .Where(s => s.Status is BackupSnapshotStatus.Completed or BackupSnapshotStatus.CompletedWithWarnings)
        .MaxBy(s => s.CreatedAt);

    public int FailedCount => Snapshots.Count(s => s.Status == BackupSnapshotStatus.Failed);

    public int RunningCount => Snapshots.Count(s =>
        s.Status is BackupSnapshotStatus.Pending or BackupSnapshotStatus.Preparing
            or BackupSnapshotStatus.Running or BackupSnapshotStatus.Verifying);

    public long StoredBytes => Snapshots.Sum(s => s.StoredSizeBytes);

    public int UnhealthyRepositories => Repositories.Count(r =>
        r.Status is BackupRepositoryStatus.Degraded or BackupRepositoryStatus.Unavailable);
}

/// <summary>One directory level inside a snapshot, plus what a restore from here would need.</summary>
public sealed class SnapshotBrowserViewModel
{
    public BackupSnapshot Snapshot { get; init; } = null!;
    public string RepositoryName { get; init; } = "";
    public string CurrentPath { get; init; } = "";
    public IReadOnlyList<EngineEntry> Entries { get; init; } = [];
    public string RestoreRoot { get; init; } = "";

    /// <summary>Breadcrumb segments, each with the path that reaches it.</summary>
    public IReadOnlyList<(string Name, string Path)> Breadcrumbs
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CurrentPath)) return [];

            var crumbs = new List<(string, string)>();
            var accumulated = "";
            foreach (var segment in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = accumulated.Length == 0 ? segment : $"{accumulated}/{segment}";
                crumbs.Add((segment, accumulated));
            }
            return crumbs;
        }
    }

    public string? ParentPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CurrentPath)) return null;
            var slash = CurrentPath.TrimEnd('/').LastIndexOf('/');
            return slash < 0 ? "" : CurrentPath[..slash];
        }
    }
}
