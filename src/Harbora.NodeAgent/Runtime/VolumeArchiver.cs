using System.Text.RegularExpressions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Runtime;

public sealed record VolumeSnapshot(string SnapshotId, string Path, long SizeBytes, string Sha256, long DurationMs);

/// <summary>
/// Snapshots and restores named volumes using a short-lived helper container.
///
/// <para>
/// Archives land in a dedicated Docker volume rather than a host directory, which keeps the "the
/// agent never bind-mounts a host path" rule absolute instead of nearly-absolute. Every helper is
/// invoked with an argv array — the snapshot id is validated to a safe character set and is never
/// concatenated into a command line, so there is no shell for it to escape into.
/// </para>
/// </summary>
public sealed partial class VolumeArchiver(
    IOptions<NodeAgentOptions> options,
    IContainerRuntime runtime,
    TimeProvider clock,
    ILogger<VolumeArchiver> log)
{
    /// <summary>Volume the archives live in. Managed by the agent, never mounted into a workload.</summary>
    public const string ArchiveVolume = "harbora-node-snapshots";

    private const string SourceMount = "/source";
    private const string TargetMount = "/target";
    private const string ArchiveMount = "/snapshots";

    private readonly NodeAgentOptions _options = options.Value;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeId();

    public sealed class ArchiveException(NodeErrorCode code, string message) : Exception(message)
    {
        public NodeErrorCode Code { get; } = code;
    }

    public async Task<VolumeSnapshot> SnapshotAsync(
        string volumeName, string snapshotId, bool compress, IProgress<string>? progress, CancellationToken ct)
    {
        Guard(volumeName, snapshotId);

        if (!await runtime.VolumeExistsAsync(volumeName, ct))
            throw new ArchiveException(NodeErrorCode.VolumeOperationFailed, $"Volume '{volumeName}' does not exist on this node.");

        await runtime.EnsureVolumeAsync(ArchiveVolume, AgentLabels(), ct);

        var startedAt = clock.GetUtcNow();
        var archive = ArchivePath(snapshotId, compress);

        var tarFlags = compress ? "-czf" : "-cf";
        var exit = await RunAsync(
            ["tar", tarFlags, archive, "-C", SourceMount, "."],
            [new VolumeMount(volumeName, SourceMount, ReadOnly: true), new VolumeMount(ArchiveVolume, ArchiveMount, false)],
            progress, ct);

        if (exit != 0)
            throw new ArchiveException(NodeErrorCode.VolumeOperationFailed, $"Archiving volume '{volumeName}' exited {exit}.");

        var checksum = await ChecksumAsync(archive, ct);
        var size = await SizeAsync(archive, ct);

        var duration = (long)(clock.GetUtcNow() - startedAt).TotalMilliseconds;
        log.LogInformation("Snapshotted volume {Volume} to {Archive} ({Size} bytes) in {Duration}ms.", volumeName, archive, size, duration);

        return new VolumeSnapshot(snapshotId, $"{ArchiveVolume}:{archive}", size, checksum, duration);
    }

    /// <summary>
    /// Restore an archive over a volume.
    ///
    /// <para>
    /// The checksum is verified before a single byte is written. A corrupt archive that half-restores
    /// leaves a database in a state nobody has a name for, and the only moment that is still
    /// preventable is before the extraction starts.
    /// </para>
    /// </summary>
    public async Task RestoreAsync(
        string volumeName, string snapshotId, string expectedSha256, bool compressed,
        IProgress<string>? progress, CancellationToken ct)
    {
        Guard(volumeName, snapshotId);

        var archive = ArchivePath(snapshotId, compressed);
        var actual = await ChecksumAsync(archive, ct);

        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArchiveException(
                NodeErrorCode.VolumeOperationFailed,
                $"Snapshot '{snapshotId}' has checksum {actual}, expected {expectedSha256}. Nothing was written.");

        await runtime.EnsureVolumeAsync(volumeName, AgentLabels(), ct);

        // Extract first, then swap with same-filesystem renames. If the swap fails halfway the
        // original tree is moved back before this helper exits.
        const string restoreScript = """
            set -e
            STAGE=/target/.harbora-restore
            PREV=/target/.harbora-previous
            rm -rf "$STAGE" "$PREV"
            mkdir -p "$STAGE"
            tar "$TAR_FLAGS" "$ARCHIVE" -C "$STAGE"
            set +e
            mkdir -p "$PREV"
            find /target -mindepth 1 -maxdepth 1 ! -name .harbora-restore ! -name .harbora-previous -exec mv {} "$PREV"/ \;
            moved=$?
            find "$STAGE" -mindepth 1 -maxdepth 1 -exec mv {} /target/ \;
            placed=$?
            if [ $moved -ne 0 ] || [ $placed -ne 0 ]; then
              find /target -mindepth 1 -maxdepth 1 ! -name .harbora-restore ! -name .harbora-previous -exec rm -rf {} \;
              find "$PREV" -mindepth 1 -maxdepth 1 -exec mv {} /target/ \;
              rm -rf "$STAGE" "$PREV"
              exit 90
            fi
            rm -rf "$PREV" "$STAGE"
            """;

        var restored = await RunAsync(
            ["sh", "-ec", restoreScript],
            [new VolumeMount(volumeName, TargetMount, false), new VolumeMount(ArchiveVolume, ArchiveMount, ReadOnly: true)],
            progress, ct,
            new Dictionary<string, string>
            {
                ["TAR_FLAGS"] = compressed ? "-xzf" : "-xf",
                ["ARCHIVE"] = archive,
            });

        if (restored == 90)
            throw new ArchiveException(
                NodeErrorCode.VolumeOperationFailed,
                $"Restoring volume '{volumeName}' failed during the swap; its original contents were put back.");
        if (restored != 0)
            throw new ArchiveException(
                NodeErrorCode.VolumeOperationFailed,
                $"Restoring volume '{volumeName}' exited {restored}; its live contents were not replaced.");

        log.LogInformation("Restored volume {Volume} from snapshot {Snapshot}.", volumeName, snapshotId);
    }

    /// <summary>Path inside the helper container. Not a host path — the archive volume is Docker-managed.</summary>
    internal static string ArchivePathFor(string snapshotId, bool compress) =>
        $"{ArchiveMount}/{snapshotId}.tar{(compress ? ".gz" : string.Empty)}";

    /// <summary>Reads the identity of a staged archive without exposing its Docker volume.</summary>
    public async Task<(long SizeBytes, string Sha256)> InspectAsync(string snapshotId, bool compress, CancellationToken ct)
    {
        Guard("snapshot", snapshotId);
        var archive = ArchivePath(snapshotId, compress);
        return (await SizeAsync(archive, ct), await ChecksumAsync(archive, ct));
    }

    /// <summary>Best-effort cleanup after an archive has safely crossed the relay.</summary>
    public async Task DeleteSnapshotAsync(string snapshotId, bool compress, CancellationToken ct)
    {
        Guard("snapshot", snapshotId);
        var archive = ArchivePath(snapshotId, compress);
        var exit = await RunAsync(
            ["rm", "-f", archive],
            [new VolumeMount(ArchiveVolume, ArchiveMount, false)], null, ct);
        if (exit != 0)
            log.LogWarning("Could not remove staged snapshot {Snapshot}; helper exited {Exit}.", snapshotId, exit);
    }

    private static string ArchivePath(string snapshotId, bool compress) => ArchivePathFor(snapshotId, compress);

    private async Task<string> ChecksumAsync(string archive, CancellationToken ct)
    {
        var output = new List<string>();

        var exit = await RunAsync(
            ["sha256sum", archive],
            [new VolumeMount(ArchiveVolume, ArchiveMount, ReadOnly: true)],
            new CaptureProgress(output), ct);

        if (exit != 0)
            throw new ArchiveException(NodeErrorCode.VolumeOperationFailed, $"Checksumming '{archive}' exited {exit}.");

        // "sha256sum" prints "<hex>  <path>".
        var hex = output.Select(line => line.Trim().Split(' ')[0])
            .FirstOrDefault(candidate => candidate.Length == 64);

        return hex ?? throw new ArchiveException(
            NodeErrorCode.VolumeOperationFailed, $"Could not read a checksum for '{archive}'.");
    }

    private async Task<long> SizeAsync(string archive, CancellationToken ct)
    {
        var output = new List<string>();

        var exit = await RunAsync(
            ["stat", "-c", "%s", archive],
            [new VolumeMount(ArchiveVolume, ArchiveMount, ReadOnly: true)],
            new CaptureProgress(output), ct);

        if (exit != 0) return 0;

        return output.Select(line => long.TryParse(line.Trim(), out var value) ? value : 0)
            .FirstOrDefault(value => value > 0);
    }

    private Task<int> RunAsync(
        IReadOnlyList<string> argv, IReadOnlyList<VolumeMount> mounts, IProgress<string>? progress,
        CancellationToken ct, IReadOnlyDictionary<string, string>? env = null) =>
        runtime.RunOneOffAsync(new OneOffRequest
        {
            ImageReference = _options.MaintenanceImage,
            Command = argv,
            Mounts = mounts,
            Env = env ?? new Dictionary<string, string>(),
            Labels = AgentLabels(),
            Resources = new ResourceLimits { MemoryBytes = 256 * 1024 * 1024, PidsLimit = 64 },
            TimeoutSeconds = 3600,
        }, progress, ct);

    private static Dictionary<string, string> AgentLabels() => new()
    {
        [NodeLabels.Managed] = "true",
        [NodeLabels.Tenant] = "harbora-system",
    };

    private sealed class CaptureProgress(List<string> output) : IProgress<string>
    {
        public void Report(string value) => output.Add(value);
    }

    /// <summary>
    /// Names reaching a helper's argv must be plain. Even without a shell, a value like
    /// <c>../../etc</c> in a path argument is a directory traversal the helper would honour.
    /// </summary>
    private static void Guard(string volumeName, string snapshotId)
    {
        if (!SafeId().IsMatch(volumeName))
            throw new ArchiveException(NodeErrorCode.ValidationFailed, $"'{volumeName}' is not a valid volume name.");

        if (!SafeId().IsMatch(snapshotId))
            throw new ArchiveException(NodeErrorCode.ValidationFailed, $"'{snapshotId}' is not a valid snapshot id.");
    }
}
