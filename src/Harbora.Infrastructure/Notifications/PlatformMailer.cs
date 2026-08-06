using System.Net;
using System.Net.Mail;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Notifications;

/// <summary>How the platform's own outgoing mail is configured, decrypted and ready to use.</summary>
public sealed record SmtpSettings(string Host, int Port, string User, string Password, string From, bool UseSsl)
{
    /// <summary>A host and a sender are the minimum that can send anything.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

/// <summary>
/// The panel's own outgoing mail: password resets, invitations, and the fallback for alert
/// channels that name no server of their own.
///
/// Until this existed, every email the platform could send required its own SMTP host, port and
/// password typed into an alert form — so the platform as a whole could not mail anyone, and a
/// forgotten password could only be fixed by an administrator by hand.
///
/// Sending reports its real outcome. The test button goes through the same path as everything
/// else, because this codebase has already shipped a Test button that reported success regardless
/// (<see cref="NotificationService"/>'s history) and nobody found out until the channel was needed.
/// </summary>
public sealed class PlatformMailer(
    HarboraDbContext db,
    ISecretProtector protector,
    ILogger<PlatformMailer> logger)
{
    /// <summary>The stored settings, password decrypted. Unconfigured comes back as such, not as an error.</summary>
    public async Task<SmtpSettings> GetSettingsAsync(CancellationToken ct)
    {
        var keys = new[]
        {
            SettingKeys.SmtpHost, SettingKeys.SmtpPort, SettingKeys.SmtpUser,
            SettingKeys.SmtpPassword, SettingKeys.SmtpFrom, SettingKeys.SmtpUseSsl
        };

        // Platform-level rows; the workspace filter would hide them from a background sender.
        var rows = await db.Settings.IgnoreQueryFilters()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var password = rows.GetValueOrDefault(SettingKeys.SmtpPassword, "");
        if (password.Length > 0)
        {
            try { password = protector.Unprotect(password); }
            catch (Exception e)
            {
                // A key rotation losing the password should read as "not configured", not crash
                // every page that asks whether mail works.
                logger.LogWarning(e, "The stored SMTP password could not be decrypted.");
                password = "";
            }
        }

        return new SmtpSettings(
            rows.GetValueOrDefault(SettingKeys.SmtpHost, ""),
            int.TryParse(rows.GetValueOrDefault(SettingKeys.SmtpPort), out var port) && port > 0 ? port : 587,
            rows.GetValueOrDefault(SettingKeys.SmtpUser, ""),
            password,
            rows.GetValueOrDefault(SettingKeys.SmtpFrom, ""),
            !string.Equals(rows.GetValueOrDefault(SettingKeys.SmtpUseSsl), "false", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct) =>
        (await GetSettingsAsync(ct)).IsConfigured;

    /// <summary>
    /// Send one message. Throws with the server's own words on failure — the caller decides whether
    /// that is a form error (the test button) or a logged warning (a background notification).
    /// </summary>
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.IsConfigured)
            throw new InvalidOperationException("Platform SMTP is not configured.");

        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.UseSsl };
        if (!string.IsNullOrWhiteSpace(settings.User))
            client.Credentials = new NetworkCredential(settings.User, settings.Password);

        using var message = new MailMessage(settings.From, to, subject, body);
        await client.SendMailAsync(message, ct);
    }
}
