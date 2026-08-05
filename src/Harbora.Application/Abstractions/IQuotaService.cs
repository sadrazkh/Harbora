namespace Harbora.Application.Abstractions;

/// <summary>
/// Enforces per-workspace tenancy limits. Every path that adds load (create app, deploy, create
/// service) asks here first, so a customer can never exceed their plan and the provider can't be
/// oversold. Zero limits on a plan mean "unlimited".
/// </summary>
public interface IQuotaService
{
    Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct);
    Task<QuotaCheck> CanAddAppAsync(Guid workspaceId, string? instanceSizeKey, Guid? excludeAppId, CancellationToken ct);
    Task<QuotaCheck> CanAddServiceAsync(Guid workspaceId, string? instanceSizeKey, CancellationToken ct);
}

public sealed record QuotaCheck(bool Allowed, string? Reason)
{
    public static readonly QuotaCheck Ok = new(true, null);
    public static QuotaCheck Deny(string reason) => new(false, reason);
}

/// <param name="MemoryUsedBytes">
/// Memory <em>reserved</em> — the sum of what every app and database was allotted, not what they
/// are measured to be using. The two differ by a lot, and the screen has to say which one it is.
/// </param>
/// <param name="DiskUsedBytes">What has actually been measured on disk.</param>
/// <param name="DiskUnmeasured">
/// How many volumes have never been measured, so a disk figure is never read as complete when it
/// is not. The plan carried a disk limit, the pricing screen showed it, and the usage screen had
/// no disk on it at all.
/// </param>
public sealed record WorkspaceUsage(
    string PlanName,
    int Apps, int MaxApps,
    int Services, int MaxServices,
    long MemoryUsedBytes, long MaxMemoryBytes,
    double CpuUsed, double MaxCpuCores,
    bool Suspended,
    long DiskUsedBytes = 0, long MaxDiskBytes = 0, int DiskUnmeasured = 0);
