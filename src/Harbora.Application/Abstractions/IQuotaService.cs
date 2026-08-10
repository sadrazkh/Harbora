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

    /// <summary>
    /// Checks non-compute governance limits as one aggregate operation. Callers describe everything
    /// they are about to add so templates and clones cannot pass six one-at-a-time checks and then
    /// exceed a cap as a batch.
    /// </summary>
    Task<QuotaCheck> CanAddGovernedResourcesAsync(
        Guid workspaceId, GovernanceQuotaDelta delta, CancellationToken ct) =>
        Task.FromResult(QuotaCheck.Ok);

    /// <summary>Checks a whole stack/clone as one unit instead of one stale snapshot per item.</summary>
    Task<QuotaCheck> CanAddWorkloadsAsync(
        Guid workspaceId, WorkloadQuotaDelta delta, CancellationToken ct) =>
        Task.FromResult(QuotaCheck.Ok);
}

/// <summary>Resources a single operation is about to add to a workspace.</summary>
public sealed record GovernanceQuotaDelta(
    int Members = 0,
    int Projects = 0,
    int Environments = 0,
    int Domains = 0,
    int Volumes = 0,
    int BackupSchedules = 0);

public sealed record WorkloadQuotaDelta(
    int Apps = 0,
    int Services = 0,
    long MemoryBytes = 0,
    double CpuCores = 0);

/// <param name="Reason">English refusal text. Never null when <paramref name="Allowed"/> is false.</param>
/// <param name="ReasonFa">
/// The same refusal in Persian, or null where nobody has translated this call site yet.
///
/// <para>
/// Deliberately optional rather than a second required field, so the plan-limit refusals in
/// <c>QuotaService</c> — written before this field existed — keep compiling and keep reading as
/// English-only, which is what they still are. A caller displaying this to a customer decides for
/// itself what a missing translation means for that surface; this type does not paper over the gap
/// by inventing text.
/// </para>
///
/// <para>
/// Deliberately not resolved here from <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>
/// either, the way a Razor view picks between two inline literals with an <c>isFa</c> switch. Half of
/// this record's callers — the cron tick, the job worker, the webhook — run with no request and so no
/// request culture; asking the ambient culture from inside a quota check would answer with whatever
/// the worker thread happened to inherit, not the customer's language. Carrying both strings and
/// letting a request-bound caller pick is the same division of labour the views already use, just
/// moved to whichever layer actually has a request to ask.
/// </para>
/// </param>
public sealed record QuotaCheck(bool Allowed, string? Reason, string? ReasonFa = null)
{
    public static readonly QuotaCheck Ok = new(true, null);
    public static QuotaCheck Deny(string reason) => new(false, reason);
    public static QuotaCheck Deny(string reason, string reasonFa) => new(false, reason, reasonFa);
}

/// <summary>
/// Thrown by a request-scoped caller — a start, a restart, anything a person just clicked — when
/// <see cref="QuotaCheck.Allowed"/> came back false. Carries both of <see cref="QuotaCheck"/>'s
/// strings instead of flattening the refusal into a plain <see cref="Exception.Message"/>, because
/// flattening is exactly how the Persian half went missing the first time: the exception held one
/// string, so the controller catching it could only ever show one string, no matter how many
/// languages the check itself was built with.
///
/// <para>
/// Reserved for callers that have a request, and therefore a culture, to choose with — the same
/// half of <see cref="IBillingGate"/>'s callers <see cref="QuotaCheck.ReasonFa"/>'s own remarks call
/// out as having one. A sessionless caller (the cron tick, the job worker, a webhook) has nothing to
/// choose with and reads <see cref="QuotaCheck"/> directly instead of throwing this.
/// </para>
/// </summary>
public sealed class QuotaRefusedException(QuotaCheck check) : InvalidOperationException(check.Reason)
{
    /// <summary>The same refusal in Persian, or null where the check itself carried none.</summary>
    public string? ReasonFa { get; } = check.ReasonFa;
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
    long DiskUsedBytes = 0, long MaxDiskBytes = 0, int DiskUnmeasured = 0,
    int Members = 0, int MaxMembers = 0,
    int Projects = 0, int MaxProjects = 0,
    int Environments = 0, int MaxEnvironments = 0,
    int Domains = 0, int MaxDomains = 0,
    int Volumes = 0, int MaxVolumes = 0,
    int BackupSchedules = 0, int MaxBackupSchedules = 0);
