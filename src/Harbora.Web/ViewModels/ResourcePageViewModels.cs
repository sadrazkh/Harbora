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
    long? MemoryBytes);

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
    BackupStatus? LastBackupStatus);

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
}

public sealed record ResourceOptionViewModel(Guid Id, string Name, string Environment, bool Compatible);

public sealed record BackupEventViewModel(
    Guid Id,
    BackupStatus Status,
    long SizeBytes,
    DateTimeOffset At,
    bool IsScheduled,
    bool? VerifiedRestorable);
