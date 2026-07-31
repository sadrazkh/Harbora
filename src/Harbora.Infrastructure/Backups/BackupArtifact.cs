namespace Harbora.Infrastructure.Backups;

/// <summary>
/// What kind of thing an artifact is, read from its own name.
///
/// Database backups used to be tar archives of the data directory and are now logical dumps, so both
/// exist in the same history. Restoring one as the other would untar a SQL file into a data
/// directory, or feed a tarball to psql. The extension is the record of how it was made, and the
/// only thing available years later.
/// </summary>
public static class BackupArtifact
{
    /// <summary>True for a tar archive of a volume — the older shape, still restorable.</summary>
    public static bool IsVolumeArchive(string? artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath)) return true;

        // The stored path may carry the encryption suffix; what matters is what is underneath.
        var name = artifactPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
            ? artifactPath[..^4]
            : artifactPath;

        return name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
    }
}
