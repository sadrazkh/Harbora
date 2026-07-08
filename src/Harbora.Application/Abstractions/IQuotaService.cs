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
    Task<QuotaCheck> CanAddServiceAsync(Guid workspaceId, CancellationToken ct);
}

public sealed record QuotaCheck(bool Allowed, string? Reason)
{
    public static readonly QuotaCheck Ok = new(true, null);
    public static QuotaCheck Deny(string reason) => new(false, reason);
}

public sealed record WorkspaceUsage(
    string PlanName,
    int Apps, int MaxApps,
    int Services, int MaxServices,
    long MemoryUsedBytes, long MaxMemoryBytes,
    double CpuUsed, double MaxCpuCores,
    bool Suspended);
