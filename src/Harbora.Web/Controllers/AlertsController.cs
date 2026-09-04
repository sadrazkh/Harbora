using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Alert rules + channels. The channel target (webhook URL, Telegram token/chat, SMTP settings)
/// is stored encrypted; the plaintext is never returned to the UI.
/// </summary>
[Authorize]
[Route("alerts")]
public sealed class AlertsController(
    HarboraDbContext db,
    INotificationService notifications,
    ISecretProtector protector,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    private bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Create(
        string name, AlertChannel channel, AlertSeverity minSeverity,
        string? webhookUrl, string? telegramToken, string? telegramChatId,
        string? smtpHost, int smtpPort, string? smtpUser, string? smtpPassword, string? emailFrom, string? emailTo,
        bool onDeployFailed, bool onAppCrashed, bool onSslExpiring, bool onDiskWarning, bool onBackupFailed,
        bool onQuotaWarning, bool onUptimeCheckFailed,
        Guid? appId, AlertMetric? metric, double? thresholdPercent, int? sustainedMinutes,
        CancellationToken ct)
    {
        // The threshold half is optional, but a half-filled one used to be accepted and silently
        // made inert: a rule with an app but no metric, or a metric but no line, sat in the table
        // looking configured and never fired. It is refused instead, and the refusal names what is
        // still missing — that is the point of this check.
        if (!ThresholdTripleIsComplete(appId, metric, thresholdPercent, out var error))
        {
            TempData["Error"] = error;
            return RedirectToAction("Index", "Monitoring");
        }

        var target = BuildTarget(channel, webhookUrl, telegramToken, telegramChatId,
            smtpHost, smtpPort, smtpUser, smtpPassword, emailFrom, emailTo);
        var hasThreshold = appId is not null && metric is not null && thresholdPercent is > 0;

        db.Alerts.Add(new Alert
        {
            WorkspaceId = WorkspaceId,
            Name = name,
            Channel = channel,
            MinSeverity = minSeverity,
            EncryptedTarget = protector.Protect(target),
            OnDeployFailed = onDeployFailed,
            OnAppCrashed = onAppCrashed,
            OnSslExpiring = onSslExpiring,
            OnDiskWarning = onDiskWarning,
            OnBackupFailed = onBackupFailed,
            OnQuotaWarning = onQuotaWarning,
            OnUptimeCheckFailed = onUptimeCheckFailed,

            AppId = hasThreshold ? appId : null,
            Metric = hasThreshold ? metric : null,
            ThresholdPercent = hasThreshold ? thresholdPercent : null,
            SustainedMinutes = Math.Clamp(sustainedMinutes ?? 5, 0, 24 * 60),

            IsEnabled = true
        });
        await db.SaveChangesAsync(ct);
        return RedirectToAction("Index", "Monitoring");
    }

    /// <summary>
    /// Changes a rule's own fields — name, channel, severity, event opt-ins and the per-app
    /// threshold — under the same policy and workspace scope as every other action here.
    ///
    /// <para>
    /// <b>The channel target is the one field this form never shows back.</b> The plaintext is
    /// deliberately never returned to the UI (see the type doc above), so the edit form's target
    /// inputs start blank. Leaving them blank keeps whatever is already stored — literally
    /// untouched, byte for byte, when nothing in the target was typed — rather than forcing every
    /// severity change to end with re-entering a Telegram bot token. Typing into one of them updates
    /// just that field; <see cref="MergeTarget"/> is what makes that a merge and not a wipe.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/edit")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Edit(
        Guid id, string name, AlertChannel channel, AlertSeverity minSeverity,
        string? webhookUrl, string? telegramToken, string? telegramChatId,
        string? smtpHost, int smtpPort, string? smtpUser, string? smtpPassword, string? emailFrom, string? emailTo,
        bool onDeployFailed, bool onAppCrashed, bool onSslExpiring, bool onDiskWarning, bool onBackupFailed,
        bool onQuotaWarning, bool onUptimeCheckFailed,
        Guid? appId, AlertMetric? metric, double? thresholdPercent, int? sustainedMinutes,
        CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (alert is null) return NotFound();

        if (!ThresholdTripleIsComplete(appId, metric, thresholdPercent, out var thresholdError))
        {
            TempData["Error"] = thresholdError;
            return RedirectToAction("Index", "Monitoring");
        }

        var targetTouched = TargetFieldsProvided(webhookUrl, telegramToken, telegramChatId,
            smtpHost, smtpPort, smtpUser, smtpPassword, emailFrom, emailTo);

        // A channel switch leaves nothing worth keeping — the stored JSON is shaped for the old
        // channel, and there is no honest way to "keep" a Telegram token as a webhook URL. So a
        // switch has to bring its own target, the same as a brand-new rule would.
        if (channel != alert.Channel && !targetTouched)
        {
            TempData["Error"] = IsFa
                ? "برای تغییر کانال، مقصد جدید را هم وارد کنید — مقصد قبلی برای کانال دیگری بود."
                : "Changing the channel needs its new target as well — the stored one was for the old channel.";
            return RedirectToAction("Index", "Monitoring");
        }

        alert.Name = name;
        alert.Channel = channel;
        alert.MinSeverity = minSeverity;
        alert.OnDeployFailed = onDeployFailed;
        alert.OnAppCrashed = onAppCrashed;
        alert.OnSslExpiring = onSslExpiring;
        alert.OnDiskWarning = onDiskWarning;
        alert.OnBackupFailed = onBackupFailed;
        alert.OnQuotaWarning = onQuotaWarning;
        alert.OnUptimeCheckFailed = onUptimeCheckFailed;

        var hasThreshold = appId is not null && metric is not null && thresholdPercent is > 0;
        alert.AppId = hasThreshold ? appId : null;
        alert.Metric = hasThreshold ? metric : null;
        alert.ThresholdPercent = hasThreshold ? thresholdPercent : null;
        alert.SustainedMinutes = Math.Clamp(sustainedMinutes ?? 5, 0, 24 * 60);

        if (targetTouched)
        {
            var merged = MergeTarget(alert, channel, webhookUrl, telegramToken, telegramChatId,
                smtpHost, smtpPort, smtpUser, smtpPassword, emailFrom, emailTo);
            alert.EncryptedTarget = protector.Protect(merged);
        }
        // else: EncryptedTarget is left exactly as stored — not even re-encrypted.

        await db.SaveChangesAsync(ct);
        return RedirectToAction("Index", "Monitoring");
    }

    /// <summary>Flips whether a rule delivers at all. A disabled rule stays in the list — it is not
    /// the same as deleting it, and the reason M1 exists is that deleting used to be the only option.</summary>
    [HttpPost("{id:guid}/toggle")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (alert is null) return NotFound();

        alert.IsEnabled = !alert.IsEnabled;
        await db.SaveChangesAsync(ct);
        return RedirectToAction("Index", "Monitoring");
    }

    [HttpPost("{id:guid}/test")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        // "Sent" used to be printed unconditionally — even when the rule wasn't ours, and even when
        // the channel rejected the message. A test that cannot fail tests nothing.
        if (!await Owns(id, ct)) return NotFound();

        var result = await notifications.SendTestAsync(id, ct);
        if (result.Delivered) TempData["Message"] = "Test notification delivered.";
        else TempData["Error"] = $"The test notification was not delivered: {result.Error}";

        return RedirectToAction("Index", "Monitoring");
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await db.Alerts.Where(a => a.Id == id && a.WorkspaceId == WorkspaceId).ExecuteDeleteAsync(ct);
        return RedirectToAction("Index", "Monitoring");
    }

    private Task<bool> Owns(Guid id, CancellationToken ct) =>
        db.Alerts.AnyAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);

    /// <summary>
    /// A per-app threshold is three fields acting as one unit: which app, which figure, and where
    /// the line is. Zero of them is a plain event rule — perfectly valid. All three is a threshold
    /// rule — also valid. Anything in between used to be accepted and quietly stripped down to the
    /// event rule, with nobody told; this is refused instead, naming exactly what is still missing.
    /// </summary>
    private bool ThresholdTripleIsComplete(
        Guid? appId, AlertMetric? metric, double? thresholdPercent, out string? error)
    {
        var hasApp = appId is not null;
        var hasMetric = metric is not null;
        var hasThreshold = thresholdPercent is > 0;

        var providedCount = (hasApp ? 1 : 0) + (hasMetric ? 1 : 0) + (hasThreshold ? 1 : 0);
        if (providedCount is 0 or 3) { error = null; return true; }

        var missingFa = new List<string>();
        var missingEn = new List<string>();
        if (!hasApp) { missingFa.Add("اپ"); missingEn.Add("an app"); }
        if (!hasMetric) { missingFa.Add("سنجه"); missingEn.Add("a metric"); }
        if (!hasThreshold) { missingFa.Add("درصد آستانه"); missingEn.Add("a threshold value"); }

        error = IsFa
            ? $"هشدار روی مصرف یک اپ ناقص است — این مورد کم است: {string.Join("، ", missingFa)}. یا هر سه را پر کنید یا هیچ‌کدام را."
            : $"The per-app threshold is incomplete — missing {string.Join(" and ", missingEn)}. Fill in all three, or leave all three blank.";
        return false;
    }

    private static string BuildTarget(
        AlertChannel channel, string? webhookUrl, string? telegramToken, string? telegramChatId,
        string? smtpHost, int smtpPort, string? smtpUser, string? smtpPassword, string? emailFrom, string? emailTo) =>
        channel switch
        {
            AlertChannel.Telegram => JsonSerializer.Serialize(new { botToken = telegramToken, chatId = telegramChatId }),
            AlertChannel.Discord or AlertChannel.Webhook => JsonSerializer.Serialize(new { url = webhookUrl }),
            AlertChannel.Email => JsonSerializer.Serialize(new { host = smtpHost, port = smtpPort, user = smtpUser, password = smtpPassword, from = emailFrom, to = emailTo, useSsl = true }),
            _ => "{}"
        };

    /// <summary>Whether the edit form's own submission touched any target-shaped field at all — the
    /// signal that decides between "leave the stored target alone" and "merge in what was typed".</summary>
    private static bool TargetFieldsProvided(
        string? webhookUrl, string? telegramToken, string? telegramChatId,
        string? smtpHost, int smtpPort, string? smtpUser, string? smtpPassword, string? emailFrom, string? emailTo) =>
        !string.IsNullOrWhiteSpace(webhookUrl) || !string.IsNullOrWhiteSpace(telegramToken) ||
        !string.IsNullOrWhiteSpace(telegramChatId) || !string.IsNullOrWhiteSpace(smtpHost) ||
        !string.IsNullOrWhiteSpace(smtpUser) || !string.IsNullOrWhiteSpace(smtpPassword) ||
        !string.IsNullOrWhiteSpace(emailFrom) || !string.IsNullOrWhiteSpace(emailTo) || smtpPort > 0;

    /// <summary>
    /// Builds the new target, keeping whatever sub-field the edit form left blank. Only called once
    /// <see cref="TargetFieldsProvided"/> says at least one field was actually typed — this is field
    /// level "blank means unchanged", not "touch nothing and the whole target survives untouched"
    /// (that shortcut already happened in <see cref="Edit"/> before this is reached).
    ///
    /// The old target is only readable when the channel did not change: a different channel's JSON
    /// has different keys, and there is nothing honest to carry forward from it.
    /// </summary>
    private string MergeTarget(
        Alert existing, AlertChannel channel, string? webhookUrl, string? telegramToken, string? telegramChatId,
        string? smtpHost, int smtpPort, string? smtpUser, string? smtpPassword, string? emailFrom, string? emailTo)
    {
        JsonElement? old = null;
        if (channel == existing.Channel && !string.IsNullOrEmpty(existing.EncryptedTarget))
        {
            try { old = JsonDocument.Parse(protector.Unprotect(existing.EncryptedTarget)).RootElement; }
            catch { old = null; }
        }

        string? Keep(string? provided, string property) =>
            !string.IsNullOrWhiteSpace(provided) ? provided
            : old is { } o && o.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()
            : null;

        int KeepPort() =>
            smtpPort > 0 ? smtpPort
            : old is { } o && o.TryGetProperty("port", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32()
            : 0;

        return channel switch
        {
            AlertChannel.Telegram => JsonSerializer.Serialize(new
            {
                botToken = Keep(telegramToken, "botToken"),
                chatId = Keep(telegramChatId, "chatId")
            }),
            AlertChannel.Discord or AlertChannel.Webhook => JsonSerializer.Serialize(new { url = Keep(webhookUrl, "url") }),
            AlertChannel.Email => JsonSerializer.Serialize(new
            {
                host = Keep(smtpHost, "host"),
                port = KeepPort(),
                user = Keep(smtpUser, "user"),
                password = Keep(smtpPassword, "password"),
                from = Keep(emailFrom, "from"),
                to = Keep(emailTo, "to"),
                useSsl = true
            }),
            _ => "{}"
        };
    }
}
