using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 10, over a real request: the export queue, the self-serve download link's redirect
/// round-trip (the TempData int-unix idiom D4 already proved once), and import's typed-name gate and
/// capability split.
///
/// <para>
/// Background workers are removed from this harness (see <c>HarboraWebFactory</c>'s own doc comment),
/// so a queued export never actually completes here — <c>Export_queues_a_self_serve_dump_with_an_
/// expiry</c> proves only that the queue got the right row. The download-link tests seed a completed
/// export directly, the same way <c>VolumeBackupHttpTests</c>/<c>VolumeDownloadLinkHttpTests</c> seed
/// what a real backup job would have produced — proving the link's own redirect/redeem/expiry
/// behaviour without depending on a background worker this harness deliberately does not run.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class DatabaseExportImportHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static readonly Regex LinkAttribute =
        new("data-download-link=\"(?<link>[^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ExpiryAttribute =
        new("data-download-link-expires=\"(?<expires>[^\"]*)\"", RegexOptions.Compiled);

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }

    private (ManagedService Service, BackupDestination Destination) SeedDatabase(string slug, Guid? workspaceId = null)
    {
        var ws = workspaceId ?? fixture.WorkspaceId;
        var service = new ManagedService
        {
            WorkspaceId = ws,
            EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Type = ManagedServiceType.PostgreSql,
            Version = "16-alpine",
            ContainerName = $"harbora-svc-{slug}",
            VolumeName = $"harbora-svc-{slug}-data",
            InternalPort = 5432,
            Username = "harbora",
            EncryptedPassword = Panel.Resolve<ISecretProtector>().Protect("s3cret"),
            DatabaseName = slug,
            Status = ServiceStatus.Running
        };
        var destination = new BackupDestination
        {
            WorkspaceId = ws, Name = "local-" + slug, Type = BackupDestinationType.Local, IsDefault = true
        };
        Panel.Seed(db =>
        {
            db.ManagedServices.Add(service);
            db.BackupDestinations.Add(destination);
        });
        return (service, destination);
    }

    /// <summary>A completed self-serve export, with a real file on disk so the download route can
    /// actually stream it — exactly the state EnforceRetentionAsync/BackupEngine.RunAsync would have
    /// left behind, seeded directly because this harness runs no background worker.</summary>
    private Backup SeedCompletedExport(ManagedService service, BackupDestination destination, string content = "-- dump\n")
    {
        var path = Path.Combine(Path.GetTempPath(), "harbora-export-http-" + Guid.NewGuid().ToString("N") + ".sql.gz");
        File.WriteAllText(path, content);

        var backup = new Backup
        {
            WorkspaceId = service.WorkspaceId, DestinationId = destination.Id,
            Type = BackupType.Database, Status = BackupStatus.Completed,
            TargetRef = service.Id.ToString(), ArtifactPath = path,
            SizeBytes = new FileInfo(path).Length,
            FinishedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow + DatabaseExportPlan.ArtifactLifetime
        };
        Panel.Seed(db => db.Backups.Add(backup));
        return backup;
    }

    // ---- export queues the right thing --------------------------------------------------------

    [Fact]
    public async Task Export_queues_a_self_serve_dump_with_an_expiry()
    {
        var (service, _) = SeedDatabase("exp-queue");
        Panel.GivenUser(fixture.WorkspaceId, "exp-queue@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.150", "exp-queue@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{service.Id}");

        var response = await client.PostFormAsync($"/databases/{service.Id}/export", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/databases/{service.Id}");

        var queued = Panel.Read(db => db.Backups.Single(b => b.TargetRef == service.Id.ToString()));
        queued.Type.Should().Be(BackupType.Database);
        queued.WorkspaceId.Should().Be(service.WorkspaceId);
        queued.ExpiresAt.Should().NotBeNull(
            "a self-serve export is time-boxed — ordinary scheduled/manual backups never carry ExpiresAt");
        queued.IsScheduled.Should().BeFalse();
    }

    // ---- the download link's own redirect round-trip -------------------------------------------

    /// <summary>Mints a link through the real form POST and returns what the page shows for it —
    /// same shape as VolumeDownloadLinkHttpTests.MintAsync, retargeted at a backup.</summary>
    private async Task<(string Link, string Token, string ExpiresAt)> MintLinkAsync(
        HttpClient client, Guid serviceId, Guid backupId)
    {
        var token = await client.AntiforgeryTokenFrom($"/databases/{serviceId}");

        var response = await client.PostFormAsync(
            $"/databases/{serviceId}/export/{backupId}/link", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found, "minting redirects back to the database's page");

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();

        var linkMatch = LinkAttribute.Match(html);
        linkMatch.Success.Should().BeTrue(
            "the page must show the link it just minted — this is the redirect round-trip the TempData " +
            "int-unix value has to survive");
        var expiresMatch = ExpiryAttribute.Match(html);
        expiresMatch.Success.Should().BeTrue("the page must say when the one-shot link stops working");

        var link = linkMatch.Groups["link"].Value;
        var slash = link.LastIndexOf('/');
        return (link, link[(slash + 1)..], expiresMatch.Groups["expires"].Value);
    }

    [Fact]
    public async Task Minting_a_download_link_survives_the_redirect_and_shows_its_expiry()
    {
        var (service, destination) = SeedDatabase("exp-mint");
        var backup = SeedCompletedExport(service, destination);
        Panel.GivenUser(fixture.WorkspaceId, "exp-mint@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.151", "exp-mint@example.com");

        var (link, token, expiresAt) = await MintLinkAsync(client, service.Id, backup.Id);

        link.Should().Contain("/backups/download/",
            "the redemption route is deliberately outside the /databases/{id}/... prefix");
        token.Should().NotBeNullOrWhiteSpace();
        expiresAt.Should().NotBeNullOrWhiteSpace(
            "if TempData's int-unix value had round-tripped as a boxed DateTime instead of a string, " +
            "this would read empty — the exact D4 trap");

        var stored = Panel.Read(db => db.BackupDownloadTokens.Single(t => t.BackupId == backup.Id));
        stored.BackupId.Should().Be(backup.Id);
    }

    [Fact]
    public async Task The_minted_link_serves_the_export_to_a_client_that_has_never_signed_in()
    {
        var (service, destination) = SeedDatabase("exp-noauth");
        var backup = SeedCompletedExport(service, destination, "-- the exported dump\n");
        Panel.GivenUser(fixture.WorkspaceId, "exp-noauth@example.com", SystemRole.Owner);
        var owner = await Panel.SignedInAs("203.0.113.152", "exp-noauth@example.com");

        var (_, token, _) = await MintLinkAsync(owner, service.Id, backup.Id);

        // A client that never called SignedInAs — no cookie, no session — because that is the whole
        // point of a link this platform hands somebody with no panel access at all.
        var stranger = Panel.ClientFrom("203.0.113.153");

        var response = await stranger.GetAsync($"/backups/download/{token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the whole point of the link is that it works without a panel session");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        (await response.Content.ReadAsStringAsync()).Should().Be("-- the exported dump\n");
    }

    [Fact]
    public async Task Redeeming_the_same_export_link_a_second_time_404s()
    {
        var (service, destination) = SeedDatabase("exp-oneshot");
        var backup = SeedCompletedExport(service, destination);
        Panel.GivenUser(fixture.WorkspaceId, "exp-oneshot@example.com", SystemRole.Owner);
        var owner = await Panel.SignedInAs("203.0.113.154", "exp-oneshot@example.com");

        var (_, token, _) = await MintLinkAsync(owner, service.Id, backup.Id);
        var stranger = Panel.ClientFrom("203.0.113.155");

        var first = await stranger.GetAsync($"/backups/download/{token}");
        var second = await stranger.GetAsync($"/backups/download/{token}");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a shareable link can be forwarded, so 'used once' is what bounds where it ends up");
    }

    [Fact]
    public async Task A_download_link_past_its_hour_404s()
    {
        var (service, destination) = SeedDatabase("exp-expired");
        var backup = SeedCompletedExport(service, destination);
        Panel.GivenUser(fixture.WorkspaceId, "exp-expired@example.com", SystemRole.Owner);
        var owner = await Panel.SignedInAs("203.0.113.156", "exp-expired@example.com");

        var (_, token, _) = await MintLinkAsync(owner, service.Id, backup.Id);

        Panel.Seed(db =>
        {
            var stored = db.BackupDownloadTokens.Single(t => t.BackupId == backup.Id);
            stored.CreatedAt -= Harbora.Infrastructure.Services.AdminerSession.Lifetime + TimeSpan.FromMinutes(1);
        });

        var stranger = Panel.ClientFrom("203.0.113.157");
        var response = await stranger.GetAsync($"/backups/download/{token}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "past the lifetime it is refused whether or not it was ever used");
    }

    [Fact]
    public async Task An_unknown_download_token_404s_rather_than_leaking_which_reason_it_failed_for()
    {
        var stranger = Panel.ClientFrom("203.0.113.158");

        var response = await stranger.GetAsync("/backups/download/not-a-real-token-at-all");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "expired, spent and never-existed are one answer to the caller");
    }

    // ---- import refuses without the typed name ---------------------------------------------------

    [Fact]
    public async Task Import_refuses_without_the_typed_name_and_reads_nothing_from_the_upload()
    {
        var (service, destination) = SeedDatabase("imp-noconfirm");
        _ = destination;
        Panel.GivenUser(fixture.WorkspaceId, "imp-noconfirm@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", "imp-noconfirm@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{service.Id}");

        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), HttpConversation.AntiforgeryField },
            { new StringContent("not-the-right-name"), "confirmName" },
            { new StringContent("dump contents"), "file", "dump.sql.gz" }
        };

        var response = await client.PostAsync($"/databases/{service.Id}/import", content);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Backups.Any(b => b.TargetRef == service.Id.ToString())).Should().BeFalse(
            "an unconfirmed import must not read the upload at all, let alone restore it");
    }

    [Fact]
    public async Task Import_with_the_exact_name_restores_and_reaches_the_database_engine()
    {
        var (service, destination) = SeedDatabase("imp-confirm");
        _ = destination;
        Panel.GivenUser(fixture.WorkspaceId, "imp-confirm@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", "imp-confirm@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{service.Id}");

        Panel.Docker.OneOffExitCode = 0;

        // A real gzip stream, not plain text: RestoreAsync's own integrity gate (ProbeArchiveAsync)
        // decompresses a database artifact before touching anything, and refuses one that is not
        // valid gzip — the same gate that turns a torn or truncated archive into a 500 rather than a
        // silent wipe.
        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), HttpConversation.AntiforgeryField },
            { new StringContent(service.Name), "confirmName" },
            { new ByteArrayContent(Gzip("-- a real looking dump\n")), "file", "dump.sql.gz" }
        };

        var response = await client.PostAsync($"/databases/{service.Id}/import", content);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/databases/{service.Id}");

        // ImportAsync stores the uploaded bytes as a completed Backup regardless of what the fake
        // Docker engine does next — RestoreAsync then loads it back, which is a real one-off call
        // against this workspace's database engine.
        Panel.Read(db => db.Backups.Any(b => b.TargetRef == service.Id.ToString()
            && b.Status == BackupStatus.Completed)).Should().BeTrue();
        Panel.Docker.OneOffCommands.Should().NotBeEmpty("the restore must actually reach the engine");
    }

    // ---- capability split: an Operator can export but not import ---------------------------------

    [Fact]
    public async Task An_operator_can_export_but_not_import()
    {
        var (service, _) = SeedDatabase("cap-operator-db");
        Panel.GivenUser(fixture.WorkspaceId, "cap-op-export@example.com", SystemRole.Operator);
        var client = await Panel.SignedInAs("203.0.113.162", "cap-op-export@example.com");
        var exportToken = await client.AntiforgeryTokenFrom($"/databases/{service.Id}");

        var exportResponse = await client.PostFormAsync($"/databases/{service.Id}/export", exportToken);
        exportResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        exportResponse.RedirectPath().Should().Be($"/databases/{service.Id}",
            "backups.run is an operator's capability, so the export action itself answered");

        using var importContent = new MultipartFormDataContent
        {
            { new StringContent(exportToken), HttpConversation.AntiforgeryField },
            { new StringContent(service.Name), "confirmName" },
            { new StringContent("dump"), "file", "dump.sql.gz" }
        };
        var importResponse = await client.PostAsync($"/databases/{service.Id}/import", importContent);

        importResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        importResponse.RedirectPath().Should().Be("/account/denied",
            "backups.restore is the heavier capability an operator does not have");
    }
}
