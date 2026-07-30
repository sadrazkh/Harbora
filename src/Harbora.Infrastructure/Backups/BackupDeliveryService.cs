using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Sends a copy of every finished backup to the workspace's delivery channels.
///
/// Failures never propagate — a chat being unreachable must not turn a backup that succeeded into a
/// backup that failed — but they are never silent either: the outcome is written onto the channel, the
/// same way alert rules record theirs, so a delivery that quietly stopped working is visible on the
/// page that offers it.
/// </summary>
public sealed class BackupDeliveryService(
    HarboraDbContext db,
    ISecretProtector protector,
    IHttpClientFactory httpFactory,
    ISystemClock clock,
    ILogger<BackupDeliveryService> logger)
{
    /// <summary>Uploading tens of megabytes over a slow link is normal; failing at 100 seconds is not.</summary>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Sends the artifact to every enabled channel in the workspace.</summary>
    public async Task DeliverAsync(Backup backup, string localPath, CancellationToken ct)
    {
        var channels = await db.BackupDeliveries
            .Where(d => d.WorkspaceId == backup.WorkspaceId && d.IsEnabled)
            .ToListAsync(ct);
        if (channels.Count == 0) return;

        foreach (var channel in channels)
            await SendAsync(channel, backup, localPath, ct);
    }

    /// <summary>Sends one artifact to one channel, recording what happened. Never throws.</summary>
    public async Task<NotificationResult> SendAsync(
        BackupDelivery channel, Backup backup, string localPath, CancellationToken ct)
    {
        NotificationResult result;
        try
        {
            if (!File.Exists(localPath))
                throw new FileNotFoundException($"The artifact is not readable at {localPath}.");

            var size = new FileInfo(localPath).Length;
            if (DeliveryPlan.RejectionReason(channel.Channel, channel.MaxSizeBytes, size) is { } tooBig)
                throw new InvalidOperationException(tooBig);

            var caption = DeliveryPlan.Caption(
                InstanceName(), backup.Type, backup.TargetRef, size, clock.UtcNow);
            var config = string.IsNullOrEmpty(channel.EncryptedConfig) ? "{}" : protector.Unprotect(channel.EncryptedConfig);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(UploadTimeout);

            await (channel.Channel switch
            {
                BackupDeliveryChannel.Telegram => SendTelegramAsync(config, localPath, caption, timeout.Token),
                BackupDeliveryChannel.Email => SendEmailAsync(config, localPath, caption, timeout.Token),
                _ => Task.CompletedTask
            });

            result = NotificationResult.Ok;
            logger.LogInformation("Backup {Id} delivered to {Channel}.", backup.Id, channel.Name);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = NotificationResult.Failed($"The upload did not finish within {UploadTimeout.TotalMinutes:0} minutes.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delivering backup {Id} to {Channel} failed.", backup.Id, channel.Name);
            result = NotificationResult.Failed(ex.Message);
        }

        await RecordAsync(channel, result, ct);
        return result;
    }

    /// <summary>Sends a small text file, so a channel can be proven before a real backup depends on it.</summary>
    public async Task<NotificationResult> SendTestAsync(Guid deliveryId, CancellationToken ct)
    {
        var channel = await db.BackupDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (channel is null) return NotificationResult.Failed("That delivery channel no longer exists.");

        var probe = Path.Combine(Path.GetTempPath(), $"harbora-delivery-test-{Guid.CreateVersion7():N}.txt");
        await File.WriteAllTextAsync(probe,
            "This is a test from Harbora. Your scheduled backups will arrive here.\n", ct);

        try
        {
            var pretend = new Backup { WorkspaceId = channel.WorkspaceId, Type = BackupType.AppConfig, TargetRef = "test" };
            return await SendAsync(channel, pretend, probe, ct);
        }
        finally
        {
            try { File.Delete(probe); } catch { /* a leftover temp file is not worth reporting */ }
        }
    }

    /// <summary>
    /// Uploads the file to a chat with <c>sendDocument</c>.
    ///
    /// The bot has to have been started by the recipient first — Telegram refuses to let a bot open a
    /// conversation — which is why a failure here is reported verbatim: "chat not found" means exactly
    /// that, and no rewording of ours would help more than Telegram's own answer.
    /// </summary>
    private async Task SendTelegramAsync(string config, string localPath, string caption, CancellationToken ct)
    {
        var target = JsonSerializer.Deserialize<TelegramTarget>(config, Json)
                     ?? throw new InvalidOperationException("This Telegram channel has no settings.");
        if (string.IsNullOrWhiteSpace(target.BotToken) || string.IsNullOrWhiteSpace(target.ChatId))
            throw new InvalidOperationException("A Telegram channel needs a bot token and a chat id.");

        var client = httpFactory.CreateClient();
        client.Timeout = UploadTimeout;

        await using var file = File.OpenRead(localPath);
        using var content = new MultipartFormDataContent
        {
            { new StringContent(target.ChatId), "chat_id" },
            { new StringContent(caption), "caption" }
        };
        var document = new StreamContent(file);
        document.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(document, "document", Path.GetFileName(localPath));

        using var response = await client.PostAsync(
            $"https://api.telegram.org/bot{Uri.EscapeDataString(target.BotToken)}/sendDocument", content, ct);
        await EnsureAcceptedAsync(response, "Telegram", ct);
    }

    private async Task SendEmailAsync(string config, string localPath, string caption, CancellationToken ct)
    {
        var target = JsonSerializer.Deserialize<EmailTarget>(config, Json)
                     ?? throw new InvalidOperationException("This email channel has no settings.");
        if (string.IsNullOrWhiteSpace(target.Host) || string.IsNullOrWhiteSpace(target.To))
            throw new InvalidOperationException("An email channel needs an SMTP host and a recipient.");

        using var client = new SmtpClient(target.Host, target.Port <= 0 ? 587 : target.Port)
        {
            EnableSsl = target.UseSsl,
            Credentials = string.IsNullOrWhiteSpace(target.User)
                ? null
                : new NetworkCredential(target.User, target.Password)
        };

        using var message = new MailMessage(
            string.IsNullOrWhiteSpace(target.From) ? target.To! : target.From!,
            target.To!,
            caption.Split('\n')[0],
            caption);

        await using var file = File.OpenRead(localPath);
        message.Attachments.Add(new Attachment(file, Path.GetFileName(localPath)));
        await client.SendMailAsync(message, ct);
    }

    /// <summary>Turns a non-2xx into a verdict carrying the service's own words.</summary>
    private static async Task EnsureAcceptedAsync(HttpResponseMessage response, string channel, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = "";
        try
        {
            var payload = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (payload.Length > 0 && !payload.StartsWith('<'))
                detail = " — " + (payload.Length > 200 ? payload[..200] : payload);
        }
        catch { /* the status is already the useful part */ }

        throw new InvalidOperationException(
            $"{channel} returned {(int)response.StatusCode} {response.ReasonPhrase}{detail}");
    }

    private async Task RecordAsync(BackupDelivery channel, NotificationResult result, CancellationToken ct)
    {
        try
        {
            channel.LastAttemptAt = clock.UtcNow;
            channel.LastError = result.Delivered ? null : Truncate(result.Error);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record the delivery outcome for {Channel}.", channel.Name);
        }
    }

    private static string? Truncate(string? error) =>
        error is null ? null : error.Length <= 400 ? error : error[..400];

    /// <summary>Which Harbora this came from — a chat may well receive backups from several.</summary>
    private static string InstanceName() =>
        Environment.GetEnvironmentVariable("PANEL_DOMAIN") is { Length: > 0 } panel ? panel : "harbora";

    private sealed record TelegramTarget(string? BotToken, string? ChatId);

    private sealed record EmailTarget(
        string? Host, int Port, string? User, string? Password, string? From, string? To, bool UseSsl);
}
