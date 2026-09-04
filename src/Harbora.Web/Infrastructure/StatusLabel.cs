using Harbora.Domain.Common;
using Harbora.Domain.Jobs;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// The word a status renders as, in the reader's language.
///
/// This exists because views printed the enum values raw — <c>Succeeded</c>, <c>running</c>,
/// <c>Manual</c> — in the middle of Persian pages. A status is the one word on a row somebody
/// actually reads, and it was the one word guaranteed to be in the wrong language. One mapping,
/// used by every badge, so two pages cannot describe one state with two different words either.
/// </summary>
public static class StatusLabel
{
    public static string For(DeploymentStatus status, bool isFa) => status switch
    {
        DeploymentStatus.Queued => isFa ? "در صف" : "Queued",
        DeploymentStatus.Building => isFa ? "در حال ساخت" : "Building",
        DeploymentStatus.Pushing => isFa ? "در حال ارسال" : "Pushing",
        DeploymentStatus.Deploying => isFa ? "در حال استقرار" : "Deploying",
        DeploymentStatus.Succeeded => isFa ? "موفق" : "Succeeded",
        DeploymentStatus.Failed => isFa ? "ناموفق" : "Failed",
        DeploymentStatus.Cancelled => isFa ? "لغوشده" : "Cancelled",
        DeploymentStatus.RolledBack => isFa ? "بازگردانی‌شده" : "Rolled back",
        // The one the first version of this mapping forgot, which is exactly what the
        // every-value-has-both-languages test exists to catch.
        DeploymentStatus.HealthChecking => isFa ? "بررسی سلامت" : "Health check",
        // 5.2 (2026-09 market-gaps round two): distinct from Queued on purpose — a deployment
        // waiting on a person and a deployment merely behind others in line are different facts,
        // and the whole point is that they must not look the same on this badge.
        DeploymentStatus.PendingApproval => isFa ? "در انتظار تأیید" : "Pending approval",
        _ => Fallback(status.ToString(), isFa)
    };

    public static string For(AppStatus status, bool isFa) => status switch
    {
        AppStatus.Created => isFa ? "ساخته‌شده" : "Created",
        AppStatus.Deploying => isFa ? "در حال استقرار" : "Deploying",
        AppStatus.Running => isFa ? "در حال اجرا" : "Running",
        AppStatus.Stopped => isFa ? "متوقف" : "Stopped",
        AppStatus.Failed => isFa ? "ناموفق" : "Failed",
        AppStatus.Crashed => isFa ? "از کار افتاده" : "Crashed",
        _ => Fallback(status.ToString(), isFa)
    };

    public static string For(DeploymentTrigger trigger, bool isFa) => trigger switch
    {
        DeploymentTrigger.Manual => isFa ? "دستی" : "Manual",
        DeploymentTrigger.GitPush => isFa ? "push گیت" : "Git push",
        DeploymentTrigger.GitTag => isFa ? "تگ گیت" : "Git tag",
        DeploymentTrigger.Webhook => isFa ? "وبهوک" : "Webhook",
        DeploymentTrigger.Cli => "CLI",
        DeploymentTrigger.Rollback => isFa ? "بازگردانی" : "Rollback",
        DeploymentTrigger.Schedule => isFa ? "زمان‌بندی" : "Schedule",
        _ => Fallback(trigger.ToString(), isFa)
    };

    /// <summary>P5 (/activity): the same five words <c>Job.IsTerminal</c> already groups by,
    /// translated once so the status chip and any "why isn't this done" sentence agree.</summary>
    public static string For(JobStatus status, bool isFa) => status switch
    {
        JobStatus.Pending => isFa ? "در صف" : "Pending",
        JobStatus.Running => isFa ? "در حال اجرا" : "Running",
        JobStatus.Succeeded => isFa ? "موفق" : "Succeeded",
        JobStatus.Failed => isFa ? "ناموفق" : "Failed",
        JobStatus.Cancelled => isFa ? "لغوشده" : "Cancelled",
        _ => Fallback(status.ToString(), isFa)
    };

    /// <summary>
    /// A deployment status that arrives as text — some read models store the enum's name.
    /// Unparseable input passes through untranslated rather than pretending to be a state.
    /// </summary>
    public static string Deploy(string? status, bool isFa) =>
        Enum.TryParse<DeploymentStatus>(status, ignoreCase: true, out var parsed)
            ? For(parsed, isFa)
            : status ?? "";

    /// <summary>
    /// Docker's own container states, which arrive as lowercase words from the engine.
    /// An unknown word passes through — inventing a translation for a state we have never seen
    /// would label it with a guess.
    /// </summary>
    public static string Container(string? state, bool isFa) => state?.ToLowerInvariant() switch
    {
        "running" => isFa ? "در حال اجرا" : "running",
        "exited" => isFa ? "خارج‌شده" : "exited",
        "restarting" => isFa ? "در حال ری‌استارت" : "restarting",
        "paused" => isFa ? "مکث‌شده" : "paused",
        "created" => isFa ? "ساخته‌شده" : "created",
        "dead" => isFa ? "مرده" : "dead",
        _ => state ?? ""
    };

    private static string Fallback(string raw, bool isFa) => raw;
}
