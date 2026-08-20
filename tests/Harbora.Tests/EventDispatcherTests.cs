using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>EventDispatcher</c> (P6, 2026-08-20 platform-options plan, "Outbound event notifications").
///
/// <para>
/// Mirrors <c>NotificationDeliveryRetryTests</c>' own idiom deliberately — same shape of harness, same
/// "call the job body directly, the way the job handler does" approach — because
/// <c>EventDelivery</c>/<c>ExecuteQueuedDeliveryAsync</c> is the same idea (a durable row, a job-queue
/// retry, an honest outcome) for a second, narrower audience.
/// </para>
/// </summary>
public class EventDispatcherTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    /// <summary>Captures exactly what one webhook POST carried — the raw body bytes and every header
    /// — so a test can play "the consumer" and verify the signature independently.</summary>
    private sealed class RecordingWebhookHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }
        public HttpRequestHeaders? LastHeaders { get; private set; }
        public Uri? LastUrl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            LastHeaders = request.Headers;
            LastUrl = request.RequestUri;
            return new HttpResponseMessage(Status) { Content = new StringContent("ok") };
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record Harness(
        EventDispatcher Dispatcher, HarboraDbContext Db, RecordingWebhookHandler Handler,
        RecordingNotificationService Notifications);

    private static Harness Build(HttpStatusCode webhookStatus = HttpStatusCode.OK)
    {
        var store = "event-dispatch-" + Guid.NewGuid();
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(store).Options);
        var scope = new NotificationQueueScope(store);
        var handler = new RecordingWebhookHandler(webhookStatus);
        var notifications = new RecordingNotificationService();

        var dispatcher = new EventDispatcher(
            db, scope.Factory, new PassthroughProtector(), new SingleHandlerFactory(handler),
            notifications, new FixedClock(), Options.Create(new NotificationOptions { DeliveryTimeoutSeconds = 10 }),
            NullLogger<EventDispatcher>.Instance);

        return new Harness(dispatcher, db, handler, notifications);
    }

    private static EventSubscription WebhookSub(
        Guid workspaceId, EventKind mask, string secret = "shared-secret", string url = "https://hooks.example.com/x") =>
        new()
        {
            WorkspaceId = workspaceId, Name = "ops-hook", Channel = AlertChannel.Webhook,
            EncryptedTarget = $$"""{"url":"{{url}}"}""",
            EncryptedSigningSecret = secret, Events = mask, IsEnabled = true
        };

    private static EventSubscription TelegramSub(Guid workspaceId, EventKind mask) => new()
    {
        WorkspaceId = workspaceId, Name = "ops-telegram", Channel = AlertChannel.Telegram,
        EncryptedTarget = """{"botToken":"tok","chatId":"1"}""", Events = mask, IsEnabled = true
    };

    // ---- mask matching / one delivery per matching subscription ---------------------------------

    [Fact]
    public async Task Publishing_an_event_enqueues_a_delivery_only_for_subscriptions_whose_mask_includes_it()
    {
        var h = Build();
        var matching = WebhookSub(Workspace, EventKind.DeploymentSucceeded | EventKind.AppCrashed);
        var notMatching = WebhookSub(Workspace, EventKind.BackupFailed);
        h.Db.EventSubscriptions.AddRange(matching, notMatching);
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.PublishAsync(Workspace, EventKind.DeploymentSucceeded,
            new Dictionary<string, string> { ["app"] = "shop" }, default);

        var deliveries = await h.Db.EventDeliveries.ToListAsync();
        deliveries.Should().ContainSingle().Which.SubscriptionId.Should().Be(matching.Id);

        var jobs = await h.Db.Jobs.Where(j => j.Kind == JobKind.EventDelivery).ToListAsync();
        jobs.Should().ContainSingle("PublishAsync enqueues through the real job queue, not a call it makes itself")
            .Which.TargetId.Should().Be(deliveries.Single().Id);
    }

    [Fact]
    public async Task Two_subscriptions_matching_the_same_event_each_get_their_own_delivery_row_and_job()
    {
        var h = Build();
        var first = WebhookSub(Workspace, EventKind.BackupSucceeded);
        var second = TelegramSub(Workspace, EventKind.BackupSucceeded);
        h.Db.EventSubscriptions.AddRange(first, second);
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.PublishAsync(Workspace, EventKind.BackupSucceeded,
            new Dictionary<string, string> { ["type"] = "Database" }, default);

        var deliveries = await h.Db.EventDeliveries.ToListAsync();
        deliveries.Should().HaveCount(2);
        deliveries.Select(d => d.SubscriptionId).Should().BeEquivalentTo([first.Id, second.Id]);
        deliveries.Select(d => d.EventId).Distinct().Should().HaveCount(2, "each delivery carries its own dedup id");

        var jobCount = await h.Db.Jobs.CountAsync(j => j.Kind == JobKind.EventDelivery);
        jobCount.Should().Be(2);
    }

    [Fact]
    public async Task A_disabled_subscription_receives_nothing()
    {
        var h = Build();
        var disabled = WebhookSub(Workspace, EventKind.DeploymentSucceeded);
        disabled.IsEnabled = false;
        h.Db.EventSubscriptions.Add(disabled);
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.PublishAsync(Workspace, EventKind.DeploymentSucceeded, new Dictionary<string, string>(), default);

        (await h.Db.EventDeliveries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Publishing_never_throws_even_when_there_is_nobody_subscribed()
    {
        var h = Build();
        var act = () => h.Dispatcher.PublishAsync(Workspace, EventKind.ServiceFailed, new Dictionary<string, string>(), default);
        await act.Should().NotThrowAsync("the plan requires the publish seam to never break the act it observes");
    }

    // ---- webhook: signed payload, verified like a real consumer would --------------------------

    [Fact]
    public async Task A_webhook_deliverys_signature_verifies_against_the_shared_secret_the_way_a_real_consumer_would()
    {
        var h = Build(HttpStatusCode.OK);
        var sub = WebhookSub(Workspace, EventKind.DeploymentSucceeded, secret: "top-secret-key");
        h.Db.EventSubscriptions.Add(sub);
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.PublishAsync(Workspace, EventKind.DeploymentSucceeded,
            new Dictionary<string, string> { ["app"] = "shop", ["deployment"] = "12" }, default);
        var delivery = await h.Db.EventDeliveries.SingleAsync();

        await h.Dispatcher.ExecuteQueuedDeliveryAsync(delivery.Id, default);

        h.Handler.Calls.Should().Be(1);
        h.Handler.LastBody.Should().NotBeNullOrEmpty();

        // The test consumer: recompute the signature independently from the shared secret and the
        // raw body, exactly as a subscriber's own webhook handler would.
        var expected = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("top-secret-key"), Encoding.UTF8.GetBytes(h.Handler.LastBody!))
        ).ToLowerInvariant();

        h.Handler.LastHeaders!.GetValues("X-Harbora-Signature").Single().Should().Be(expected);
        h.Handler.LastHeaders!.GetValues("X-Harbora-Event").Single().Should().Be("deployment.succeeded");

        // The payload itself carries the stable dedup id, the event name, the workspace and the facts.
        using var payload = JsonDocument.Parse(h.Handler.LastBody!);
        payload.RootElement.GetProperty("event").GetString().Should().Be("deployment.succeeded");
        payload.RootElement.GetProperty("workspace").GetString().Should().Be(Workspace.ToString());
        payload.RootElement.GetProperty("id").GetString().Should().Be(delivery.EventId);
        payload.RootElement.GetProperty("data").GetProperty("app").GetString().Should().Be("shop");

        var stored = await h.Db.EventDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        stored.Status.Should().Be(NotificationDeliveryStatus.Sent);
        stored.HttpStatusCode.Should().Be(200);
        stored.Error.Should().BeNull();
    }

    // ---- delivery honesty: every attempt writes a row with its real outcome ---------------------

    [Fact]
    public async Task A_failing_endpoint_leaves_a_red_delivery_row_and_the_subscriptions_own_LastError()
    {
        var h = Build(HttpStatusCode.InternalServerError);
        var sub = WebhookSub(Workspace, EventKind.ServiceFailed);
        h.Db.EventSubscriptions.Add(sub);
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.PublishAsync(Workspace, EventKind.ServiceFailed,
            new Dictionary<string, string> { ["service"] = "pg" }, default);
        var delivery = await h.Db.EventDeliveries.SingleAsync();

        // JobExecutionPolicy.MaxAttemptsFor(EventDelivery) is 3 — spend the whole budget, the way a
        // retried Job row would across its backoff waits.
        for (var i = 0; i < 3; i++)
        {
            var attempt = async () => await h.Dispatcher.ExecuteQueuedDeliveryAsync(delivery.Id, default);
            (await attempt.Should().ThrowAsync<NotificationChannelException>()).Which.IsRetryable.Should().BeTrue();
        }

        var stored = await h.Db.EventDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        stored.Status.Should().Be(NotificationDeliveryStatus.Failed);
        stored.Attempts.Should().Be(3);
        stored.Error.Should().Contain("500");

        var subscription = await h.Db.EventSubscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        subscription.LastError.Should().Contain("500",
            "the convenience copy on the subscription is what AttentionService reads to feed the dashboard");
        subscription.LastAttemptAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_404_from_the_endpoint_is_not_retried_at_all()
    {
        var h = Build(HttpStatusCode.NotFound);
        var sub = WebhookSub(Workspace, EventKind.DeploymentFailed);
        h.Db.EventSubscriptions.Add(sub);
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.PublishAsync(Workspace, EventKind.DeploymentFailed, new Dictionary<string, string>(), default);
        var delivery = await h.Db.EventDeliveries.SingleAsync();

        var attempt = async () => await h.Dispatcher.ExecuteQueuedDeliveryAsync(delivery.Id, default);
        (await attempt.Should().ThrowAsync<NotificationChannelException>()).Which.IsRetryable.Should().BeFalse();

        var stored = await h.Db.EventDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        stored.Status.Should().Be(NotificationDeliveryStatus.Failed, "a 4xx is the message being refused, not a bad moment");
        stored.Attempts.Should().Be(1, "one attempt, never retried");
        stored.HttpStatusCode.Should().Be(404);
    }

    [Fact]
    public async Task An_endpoint_that_fails_once_then_accepts_ends_up_Sent_with_both_attempts_visible()
    {
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.BadGateway, HttpStatusCode.OK]);
        var h = Build();
        var sub = WebhookSub(Workspace, EventKind.BackupFailed);
        h.Db.EventSubscriptions.Add(sub);
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.PublishAsync(Workspace, EventKind.BackupFailed, new Dictionary<string, string>(), default);
        var delivery = await h.Db.EventDeliveries.SingleAsync();

        h.Handler.Status = statuses.Dequeue();
        var first = async () => await h.Dispatcher.ExecuteQueuedDeliveryAsync(delivery.Id, default);
        await first.Should().ThrowAsync<NotificationChannelException>();

        h.Handler.Status = statuses.Dequeue();
        await h.Dispatcher.ExecuteQueuedDeliveryAsync(delivery.Id, default);

        var stored = await h.Db.EventDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        stored.Status.Should().Be(NotificationDeliveryStatus.Sent);
        stored.Attempts.Should().Be(2);
        stored.Error.Should().BeNull();
    }

    // ---- Telegram: reuses NotificationService's own channel code, not a fork --------------------

    [Fact]
    public async Task A_telegram_subscription_is_sent_through_the_same_notification_service_channel_code()
    {
        var h = Build();
        var sub = TelegramSub(Workspace, EventKind.AppCrashed);
        h.Db.EventSubscriptions.Add(sub);
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.PublishAsync(Workspace, EventKind.AppCrashed,
            new Dictionary<string, string> { ["app"] = "shop", ["reason"] = "CrashLooping" }, default);
        var delivery = await h.Db.EventDeliveries.SingleAsync();

        await h.Dispatcher.ExecuteQueuedDeliveryAsync(delivery.Id, default);

        h.Notifications.TelegramMessages.Should().ContainSingle()
            .Which.EncryptedTarget.Should().Be(sub.EncryptedTarget);
        h.Handler.Calls.Should().Be(0, "a Telegram delivery never touches the webhook HTTP client");

        var stored = await h.Db.EventDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        stored.Status.Should().Be(NotificationDeliveryStatus.Sent);
    }

    // ---- a deleted subscription suppresses rather than fails ------------------------------------

    [Fact]
    public async Task A_delivery_whose_subscription_was_deleted_between_enqueue_and_the_job_running_is_suppressed_not_failed()
    {
        var h = Build();
        var sub = WebhookSub(Workspace, EventKind.DeploymentSucceeded);
        h.Db.EventSubscriptions.Add(sub);
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.PublishAsync(Workspace, EventKind.DeploymentSucceeded, new Dictionary<string, string>(), default);
        var delivery = await h.Db.EventDeliveries.SingleAsync();

        h.Db.EventSubscriptions.Remove(await h.Db.EventSubscriptions.SingleAsync(s => s.Id == sub.Id));
        await h.Db.SaveChangesAsync();

        await h.Dispatcher.ExecuteQueuedDeliveryAsync(delivery.Id, default);

        var stored = await h.Db.EventDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        stored.Status.Should().Be(NotificationDeliveryStatus.Suppressed);
        h.Handler.Calls.Should().Be(0);
    }
}
