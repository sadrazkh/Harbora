using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// A workspace's outbound event subscriptions (P6, 2026-08-20 platform-options plan, "Outbound event
/// notifications: HTTP webhooks + Telegram"). Deliberately not a new channel — see
/// <see cref="EventSubscription"/>'s own doc — the target JSON, encryption and Telegram send path are
/// the same ones <c>AlertsController</c> already uses for its own Webhook/Telegram channels; this
/// controller is the new half, the same way <c>EventDispatcher</c> is: which events go where, and the
/// delivery log that proves it.
///
/// <para>
/// Route is the plan's own suggested alternative to living beside the alert channels
/// (<c>/notifications/webhooks</c>) — chosen to avoid touching <c>MonitoringDashboardViewModel</c>
/// and the already-large Monitoring page, which several other sub-projects on this plan also touch.
/// </para>
/// </summary>
[Authorize]
[Route("notifications/webhooks")]
public sealed class EventSubscriptionsController(
    HarboraDbContext db,
    ISecretProtector protector,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    private bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Event subscriptions";

        var subscriptions = await db.EventSubscriptions
            .Where(s => s.WorkspaceId == WorkspaceId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new EventSubscriptionRow(
                s.Id, s.Name, s.Channel, s.Events, s.IsEnabled, s.LastAttemptAt, s.LastError))
            .ToListAsync(ct);

        // Recent deliveries across every subscription this workspace owns — the log a person opens
        // after "webhooks is not delivering" to see exactly which attempt, of which event, said what.
        var subscriptionIds = subscriptions.Select(s => s.Id).ToList();
        var names = subscriptions.ToDictionary(s => s.Id, s => s.Name);
        var deliveries = await db.EventDeliveries
            .Where(d => subscriptionIds.Contains(d.SubscriptionId))
            .OrderByDescending(d => d.CreatedAt)
            .Take(30)
            .Select(d => new { d.Id, d.SubscriptionId, d.Event, d.Status, d.HttpStatusCode, d.Error, d.LastAttemptAt, d.Attempts })
            .ToListAsync(ct);

        return View(new EventSubscriptionsPageViewModel
        {
            Subscriptions = subscriptions,
            RecentDeliveries = deliveries.Select(d => new EventDeliveryRow(
                d.Id, names.GetValueOrDefault(d.SubscriptionId, "?"), d.Event, d.Status,
                d.HttpStatusCode, d.Error, d.LastAttemptAt, d.Attempts)).ToList(),
            // Read once, then gone — the plan's own "shown once, at creation" rule for the signing
            // secret. TempData survives exactly the one redirect Create makes; reading it here clears
            // it, so a page reload never shows it again.
            NewSecret = TempData["NewSecret"] as string
        });
    }

    /// <summary>
    /// The owner's decision, enforced here rather than only in the UI: HTTP webhooks and Telegram,
    /// nothing else — <see cref="AlertChannel"/> itself also carries Discord and Email, and this is
    /// the one place in the product a value from that enum can be refused instead of accepted.
    /// </summary>
    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Create(
        string name, AlertChannel channel, string? webhookUrl, string? telegramToken, string? telegramChatId,
        bool onDeploymentSucceeded, bool onDeploymentFailed, bool onAppCrashed,
        bool onBackupSucceeded, bool onBackupFailed, bool onServiceFailed,
        bool onMaintenanceOn, bool onMaintenanceOff,
        CancellationToken ct)
    {
        if (channel is not (AlertChannel.Webhook or AlertChannel.Telegram))
        {
            TempData["Error"] = IsFa
                ? "برای اشتراک رویداد فقط وب‌هوک یا تلگرام پشتیبانی می‌شود."
                : "Event subscriptions support HTTP webhooks or Telegram only.";
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = IsFa ? "یک نام برای اشتراک وارد کنید." : "Give this subscription a name.";
            return RedirectToAction(nameof(Index));
        }

        var mask = BuildMask(onDeploymentSucceeded, onDeploymentFailed, onAppCrashed,
            onBackupSucceeded, onBackupFailed, onServiceFailed, onMaintenanceOn, onMaintenanceOff);
        if (mask == EventKind.None)
        {
            TempData["Error"] = IsFa
                ? "دست‌کم یک رویداد را برای اشتراک انتخاب کنید."
                : "Choose at least one event to subscribe to.";
            return RedirectToAction(nameof(Index));
        }

        var target = channel == AlertChannel.Telegram
            ? JsonSerializer.Serialize(new { botToken = telegramToken, chatId = telegramChatId })
            : JsonSerializer.Serialize(new { url = webhookUrl });

        // Only a webhook needs a signing secret — Telegram has nothing to sign; the target itself
        // (bot token + chat id) is the credential.
        string? plaintextSecret = null;
        var encryptedSecret = "";
        if (channel == AlertChannel.Webhook)
        {
            plaintextSecret = EventSubscriptionSecret.Mint();
            encryptedSecret = protector.Protect(plaintextSecret);
        }

        db.EventSubscriptions.Add(new EventSubscription
        {
            WorkspaceId = WorkspaceId,
            Name = name,
            Channel = channel,
            EncryptedTarget = protector.Protect(target),
            Events = mask,
            IsEnabled = true,
            EncryptedSigningSecret = encryptedSecret
        });
        await db.SaveChangesAsync(ct);

        if (plaintextSecret is not null)
            TempData["NewSecret"] = plaintextSecret;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/toggle")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        var sub = await db.EventSubscriptions.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (sub is null) return NotFound();

        sub.IsEnabled = !sub.IsEnabled;
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        // Deleting the row also removes its own delivery history, so a resubscribed hook does not
        // read a stale row's old failures. Load + Remove rather than ExecuteDeleteAsync: the deletion
        // is not a single-table bulk operation, and EF's InMemory provider (this codebase's test
        // lane) does not support the bulk form at all.
        var sub = await db.EventSubscriptions.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (sub is null) return NotFound();

        var deliveries = await db.EventDeliveries.Where(d => d.SubscriptionId == id).ToListAsync(ct);
        db.EventDeliveries.RemoveRange(deliveries);
        db.EventSubscriptions.Remove(sub);
        await db.SaveChangesAsync(ct);

        return RedirectToAction(nameof(Index));
    }

    private static EventKind BuildMask(
        bool deploymentSucceeded, bool deploymentFailed, bool appCrashed,
        bool backupSucceeded, bool backupFailed, bool serviceFailed,
        bool maintenanceOn, bool maintenanceOff)
    {
        var mask = EventKind.None;
        if (deploymentSucceeded) mask |= EventKind.DeploymentSucceeded;
        if (deploymentFailed) mask |= EventKind.DeploymentFailed;
        if (appCrashed) mask |= EventKind.AppCrashed;
        if (backupSucceeded) mask |= EventKind.BackupSucceeded;
        if (backupFailed) mask |= EventKind.BackupFailed;
        if (serviceFailed) mask |= EventKind.ServiceFailed;
        if (maintenanceOn) mask |= EventKind.MaintenanceOn;
        if (maintenanceOff) mask |= EventKind.MaintenanceOff;
        return mask;
    }
}
