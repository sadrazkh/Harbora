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
/// webhook channels go out over HTTP, email over SMTP. Failures are logged, never thrown, so a
/// broken channel can't break a deploy/backup.
/// </summary>
public sealed class NotificationService(
    HarboraDbContext db,
    ISecretProtector protector,
    IHttpClientFactory httpFactory,
    ILogger<NotificationService> logger) : INotificationService
{
    public async Task NotifyAsync(Guid workspaceId, AlertEvent evt, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var alerts = await db.Alerts.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && a.IsEnabled && a.MinSeverity <= severity)
            .ToListAsync(ct);

        foreach (var alert in alerts.Where(a => Matches(a, evt)))
            await DispatchSafe(alert, severity, title, body, ct);
    }

    public async Task SendTestAsync(Guid alertId, CancellationToken ct)
    {
        var alert = await db.Alerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is not null)
            await DispatchSafe(alert, AlertSeverity.Info, "Harbora test", "This is a test notification from Harbora.", ct);
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

    private async Task DispatchSafe(Alert alert, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        try
        {
            var target = string.IsNullOrEmpty(alert.EncryptedTarget) ? "{}" : protector.Unprotect(alert.EncryptedTarget);
            await (alert.Channel switch
            {
                AlertChannel.Telegram => SendTelegram(target, title, body, ct),
                AlertChannel.Discord => SendDiscord(target, severity, title, body, ct),
                AlertChannel.Webhook => SendWebhook(target, severity, title, body, ct),
                AlertChannel.Email => SendEmail(target, title, body, ct),
                _ => Task.CompletedTask
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification via {Channel} failed for alert {Id}.", alert.Channel, alert.Id);
        }
    }

    private async Task SendTelegram(string target, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<TelegramTarget>(target)!;
        var client = httpFactory.CreateClient();
        var text = $"*{title}*\n{body}";
        await client.PostAsJsonAsync(
            $"https://api.telegram.org/bot{t.BotToken}/sendMessage",
            new { chat_id = t.ChatId, text, parse_mode = "Markdown" }, ct);
    }

    private async Task SendDiscord(string target, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<UrlTarget>(target)!;
        var client = httpFactory.CreateClient();
        var color = severity switch { AlertSeverity.Critical => 15158332, AlertSeverity.Warning => 15844367, _ => 3066993 };
        await client.PostAsJsonAsync(t.Url, new { embeds = new[] { new { title, description = body, color } } }, ct);
    }

    private async Task SendWebhook(string target, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<UrlTarget>(target)!;
        var client = httpFactory.CreateClient();
        await client.PostAsJsonAsync(t.Url, new { severity = severity.ToString(), title, body, at = DateTimeOffset.UtcNow }, ct);
    }

    private Task SendEmail(string target, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<EmailTarget>(target)!;
        using var client = new SmtpClient(t.Host, t.Port)
        {
            EnableSsl = t.UseSsl,
            Credentials = new NetworkCredential(t.User, t.Password)
        };
        using var message = new MailMessage(t.From, t.To, title, body);
        return client.SendMailAsync(message, ct);
    }

    private sealed record TelegramTarget(string BotToken, string ChatId);
    private sealed record UrlTarget(string Url);
    private sealed record EmailTarget(string Host, int Port, string User, string Password, string From, string To, bool UseSsl);
}
