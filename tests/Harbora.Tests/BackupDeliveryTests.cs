using System.Net;
using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sending a copy of each scheduled backup to Telegram or email.
///
/// The failure mode worth designing against is a channel that looks configured and silently carries
/// nothing: Telegram refuses documents over 50 MB, and mail servers refuse attachments long before
/// that. A backup channel nobody can tell is broken is worse than no backup channel.
/// </summary>
public class BackupDeliveryTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private sealed class Responder(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public int Calls;
        public string? LastUrl;
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

    private static (BackupDeliveryService Service, HarboraDbContext Db, BackupDelivery Channel, string File) Build(
        HttpMessageHandler handler, long fileBytes = 1024, long maxSizeBytes = 0,
        string config = """{"botToken":"123:abc","chatId":"-1001"}""")
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("delivery-" + Guid.NewGuid()).Options);

        var channel = new BackupDelivery
        {
            WorkspaceId = Workspace, Name = "ops chat", Channel = BackupDeliveryChannel.Telegram,
            EncryptedConfig = config, IsEnabled = true, MaxSizeBytes = maxSizeBytes
        };
        db.BackupDeliveries.Add(channel);
        db.SaveChanges();

        var path = Path.Combine(Path.GetTempPath(), $"harbora-delivery-{Guid.NewGuid():N}.tgz");
        File.WriteAllBytes(path, new byte[fileBytes]);

        var service = new BackupDeliveryService(db, new PassthroughProtector(),
            new SingleHandlerFactory(handler), new FixedClock(), NullLogger<BackupDeliveryService>.Instance);

        return (service, db, channel, path);
    }

    private static Backup ABackup() =>
        new() { WorkspaceId = Workspace, Type = BackupType.Database, TargetRef = "shop-db" };

    // ---- will it fit ----

    [Fact]
    public void An_artifact_within_the_limit_is_not_rejected()
        => DeliveryPlan.RejectionReason(BackupDeliveryChannel.Telegram, 0, 10 * 1024 * 1024).Should().BeNull();

    [Fact]
    public void An_artifact_over_telegrams_ceiling_is_refused_with_both_numbers()
    {
        var reason = DeliveryPlan.RejectionReason(BackupDeliveryChannel.Telegram, 0, 180L * 1024 * 1024);

        reason.Should().NotBeNull();
        reason.Should().Contain("180").And.Contain("50");
        reason.Should().Contain("storage destination",
            "the answer is never 'make your backup smaller', so the message has to point somewhere");
    }

    [Fact]
    public void Email_is_held_to_a_smaller_default_than_telegram()
    {
        // Mail servers reject attachments long before 50 MB, so accepting one that size would just
        // move the failure to somewhere nobody is looking.
        DeliveryPlan.DefaultLimitFor(BackupDeliveryChannel.Email)
            .Should().BeLessThan(DeliveryPlan.DefaultLimitFor(BackupDeliveryChannel.Telegram));
    }

    [Fact]
    public void A_configured_limit_overrides_the_channel_default()
    {
        // A self-hosted mail server may allow far more, or far less, than the assumption.
        DeliveryPlan.LimitFor(BackupDeliveryChannel.Email, 100L * 1024 * 1024)
            .Should().Be(100L * 1024 * 1024);

        DeliveryPlan.LimitFor(BackupDeliveryChannel.Email, 0)
            .Should().Be(DeliveryPlan.EmailMaxBytes, "0 means 'use the channel's own ceiling'");
    }

    [Fact]
    public void The_caption_says_which_instance_and_what_was_backed_up()
    {
        // A chat that receives backups from several places is otherwise a pile of identical files.
        var caption = DeliveryPlan.Caption("panel.example.com", BackupType.Database, "shop-db",
            5L * 1024 * 1024, new DateTimeOffset(2026, 7, 31, 3, 0, 0, TimeSpan.Zero));

        caption.Should().Contain("panel.example.com").And.Contain("Database").And.Contain("shop-db");
        caption.Should().Contain("2026-07-31").And.Contain("5 MB");
    }

    // ---- sending it ----

    [Fact]
    public async Task A_delivered_backup_leaves_no_error_on_the_channel()
    {
        var (service, db, channel, file) = Build(new Responder(HttpStatusCode.OK));
        try
        {
            var result = await service.SendAsync(channel, ABackup(), file, default);

            result.Delivered.Should().BeTrue(because: result.Error);
            (await db.BackupDeliveries.FirstAsync()).LastError.Should().BeNull();
            (await db.BackupDeliveries.FirstAsync()).LastAttemptAt.Should().NotBeNull();
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Telegram_refusing_the_chat_is_recorded_verbatim()
    {
        // "chat not found" means the recipient never pressed Start. No rewording of ours would help
        // more than Telegram's own answer.
        var (service, db, channel, file) = Build(
            new Responder(HttpStatusCode.BadRequest, """{"ok":false,"description":"chat not found"}"""));
        try
        {
            var result = await service.SendAsync(channel, ABackup(), file, default);

            result.Delivered.Should().BeFalse();
            result.Error.Should().Contain("chat not found");
            (await db.BackupDeliveries.FirstAsync()).LastError.Should().Contain("chat not found");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task An_oversized_artifact_is_never_uploaded()
    {
        // Refused before the request, not after a long upload that was always going to be rejected.
        var handler = new Responder(HttpStatusCode.OK);
        var (service, _, channel, file) = Build(handler, fileBytes: 2 * 1024 * 1024, maxSizeBytes: 1024 * 1024);
        try
        {
            var result = await service.SendAsync(channel, ABackup(), file, default);

            result.Delivered.Should().BeFalse();
            result.Error.Should().Contain("accepts at most");
            handler.Calls.Should().Be(0);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task A_missing_artifact_is_reported_rather_than_sent_as_nothing()
    {
        var (service, _, channel, file) = Build(new Responder(HttpStatusCode.OK));
        File.Delete(file);

        var result = await service.SendAsync(channel, ABackup(), file, default);

        result.Delivered.Should().BeFalse();
        result.Error.Should().Contain("not readable");
    }

    [Fact]
    public async Task A_channel_missing_its_settings_says_what_is_missing()
    {
        var (service, _, channel, file) = Build(new Responder(HttpStatusCode.OK), config: """{"botToken":"123:abc"}""");
        try
        {
            var result = await service.SendAsync(channel, ABackup(), file, default);

            result.Delivered.Should().BeFalse();
            result.Error.Should().Contain("chat id");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task The_bot_token_is_escaped_into_the_url()
    {
        // It is a path segment containing a colon; an unescaped one produces a request to a URL that
        // is not the API call anyone intended.
        var handler = new Responder(HttpStatusCode.OK);
        var (service, _, channel, file) = Build(handler);
        try
        {
            await service.SendAsync(channel, ABackup(), file, default);

            handler.LastUrl.Should().Contain("123%3Aabc").And.Contain("/sendDocument");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task A_disabled_channel_receives_nothing()
    {
        var handler = new Responder(HttpStatusCode.OK);
        var (service, db, channel, file) = Build(handler);
        try
        {
            channel.IsEnabled = false;
            await db.SaveChangesAsync();

            await service.DeliverAsync(ABackup(), file, default);

            handler.Calls.Should().Be(0);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Another_workspaces_channel_is_not_used()
    {
        // Backups are the last thing that should leak across tenants.
        var handler = new Responder(HttpStatusCode.OK);
        var (service, _, _, file) = Build(handler);
        try
        {
            var someoneElse = new Backup { WorkspaceId = Guid.CreateVersion7(), Type = BackupType.Database };

            await service.DeliverAsync(someoneElse, file, default);

            handler.Calls.Should().Be(0);
        }
        finally { File.Delete(file); }
    }

    // ---- the engine actually hands finished backups over ----

    [Fact]
    public async Task A_finished_backup_is_delivered_to_the_channel()
    {
        // The wiring, end to end: a real backup runs through the engine and the channel receives the
        // artifact. Without this, "delivery is configured" and "delivery happens" are separate claims.
        using var h = new BackupHarness();
        h.Db.BackupDeliveries.Add(new BackupDelivery
        {
            WorkspaceId = h.WorkspaceId, Name = "ops chat", Channel = BackupDeliveryChannel.Telegram,
            EncryptedConfig = """{"botToken":"123:abc","chatId":"-1001"}""", IsEnabled = true
        });
        await h.Db.SaveChangesAsync();

        var id = await h.Engine().QueueBackupAsync(
            h.WorkspaceId, BackupType.FullPlatform, "platform", h.Destination.Id, scheduled: true, default);
        await h.Engine().RunAsync(id, default);

        var backup = await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == id);
        backup.Status.Should().Be(BackupStatus.Completed);
        h.DeliveryHttp.RequestedUrls.Should().Contain(u => u.Contains("/sendDocument"),
            "a scheduled backup nobody receives is the failure this feature exists to prevent");
    }

    [Fact]
    public async Task A_failing_channel_does_not_fail_the_backup()
    {
        // The artifact is already stored by then. A chat being unreachable must not turn a backup
        // that succeeded into one the history records as failed.
        using var h = new BackupHarness();
        h.DeliveryHttp.Status = System.Net.HttpStatusCode.BadRequest;
        h.Db.BackupDeliveries.Add(new BackupDelivery
        {
            WorkspaceId = h.WorkspaceId, Name = "broken", Channel = BackupDeliveryChannel.Telegram,
            EncryptedConfig = """{"botToken":"123:abc","chatId":"-1001"}""", IsEnabled = true
        });
        await h.Db.SaveChangesAsync();

        var id = await h.Engine().QueueBackupAsync(
            h.WorkspaceId, BackupType.FullPlatform, "platform", h.Destination.Id, scheduled: true, default);
        await h.Engine().RunAsync(id, default);

        (await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == id)).Status
            .Should().Be(BackupStatus.Completed);
        (await h.Db.BackupDeliveries.AsNoTracking().FirstAsync()).LastError
            .Should().NotBeNull("but the channel itself must show that it is failing");
    }
}
