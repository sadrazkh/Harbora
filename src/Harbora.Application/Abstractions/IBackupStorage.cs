using Harbora.Domain.Backups;

namespace Harbora.Application.Abstractions;

/// <summary>
/// Destination-agnostic backup artifact storage. Backups are always staged as a local file
/// first (a named volume shared with the one-off tar containers), then pushed to the chosen
/// destination — local keep-in-place, or upload to an S3-compatible bucket.
/// </summary>
public interface IBackupStorage
{
    /// <summary>Local directory (also a docker volume) where artifacts are staged/produced.</summary>
    string LocalStagingDir { get; }

    /// <summary>Publish a staged local file to the destination. Returns an artifact reference.</summary>
    Task<(string ArtifactRef, long SizeBytes)> PutFileAsync(BackupDestination dest, string key, string localFilePath, CancellationToken ct);

    /// <summary>
    /// Ensure the artifact is available as a local file (downloading from S3 or SFTP if needed).
    /// </summary>
    /// <param name="localFileName">
    /// What to call the downloaded copy in the staging directory, when the caller needs a name of
    /// its own. The default — the artifact's own file name — is the same name for every reader of
    /// one artifact, so two concurrent fetches write over each other's download. A caller that can
    /// have two reads of one artifact in flight passes something unique to each; a caller that
    /// cannot leaves it alone.
    /// <para>
    /// Ignored for a local destination, which has nothing to download: the artifact is already a
    /// file and its own path is returned.
    /// </para>
    /// </param>
    /// <remarks>
    /// After the token rather than before it, so every existing three-argument call still binds.
    /// </remarks>
    Task<string> GetToLocalAsync(
        BackupDestination dest, string artifactRef, CancellationToken ct, string? localFileName = null);

    Task DeleteAsync(BackupDestination dest, string artifactRef, CancellationToken ct);
}
