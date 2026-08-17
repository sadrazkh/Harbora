using System.Net;
using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The job body a queued <see cref="NotificationDelivery"/> runs on
/// (<see cref="NotificationService.ExecuteQueuedDeliveryAsync"/>, N1 — 2026-08-16 notification-system
/// spec). A channel's refusal used to be lost the moment it happened: <c>DispatchSafe</c> was one
/// attempt inside <c>NotifyAsync</c>'s own call stack, and the only trace was <c>Alert.LastError</c> —
/// overwritten by whatever the next attempt to that same rule happened to be, however unrelated. Every
/// test here calls the job body directly, the way <c>NotificationDeliveryJobHandler</c> (and, through
/// it, a retried <c>Job</c> row) does — not through a full <c>JobWorker</c>, whose own claim/backoff
/// loop is exercised by <c>DurableJobQueueTests</c>.
/// </summary>
public class NotificationDeliveryRetryTests
{
    private sealed class SequenceResponder(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _index;
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var status = statuses[Math.Min(_index, statuses.Length - 1)];
            _index++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("channel said no") });
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (NotificationService Service, HarboraDbContext Db, NotificationQueueScope Scope) Build(
        HttpMessageHandler handler)
    {
        var store = "notif-retry-" + Guid.NewGuid();
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(store).Options);
        var scope = new NotificationQueueScope(store);
        var service = new NotificationService(db, new PassthroughProtector(),
            new SingleHandlerFactory(handler),
            new PlatformMailer(db, new PassthroughProtector(), NullLogger<PlatformMailer>.Instance),
            Harbora.Infrastructure.Functions.NullFunctionEventBus.Instance,
            scope.Factory, new FixedClock(),
            Microsoft.Extensions.Options.Options.Create(new NotificationOptions { DeliveryTimeoutSeconds = 10 }),
            new NotificationTemplateCatalog(),
            NullLogger<NotificationService>.Instance);
        return (service, db, scope);
    }

    private static Alert SeedRule(HarboraDbContext db, Guid workspaceId) =>
        new()
        {
            WorkspaceId = workspaceId, Name = "ops", Channel = AlertChannel.Webhook,
            EncryptedTarget = """{"url":"https://hooks.example.com/x"}""", IsEnabled = true
        };

    private static NotificationDelivery SeedDelivery(Alert rule, string subject = "t") => new()
    {
        WorkspaceId = rule.WorkspaceId, Purpose = NotificationDeliveryPurpose.AlertDispatch,
        Channel = rule.Channel, AlertId = rule.Id, Subject = subject, EncryptedBody = "body"
    };

    [Fact]
    public async Task A_channel_that_answers_502_three_times_ends_up_Failed_and_every_attempt_is_visible()
    {
        var (service, db, _) = Build(new SequenceResponder(
            HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway));
        var rule = SeedRule(db, Guid.CreateVersion7());
        var delivery = SeedDelivery(rule);
        db.Alerts.Add(rule);
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        Func<Task> Attempt() => () => service.ExecuteQueuedDeliveryAsync(delivery.Id, default);

        // Attempts 1 and 2: retryable (502), so the row goes back to Pending — the state a re-queued
        // Job leaves it in between backoff waits.
        (await Attempt().Should().ThrowAsync<NotificationChannelException>()).Which.IsRetryable.Should().BeTrue();
        var afterFirst = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        afterFirst.Status.Should().Be(NotificationDeliveryStatus.Pending);
        afterFirst.Attempts.Should().Be(1);
        afterFirst.LastError.Should().Contain("502");

        await Attempt().Should().ThrowAsync<NotificationChannelException>();
        var afterSecond = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        afterSecond.Status.Should().Be(NotificationDeliveryStatus.Pending);
        afterSecond.Attempts.Should().Be(2);

        // Attempt 3: JobExecutionPolicy.MaxAttemptsFor(NotificationDelivery) is 3 — this is the last
        // one, and a delivery that has spent its whole allowance is Failed, not Pending again.
        await Attempt().Should().ThrowAsync<NotificationChannelException>();
        var final = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        final.Status.Should().Be(NotificationDeliveryStatus.Failed);
        final.Attempts.Should().Be(3, "the attempts are visible — this is the row a person reads");
        final.LastError.Should().Contain("502");
    }

    [Fact]
    public async Task A_404_is_not_retried_at_all()
    {
        var (service, db, _) = Build(new SequenceResponder(HttpStatusCode.NotFound));
        var rule = SeedRule(db, Guid.CreateVersion7());
        var delivery = SeedDelivery(rule);
        db.Alerts.Add(rule);
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        var attempt = async () => await service.ExecuteQueuedDeliveryAsync(delivery.Id, default);
        (await attempt.Should().ThrowAsync<NotificationChannelException>()).Which.IsRetryable.Should().BeFalse();

        var stored = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        stored.Status.Should().Be(NotificationDeliveryStatus.Failed, "a 4xx is the message being refused, not a bad moment");
        stored.Attempts.Should().Be(1, "one attempt, never retried");
    }

    [Fact]
    public async Task A_channel_that_fails_once_then_accepts_it_is_recorded_as_delivered_with_both_attempts_visible()
    {
        var (service, db, _) = Build(new SequenceResponder(HttpStatusCode.BadGateway, HttpStatusCode.OK));
        var rule = SeedRule(db, Guid.CreateVersion7());
        var delivery = SeedDelivery(rule);
        db.Alerts.Add(rule);
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        var first = async () => await service.ExecuteQueuedDeliveryAsync(delivery.Id, default);
        await first.Should().ThrowAsync<NotificationChannelException>();

        await service.ExecuteQueuedDeliveryAsync(delivery.Id, default);

        var stored = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        stored.Status.Should().Be(NotificationDeliveryStatus.Sent);
        stored.Attempts.Should().Be(2, "both attempts happened and both are counted");
        stored.LastError.Should().BeNull("the eventual success is what the row now says");
    }

    [Fact]
    public async Task A_later_successful_delivery_does_not_erase_an_earlier_deliverys_recorded_failure()
    {
        // The defect N1 exists to close, reproduced directly: a critical alert refused this morning,
        // a test message accepted at noon. Two separate rows now, and the second succeeding must not
        // touch the first.
        var (service, db, _) = Build(new SequenceResponder(HttpStatusCode.NotFound, HttpStatusCode.OK));
        var rule = SeedRule(db, Guid.CreateVersion7());
        var morning = SeedDelivery(rule, "critical alert");
        db.Alerts.Add(rule);
        db.NotificationDeliveries.Add(morning);
        await db.SaveChangesAsync();

        var runMorning = async () => await service.ExecuteQueuedDeliveryAsync(morning.Id, default);
        await runMorning.Should().ThrowAsync<NotificationChannelException>();
        var morningAfterFailure = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == morning.Id);
        morningAfterFailure.Status.Should().Be(NotificationDeliveryStatus.Failed);
        morningAfterFailure.LastError.Should().Contain("404");

        var noon = SeedDelivery(rule, "test message");
        db.NotificationDeliveries.Add(noon);
        await db.SaveChangesAsync();
        await service.ExecuteQueuedDeliveryAsync(noon.Id, default);

        var morningAfterNoon = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == morning.Id);
        morningAfterNoon.Status.Should().Be(NotificationDeliveryStatus.Failed,
            "a later, unrelated delivery succeeding must not erase what this one recorded");
        morningAfterNoon.LastError.Should().Contain("404", "the morning's failure is still readable");

        var noonRow = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == noon.Id);
        noonRow.Status.Should().Be(NotificationDeliveryStatus.Sent);
    }

    [Fact]
    public async Task A_delivery_routed_through_unconfigured_platform_smtp_is_suppressed_not_thrown()
    {
        // Doc 09 §6: a channel with no configuration degrades to Suppressed with a reason, never an
        // exception — and never counted as a retryable attempt, since retrying finds the same absence.
        var (service, db, _) = Build(new SequenceResponder(HttpStatusCode.OK));
        var delivery = new NotificationDelivery
        {
            Purpose = NotificationDeliveryPurpose.PasswordReset, Channel = AlertChannel.Email,
            RecipientAddress = "person@example.com", Subject = "Reset your password", EncryptedBody = "link"
        };
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        var run = async () => await service.ExecuteQueuedDeliveryAsync(delivery.Id, default);
        await run.Should().NotThrowAsync();

        var stored = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        stored.Status.Should().Be(NotificationDeliveryStatus.Suppressed);
        stored.LastError.Should().Contain("not configured");
        stored.Attempts.Should().Be(0, "never actually attempted — there was nowhere to send it");
    }

    // ---- §3 way two: a workspace with no alert rule still has somebody who was told ----

    [Fact]
    public async Task A_workspace_with_no_alert_rule_reaches_its_admins_instead_of_nobody()
    {
        var (service, db, _) = Build(new SequenceResponder(HttpStatusCode.OK));
        var workspaceId = Guid.CreateVersion7();
        var admin = new User { Email = "admin@example.com", DisplayName = "Admin", IsActive = true };
        db.Users.Add(admin);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspaceId, UserId = admin.Id, Role = WorkspaceRole.Admin
        });
        await db.SaveChangesAsync();

        var reached = await service.NotifyAsync(
            workspaceId, NotificationEventData.Create(AlertEvent.DeployFailed,
                ("AppName", "Deploy failed"), ("Reason", "reason")),
            AlertSeverity.Critical, default);

        reached.Should().Be(0, "no Alert rule matched — the count is still honest about that");
        var delivery = await db.NotificationDeliveries.SingleAsync();
        delivery.Purpose.Should().Be(NotificationDeliveryPurpose.NoRecipientFallback);
        delivery.RecipientAddress.Should().Be("admin@example.com");
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending);
    }

    [Fact]
    public async Task A_workspace_with_no_alert_rule_and_no_admin_is_recorded_as_such_rather_than_silently_returning_zero()
    {
        var (service, db, _) = Build(new SequenceResponder(HttpStatusCode.OK));
        var workspaceId = Guid.CreateVersion7();

        var reached = await service.NotifyAsync(
            workspaceId, NotificationEventData.Create(AlertEvent.DeployFailed,
                ("AppName", "Deploy failed"), ("Reason", "reason")),
            AlertSeverity.Critical, default);

        reached.Should().Be(0);
        var delivery = await db.NotificationDeliveries.SingleAsync();
        delivery.Purpose.Should().Be(NotificationDeliveryPurpose.NoRecipientFallback);
        delivery.Status.Should().Be(NotificationDeliveryStatus.Suppressed,
            "recorded as such rather than the caller only ever seeing a bare zero");
        delivery.LastError.Should().NotBeNullOrEmpty();
    }
}
