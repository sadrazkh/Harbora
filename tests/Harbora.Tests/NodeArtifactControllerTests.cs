using System.Text;
using FluentAssertions;
using Harbora.Infrastructure.Backups;
using Harbora.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

public sealed class NodeArtifactControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-relay-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task Upload_chunks_must_be_contiguous_and_completion_consumes_the_ticket()
    {
        Directory.CreateDirectory(_root);
        var registry = new ArtifactRelayRegistry(TimeProvider.System);
        var destination = Path.Combine(_root, "snapshot.tgz");
        var ticket = registry.CreateUpload(destination);

        var first = Controller(registry, ticket.Token, "abc", offset: 0);
        (await first.UploadChunk(ticket.Id, default)).Should().BeOfType<OkObjectResult>();

        var wrongOffset = Controller(registry, ticket.Token, "ignored", offset: 1);
        (await wrongOffset.UploadChunk(ticket.Id, default)).Should().BeOfType<ConflictObjectResult>();

        var second = Controller(registry, ticket.Token, "def", offset: 3);
        (await second.UploadChunk(ticket.Id, default)).Should().BeOfType<OkObjectResult>();
        (await second.CompleteUpload(ticket.Id, default)).Should().BeOfType<OkObjectResult>();

        (await File.ReadAllTextAsync(destination)).Should().Be("abcdef");
        var replay = Controller(registry, ticket.Token, "x", offset: 6);
        (await replay.UploadChunk(ticket.Id, default)).Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task A_wrong_bearer_token_cannot_create_a_partial_file()
    {
        Directory.CreateDirectory(_root);
        var registry = new ArtifactRelayRegistry(TimeProvider.System);
        var destination = Path.Combine(_root, "snapshot.tgz");
        var ticket = registry.CreateUpload(destination);

        var controller = Controller(registry, new string('f', 64), "secret bytes", offset: 0);

        (await controller.UploadChunk(ticket.Id, default)).Should().BeOfType<UnauthorizedResult>();
        Directory.EnumerateFiles(_root).Should().BeEmpty();
    }

    private static NodeArtifactController Controller(
        ArtifactRelayRegistry registry, string token, string body, long offset)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + token;
        context.Request.Headers["X-Artifact-Offset"] = offset.ToString();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;

        return new NodeArtifactController(registry, NullLogger<NodeArtifactController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
