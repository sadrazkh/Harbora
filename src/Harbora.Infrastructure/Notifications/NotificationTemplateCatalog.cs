using System.Net;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// The concrete template catalog (N4, 2026-08-16 notification-system spec, "in the reader's own
/// language") — one function per <see cref="AlertEvent"/>, each producing a subject and a body in
/// Persian or English from the facts a raise site handed over.
///
/// <para>
/// <b>Why plain code rather than <c>Harbora.Web</c>'s <c>SharedResource</c>/.resx.</b>
/// <c>Harbora.Infrastructure</c> cannot reference <c>Harbora.Web</c> — <c>Harbora.Web</c> already
/// references <c>Harbora.Infrastructure</c>, so the reverse would be circular — and every raise site
/// this catalog serves (<c>MetricsCollector</c>, <c>DeploymentPipeline</c>, <c>BillingTick</c>,
/// <c>CertificateWatcher</c>, the backup module) runs from a background job with no
/// <c>HttpContext</c>, which is exactly where <c>SharedResource</c>'s request-scoped
/// <c>IStringLocalizer</c> wiring (<c>Program.cs:80-99</c>) does not reach. This codebase already has
/// a working answer to "bilingual text with no request behind it": <c>BillingTick.LowBalanceMessage</c>
/// composes Persian and English directly in <c>Harbora.Infrastructure</c>, because nothing there has a
/// culture to read off a request. This catalog is that same pattern, generalised to every event and,
/// unlike <c>LowBalanceMessage</c>, picking one language per recipient instead of saying both — which
/// N4 can finally do because <c>NotificationService</c> now knows who is being told.
/// </para>
///
/// <para>
/// <b>The road not taken.</b> Rendering in <c>Harbora.Web</c> via an interface implemented there and
/// resolved from <c>Harbora.Infrastructure</c> through DI — mirroring <c>ICurrentUser</c>/
/// <c>IWorkspaceScope</c> — was considered and rejected: every existing example of that pattern is
/// request-scoped (a controller resolves <c>ICurrentUser</c> inside a request), and background code in
/// this codebase already uses its <i>own</i> implementations rather than calling into
/// <c>Harbora.Web</c> (a job worker resolves the system <c>IWorkspaceScope</c>, not
/// <c>HttpWorkspaceScope</c>). Routing a queued delivery's render through a Web-supplied service would
/// be a new, unprecedented direction for this codebase's background jobs to depend in, for content
/// that — Persian and English interpolated prose — this codebase already writes directly in
/// Infrastructure today.
/// </para>
///
/// <para>
/// <b>The indirection doc 09 §4.2 asks for</b> is <see cref="INotificationTemplateCatalog"/> itself:
/// <c>NotificationService</c> and every raise site depend on the interface, never on this class by
/// name, so a later per-workspace-branded implementation (reading an override before falling back to
/// this one) is a registration change, not a call-site change. Not in N4: using it.
/// </para>
/// </summary>
public sealed class NotificationTemplateCatalog : INotificationTemplateCatalog
{
    /// <summary>The platform's own default — <c>User.PreferredCulture</c> already documents it, and
    /// an unrecognised or missing culture renders as this rather than throwing.</summary>
    public const string DefaultCulture = "fa";

    public RenderedNotification Render(NotificationEventData data, string? culture)
    {
        var isFa = !string.Equals(culture, "en", StringComparison.OrdinalIgnoreCase);
        var (subject, text) = isFa ? RenderFa(data) : RenderEn(data);
        return new RenderedNotification(subject, text, Wrap(text, isFa));
    }

    /// <summary>
    /// N5 ("noise control"): composes, never re-derives — every <see cref="DigestLine"/> was already
    /// rendered once, in this same reader's own culture, at the moment <c>NotificationService</c>
    /// queued it. This only joins them into one subject and one body, through the same
    /// <see cref="Wrap"/> every other template shares, so the digest email looks like the rest of the
    /// platform's mail rather than a separate format invented for it.
    /// </summary>
    public RenderedNotification RenderDigest(IReadOnlyList<DigestLine> lines, string? culture)
    {
        var isFa = !string.Equals(culture, "en", StringComparison.OrdinalIgnoreCase);
        var subject = isFa
            ? $"{lines.Count} بروزرسانی در انتظار شماست"
            : $"{lines.Count} update{(lines.Count == 1 ? "" : "s")} waiting for you";
        var text = string.Join("\n\n", lines.Select(l => $"{l.Title}\n{l.Body}"));
        return new RenderedNotification(subject, text, Wrap(text, isFa));
    }

