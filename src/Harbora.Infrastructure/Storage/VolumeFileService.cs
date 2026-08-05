using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Deployments;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Storage;

/// <summary>What happened, and what to tell somebody when it did not work.</summary>
public sealed record VolumeFileOutcome(bool Ok, string? Reason)
{
    public static readonly VolumeFileOutcome Success = new(true, null);
    public static VolumeFileOutcome Refused(string reason) => new(false, reason);
}

/// <summary>
/// Reading and writing inside a named volume.
///
/// A Docker volume has no path on the host that the platform is allowed to assume, so every
/// operation runs in a throwaway container with the volume mounted — the same approach that
/// measures a volume's size. That is also what makes this work on a node: the helper runs wherever
/// the volume is, and the engine seam decides where that is.
///
/// Reads mount the volume read-only. Not a formality: a listing that could modify what it is
/// listing is a listing somebody will eventually run against production to find out what is there.
/// </summary>
public sealed class VolumeFileService(
    IServerEngineFactory engines,
    ILogger<VolumeFileService> log)
{
    /// <summary>
    /// The largest file this will read into the browser or accept as an upload.
    ///
    /// It exists because both directions go through base64 in memory, on the panel, for one
    /// request. A volume can hold a 40 GB database file and somebody will click it.
    /// </summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    public async Task<IReadOnlyList<VolumeEntry>> ListAsync(
        Guid serverId, string volumeName, string normalisedPath, CancellationToken ct)
    {
        var output = new StringBuilder();

        var exit = await RunAsync(serverId, volumeName, readOnly: true,
            VolumeFileCommands.Listing(VolumeFileCommands.MountRoot + Suffix(normalisedPath)),
            output, ct);

        // A directory that is not there and a directory that is empty both list nothing. The
        // caller cannot act differently on the two, and inventing an error for the first would
        // make a volume that has not been written to yet look broken.
        return exit == 0 ? VolumeFileCommands.ParseListing(output.ToString()) : [];
    }

    /// <summary>The bytes of one file, or null when it could not be read.</summary>
    public async Task<byte[]?> ReadAsync(
        Guid serverId, string volumeName, string normalisedPath, CancellationToken ct)
    {
        var output = new StringBuilder();

        var exit = await RunAsync(serverId, volumeName, readOnly: true,
            VolumeFileCommands.Read(VolumeFileCommands.MountRoot + Suffix(normalisedPath)),
            output, ct);

        if (exit != 0) return null;

        // Through the same rule the listing goes through, for the same reason: the stream is framed
        // and decoding it raw throws, after which the read returns nothing and the browser is told
        // the file does not exist.
        var content = VolumeFileCommands.ParseBase64(output.ToString());
        if (content is null)
            log.LogWarning("Unreadable base64 while reading {Path} from {Volume}.", normalisedPath, volumeName);

        return content;
    }

    public async Task<VolumeFileOutcome> WriteAsync(
        Guid serverId, string volumeName, string normalisedPath, byte[] content, CancellationToken ct)
    {
        if (normalisedPath.Length == 0) return VolumeFileOutcome.Refused("A file needs a name.");
        if (content.LongLength > MaxFileBytes)
            return VolumeFileOutcome.Refused($"Files larger than {MaxFileBytes / 1024 / 1024} MB cannot be uploaded here.");

        var exit = await RunAsync(serverId, volumeName, readOnly: false,
            VolumeFileCommands.Write(
                VolumeFileCommands.MountRoot + Suffix(normalisedPath), Convert.ToBase64String(content)),
            new StringBuilder(), ct);

        return exit == 0
            ? VolumeFileOutcome.Success
            : VolumeFileOutcome.Refused("The file could not be written. The volume may be full or read-only.");
    }

    public async Task<VolumeFileOutcome> DeleteAsync(
        Guid serverId, string volumeName, string normalisedPath, CancellationToken ct)
    {
        // Deleting the root would empty the volume, which is not what any button on the page says.
        if (normalisedPath.Length == 0)
            return VolumeFileOutcome.Refused("The root of a volume cannot be deleted from here.");

        var exit = await RunAsync(serverId, volumeName, readOnly: false,
            VolumeFileCommands.Delete(VolumeFileCommands.MountRoot + Suffix(normalisedPath)),
            new StringBuilder(), ct);

        return exit == 0 ? VolumeFileOutcome.Success : VolumeFileOutcome.Refused("It could not be removed.");
    }

    public async Task<VolumeFileOutcome> MakeDirectoryAsync(
        Guid serverId, string volumeName, string normalisedPath, CancellationToken ct)
    {
        if (normalisedPath.Length == 0) return VolumeFileOutcome.Refused("A folder needs a name.");

        var exit = await RunAsync(serverId, volumeName, readOnly: false,
            VolumeFileCommands.MakeDirectory(VolumeFileCommands.MountRoot + Suffix(normalisedPath)),
            new StringBuilder(), ct);

        return exit == 0 ? VolumeFileOutcome.Success : VolumeFileOutcome.Refused("The folder could not be created.");
    }

    private static string Suffix(string normalisedPath) =>
        normalisedPath.Length == 0 ? string.Empty : "/" + normalisedPath;

    private async Task<int> RunAsync(
        Guid serverId, string volumeName, bool readOnly,
        IReadOnlyList<string> command, StringBuilder output, CancellationToken ct)
    {
        var docker = await engines.ResolveAsync(serverId, ct);

        try
        {
            return await docker.RunOneOffAsync(
                new DockerOneOffRequest(
                    VolumeFileCommands.HelperImage, command,
                    [(volumeName, VolumeFileCommands.MountRoot, readOnly)]),
                new InlineProgress<string>(line => { lock (output) output.AppendLine(line); }),
                ct);
        }
        catch (Exception e)
        {
            // A node that cannot run a one-off refuses by name rather than failing obscurely, and
            // that refusal has to reach the page as "this node cannot do it" rather than as a 500.
            log.LogWarning(e, "Volume file operation failed on {Volume}.", volumeName);
            return -1;
        }
    }
}
