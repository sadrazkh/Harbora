using System.Text;
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
    /// The namespace every customer-raised event lives in (F3, 2026-08-21 functions-and-services
    /// plan, "Custom events from customer apps"). Forced server-side by
    /// <see cref="NormaliseCustomKey"/> — a caller cannot post <c>deployment.succeeded</c> and
    /// impersonate a platform event; whatever it sends lands under this prefix instead.
    /// </summary>
    public const string CustomPrefix = "custom.";

    /// <summary>Longest key <see cref="FunctionDefinition.EventKey"/>'s column can hold.</summary>
    private const int MaxKeyLength = 64;

    public static bool IsCustom(string? key) =>
        key is not null && key.StartsWith(CustomPrefix, StringComparison.Ordinal) && key.Length > CustomPrefix.Length;

    /// <summary>
    /// A key a function may subscribe to — one of the platform's own, or a caller's own
    /// <c>custom.*</c> one. A custom key never needed a place in <see cref="All"/> to begin with:
    /// unlike the platform's own vocabulary, it is not fixed code, it is whatever a workspace's own
    /// apps choose to raise.
    /// </summary>
    public static bool IsSubscribable(string? key) => IsKnown(key) || IsCustom(key);

    /// <summary>
    /// Turns whatever a caller posted as an event key into one under <see cref="CustomPrefix"/>, or
    /// null when nothing usable survives. This is the one place the namespace is forced — not a
    /// courtesy the ingest endpoint could skip, the only door custom events have.
    ///
    /// <para>
    /// A leading <c>custom.</c> the caller already typed is not doubled. Anything outside
    /// <c>[a-z0-9._-]</c> (case folded first) collapses into a single separator, the same idea
    /// <c>FunctionSlug.Normalise</c> uses for hyphens — a key is an identifier, not free text, and
    /// letting whitespace or punctuation through verbatim is how two customers' "Order Paid!" and
    /// "order paid" end up as two keys that look like one in a log line.
    /// </para>
    /// </summary>
    public static string? NormaliseCustomKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed.StartsWith(CustomPrefix, StringComparison.Ordinal))
            trimmed = trimmed[CustomPrefix.Length..];

        var sb = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] is not ('.' or '_' or '-'))
                sb.Append('.');
        }

        var suffix = sb.ToString().Trim('.', '_', '-');
        var maxSuffixLength = MaxKeyLength - CustomPrefix.Length;
        if (suffix.Length > maxSuffixLength) suffix = suffix[..maxSuffixLength].Trim('.', '_', '-');

        return suffix.Length == 0 ? null : CustomPrefix + suffix;
    }

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

/// <summary>
/// One <c>custom.*</c> key a workspace's own apps have actually raised (F3, 2026-08-21
/// functions-and-services plan). Not a schema registry — there is no type, no shape, nothing to
/// validate a payload against — just the fact that this key exists, so it can be found and
/// subscribed to.
///
/// <para>
/// This is what stands between "unknown keys are dropped silently" and "unknown keys are accepted
/// but visible": an app emitting <c>custom.order.paid</c> before any function subscribes still
/// updates this row, so the workspace can see the key was received and go subscribe a function to
/// it, instead of the event vanishing behind an ingest endpoint's 200 with nothing to show for it.
/// </para>
/// </summary>
public class FunctionCustomEventKey : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Already namespaced — always starts with <see cref="FunctionEvents.CustomPrefix"/>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>How many ingests have carried this key. Not a delivery count — publishing to zero
    /// subscribers still increments it, which is the whole point: it is what proves the key arrived.</summary>
    public int TimesSeen { get; set; }
}
