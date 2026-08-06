using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Delivers notifications to every matching alert. Channel targets are stored encrypted as JSON;
/// webhook channels go out over HTTP, email over SMTP. Failures never propagate — a broken channel
/// must not break a deploy or a backup — but they are no longer silent either: the outcome is written
/// back onto the alert rule, so the page that offers the channel is the page that admits it is failing.
/// </summary>
public sealed class NotificationService(
    HarboraDbContext db,
    ISecretProtector protector,
    IHttpClientFactory httpFactory,
    PlatformMailer platformMailer,
    Microsoft.Extensions.Options.IOptions<NotificationOptions> options,
    ILogger<NotificationService> logger) : INotificationService
{
    private readonly NotificationOptions _options = options.Value;

    /// <summary>
    /// Channel targets are written by the controller with camelCase names ("url", "botToken") and read
    /// back into PascalCase records. System.Text.Json matches case-sensitively by default, so every
    /// field came back null and every channel failed with "not an absolute URL" — for as long as
    /// notifications have existed. Nobody saw it because the failure was swallowed and the Test button
    /// reported success regardless. Reading case-insensitively fixes the targets already stored.
    /// </summary>
    private static readonly JsonSerializerOptions TargetJson = new() { PropertyNameCaseInsensitive = true };
    public async Task NotifyAsync(Guid workspaceId, AlertEvent evt, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        // Tracked, not AsNoTracking: the delivery outcome is written back onto these rows.
        var alerts = await db.Alerts
            .Where(a => a.WorkspaceId == workspaceId && a.IsEnabled && a.MinSeverity <= severity)
            .ToListAsync(ct);

        foreach (var alert in alerts.Where(a => Matches(a, evt)))
            await DispatchSafe(alert, severity, title, body, ct);
    }

    /// <summary>
    /// One rule, by id. IgnoreQueryFilters because the caller is a background evaluator with no
    /// session — the workspace filter would find nothing and report a clean pass.
    /// </summary>
    public async Task<NotificationResult> NotifyRuleAsync(
        Guid alertId, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var alert = await db.Alerts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return NotificationResult.Failed("That alert rule no longer exists.");
        if (!alert.IsEnabled) return NotificationResult.Failed("That alert rule is disabled.");

        return await DispatchSafe(alert, severity, title, body, ct);
    }

    public async Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return NotificationResult.Failed("That alert rule no longer exists.");

        return await DispatchSafe(alert, AlertSeverity.Info, "Harbora test",
            "This is a test notification from Harbora.", ct);
    }

    private static bool Matches(Alert a, AlertEvent evt) => evt switch
    {
        AlertEvent.DeployFailed => a.OnDeployFailed,
        AlertEvent.AppCrashed => a.OnAppCrashed,
        AlertEvent.SslExpiring => a.OnSslExpiring,
        AlertEvent.DiskWarning => a.OnDiskWarning,
        AlertEvent.BackupFailed => a.OnBackupFailed,
        AlertEvent.Test => true,
        _ => false
    };

    private async Task<NotificationResult> DispatchSafe(Alert alert, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        NotificationResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.DeliveryTimeout);

            var target = string.IsNullOrEmpty(alert.EncryptedTarget) ? "{}" : protector.Unprotect(alert.EncryptedTarget);
            await (alert.Channel switch
            {
                AlertChannel.Telegram => SendTelegram(target, title, body, timeout.Token),
                AlertChannel.Discord => SendDiscord(target, severity, title, body, timeout.Token),
                AlertChannel.Webhook => SendWebhook(target, severity, title, body, timeout.Token),
                AlertChannel.Email => SendEmail(target, title, body, timeout.Token),
                _ => Task.CompletedTask
            });
            result = NotificationResult.Ok;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = NotificationResult.Failed(
                $"The channel did not respond within {_options.DeliveryTimeout.TotalSeconds:0.##} seconds.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification via {Channel} failed for alert {Id}.", alert.Channel, alert.Id);
            result = NotificationResult.Failed(ex.Message);
        }

        await RecordAttemptAsync(alert, result, ct);
        return result;
    }

    /// <summary>
    /// Writes the outcome back onto the rule. Best-effort by design: the notification has already been
    /// delivered (or not) by this point, and turning bookkeeping into a second failure helps nobody.
    /// </summary>
    private async Task RecordAttemptAsync(Alert alert, NotificationResult result, CancellationToken ct)
    {
        try
        {
            alert.LastAttemptAt = DateTimeOffset.UtcNow;
            alert.LastError = result.Delivered ? null : Truncate(result.Error);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record the delivery outcome for alert {Id}.", alert.Id);
        }
    }

    private static string? Truncate(string? error) =>
        error is null ? null : error.Length <= 400 ? error : error[..400];

    /// <summary>
    /// Turns an HTTP response into a verdict.
    ///
    /// This is the crux: the response used to be discarded, so a webhook answering 404 — a typo in the
    /// URL, a revoked Discord hook, a wrong Telegram chat id — was indistinguishable from one that
    /// worked, and the panel reported every one of them as sent.
    /// </summary>
    private static async Task EnsureAcceptedAsync(HttpResponseMessage response, string channel, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = "";
        try
        {
            var payload = (await response.Content.ReadAsStringAsync(ct)).Trim();
            // An API's error body names the mistake ("chat not found"); an HTML error page just fills
            // the message with markup, and the status code already said everything it has to say.
            if (payload.Length > 0 && !payload.StartsWith('<'))
                detail = " — " + (payload.Length > 200 ? payload[..200] : payload);
        }
        catch { /* the status alone is already the useful part */ }

        throw new InvalidOperationException(
            $"{channel} returned {(int)response.StatusCode} {response.ReasonPhrase}{detail}");
    }

    private async Task SendTelegram(string target, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<TelegramTarget>(target, TargetJson)!;
        var client = httpFactory.CreateClient();
        var text = $"*{title}*\n{body}";
        using var response = await client.PostAsJsonAsync(
            $"https://api.telegram.org/bot{Uri.EscapeDataString(t.BotToken)}/sendMessage",
            new { chat_id = t.ChatId, text, parse_mode = "Markdown" }, ct);
        await EnsureAcceptedAsync(response, "Telegram", ct);
    }

    private async Task SendDiscord(string target, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<UrlTarget>(target, TargetJson)!;
        GuardOutboundUrl(t.Url);
        var client = httpFactory.CreateClient();
        var color = severity switch { AlertSeverity.Critical => 15158332, AlertSeverity.Warning => 15844367, _ => 3066993 };
        using var response = await client.PostAsJsonAsync(
            t.Url, new { embeds = new[] { new { title, description = body, color } } }, ct);
        await EnsureAcceptedAsync(response, "Discord", ct);
    }

    private async Task SendWebhook(string target, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<UrlTarget>(target, TargetJson)!;
        GuardOutboundUrl(t.Url);
        var client = httpFactory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            t.Url, new { severity = severity.ToString(), title, body, at = DateTimeOffset.UtcNow }, ct);
        await EnsureAcceptedAsync(response, "The webhook", ct);
    }

    /// <summary>SSRF guard (doc 10 §2.8): refuse to call internal/reserved targets. Throws;
    /// DispatchSafe logs and swallows so a blocked channel never breaks a deploy/backup.</summary>
    private static void GuardOutboundUrl(string url)
    {
        if (!Security.UrlSafety.IsAllowedOutboundUrl(url, out var reason))
            throw new InvalidOperationException($"Refusing to call webhook URL: {reason}.");
    }

    private async Task SendEmail(string target, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<EmailTarget>(target, TargetJson)!;

        // An alert that names only a recipient uses the platform's own account — one SMTP password
        // typed once in platform settings, not once per alert. A full per-alert server still wins,
        // for the installation that routes alerts through somewhere else.
        if (string.IsNullOrWhiteSpace(t.Host) && !string.IsNullOrWhiteSpace(t.To))
        {
            await platformMailer.SendAsync(t.To, title, body, ct);
            return;
        }

        using var client = new SmtpClient(t.Host, t.Port)
        {
            EnableSsl = t.UseSsl,
            Credentials = new NetworkCredential(t.User, t.Password)
        };
        using var message = new MailMessage(t.From, t.To, title, body);
        await client.SendMailAsync(message, ct);
    }

    private sealed record TelegramTarget(string BotToken, string ChatId);
    private sealed record UrlTarget(string Url);
    private sealed record EmailTarget(string Host, int Port, string User, string Password, string From, string To, bool UseSsl);
}