    /// <summary>N5: the opt-in weekly summary, one sentence of counts by severity.</summary>
    public RenderedNotification RenderWeeklyReport(WeeklyReportSummary summary, string? culture)
    {
        var isFa = !string.Equals(culture, "en", StringComparison.OrdinalIgnoreCase);
        var subject = isFa ? "خلاصه هفتگی شما" : "Your weekly summary";
        var text = isFa
            ? $"در بازه {summary.PeriodStart:yyyy-MM-dd} تا {summary.PeriodEnd:yyyy-MM-dd}: " +
              $"{summary.CriticalCount} بحرانی، {summary.WarningCount} هشدار و {summary.InfoCount} اطلاعاتی."
            : $"From {summary.PeriodStart:yyyy-MM-dd} to {summary.PeriodEnd:yyyy-MM-dd}: " +
              $"{summary.CriticalCount} critical, {summary.WarningCount} warning and {summary.InfoCount} informational.";
        return new RenderedNotification(subject, text, Wrap(text, isFa));
    }

    /// <summary>
    /// The one mechanically-derived part of a template: escape the text alternative into a minimal,
    /// direction-aware HTML document. Every event's <em>words</em> come from the switch below;
    /// this is only the markup they are poured into, so seven templates do not each repeat the same
    /// paragraph-wrapping.
    /// </summary>
    private static string Wrap(string text, bool isFa)
    {
        var paragraphs = text
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + WebUtility.HtmlEncode(p).Replace("\n", "<br>") + "</p>");
        var dir = isFa ? "rtl" : "ltr";
        var lang = isFa ? "fa" : "en";
        return $"<div lang=\"{lang}\" dir=\"{dir}\">{string.Concat(paragraphs)}</div>";
    }

    // ---- Persian --------------------------------------------------------------------------

    private static (string Subject, string Text) RenderFa(NotificationEventData d) => d.Type switch
    {
        AlertEvent.DeployFailed => (
            $"استقرار ناموفق: {d.Get("AppName")} #{d.Get("DeploymentNumber")}",
            $"استقرار «{d.Get("AppName")}» (#{d.Get("DeploymentNumber")}) با شکست مواجه شد.\n\n{d.Get("Reason")}"),

        AlertEvent.AppCrashed => (
            $"کرش برنامه: {d.Get("AppName")}",
            $"کانتینر «{d.Get("AppName")}» {CrashPhrase(d.Get("Reason"), isFa: true)}."),

        AlertEvent.SslExpiring => Ssl(d, isFa: true),

        AlertEvent.DiskWarning => (
            "فضای دیسک رو به اتمام",
            $"میزان استفاده از دیسک روی {d.Get("ServerName")} به {d.Get("Percent")}٪ رسیده است."),

        AlertEvent.BackupFailed => BackupFailed(d, isFa: true),

        AlertEvent.ThresholdBreached => Threshold(d, isFa: true),

        AlertEvent.LowBalance => (
            $"اعتبار رو به پایان است: {d.Get("WorkspaceName")}",
            $"اعتبار فضای کاری «{d.Get("WorkspaceName")}» با نرخ ساعت گذشته تقریباً برای {d.Get("Hours")} " +
            "ساعت دیگر کافی است. با رسیدن اعتبار به صفر، برنامه‌ها و پایگاه‌های داده‌ی آن تا زمان شارژ حساب " +
            "متوقف می‌شوند."),

        // Test (a synchronous, unlocalised ping — see NotificationService.DispatchSafe) and any future
        // member appended without a same-day template both land here rather than throwing: a reader
        // still gets a notification, and NotificationTemplateCensusTests is what notices the gap.
        _ => ($"رویداد: {d.Type}", "")
    };

    // ---- English ----------------------------------------------------------------------------

    private static (string Subject, string Text) RenderEn(NotificationEventData d) => d.Type switch
    {
        AlertEvent.DeployFailed => (
            $"Deploy failed: {d.Get("AppName")} #{d.Get("DeploymentNumber")}",
            $"The deployment of \"{d.Get("AppName")}\" (#{d.Get("DeploymentNumber")}) failed.\n\n{d.Get("Reason")}"),

        AlertEvent.AppCrashed => (
            $"App crashed: {d.Get("AppName")}",
            $"The container for \"{d.Get("AppName")}\" {CrashPhrase(d.Get("Reason"), isFa: false)}."),

        AlertEvent.SslExpiring => Ssl(d, isFa: false),

        AlertEvent.DiskWarning => (
            "Low disk space",
            $"Disk usage on {d.Get("ServerName")} is at {d.Get("Percent")}%."),

        AlertEvent.BackupFailed => BackupFailed(d, isFa: false),

        AlertEvent.ThresholdBreached => Threshold(d, isFa: false),

        AlertEvent.LowBalance => (
            $"Balance running low: {d.Get("WorkspaceName")}",
            $"Workspace \"{d.Get("WorkspaceName")}\" has about {d.Get("Hours")} more hour(s) of balance " +
            "at what the last hour cost it. When the balance reaches zero its apps and databases are " +
            "stopped until it is topped up."),

        _ => ($"Event: {d.Type}", "")
    };

    // ---- shared field decoding --------------------------------------------------------------

    /// <summary>
    /// <c>MetricsCollector.ReconcileAppStatusesAsync</c> passes a machine key
    /// (<c>"CrashLooping"</c>/<c>"Exited"</c>), not English prose — this is the one place either
    /// language's phrase for it is chosen.
    /// </summary>
    private static string CrashPhrase(string reason, bool isFa) => reason switch
    {
        "CrashLooping" => isFa ? "مدام دچار کرش شده و مجدداً راه‌اندازی می‌شود" : "keeps crashing and being restarted",
        _ => isFa ? "به‌طور غیرمنتظره متوقف شد" : "exited unexpectedly"
    };

    /// <summary><c>CertificateWatcher</c> passes <c>Expired</c> ("true"/"false") rather than choosing
    /// the sentence itself, so both languages describe the same two cases here, once.</summary>
    private static (string, string) Ssl(NotificationEventData d, bool isFa)
    {
        var host = d.Get("Host");
        var app = d.Get("AppName");
        var expiry = d.Get("ExpiryDate");

        if (d.Get("Expired") == "true")
            return isFa
                ? ($"گواهی SSL منقضی شد: {host}",
                   $"گواهی SSL دامنه {host} ({app}) در تاریخ {expiry} منقضی شده است. بازدیدکنندگان هم‌اکنون " +
                   "هشدار امنیتی می‌بینند.")
                : ($"Certificate expired: {host}",
                   $"The certificate for {host} ({app}) expired on {expiry}. Visitors are seeing a " +
                   "security warning right now.");

        var days = d.Get("Days");
        return isFa
            ? ($"انقضای گواهی SSL: {host}",
               $"گواهی SSL دامنه {host} ({app}) تا {days} روز دیگر، در تاریخ {expiry}، منقضی می‌شود. تمدید " +
               "خودکار باید تاکنون انجام شده باشد؛ بررسی کنید پورت 80 در دسترس است و DNS همچنان به اینجا " +
               "اشاره می‌کند.")
            : ($"Certificate expiring: {host}",
               $"The certificate for {host} ({app}) expires in {days} days, on {expiry}. Renewal should " +
               "already have happened, so check that port 80 is reachable and that DNS still points here.");
    }

    /// <summary>
    /// Three raise sites feed <see cref="AlertEvent.BackupFailed"/> — the legacy engine's own catch
    /// block, the restore-verifier's dry run, and the backup module's bridge
    /// (<c>BackupNotificationService</c>), which already collapses ten of its own event kinds onto
    /// this one <see cref="AlertEvent"/> (see that class's own comment on why). Only the first two
    /// pass a <c>TargetRef</c>; the module bridge leaves it blank and puts everything into
    /// <c>Detail</c>, which is why the subject degrades gracefully when it is empty.
    /// </summary>
    private static (string, string) BackupFailed(NotificationEventData d, bool isFa)
    {
        var target = d.Get("TargetRef");
        var subject = (isFa, target.Length > 0) switch
        {
            (true, true) => $"شکست پشتیبان‌گیری: {target}",
            (true, false) => "شکست پشتیبان‌گیری",
            (false, true) => $"Backup failed: {target}",
            (false, false) => "Backup failed"
        };
        return (subject, d.Get("Detail"));
    }

    /// <summary>
    /// <c>MetricsCollector.EvaluateThresholdsAsync</c> passes <c>Metric</c> as one of
    /// <c>AlertMetric</c>'s own names, not a pre-chosen unit word — <c>RestartRate</c> counts restarts
    /// over a window; <c>CpuPercent</c>/<c>MemoryPercent</c> hold above a percentage.
    /// </summary>
    private static (string, string) Threshold(NotificationEventData d, bool isFa)
    {
        var app = d.Get("AppName");
        var minutes = d.Get("SustainedMinutes");
        var threshold = d.Get("Threshold");

        if (d.Get("Metric") == "RestartRate")
        {
            var count = d.Get("Observed");
            return isFa
                ? ($"{app}: {count} بار ری‌استارت در {minutes} دقیقه",
                   $"برنامه {app} در {minutes} دقیقه گذشته {count} بار ری‌استارت شده است؛ برابر یا بیشتر از " +
                   $"آستانه تنظیم‌شده {threshold}.")
                : ($"{app}: {count} restart(s) in {minutes} minute(s)",
                   $"{app} has restarted {count} time(s) in the last {minutes} minute(s) — at or above the " +
                   $"configured {threshold}.");
        }

        var unitFa = d.Get("Metric") == "CpuPercent" ? "CPU" : "حافظه";
        var unitEn = d.Get("Metric") == "CpuPercent" ? "CPU" : "memory";
        return isFa
            ? ($"{app}: {unitFa} بالای {threshold}٪",
               $"برنامه {app} به مدت {minutes} دقیقه بالای {threshold}٪ از سهمیه {unitFa} خود بوده است.")
            : ($"{app}: {unitEn} above {threshold}%",
               $"{app} has held above {threshold}% of its {unitEn} allocation for {minutes} minute(s).");
    }
}
