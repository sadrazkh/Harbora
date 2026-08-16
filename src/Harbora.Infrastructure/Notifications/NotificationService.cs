using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Delivers notifications to every matching alert. Channel targets are stored encrypted as JSON;
/// webhook channels go out over HTTP, email over SMTP.
///
/// <para>
/// N1 (2026-08-16 notification-system spec) moved the actual channel attempt off this class's own
/// call stack and onto the job queue: <see cref="NotifyAsync"/> and <see cref="NotifyRuleAsync"/> now
/// write a durable <see cref="NotificationDelivery"/> row and enqueue <see cref="JobKind.NotificationDelivery"/>
/// rather than dispatching inline. <see cref="ExecuteQueuedDeliveryAsync"/> is the job's body —
/// <c>NotificationDeliveryJobHandler</c> is the thin <c>IJobHandler</c> adapter that calls it — and it
/// is what makes a channel refusal retried three times with backoff instead of lost the moment it
/// happens. <see cref="SendTestAsync"/> is unchanged: the panel's Test button wants the server's own
/// words immediately, not a queued verdict.
/// </para>
/// </summary>
public sealed class NotificationService(
    HarboraDbContext db,
    ISecretProtector protector,
    IHttpClientFactory httpFactory,
    PlatformMailer platformMailer,
    IFunctionEventBus functionEvents,
    IServiceScopeFactory scopeFactory,
    ISystemClock clock,
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
    /// <summary>
    /// <inheritdoc cref="INotificationService.NotifyAsync" path="/summary/para"/>
    ///
    /// <para>
    /// The number counts rules the message was <i>handed to</i>, not rules that took it. A channel
    /// that answered 404 is counted, because it is a rule that exists and its refusal is recorded on
    /// the row itself; zero means the workspace has nowhere for this to go, which is the outcome
    /// that has no other home — nothing throws and no row changes.
    /// </para>
    /// </summary>
    public async Task<int> NotifyAsync(Guid workspaceId, AlertEvent evt, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var alerts = await db.Alerts
            .Where(a => a.WorkspaceId == workspaceId && a.IsEnabled && a.MinSeverity <= severity)
            .ToListAsync(ct);

        var matching = alerts.Where(a => Matches(a, evt)).ToList();

        foreach (var alert in matching)
            await EnqueueDeliveryAsync(new NotificationDelivery
            {
                WorkspaceId = workspaceId,
                Purpose = NotificationDeliveryPurpose.AlertDispatch,
                Channel = alert.Channel,
                AlertId = alert.Id,
                Severity = severity,
                Subject = title,
                EncryptedBody = protector.Protect(body)
            }, ct);

        // §3 way two: nothing seeds an Alert row, so a workspace that never visited the alerts page
        // matches zero rules here — not because it opted out, but because there was never anything to
        // opt out of. Every rule with sufficient severity that DID opt out of this event is left
        // alone: that is a choice the fallback below must not override, which is why this asks "does
        // any rule exist at all" rather than reusing `matching`.
        if (matching.Count == 0 && evt != AlertEvent.Test
            && !await db.Alerts.AnyAsync(a => a.WorkspaceId == workspaceId, ct))
            await EnqueueFallbackToAdminsAsync(workspaceId, severity, title, body, ct);

        // Functions subscribe to the same happenings people do, so they are told from here rather
        // than from a second set of raise-sites kept in step by hand — the arrangement that ends with
        // an operator being emailed about a crash no function was ever told about.
        //
        // Deliberately outside the rule matching above: a workspace with no alert rules still has
        // functions, and making code depend on somebody having configured a notification channel
        // would be an invisible coupling nobody could debug.
        if (Domain.Functions.FunctionEvents.ForAlert(evt) is { } functionEventKey)
            await functionEvents.PublishAsync(
                Domain.Functions.FunctionEvent.Create(
                    functionEventKey, workspaceId, title,
                    ("title", title), ("body", body), ("severity", severity.ToString())),
                ct);

        return matching.Count;
    }

    /// <summary>
    /// One rule, by id. IgnoreQueryFilters because the caller is a background evaluator with no
    /// session — the workspace filter would find nothing and report a clean pass.
    ///
    /// <para>
    /// Queues, like <see cref="NotifyAsync"/> — the returned <see cref="NotificationResult"/> now
    /// means "handed to the queue", not "delivered": nothing here can know the channel's answer until
    /// <see cref="ExecuteQueuedDeliveryAsync"/> runs. No caller reads <c>.Delivered</c>/<c>.Error</c>
    /// from this method today; the type is kept so the early "no such rule"/"disabled" refusals still
    /// have a way to say why nothing was queued.
    /// </para>
    /// </summary>
    public async Task<NotificationResult> NotifyRuleAsync(
        Guid alertId, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var alert = await db.Alerts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return NotificationResult.Failed("That alert rule no longer exists.");
        if (!alert.IsEnabled) return NotificationResult.Failed("That alert rule is disabled.");

        await EnqueueDeliveryAsync(new NotificationDelivery
        {
            WorkspaceId = alert.WorkspaceId,
            Purpose = NotificationDeliveryPurpose.AlertDispatch,
            Channel = alert.Channel,
            AlertId = alert.Id,
            Severity = severity,
            Subject = title,
            EncryptedBody = protector.Protect(body)
        }, ct);

        return NotificationResult.Ok;
    }

    public async Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return NotificationResult.Failed("That alert rule no longer exists.");

        return await DispatchSafe(alert, AlertSeverity.Info, "Harbora test",
            "This is a test notification from Harbora.", ct);
    }

    /// <summary>
    /// Which rules an event goes to.
    ///
    /// <para>
    /// The default is <c>false</c>, which is the safe answer for a rule-matching function and a trap
    /// for whoever appends the next <see cref="AlertEvent"/>: an event with no arm here is delivered
    /// to nobody, raises nothing, throws nothing, and leaves its caller reporting a notification
    /// sent. Anything appended to that enum needs a line in this switch on the same day.
    /// </para>
    ///
    /// <para>
    /// <see cref="AlertEvent.LowBalance"/> answers true for every rule rather than reading an opt-in
    /// flag of its own, and that is deliberate. Its five neighbours are things that happened to one
    /// resource; this one says the whole workspace is about to stop, and it is the last message the
    /// platform sends a customer while they can still do something about it — an install where
    /// somebody had quietly unticked it would deliver silence and a suspension. The customer's own
    /// out is the one every rule already has: switch the rule off, or set its minimum severity above
    /// Warning. Adding a sixth checkbox would also mean a column, a migration and a bilingual label,
    /// which is a lot of surface to build for the answer "no thank you, do not tell me".
    /// </para>
    /// </summary>
    private static bool Matches(Alert a, AlertEvent evt) => evt switch
    {
        AlertEvent.DeployFailed => a.OnDeployFailed,
        AlertEvent.AppCrashed => a.OnAppCrashed,
        AlertEvent.SslExpiring => a.OnSslExpiring,
        AlertEvent.DiskWarning => a.OnDiskWarning,
        AlertEvent.BackupFailed => a.OnBackupFailed,
        AlertEvent.LowBalance => true,
        AlertEvent.Test => true,
        _ => false
    };

    /// <summary>Used only by <see cref="SendTestAsync"/>: one attempt, swallowed into a
    /// <see cref="NotificationResult"/> the panel's Test button can show immediately.</summary>
    private async Task<NotificationResult> DispatchSafe(Alert alert, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        NotificationResult result;
        try
        {
            await DispatchOnceAsync(alert.Channel, alert.EncryptedTarget, severity, title, body, ct);
            result = NotificationResult.Ok;
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
    /// One channel attempt. Throws rather than swallowing — this is the seam
    /// <see cref="ExecuteQueuedDeliveryAsync"/> runs on the job queue and <see cref="DispatchSafe"/>
    /// (the Test button's synchronous path) both share, so the four channel senders below are written
    /// once. A non-2xx response or the delivery's own timeout budget becomes a
    /// <see cref="NotificationChannelException"/> carrying whether a second attempt is worth trying;
    /// everything else propagates as whatever the sender itself threw.
    /// </summary>
    private async Task DispatchOnceAsync(
        AlertChannel channel, string encryptedTarget, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.DeliveryTimeout);

        try
        {
            var target = string.IsNullOrEmpty(encryptedTarget) ? "{}" : protector.Unprotect(encryptedTarget);
            await (channel switch
            {
                AlertChannel.Telegram => SendTelegram(target, title, body, timeout.Token),
                AlertChannel.Discord => SendDiscord(target, severity, title, body, timeout.Token),
                AlertChannel.Webhook => SendWebhook(target, severity, title, body, timeout.Token),
                AlertChannel.Email => SendEmail(target, title, body, timeout.Token),
                _ => Task.CompletedTask
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The delivery's own budget, not the caller's cancellation — a DNS blip or a slow
            // endpoint, which a second attempt can plausibly get past.
            throw new NotificationChannelException(
                $"The channel did not respond within {_options.DeliveryTimeout.TotalSeconds:0.##} seconds.",
                isRetryable: true);
        }
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
    /// The job body <c>NotificationDeliveryJobHandler</c> runs — one attempt at one queued
    /// <see cref="NotificationDelivery"/>. Rethrows on failure so
    /// <see cref="Harbora.Infrastructure.Jobs.JobExecutionPolicy"/>'s retry/backoff decides what
    /// happens next; the row itself is updated either way, which is what makes "the attempts are
    /// visible" true regardless of how the job worker judges the exception.
    /// </summary>
    public async Task ExecuteQueuedDeliveryAsync(Guid deliveryId, CancellationToken ct)
    {
        var delivery = await db.NotificationDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null) return; // Nothing to do — the row is gone; re-running finds no work.

        // Idempotent against a re-claim after a crash: a delivery already settled must never be sent
        // twice because the worker's own bookkeeping did not make it to disk in time.
        if (delivery.Status is NotificationDeliveryStatus.Sent or NotificationDeliveryStatus.Suppressed)
            return;

        Alert? alert = null;
        if (delivery.Purpose == NotificationDeliveryPurpose.AlertDispatch)
        {
            alert = delivery.AlertId is { } alertId
                ? await db.Alerts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == alertId, ct)
                : null;
            if (alert is null)
            {
                // The rule was deleted between this being queued and the job running. Not a channel
                // refusal — there is no channel any more — so this is Suppressed, not Failed, and
                // never retried.
                delivery.Status = NotificationDeliveryStatus.Suppressed;
                delivery.LastError = "That alert rule no longer exists.";
                delivery.LastAttemptAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        // Doc 09 §6: a channel with no configuration degrades to Suppressed with a reason rather than
        // throwing. Checked up front so an unconfigured install does not spend three attempts and
        // thirty-one minutes of backoff finding that out.
        if (UsesPlatformMailer(alert) && !await platformMailer.IsConfiguredAsync(ct))
        {
            delivery.Status = NotificationDeliveryStatus.Suppressed;
            delivery.LastError = "Platform SMTP is not configured.";
            delivery.LastAttemptAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            if (alert is not null)
                await RecordAttemptAsync(alert, NotificationResult.Failed(delivery.LastError), ct);
            return;
        }

        delivery.Attempts++;
        delivery.LastAttemptAt = clock.UtcNow;

        try
        {
            var body = protector.Unprotect(delivery.EncryptedBody);
            if (alert is not null)
                await DispatchOnceAsync(alert.Channel, alert.EncryptedTarget, delivery.Severity, delivery.Subject, body, ct);
            else
                await platformMailer.SendAsync(delivery.RecipientAddress!, delivery.Subject, body, ct);

            delivery.Status = NotificationDeliveryStatus.Sent;
            delivery.LastError = null;
            await db.SaveChangesAsync(ct);

            if (alert is not null) await RecordAttemptAsync(alert, NotificationResult.Ok, ct);
        }
        catch (Exception ex)
        {
            delivery.LastError = Truncate(ex.Message);
            var canRetry = Jobs.JobExecutionPolicy.IsRetryable(ex)
                           && delivery.Attempts < Jobs.JobExecutionPolicy.MaxAttemptsFor(JobKind.NotificationDelivery);
            // Pending sends it back through the queue for another attempt; anything else is this
            // delivery's final word — never erased by whatever a later, unrelated delivery does.
            delivery.Status = canRetry ? NotificationDeliveryStatus.Pending : NotificationDeliveryStatus.Failed;
            await db.SaveChangesAsync(ct);

            if (alert is not null) await RecordAttemptAsync(alert, NotificationResult.Failed(ex.Message), ct);

            throw;
        }
    }

    /// <summary>
    /// Whether this delivery will route through the platform's own SMTP rather than a channel that
    /// carries its own server — an alert whose Email target names no host of its own, or any purpose
    /// with no alert at all (every transactional message and the admin fallback below).
    /// </summary>
    private bool UsesPlatformMailer(Alert? alert)
    {
        if (alert is null) return true;
        if (alert.Channel != AlertChannel.Email) return false;

        try
        {
            var target = string.IsNullOrEmpty(alert.EncryptedTarget) ? "{}" : protector.Unprotect(alert.EncryptedTarget);
            var t = JsonSerializer.Deserialize<EmailTarget>(target, TargetJson);
            return string.IsNullOrWhiteSpace(t?.Host) && !string.IsNullOrWhiteSpace(t?.To);
        }
        catch
        {
            // An unreadable target is a real problem, but not this method's to diagnose — the actual
            // attempt below will hit the same decryption/parse failure and record it properly.
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="delivery"/> and, unless it was already settled (the no-admin fallback
    /// below hands this a row that is already <see cref="NotificationDeliveryStatus.Suppressed"/>),
    /// queues the job that will attempt it.
    ///
    /// <para>
    /// Runs on a fresh scope of its own rather than the caller's shared <see cref="db"/> — the one
    /// thing N1's own spec calls out as easy to miss. <see cref="NotifyAsync"/> and
    /// <see cref="NotifyRuleAsync"/> are called from deep inside a raiser's own unit of work
    /// (<c>MetricsCollector</c> batches a whole tick into one save; <c>DeploymentPipeline</c>'s
    /// failure path already saves once and does not save again before this runs). The old
    /// <c>RecordAttemptAsync</c> called <c>SaveChangesAsync</c> on the shared context and so, as a
    /// side effect nobody asked for, flushed whatever the caller had not saved yet — a habit this must
    /// not inherit in the other direction: enqueuing here must neither depend on the caller saving
    /// afterwards (proven unsafe — <c>DeploymentPipeline</c> does not) nor force the caller's own
    /// half-built state to commit early. An isolated scope does both: it persists regardless of what
    /// the caller goes on to do, and it touches nothing the caller was still holding.
    /// </para>
    /// </summary>
    private async Task EnqueueDeliveryAsync(NotificationDelivery delivery, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        scopedDb.NotificationDeliveries.Add(delivery);
        await scopedDb.SaveChangesAsync(ct);

        if (delivery.Status == NotificationDeliveryStatus.Pending)
            await scope.ServiceProvider.GetRequiredService<IJobQueue>()
                .EnqueueAsync(JobKind.NotificationDelivery, delivery.Id, ct);
    }

    /// <summary>
    /// §3 way two, closed: a workspace with no alert rule at all now has somebody who was told — every
    /// workspace Admin, by email, rather than the fact vanishing the moment the dispatch loop ran zero
    /// times. Not every member: a Viewer or a Developer did not sign up to be paged for an
    /// unconfigured channel, and Admins are the smallest audience the owner's answer to §7 Q2 asks for.
    /// </summary>
    private async Task EnqueueFallbackToAdminsAsync(
        Guid workspaceId, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var admins = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId && m.Role == Domain.Common.WorkspaceRole.Admin
                        && m.User!.IsActive)
            .Select(m => m.User!.Email)
            .Distinct()
            .ToListAsync(ct);

        if (admins.Count == 0)
        {
            // Recorded as such rather than silently doing nothing: even a workspace with nobody at
            // all to tell leaves a row a person can find later, instead of this looking identical to
            // a successful, quiet delivery.
            await EnqueueDeliveryAsync(new NotificationDelivery
            {
                WorkspaceId = workspaceId,
                Purpose = NotificationDeliveryPurpose.NoRecipientFallback,
                Channel = AlertChannel.Email,
                Severity = severity,
                Subject = title,
                EncryptedBody = protector.Protect(body),
                Status = NotificationDeliveryStatus.Suppressed,
                LastError = "This workspace has no alert rule and no admin to notify instead.",
                LastAttemptAt = clock.UtcNow
            }, ct);
            return;
        }

        foreach (var email in admins)
            await EnqueueDeliveryAsync(new NotificationDelivery
            {
                WorkspaceId = workspaceId,
                Purpose = NotificationDeliveryPurpose.NoRecipientFallback,
                Channel = AlertChannel.Email,
                RecipientAddress = email,
                Severity = severity,
                Subject = title,
                EncryptedBody = protector.Protect(body)
            }, ct);
    }

    /// <summary>
    /// Turns an HTTP response into a verdict.
    ///
    /// This is the crux: the response used to be discarded, so a webhook answering 404 — a typo in the
    /// URL, a revoked Discord hook, a wrong Telegram chat id — was indistinguishable from one that
    /// worked, and the panel reported every one of them as sent.
    ///
    /// <para>
    /// §7 Q4(a): a 5xx is retryable, a 4xx is not — a bad gateway is the far end having a bad moment,
    /// a 404 is the message itself being refused, and retrying that three times over half an hour
    /// buries the one sentence that says why under three copies of itself.
    /// </para>
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

        throw new NotificationChannelException(
            $"{channel} returned {(int)response.StatusCode} {response.ReasonPhrase}{detail}",
            isRetryable: (int)response.StatusCode >= 500);
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
