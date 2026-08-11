using Harbora.Infrastructure.Backups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers.Api;

/// <summary>Chunked, one-use artifact transport for enrolled nodes.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/node-artifacts")]
public sealed class NodeArtifactController(ArtifactRelayRegistry relays, ILogger<NodeArtifactController> log)
    : ControllerBase
{
    private const long MaxChunkBytes = 32L * 1024 * 1024;

    [HttpPatch("{id:guid}")]
    [RequestSizeLimit(MaxChunkBytes)]
    public async Task<IActionResult> UploadChunk(Guid id, CancellationToken ct)
    {
        if (!relays.TryAuthorize(id, BearerToken(), ArtifactRelayDirection.UploadToPanel, out var lease))
            return Unauthorized();
        if (!long.TryParse(Request.Headers["X-Artifact-Offset"], out var offset) || offset < 0)
            return BadRequest("X-Artifact-Offset is required.");
        if (Request.ContentLength is > MaxChunkBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        await lease!.Gate.WaitAsync(ct);
        try
        {
            var temporary = ArtifactRelayRegistry.PartialPath(lease);
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            await using var output = new FileStream(
                temporary, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (output.Length != offset)
                return Conflict(new { expectedOffset = output.Length });
            output.Position = offset;

            var buffer = new byte[1024 * 1024];
            long written = 0;
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > MaxChunkBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);
            return Ok(new { nextOffset = offset + written });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Node artifact chunk {RelayId} failed.", id);
            return Problem("The artifact chunk could not be received.", statusCode: 500);
        }
        finally
        {
            lease.Gate.Release();
        }
    }

    [HttpPost("{id:guid}/upload/complete")]
    public async Task<IActionResult> CompleteUpload(Guid id, CancellationToken ct)
    {
        var token = BearerToken();
        if (!relays.TryAuthorize(id, token, ArtifactRelayDirection.UploadToPanel, out var lease))
            return Unauthorized();

        await lease!.Gate.WaitAsync(ct);
        try
        {
            var partial = ArtifactRelayRegistry.PartialPath(lease);
            if (!System.IO.File.Exists(partial)) return NotFound();
            System.IO.File.Move(partial, lease.Path, overwrite: true);
            if (!relays.TryConsume(id, token, ArtifactRelayDirection.UploadToPanel, out _))
                return Unauthorized();
            return Ok(new { bytes = new FileInfo(lease.Path).Length });
        }
        finally
        {
            lease.Gate.Release();
        }
    }

    [HttpGet("{id:guid}")]
    public IActionResult DownloadChunk(Guid id)
    {
        if (!relays.TryAuthorize(id, BearerToken(), ArtifactRelayDirection.DownloadFromPanel, out var lease))
            return Unauthorized();
        if (!System.IO.File.Exists(lease!.Path)) return NotFound();
        return PhysicalFile(lease.Path, "application/gzip", enableRangeProcessing: true);
    }

    [HttpPost("{id:guid}/download/complete")]
    public IActionResult CompleteDownload(Guid id)
    {
        return relays.TryConsume(id, BearerToken(), ArtifactRelayDirection.DownloadFromPanel, out _)
            ? Ok()
            : Unauthorized();
    }

    private string? BearerToken()
    {
        var value = Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value[7..].Trim()
            : null;
    }
}
