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

    /// <summary>Ensure the artifact is available as a local file (downloading from S3 if needed).</summary>
    Task<string> GetToLocalAsync(BackupDestination dest, string artifactRef, CancellationToken ct);

    Task DeleteAsync(BackupDestination dest, string artifactRef, CancellationToken ct);
}
