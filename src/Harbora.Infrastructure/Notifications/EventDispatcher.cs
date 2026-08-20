using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Matches an event against a workspace's <see cref="EventSubscription"/> rows and delivers it (P6,
/// 2026-08-20 platform-options plan, "Outbound event notifications: HTTP webhooks + Telegram").
///
/// <para>
/// <b>What this reuses, and does not fork:</b> a Telegram subscription is sent through
/// <see cref="INotificationService.SendTelegramAsync"/> — the exact same private
/// <c>NotificationService.DispatchOnceAsync</c>/<c>SendTelegram</c> call path, target-JSON shape and
/// response handling an <c>Alert</c>'s own Telegram channel already runs through. The webhook target
/// is decrypted from the same <c>{"url":"..."}</c> JSON <c>AlertsController.BuildTarget</c> already
/// writes for <see cref="AlertChannel.Webhook"/>, the same <see cref="ISecretProtector"/>, and the
/// same SSRF guard (<see cref="Security.UrlSafety.IsAllowedOutboundUrl"/>) an Alert webhook already
/// passes through. <see cref="NotificationChannelException"/> is the same exception type
/// <see cref="Jobs.JobExecutionPolicy.IsRetryable"/> already classifies for the notification queue —
/// reused here rather than a second retryable-fault type.
/// </para>
///
/// <para>
/// <b>What is genuinely new, not reuse:</b> a webhook's payload shape and its HMAC-SHA256 signature.
/// The Functions host's own invocation call
/// (<c>Functions.FunctionInvoker</c>/<c>FunctionProject.SecretHeader</c>) turned out, on inspection,
/// to be a shared-secret header compared for equality — not an HMAC signature over the body — so
/// there was no existing "sign an outbound call" shape to reuse for that half. The actual HMAC-SHA256
/// idiom already living in this codebase is <c>Git.GitWebhookProcessor.Verify</c>'s <i>inbound</i>
/// verification of a GitHub/Gitea webhook: <c>HMACSHA256.HashData(secretBytes, bodyBytes)</c>, hex,
/// lowercase. This mirrors that exact recipe in the outbound direction and carries it in a
/// <c>sha256=</c>-prefixed header, the same convention <c>GitWebhookProcessor</c> already parses on
/// the way in — so a consumer who already speaks "verify a GitHub-shaped webhook signature" needs
/// nothing new to verify this one.
/// </para>
///
/// <para>
/// <b>Enqueue only, per the plan's own words: "the publish seams must not slow or fail the acts they
/// observe."</b> <see cref="PublishAsync"/> never makes an HTTP call — it writes durable
/// <see cref="EventDelivery"/> rows and queues <see cref="JobKind.EventDelivery"/> jobs, on an
/// isolated scope of its own for the exact reason
/// <c>NotificationService.EnqueueDeliveryAsync</c>'s own doc gives: it must persist regardless of
/// what the caller (a deployment pipeline mid-cutover, a backup engine's own catch block) goes on to
/// do, and must not flush whatever the caller's own unit of work has not saved yet. It never throws:
/// every raise site treats it exactly as it already treats <c>INotificationService.NotifyAsync</c> —
/// best-effort, logged, never allowed to turn a successful deploy or backup into a failed one.
/// </para>
/// </summary>
public sealed class EventDispatcher(
    HarboraDbContext db,
    IServiceScopeFactory scopeFactory,
    ISecretProtector protector,
    IHttpClientFactory httpFactory,
    INotificationService notifications,
    ISystemClock clock,
    IOptions<NotificationOptions> options,
    ILogger<EventDispatcher> logger) : IEventPublisher
{
    private readonly NotificationOptions _options = options.Value;
    private static readonly JsonSerializerOptions TargetJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions PayloadJson = new() { PropertyNamingPolicy = null };

    /// <summary>
    /// The wire name each event goes out under — the plan's own vocabulary
    /// (<c>deployment.succeeded</c>, <c>backup.failed</c>, …), not the C# member name.
    /// <see cref="EventKind.MaintenanceOn"/>/<see cref="EventKind.MaintenanceOff"/> (P5, 2026-08-20
    /// platform-options plan) are mapped here even though they are still excluded from
    /// <see cref="EventKind.Publishable"/> and so cannot be subscribed to yet — the wire name a
    /// future subscription would need to match on is a fact about the event, not about whether
    /// anyone can hear it, and <c>AppOperationsService.SetMaintenanceModeAsync</c> already publishes
    /// both. Any kind actually reaching this default case is a genuine caller bug worth throwing on:
    /// every real publishable event has a case above.
    /// </summary>
    private static string WireName(EventKind kind) => kind switch
    {
        EventKind.DeploymentSucceeded => "deployment.succeeded",
        EventKind.DeploymentFailed => "deployment.failed",
        EventKind.AppCrashed => "app.crashed",
        EventKind.BackupSucceeded => "backup.succeeded",
        EventKind.BackupFailed => "backup.failed",
        EventKind.ServiceFailed => "service.failed",
        EventKind.MaintenanceOn => "maintenance.on",
        EventKind.MaintenanceOff => "maintenance.off",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a single publishable event.")
    };

    /// <inheritdoc/>
    public async Task PublishAsync(
        Guid workspaceId, EventKind kind, IReadOnlyDictionary<string, string> resource, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

            // Background work reading a workspace-filtered table from inside a caller that may itself
            // be running under a different (or no) request scope — IgnoreQueryFilters + an explicit
            // WorkspaceId predicate, the tenant-filter trap's own prescribed shape, rather than trust
            // whatever scope this DbContext instance happens to carry.
            var subscriptions = await scopedDb.EventSubscriptions.IgnoreQueryFilters()
                .Where(s => s.WorkspaceId == workspaceId && s.IsEnabled && (s.Events & kind) == kind)
                .ToListAsync(ct);

            if (subscriptions.Count == 0) return;

            var now = clock.UtcNow;
            var wire = WireName(kind);
            var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();

            var deliveries = new List<EventDelivery>(subscriptions.Count);
            foreach (var subscription in subscriptions)
            {
                var deliveryId = Guid.CreateVersion7();
                var payload = new EventPayload(
                    deliveryId.ToString(), wire, workspaceId.ToString(),
                    now.ToString("O"), resource);
                var payloadJson = JsonSerializer.Serialize(payload, PayloadJson);

                var delivery = new EventDelivery
                {
                    Id = deliveryId,
                    WorkspaceId = workspaceId,
                    SubscriptionId = subscription.Id,
                    Event = kind,
                    EventId = deliveryId.ToString(),
                    PayloadJson = payloadJson,
                    Status = NotificationDeliveryStatus.Pending
                };
                deliveries.Add(delivery);
                scopedDb.EventDeliveries.Add(delivery);
            }

            await scopedDb.SaveChangesAsync(ct);

            foreach (var delivery in deliveries)
                await jobs.EnqueueAsync(JobKind.EventDelivery, delivery.Id, workspaceId, ct);
        }
        catch (Exception ex)
        {
            // Same rule DeploymentPipeline's TellSomebody and NotificationService's own best-effort
            // callers already apply: the event this describes already happened and is already
            // recorded on its own row/page — a subscriber not being told about it is a real gap, but
            // it must never become a reason the underlying deploy/backup/crash itself reads as failed.
            logger.LogWarning(ex, "Could not publish {Event} for workspace {Workspace}.", kind, workspaceId);
        }
    }

    /// <summary>Job body for <see cref="JobKind.EventDelivery"/> — one attempt at one queued
    /// <see cref="EventDelivery"/> row. Rethrows on failure so
    /// <see cref="Jobs.JobExecutionPolicy"/>'s retry/backoff decides what happens next, exactly the
    /// shape <c>NotificationService.ExecuteQueuedDeliveryAsync</c> already established.</summary>
    public async Task ExecuteQueuedDeliveryAsync(Guid deliveryId, CancellationToken ct)
    {
        var delivery = await db.EventDeliveries.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null) return; // The row is gone; re-running finds no work.

        // Idempotent against a re-claim after a crash — the same guard NotificationService's own job
        // body applies.
        if (delivery.Status is NotificationDeliveryStatus.Sent or NotificationDeliveryStatus.Suppressed)
            return;

        var subscription = await db.EventSubscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == delivery.SubscriptionId, ct);
        if (subscription is null)
        {
            // Deleted between enqueue and the job running — nowhere left to send to. Not a channel
            // refusal (there is no channel any more), so Suppressed, not Failed, and never retried —
            // the same distinction NotificationService draws for a deleted Alert rule.
            delivery.Status = NotificationDeliveryStatus.Suppressed;
            delivery.Error = "That subscription no longer exists.";
            delivery.LastAttemptAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        delivery.Attempts++;
        delivery.LastAttemptAt = clock.UtcNow;

        try
        {
            int? statusCode = null;
            if (subscription.Channel == AlertChannel.Telegram)
            {
                await notifications.SendTelegramAsync(
                    subscription.EncryptedTarget, WireName(delivery.Event), TelegramBody(delivery.PayloadJson), ct);
            }
            else
            {
                statusCode = await SendWebhookAsync(subscription, delivery, ct);
            }

            delivery.HttpStatusCode = statusCode;
            delivery.Status = NotificationDeliveryStatus.Sent;
            delivery.Error = null;
            await db.SaveChangesAsync(ct);

            await RecordAttemptAsync(subscription, error: null, ct);
        }
        catch (Exception ex)
        {
            delivery.Error = Truncate(ex.Message);
            var canRetry = Jobs.JobExecutionPolicy.IsRetryable(ex)
                           && delivery.Attempts < Jobs.JobExecutionPolicy.MaxAttemptsFor(JobKind.EventDelivery);
            delivery.Status = canRetry ? NotificationDeliveryStatus.Pending : NotificationDeliveryStatus.Failed;
            await db.SaveChangesAsync(ct);

            await RecordAttemptAsync(subscription, ex.Message, ct);

            throw;
        }
    }

    /// <summary>
    /// One POST, signed. The SSRF guard and the case-insensitive target JSON are the same ones
    /// <c>NotificationService</c>'s own webhook sender uses for an Alert's webhook channel — see the
    /// class doc for why the signature itself is not reused from there (nothing there is signed).
    /// </summary>
    private async Task<int> SendWebhookAsync(EventSubscription subscription, EventDelivery delivery, CancellationToken ct)
    {
        var targetJson = string.IsNullOrEmpty(subscription.EncryptedTarget) ? "{}" : protector.Unprotect(subscription.EncryptedTarget);
        var target = JsonSerializer.Deserialize<WebhookTarget>(targetJson, TargetJson);
        var url = target?.Url;

        if (!Security.UrlSafety.IsAllowedOutboundUrl(url, out var reason))
            throw new NotificationChannelException($"Refusing to call webhook URL: {reason}.", isRetryable: false);

        var secret = string.IsNullOrEmpty(subscription.EncryptedSigningSecret)
            ? string.Empty : protector.Unprotect(subscription.EncryptedSigningSecret);
        var bodyBytes = Encoding.UTF8.GetBytes(delivery.PayloadJson);
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bodyBytes)).ToLowerInvariant();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.DeliveryTimeout);

        try
        {
            var client = httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Harbora-Signature", $"sha256={signature}");
            request.Headers.TryAddWithoutValidation("X-Harbora-Event", WireName(delivery.Event));
            request.Headers.TryAddWithoutValidation("X-Harbora-Delivery", delivery.EventId);

            using var response = await client.SendAsync(request, timeout.Token);
            // Recorded before the success check so a refusal's own status code is on the row too —
            // "every attempt writes a row with its real outcome" includes what code the far end gave,
            // not only whether it counted as accepted.
            delivery.HttpStatusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var detail = "";
                try
                {
                    var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
                    if (text.Length > 0 && !text.StartsWith('<'))
                        detail = " — " + (text.Length > 200 ? text[..200] : text);
                }
                catch { /* the status alone is already the useful part */ }

                // §7 Q4(a), the same idiom NotificationService.EnsureAcceptedAsync already applies to
                // an Alert's own webhook: a 5xx is the far end having a bad moment, worth a second
                // attempt; a 4xx is the message itself being refused.
                throw new NotificationChannelException(
                    $"The webhook returned {(int)response.StatusCode} {response.ReasonPhrase}{detail}",
                    isRetryable: (int)response.StatusCode >= 500);
            }

            return (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NotificationChannelException(
                $"The webhook did not respond within {_options.DeliveryTimeout.TotalSeconds:0.##} seconds.",
                isRetryable: true);
        }
    }

    /// <summary>A plain-text rendering of the payload for Telegram — the same facts the webhook
    /// carries as JSON, read as lines a person can actually read in a chat.</summary>
    private static string TelegramBody(string payloadJson)
    {
        try
        {
            var doc = JsonDocument.Parse(payloadJson);
            var lines = new List<string> { $"workspace: {doc.RootElement.GetProperty("workspace").GetString()}" };
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                lines.AddRange(data.EnumerateObject().Select(p => $"{p.Name}: {p.Value}"));
            return string.Join('\n', lines);
        }
        catch (JsonException)
        {
            return payloadJson; // Best-effort rendering only — the signed payload itself is unaffected.
        }
    }

    /// <summary>Writes the outcome back onto the subscription, best-effort — mirrors
    /// <c>NotificationService.RecordAttemptAsync</c>: the delivery row is already the record; this is
    /// only the convenience copy a list page reads without joining it.</summary>
    private async Task RecordAttemptAsync(EventSubscription subscription, string? error, CancellationToken ct)
    {
        try
        {
            subscription.LastAttemptAt = clock.UtcNow;
            subscription.LastError = Truncate(error);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record the delivery outcome for subscription {Id}.", subscription.Id);
        }
    }

    private static string? Truncate(string? error) =>
        error is null ? null : error.Length <= 400 ? error : error[..400];

    private sealed record EventPayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("workspace")] string Workspace,
        [property: JsonPropertyName("timestamp")] string Timestamp,
        [property: JsonPropertyName("data")] IReadOnlyDictionary<string, string> Data);

    private sealed record WebhookTarget(string Url);
}
