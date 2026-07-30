using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Backups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Managing the channels that receive a copy of each scheduled backup.
///
/// Split from the rest of <see cref="BackupsController"/> only for size; the routes and the
/// authorisation policy are the same.
/// </summary>
public sealed partial class BackupsController
{
    [HttpPost("deliveries")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> CreateDelivery(
        string name, BackupDeliveryChannel channel,
        string? botToken, string? chatId,
        string? smtpHost, int smtpPort, string? smtpUser, string? smtpPassword, string? emailFrom, string? emailTo,
        int maxSizeMb, CancellationToken ct)
    {
        var config = channel switch
        {
            BackupDeliveryChannel.Telegram => JsonSerializer.Serialize(new { botToken, chatId }),
            _ => JsonSerializer.Serialize(new
            {
                host = smtpHost, port = smtpPort, user = smtpUser, password = smtpPassword,
                from = emailFrom, to = emailTo, useSsl = true
            })
        };

        db.BackupDeliveries.Add(new BackupDelivery
        {
            WorkspaceId = WorkspaceId,
            Name = string.IsNullOrWhiteSpace(name) ? channel.ToString() : name.Trim(),
            Channel = channel,
            EncryptedConfig = protector.Protect(config),
            // Stored in bytes; 0 means "use the channel's own ceiling".
            MaxSizeBytes = maxSizeMb > 0 ? maxSizeMb * 1024L * 1024 : 0,
            IsEnabled = true
        });
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Delivery channel added. Send a test to confirm it works.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("deliveries/{id:guid}/test")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> TestDelivery(Guid id, CancellationToken ct)
    {
        if (!await db.BackupDeliveries.AnyAsync(d => d.Id == id && d.WorkspaceId == WorkspaceId, ct))
            return NotFound();

        // Reports what actually happened rather than "sent" — the whole reason this button exists is
        // to find out before a real backup depends on the answer.
        var result = await delivery.SendTestAsync(id, ct);
        if (result.Delivered) TempData["Message"] = "Test file delivered.";
        else TempData["Error"] = $"The test was not delivered: {result.Error}";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("deliveries/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> DeleteDelivery(Guid id, CancellationToken ct)
    {
        await db.BackupDeliveries.Where(d => d.Id == id && d.WorkspaceId == WorkspaceId).ExecuteDeleteAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Reads the chat ids that have written to a Telegram bot recently.
    ///
    /// Finding your own numeric chat id is the step people get stuck on: a bot cannot start a
    /// conversation, so the recipient has to message it first, and Telegram then reports the id only
    /// through <c>getUpdates</c>. Rather than sending someone to a third-party "what is my id" bot,
    /// the panel asks for them — after they have pressed Start.
    /// </summary>
    [HttpPost("deliveries/chat-ids")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> FindChatIds(string botToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(botToken))
            return BadRequest(new { error = "Enter the bot token first." });

        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync(
                $"https://api.telegram.org/bot{Uri.EscapeDataString(botToken.Trim())}/getUpdates", ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return Ok(new { error = $"Telegram returned {(int)response.StatusCode}. Check the bot token." });

            var chats = new List<object>();
            var seen = new HashSet<string>();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var updates))
                foreach (var update in updates.EnumerateArray())
                    foreach (var name in (string[])["message", "channel_post", "my_chat_member"])
                        if (update.TryGetProperty(name, out var msg) && msg.TryGetProperty("chat", out var chat)
                            && chat.TryGetProperty("id", out var idEl))
                        {
                            var id = idEl.ToString();
                            if (!seen.Add(id)) continue;
                            chats.Add(new
                            {
                                id,
                                title = chat.TryGetProperty("title", out var t) ? t.GetString()
                                      : chat.TryGetProperty("username", out var u) ? "@" + u.GetString()
                                      : chat.TryGetProperty("first_name", out var f) ? f.GetString()
                                      : "(chat)"
                            });
                        }

            return Ok(new
            {
                chats,
                // getUpdates only returns what has arrived since the last poll, so an empty list is
                // the expected answer for a bot nobody has messaged yet — say so.
                hint = chats.Count == 0
                    ? "No messages yet. Open the bot in Telegram, press Start, then try again."
                    : null
            });
        }
        catch (Exception ex)
        {
            return Ok(new { error = ex.Message });
        }
    }
}
