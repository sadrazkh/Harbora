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
