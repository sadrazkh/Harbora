using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Templates;

namespace Harbora.Web.ViewModels;

public sealed class ApplicationsPageViewModel
{
    public IReadOnlyList<ApplicationRowViewModel> Apps { get; init; } = [];
    public IReadOnlyList<TemplateCatalogItemViewModel> QuickStarts { get; init; } = [];
    public int Running => Apps.Count(a => a.Status == AppStatus.Running);
    public int Building => Apps.Count(a => a.Status == AppStatus.Deploying);
    public int Stopped => Apps.Count(a => a.Status == AppStatus.Stopped);
    public int Failed => Apps.Count(a => a.Status is AppStatus.Failed or AppStatus.Crashed);
}

public sealed record ApplicationRowViewModel(
    Guid Id,
    string Name,
    string Slug,
    AppSourceType SourceType,
    ServiceKind Kind,
    AppStatus Status,
    string Project,
    string Environment,
    string? Domain,
    string? InstanceSize,
    DeploymentStatus? LastDeploymentStatus,
    int? LastDeploymentNumber,
    DateTimeOffset? LastDeploymentAt,
    string? LastCommit,
    bool CanOperate,
    double? CpuPercent,
    long? MemoryBytes,
    /// <summary>
    /// What this app was allotted, so the measurement above it has a denominator. The list showed
    /// the sample alone — and "512 MB" answers nothing on its own, since full or empty depends
    /// entirely on whether the app was given 512 MB or 8 GB. Zero means no ceiling was set.
    /// </summary>
    long MemoryLimitBytes = 0,
    /// <summary>
    /// How long the last deployment took, when it finished. Null both for a deployment still running
    /// (no <c>FinishedAt</c> yet) and for an app that has never deployed at all — the Applications
    /// list (2026-08-19 apps-redesign) reads this rather than an em dash either way.
    /// </summary>
    TimeSpan? LastDeploymentDuration = null,
    /// <summary>
    /// Up to ten CPU samples across the last hour, bucketed by <c>MetricBucketing</c>, for the
    /// Applications list's HEALTH · 1H micro-chart. Empty when nothing was measured in the window —
    /// not the same as an app that measured a flat zero.
    /// </summary>
    IReadOnlyList<double>? CpuSeries = null);

public sealed class DatabasesPageViewModel
{
    public IReadOnlyList<DatabaseRowViewModel> Databases { get; init; } = [];
    public IReadOnlyList<ServiceCatalogEntry> Catalog { get; init; } = [];
    public DatabaseOverviewViewModel? Selected { get; init; }
    public int Healthy => Databases.Count(d => d.Status == ServiceStatus.Running);
    public int Warnings => Databases.Count(d => d.Status is ServiceStatus.Provisioning or ServiceStatus.Failed);
}

public sealed record DatabaseRowViewModel(
    Guid Id,
    string Name,
    ManagedServiceType Type,
    string Version,
    ServiceStatus Status,
    string Project,
    string Environment,
    string ContainerName,
    int InternalPort,
    string Username,
    string DatabaseName,
    string VolumeName,
    long? StorageBytes,
    DateTimeOffset? StorageMeasuredAt,
    double? CpuPercent,
    long? MemoryBytes,
    int LinkedApps,
    DateTimeOffset? LastBackupAt,
    BackupStatus? LastBackupStatus,
    /// <summary>The allotted memory, for the same reason as on an application row.</summary>
    long MemoryLimitBytes = 0,
    /// <summary>Why the last provision attempt failed, when <see cref="Status"/> is
    /// <see cref="ServiceStatus.Failed"/> — P4, 2026-08-17 app-environment-management design.</summary>
    string? ErrorMessage = null);

// DatabaseOverviewViewModel moved to ViewModels/DatabaseTabViewModel.cs — it is now a
// DatabaseTabViewModel (the database shell's tabs, mirroring AppTabViewModel), not a standalone model.

public sealed record ResourceOptionViewModel(Guid Id, string Name, string Environment, bool Compatible);

/// <summary>One app <c>DatabasesController.RotateConfirm</c> lists, for the redeploy checkbox
/// beside it — P4, 2026-08-17 app-environment-management design.</summary>
public sealed record RotatedAppRowViewModel(Guid Id, string Name);

public sealed record BackupEventViewModel(
    Guid Id,
    BackupStatus Status,
    long SizeBytes,
    DateTimeOffset At,
    bool IsScheduled,
    bool? VerifiedRestorable);
