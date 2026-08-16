using System.Net;
using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the CLI tells you when an upload is refused.
///
/// `DeployArchive` answers 404 or 403 before it reads a byte of the body. The connection is then torn
/// down while the CLI is still writing megabytes into it, the write fails, and HttpClient reports the
/// transport error — "Error while copying content to a stream." — with the real status discarded.
/// Two runs of a real deploy failed that way and named nothing. So: ask before sending, and if the
/// send still fails, say which request failed rather than stating a fact about a stream.
/// </summary>
public class ArchiveUploadTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(reply(request));
        }
    }

    private static string TempArchive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"harbora-{Guid.NewGuid():N}.tar.gz");
        File.WriteAllBytes(path, [0x1f, 0x8b, 0x08, 0x00]);
        return path;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    [Fact]
    public async Task The_server_is_asked_before_the_body_is_sent()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, """{"deploymentId":"abc"}"""));
        var api = new ApiClient("https://panel.example.com", "tok", stub);
        var archive = TempArchive();

        await api.PostFileAsync("apps/kousar-kolie/deploy/archive", archive);

        stub.Seen!.Headers.ExpectContinue.Should().BeTrue(
            "without it the server cannot refuse until megabytes are already on the wire");
        stub.Seen.RequestUri!.AbsolutePath.Should().Be("/api/v1/apps/kousar-kolie/deploy/archive");

        File.Delete(archive);
    }

    [Fact]
    public async Task A_refused_upload_reports_what_the_server_said()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.NotFound, """{"error":"App not found."}"""));
        var api = new ApiClient("https://panel.example.com", "tok", stub);
        var archive = TempArchive();

        var act = () => api.PostFileAsync("apps/Kousar-kolie/deploy/archive", archive);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("App not found");

        File.Delete(archive);
    }

    [Fact]
    public async Task A_connection_torn_down_mid_upload_names_the_request()
    {
        var stub = new StubHandler(_ => throw new HttpRequestException("Error while copying content to a stream."));
        var api = new ApiClient("https://panel.example.com", "tok", stub);
        var archive = TempArchive();

        var act = () => api.PostFileAsync("apps/kousar-kolie/deploy/archive", archive);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.Message.Should().Contain("apps/kousar-kolie/deploy/archive");
        thrown.InnerException.Should().NotBeNull("the transport detail is still worth keeping");

        File.Delete(archive);
    }
}
