using System.Text.RegularExpressions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Security;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Runtime;

/// <summary>
/// Exchanges a snapshot with the node's configured control plane. The command cannot name another
/// host, and repository credentials never reach the node.
/// </summary>
public sealed partial class ArtifactRelayTransfer(
    IContainerRuntime runtime,
    VolumeArchiver archiver,
    SecretRedactor redactor,
    IOptions<NodeAgentOptions> options)
{
    private readonly NodeAgentOptions _options = options.Value;

    [GeneratedRegex("^[A-Fa-f0-9]{64}$")]
    private static partial Regex SafeToken();

    public async Task<TransferSnapshotResult> TransferAsync(TransferSnapshotRequest request, CancellationToken ct)
    {
        if (!SafeToken().IsMatch(request.RelayToken))
            throw new VolumeArchiver.ArchiveException(NodeErrorCode.ValidationFailed, "The relay token is invalid.");

        redactor.Register(request.RelayToken);
        await runtime.EnsureVolumeAsync(VolumeArchiver.ArchiveVolume, AgentLabels(), ct);
        await runtime.PullImageAsync(_options.ArtifactTransferImage, null, ct);

        var relayUri = new Uri(
            new Uri(_options.ControlPlaneUrl.TrimEnd('/') + "/"),
            $"api/node-artifacts/{request.RelayId:D}");
        var archive = VolumeArchiver.ArchivePathFor(request.SnapshotId, compress: true);

        if (request.Direction == SnapshotTransferDirection.DownloadFromPanel && request.ArtifactSizeBytes <= 0)
            throw new VolumeArchiver.ArchiveException(NodeErrorCode.ValidationFailed, "The download size is invalid.");

        const string configScript =
            "umask 077; printf 'header = \"Authorization: Bearer %s\"\\n' \"$TRANSFER_TOKEN\" > /tmp/relay.conf; unset TRANSFER_TOKEN; " +
            "CHUNK_MIB=16; CHUNK_BYTES=16777216; " +
            "if [ \"$TRANSFER_DIRECTION\" = upload ]; then " +
            "SIZE=$(stat -c %s \"$SNAPSHOT_PATH\"); BLOCK=0; " +
            "while [ $((BLOCK * 1048576)) -lt \"$SIZE\" ]; do OFFSET=$((BLOCK * 1048576)); " +
            "dd if=\"$SNAPSHOT_PATH\" bs=1048576 skip=\"$BLOCK\" count=\"$CHUNK_MIB\" 2>/dev/null | " +
            "curl --fail --silent --show-error --config /tmp/relay.conf --request PATCH " +
            "--header \"X-Artifact-Offset: $OFFSET\" --data-binary @- \"$RELAY_URL\"; BLOCK=$((BLOCK + CHUNK_MIB)); done; " +
            "curl --fail --silent --show-error --config /tmp/relay.conf --request POST \"$RELAY_URL/upload/complete\"; " +
            "else rm -f \"$SNAPSHOT_PATH\"; START=0; " +
            "while [ \"$START\" -lt \"$ARTIFACT_SIZE\" ]; do END=$((START + CHUNK_BYTES - 1)); " +
            "if [ \"$END\" -ge \"$ARTIFACT_SIZE\" ]; then END=$((ARTIFACT_SIZE - 1)); fi; " +
            "curl --fail --silent --show-error --config /tmp/relay.conf --range \"$START-$END\" " +
            "\"$RELAY_URL\" >> \"$SNAPSHOT_PATH\"; START=$((END + 1)); done; " +
            "curl --fail --silent --show-error --config /tmp/relay.conf --request POST \"$RELAY_URL/download/complete\"; fi; " +
            "rm -f /tmp/relay.conf";

        var direction = request.Direction == SnapshotTransferDirection.UploadToPanel ? "upload" : "download";
        var exit = await runtime.RunOneOffAsync(new OneOffRequest
        {
            ImageReference = _options.ArtifactTransferImage,
            Command = ["sh", "-ec", configScript],
            Env = new Dictionary<string, string>
            {
                ["TRANSFER_TOKEN"] = request.RelayToken,
                ["TRANSFER_DIRECTION"] = direction,
                ["SNAPSHOT_PATH"] = archive,
                ["RELAY_URL"] = relayUri.AbsoluteUri,
                ["ARTIFACT_SIZE"] = request.ArtifactSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            Mounts = [new VolumeMount(VolumeArchiver.ArchiveVolume, "/snapshots", false)],
            Labels = AgentLabels(),
            Resources = new ResourceLimits { MemoryBytes = 128 * 1024 * 1024, PidsLimit = 32 },
            TimeoutSeconds = 3600,
        }, null, ct);

        if (exit != 0)
            throw new VolumeArchiver.ArchiveException(
                NodeErrorCode.VolumeOperationFailed, $"Snapshot relay {direction} exited {exit}.");

        var (size, sha256) = await archiver.InspectAsync(request.SnapshotId, compress: true, ct);
        if (request.Direction == SnapshotTransferDirection.DownloadFromPanel
            && !string.IsNullOrWhiteSpace(request.ExpectedSha256)
            && !sha256.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            await archiver.DeleteSnapshotAsync(request.SnapshotId, compress: true, CancellationToken.None);
            throw new VolumeArchiver.ArchiveException(
                NodeErrorCode.VolumeOperationFailed,
                $"Downloaded snapshot checksum {sha256} does not match {request.ExpectedSha256}; nothing was restored.");
        }

        if (request.Direction == SnapshotTransferDirection.UploadToPanel)
            await archiver.DeleteSnapshotAsync(request.SnapshotId, compress: true, CancellationToken.None);

        return new TransferSnapshotResult { SnapshotId = request.SnapshotId, SizeBytes = size, Sha256 = sha256 };
    }

    private static Dictionary<string, string> AgentLabels() => new()
    {
        [NodeLabels.Managed] = "true",
        [NodeLabels.Tenant] = "harbora-system",
    };
}
