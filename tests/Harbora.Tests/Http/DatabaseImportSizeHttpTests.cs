using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Round nine, item three, over a real request: a database import larger than ASP.NET's 128 MB form
/// default gets as far as the action. Before, the antiforgery check read the form first, hit that
/// default and answered a bare 400, so the import that says "up to 2 GB" refused anything over 128 MB.
///
/// <para>
/// The body is generated as it is sent rather than held in memory, and the typed name is wrong on
/// purpose: the action then refuses before it reads the file, so the only thing proved is the one in
/// question — that the form was read whole — and nothing is restored or stored. The form reader's
/// own temporary file for it goes when the request does. Two gigabytes is not sent: that is stated as
/// unverified, and the refusal above the ceiling is held by <see cref="UploadLimitTests"/>.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class DatabaseImportSizeHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private const long OverTheFormDefault = 129L * 1024 * 1024;

    [Fact]
    public async Task An_import_larger_than_the_form_reader_s_128_MB_default_reaches_the_action()
    {
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId, ServerId = Guid.CreateVersion7(),
            Name = "imp-large", Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
            ContainerName = "harbora-svc-imp-large", VolumeName = "harbora-svc-imp-large-data", InternalPort = 5432,
            Username = "harbora", EncryptedPassword = Panel.Resolve<ISecretProtector>().Protect("s3cret"),
            DatabaseName = "imp_large", Status = ServiceStatus.Running
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        Panel.GivenUser(fixture.WorkspaceId, "imp-large@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.190", "imp-large@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{service.Id}");

        var file = new StreamContent(new ZeroStream(OverTheFormDefault));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), HttpConversation.AntiforgeryField },
            { new StringContent("not-the-right-name"), "confirmName" },
            { file, "file", "dump.sql.gz" }
        };

        var response = await client.PostAsync($"/databases/{service.Id}/import", content);

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "a 400 here is the form reader's default refusing inside the antiforgery check");
        response.RedirectPath().Should().Be($"/databases/{service.Id}");
        Panel.Read(db => db.Backups.Any(b => b.TargetRef == service.Id.ToString() && b.Type == BackupType.Database))
            .Should().BeFalse("an unconfirmed import stores nothing, however large");

        // The action's own refusal, not the size refusal: the redirect alone would look the same if
        // the form had stopped at 128 MB and the size guard had answered instead.
        var page = WebUtility.HtmlDecode(await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync());
        page.Should().Contain("وارد کردن تأیید نشد");
    }

    /// <summary>A body of zeros of a given length, produced as it is read.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
