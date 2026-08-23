using System.Formats.Tar;
using Docker.DotNet;
using Docker.DotNet.Models;
using Harbora.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// <c>docker cp</c> semantics for one file, backed directly by Docker.DotNet's container-archive
/// endpoints (<c>GetArchiveFromContainerAsync</c>/<c>ExtractArchiveToContainerAsync</c>) — the
/// standard Docker Engine API operation for reading/writing a container's own filesystem without
/// touching the image, already exposed by the client library this codebase uses everywhere else
/// (<see cref="DockerEngine"/>), just not previously wrapped for this purpose.
///
/// <para>
/// This is what lets C2 (2026-08-22 config-delivery plan) replace a value inside
/// <c>appsettings.json</c> without rebuilding the image: the file is read out of a freshly created
/// (not yet started) container, patched in memory by the matching <c>IConfigFileEditor</c>, and
/// written back before <c>StartContainerAsync</c> ever runs the app's own process — so the
/// placeholder in the image is never what the app actually reads.
/// </para>
///
/// <para>
/// <b>Unverified without a live daemon.</b> There is no Docker available in this development
/// environment, so this class has been written against Docker.DotNet's documented API shape and
/// exercised only by the format editors' own unit tests and by <c>PipelineFakes</c>' test double —
/// never against a real container. Say so plainly rather than claiming more.
/// </para>
/// </summary>
public sealed class DockerContainerConfigFileWriter(IDockerClient client, ILogger<DockerContainerConfigFileWriter> logger)
    : IContainerConfigFileWriter
{
    public async Task<byte[]?> ReadFileAsync(string containerNameOrId, string absolutePath, CancellationToken ct)
    {
        Stream tarStream;
        try
        {
            var response = await client.Containers.GetArchiveFromContainerAsync(
                containerNameOrId, new GetArchiveFromContainerParameters { Path = absolutePath }, statOnly: false, ct);
            tarStream = response.Stream;
        }
        catch (DockerContainerNotFoundException) { return null; }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404) { return null; }

        await using (tarStream)
        await using (var reader = new TarReader(tarStream))
        {
            var entry = await reader.GetNextEntryAsync(cancellationToken: ct);
            if (entry?.DataStream is null) return null;

            using var buffer = new MemoryStream();
            await entry.DataStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
    }

    public async Task WriteFileAsync(string containerNameOrId, string absolutePath, byte[] content, CancellationToken ct)
    {
        var relativeName = absolutePath.TrimStart('/');

        await using var tarBuffer = new MemoryStream();
        await using (var writer = new TarWriter(tarBuffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, relativeName)
            {
                DataStream = new MemoryStream(content)
            };
            await writer.WriteEntryAsync(entry, ct);
        }
        tarBuffer.Position = 0;

        await client.Containers.ExtractArchiveToContainerAsync(
            containerNameOrId,
            new ContainerPathStatParameters { Path = "/", AllowOverwriteDirWithFile = false },
            tarBuffer, ct);

        logger.LogInformation("Wrote {Path} into container {Container}.", absolutePath, containerNameOrId);
    }

    public async Task<IReadOnlyList<string>?> ListDirectoryAsync(string containerNameOrId, string absoluteDirectoryPath, CancellationToken ct)
    {
        Stream tarStream;
        try
        {
            var response = await client.Containers.GetArchiveFromContainerAsync(
                containerNameOrId, new GetArchiveFromContainerParameters { Path = absoluteDirectoryPath }, statOnly: false, ct);
            tarStream = response.Stream;
        }
        catch (DockerContainerNotFoundException) { return null; }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404) { return null; }

        var names = new List<string>();
        await using (tarStream)
        await using (var reader = new TarReader(tarStream))
        {
            // Docker's archive of a directory contains the directory itself as the first entry, then
            // every descendant prefixed by its base name. Only the direct children — one path segment
            // past that prefix — are what "what is actually in this directory" means here.
            TarEntry? entry;
            string? rootPrefix = null;
            while ((entry = await reader.GetNextEntryAsync(cancellationToken: ct)) is not null)
            {
                var name = entry.Name.TrimEnd('/');
                if (rootPrefix is null) { rootPrefix = name; continue; }

                if (!name.StartsWith(rootPrefix + "/", StringComparison.Ordinal)) continue;
                var remainder = name[(rootPrefix.Length + 1)..];
                if (remainder.Length > 0 && !remainder.Contains('/'))
                    names.Add(remainder);
            }
        }

        return names;
    }
}
