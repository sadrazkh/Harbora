using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Infrastructure.Notifications;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whether a notification actually arrived.
///
/// The response from a webhook, Discord or Telegram used to be discarded, so a URL answering 404 —
/// a typo, a revoked hook, a wrong chat id — was indistinguishable from one that worked. The panel's
/// Test button printed "Test notification sent." before the request had even been judged, which meant
/// a channel could be dead for months while the alerts page looked healthy.
/// </summary>
public class NotificationDeliveryTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private sealed class Responder(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public int Calls;
        public string? LastUrl;

        /// <summary>Answer differently on the next call, so one rule can be watched failing then recovering.</summary>
        public HttpStatusCode Status { get; set; } = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body) });
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (NotificationService Service, HarboraDbContext Db, Alert Rule) Build(
        HttpMessageHandler handler, AlertChannel channel = AlertChannel.Webhook,
        string target = """{"Url":"https://hooks.example.com/abc"}""", double timeoutSeconds = 10)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("notify-" + Guid.NewGuid()).Options);

        var rule = new Alert
        {
            WorkspaceId = Workspace, Name = "ops", Channel = channel,
            MinSeverity = AlertSeverity.Info, EncryptedTarget = target, IsEnabled = true
        };
        db.Alerts.Add(rule);
        db.SaveChanges();

        var service = new NotificationService(db, new PassthroughProtector(),
            new SingleHandlerFactory(handler),
            Microsoft.Extensions.Options.Options.Create(
                new NotificationOptions { DeliveryTimeoutSeconds = timeoutSeconds }),
            NullLogger<NotificationService>.Instance);
        return (service, db, rule);
    }

    // ---- the target the panel actually stores ----
    //
    // AlertsController serialises anonymous objects, so the stored JSON is camelCase ("url",
    // "botToken"). The service reads it into PascalCase records. System.Text.Json matches
    // case-sensitively by default, so every field came back null and every channel failed with
    // "not an absolute URL" — for as long as notifications have existed. It stayed invisible because
    // the failure was swallowed and the Test button reported success regardless. These use the exact
    // shape the controller writes, so the two sides cannot drift apart again.

    [Fact]
    public async Task A_webhook_target_written_by_the_panel_is_readable()
    {
        var handler = new Responder(HttpStatusCode.OK);
        var stored = System.Text.Json.JsonSerializer.Serialize(new { url = "https://hooks.example.com/abc" });
        var (service, _, rule) = Build(handler, AlertChannel.Webhook, stored);

        var result = await service.SendTestAsync(rule.Id, default);

        result.Delivered.Should().BeTrue(because: result.Error);
        handler.LastUrl.Should().Be("https://hooks.example.com/abc");
    }

    [Fact]
    public async Task A_telegram_target_written_by_the_panel_is_readable()
    {
        var handler = new Responder(HttpStatusCode.OK);
        var stored = System.Text.Json.JsonSerializer.Serialize(new { botToken = "123:abc", chatId = "-100" });
        var (service, _, rule) = Build(handler, AlertChannel.Telegram, stored);

        var result = await service.SendTestAsync(rule.Id, default);

        result.Delivered.Should().BeTrue(because: result.Error);
        handler.LastUrl.Should().Contain("123%3Aabc", "the token is a path segment and must be escaped");
    }

    [Fact]
    public async Task A_discord_target_written_by_the_panel_is_readable()
    {
        var handler = new Responder(HttpStatusCode.NoContent);
        var stored = System.Text.Json.JsonSerializer.Serialize(new { url = "https://discord.com/api/webhooks/1/x" });
        var (service, _, rule) = Build(handler, AlertChannel.Discord, stored);

        (await service.SendTestAsync(rule.Id, default)).Delivered.Should().BeTrue();
    }

    [Fact]
    public async Task A_webhook_that_accepts_the_message_is_reported_as_delivered()
    {
        var (service, db, rule) = Build(new Responder(HttpStatusCode.OK));

        var result = await service.SendTestAsync(rule.Id, default);

        result.Delivered.Should().BeTrue();
        result.Error.Should().BeNull();
        (await db.Alerts.FirstAsync()).LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_webhook_that_answers_404_is_not_reported_as_sent()
    {
        // The bug in one line: this used to be indistinguishable from success.
        var (service, db, rule) = Build(new Responder(HttpStatusCode.NotFound, "no such hook"));

        var result = await service.SendTestAsync(rule.Id, default);

        result.Delivered.Should().BeFalse();
        result.Error.Should().Contain("404");
        result.Error.Should().Contain("no such hook", "the endpoint's own words identify the mistake");
    }

    [Fact]
    public async Task An_html_error_page_is_not_pasted_into_the_message()
    {
        // Seen live: a 404 from a panel route returned a full themed page, and the alert message
        // became "...404 Not Found — <!DOCTYPE html>". The status is the useful part.
        var (service, _, rule) = Build(new Responder(HttpStatusCode.NotFound, "<!DOCTYPE html><html>…"));

        var result = await service.SendTestAsync(rule.Id, default);

        result.Error.Should().Contain("404");
        result.Error.Should().NotContain("DOCTYPE");
    }

    [Fact]
    public async Task A_server_error_from_the_channel_is_a_failure_too()
    {
        var (service, _, rule) = Build(new Responder(HttpStatusCode.InternalServerError));

        (await service.SendTestAsync(rule.Id, default)).Delivered.Should().BeFalse();
    }

    [Fact]
    public async Task Telegram_rejecting_the_chat_id_is_surfaced()
    {
        var (service, _, rule) = Build(
            new Responder(HttpStatusCode.BadRequest, """{"ok":false,"description":"chat not found"}"""),
            AlertChannel.Telegram, """{"BotToken":"123:abc","ChatId":"-100"}""");

        var result = await service.SendTestAsync(rule.Id, default);

        result.Delivered.Should().BeFalse();
        result.Error.Should().Contain("chat not found");
    }

    [Fact]
    public async Task A_failure_is_recorded_on_the_rule_so_the_page_can_show_it()
    {
        // Logs are not where the person configuring alerts is looking.
        var (service, db, rule) = Build(new Responder(HttpStatusCode.Forbidden));

        await service.NotifyAsync(Workspace, AlertEvent.DeployFailed, AlertSeverity.Critical, "t", "b", default);

        var stored = await db.Alerts.FirstAsync();
        stored.LastError.Should().Contain("403");
        stored.LastAttemptAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_channel_that_recovers_stops_being_marked_as_failing()
    {
        // Same rule, same context: an earlier version of this test built a second context for the
        // recovery, which meant it never actually watched an error being cleared.
        var handler = new Responder(HttpStatusCode.NotFound);
        var (service, db, rule) = Build(handler);

        await service.SendTestAsync(rule.Id, default);
        (await db.Alerts.FirstAsync()).LastError.Should().NotBeNull();

        handler.Status = HttpStatusCode.NoContent;
        await service.SendTestAsync(rule.Id, default);

        (await db.Alerts.FirstAsync()).LastError
            .Should().BeNull("a stale error would send someone hunting a problem that is already fixed");
    }

    [Fact]
    public async Task An_unreachable_channel_never_throws_into_the_caller()
    {
        // A deploy reporting its own failure must not fail again because the alert channel is down.
        var (service, _, _) = Build(new ThrowingHandler());

        var notify = async () => await service.NotifyAsync(
            Workspace, AlertEvent.DeployFailed, AlertSeverity.Critical, "t", "b", default);

        await notify.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_test_for_an_alert_that_no_longer_exists_says_so()
        => (await Build(new Responder(HttpStatusCode.OK)).Service.SendTestAsync(Guid.CreateVersion7(), default))
            .Delivered.Should().BeFalse();

    [Fact]
    public async Task An_internal_url_is_refused_before_any_request_is_made()
    {
        // The SSRF guard must run first: reaching a link-local address is the request we must not send.
        var handler = new Responder(HttpStatusCode.OK);
        var (service, _, rule) = Build(handler, AlertChannel.Webhook, """{"Url":"http://169.254.169.254/latest/meta-data/"}""");

        var result = await service.SendTestAsync(rule.Id, default);

        result.Delivered.Should().BeFalse();
        handler.Calls.Should().Be(0, "the point of the guard is that the call never happens");
    }

    [Fact]
    public async Task A_channel_that_never_answers_gives_up_instead_of_holding_the_caller()
    {
        // The deploy pipeline awaits this. Left unbounded it waits on the handler's 100-second
        // default, per alert rule, before it can report the failure that triggered the alert.
        var (service, db, rule) = Build(new HangingHandler(), timeoutSeconds: 0.15);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.SendTestAsync(rule.Id, default);
        started.Stop();

        result.Delivered.Should().BeFalse();
        result.Error.Should().Contain("did not respond");
        (await db.Alerts.FirstAsync()).LastError.Should().Contain("did not respond");

        // The wording alone is not enough: HttpClient's own 100-second default produces the very same
        // message eventually. Giving up *promptly* is the behaviour being bought here.
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "the configured timeout must be what ends the attempt");
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }
}
