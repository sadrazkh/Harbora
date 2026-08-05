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
    long MemoryLimitBytes = 0);

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
    long MemoryLimitBytes = 0);

public sealed class DatabaseOverviewViewModel
{
    public required DatabaseRowViewModel Database { get; init; }
    public string Connection { get; init; } = string.Empty;
    public bool Reveal { get; init; }
    public bool CanManage { get; init; }
    public string? Network { get; init; }
    public IReadOnlyList<string> UsedBy { get; init; } = [];
    public IReadOnlyList<ResourceOptionViewModel> Apps { get; init; } = [];
    public IReadOnlyList<BackupEventViewModel> Backups { get; init; } = [];
    public DateTimeOffset? NextBackupAt { get; init; }
    public int? BackupIntervalHours { get; init; }

    /// <summary>The resource plans this workspace may move between, current one preselected.</summary>
    public IReadOnlyList<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem> Sizes { get; init; } = [];

    /// <summary>What the container is actually running, so version drift can be shown.</summary>
    public string? RunningImage { get; init; }

    /// <summary>The resource plan, or null for a database created before they had one.</summary>
    public string? InstanceSizeKey { get; init; }
    public long MemoryLimitBytes { get; init; }
    public double CpuLimit { get; init; }

    /// <summary>Whether connections to it are encrypted, as recorded at the last provision.</summary>
    public bool TlsEnabled { get; init; }
}

public sealed record ResourceOptionViewModel(Guid Id, string Name, string Environment, bool Compatible);

public sealed record BackupEventViewModel(
    Guid Id,
    BackupStatus Status,
    long SizeBytes,
    DateTimeOffset At,
    bool IsScheduled,
    bool? VerifiedRestorable);
