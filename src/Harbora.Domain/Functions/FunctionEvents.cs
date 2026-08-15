using Harbora.Domain.Common;

namespace Harbora.Domain.Functions;

/// <summary>One thing the platform can tell a function about.</summary>
/// <param name="Key">Stored on a subscribing function; frozen once a row names it.</param>
/// <param name="Group">For grouping the picker; matches the families the owner asked for.</param>
public sealed record FunctionEventKind(string Key, string Group, string NameEn, string NameFa)
{
    public string Name(bool isFa) => isFa ? NameFa : NameEn;
}

/// <summary>
/// The events a function may subscribe to, as code.
///
/// <para>
/// Most of these are published from the one place that already knows something happened — the alert
/// dispatcher. Reusing it rather than adding a second set of raise-sites is what keeps the two from
/// drifting apart, which is how a platform ends up notifying a human about a crash it never told a
/// function about.
/// </para>
/// </summary>
public static class FunctionEvents
{
    public const string GroupDeploy = "deploy";
    public const string GroupOps = "ops";
    public const string GroupGit = "git";
    public const string GroupWorkspace = "workspace";

    // --- deployments and apps ---
    public const string DeploymentSucceeded = "deployment.succeeded";
    public const string DeploymentFailed = "deployment.failed";
    public const string AppCrashed = "app.crashed";

    // --- backups and monitoring ---
    public const string BackupFailed = "backup.failed";
    public const string DiskWarning = "monitoring.disk_warning";
    public const string ThresholdBreached = "monitoring.threshold_breached";
    public const string SslExpiring = "certificate.expiring";
    public const string LowBalance = "billing.low_balance";

    // --- git ---
    public const string GitPush = "git.push";
    public const string GitTag = "git.tag";

    // --- workspaces and people ---
    public const string WorkspaceCreated = "workspace.created";
    public const string WorkspaceSuspended = "workspace.suspended";
    public const string WorkspaceResumed = "workspace.resumed";
    public const string MemberInvited = "member.invited";
    public const string MemberJoined = "member.joined";

    public static readonly IReadOnlyList<FunctionEventKind> All =
    [
        new(DeploymentSucceeded, GroupDeploy, "Deployment succeeded", "استقرار موفق"),
        new(DeploymentFailed,    GroupDeploy, "Deployment failed",    "استقرار ناموفق"),
        new(AppCrashed,          GroupDeploy, "Application crashed",  "کرش برنامه"),

        new(BackupFailed,      GroupOps, "Backup failed",       "شکست پشتیبان‌گیری"),
        new(DiskWarning,       GroupOps, "Disk warning",        "هشدار دیسک"),
        new(ThresholdBreached, GroupOps, "Threshold breached",  "عبور از آستانه"),
        new(SslExpiring,       GroupOps, "Certificate expiring","انقضای گواهی SSL"),
        new(LowBalance,        GroupOps, "Low balance",         "موجودی کم"),

        new(GitPush, GroupGit, "Git push", "پوش گیت"),
        new(GitTag,  GroupGit, "Git tag",  "تگ گیت"),

        new(WorkspaceCreated,   GroupWorkspace, "Workspace created",   "ساخت ورک‌اسپیس"),
        new(WorkspaceSuspended, GroupWorkspace, "Workspace suspended", "تعلیق ورک‌اسپیس"),
        new(WorkspaceResumed,   GroupWorkspace, "Workspace resumed",   "رفع تعلیق ورک‌اسپیس"),
        new(MemberInvited,      GroupWorkspace, "Member invited",      "دعوت عضو"),
        new(MemberJoined,       GroupWorkspace, "Member joined",       "پیوستن عضو")
    ];

    public static bool IsKnown(string? key) => key is not null && All.Any(e => e.Key == key);

    public static FunctionEventKind? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(e => e.Key == key);

    /// <summary>
    /// The alert this platform already raises, as the event a function subscribes to.
    ///
    /// <para>
    /// <see cref="AlertEvent.Test"/> maps to nothing on purpose: it exists so an operator can prove a
    /// notification channel works, and firing customer code from a connectivity test would make that
    /// button unsafe to press.
    /// </para>
    /// </summary>
    public static string? ForAlert(AlertEvent alert) => alert switch
    {
        AlertEvent.DeployFailed      => DeploymentFailed,
        AlertEvent.AppCrashed        => AppCrashed,
        AlertEvent.SslExpiring       => SslExpiring,
        AlertEvent.DiskWarning       => DiskWarning,
        AlertEvent.BackupFailed      => BackupFailed,
        AlertEvent.ThresholdBreached => ThresholdBreached,
        AlertEvent.LowBalance        => LowBalance,
        _ => null
    };
}

/// <summary>
/// One published event, on its way to whichever functions asked for it.
/// </summary>
/// <param name="Key">A key from <see cref="FunctionEvents"/>.</param>
/// <param name="WorkspaceId">
/// Whose functions may see it. An event never crosses this boundary — a customer's code learning
/// that another customer's deployment failed would be a tenancy leak dressed as a feature.
/// </param>
/// <param name="Subject">What it happened to, for the log line: an app slug, a backup name.</param>
/// <param name="Data">Flat, already-redacted detail handed to the function as <c>event.data</c>.</param>
public sealed record FunctionEvent(
    string Key,
    Guid WorkspaceId,
    string? Subject,
    IReadOnlyDictionary<string, string?> Data)
{
    public static FunctionEvent Create(string key, Guid workspaceId, string? subject = null,
        params (string Key, string? Value)[] data) =>
        new(key, workspaceId, subject, data.ToDictionary(d => d.Key, d => d.Value));
}
