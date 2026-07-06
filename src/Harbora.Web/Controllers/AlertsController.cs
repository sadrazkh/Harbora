using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
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

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string name, AlertChannel channel, AlertSeverity minSeverity,
        string? webhookUrl, string? telegramToken, string? telegramChatId,
        string? smtpHost, int smtpPort, string? smtpUser, string? smtpPassword, string? emailFrom, string? emailTo,
        bool onDeployFailed, bool onAppCrashed, bool onSslExpiring, bool onDiskWarning, bool onBackupFailed,
        CancellationToken ct)
    {
        var target = channel switch
        {
            AlertChannel.Telegram => JsonSerializer.Serialize(new { botToken = telegramToken, chatId = telegramChatId }),
            AlertChannel.Discord or AlertChannel.Webhook => JsonSerializer.Serialize(new { url = webhookUrl }),
            AlertChannel.Email => JsonSerializer.Serialize(new { host = smtpHost, port = smtpPort, user = smtpUser, password = smtpPassword, from = emailFrom, to = emailTo, useSsl = true }),
            _ => "{}"
        };

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
            IsEnabled = true
        });
        await db.SaveChangesAsync(ct);
        return RedirectToAction("Index", "Monitoring");
    }

    [HttpPost("{id:guid}/test")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        if (await Owns(id, ct)) await notifications.SendTestAsync(id, ct);
        TempData["Message"] = "Test notification sent.";
        return RedirectToAction("Index", "Monitoring");
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await db.Alerts.Where(a => a.Id == id && a.WorkspaceId == WorkspaceId).ExecuteDeleteAsync(ct);
        return RedirectToAction("Index", "Monitoring");
    }

    private Task<bool> Owns(Guid id, CancellationToken ct) =>
        db.Alerts.AnyAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
}
