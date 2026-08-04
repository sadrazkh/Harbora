using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>Where a target's data actually lives, or why it cannot be reached.</summary>
public sealed record ResolvedTarget(bool Succeeded, string? SourcePath = null, string? Error = null);

/// <summary>
/// Turns a policy's target into a path the engine can read.
///
/// <para>
/// The one place that decides what a backup is allowed to read. A backup engine given an arbitrary
/// path is an arbitrary-file-read primitive with a download button attached, so the answer is
/// constrained here rather than at each call site.
/// </para>
/// </summary>
public interface IBackupTargetResolver
{
    ResolvedTarget Resolve(BackupTargetType targetType, string targetRef);
}

/// <inheritdoc />
public sealed class BackupTargetResolver(IOptions<BackupModuleOptions> options) : IBackupTargetResolver
{
    private readonly BackupModuleOptions _options = options.Value;

    public ResolvedTarget Resolve(BackupTargetType targetType, string targetRef)
    {
        if (string.IsNullOrWhiteSpace(targetRef))
            return new ResolvedTarget(false, Error: "This policy has no target.");

        return targetType switch
        {
            BackupTargetType.Directory => ResolveDirectory(targetRef),

            // Volumes are backed up by Harbora's existing backup feature, which tars them through a
            // helper container that mounts the volume by name. Reproducing that here would need a
            // volume-inspect call the platform's Docker abstraction does not expose, and it could
            // not have been exercised on the machine this branch was written on. Refusing is
            // better than a path built from a guess about where Docker keeps its volumes.
            BackupTargetType.DockerVolume => new ResolvedTarget(false, Error:
                "Docker volumes are not a target for this module yet. Use Harbora's existing backup " +
                "feature for volumes, or back up a directory."),

            BackupTargetType.Application or BackupTargetType.Database => new ResolvedTarget(false, Error:
                $"{targetType} targets are not implemented yet."),

            _ => new ResolvedTarget(false, Error: $"{targetType} is not a target this module can read.")
        };
    }

    /// <summary>
    /// A directory target must sit inside a configured root.
    ///
    /// <para>
    /// Fails closed: with no roots configured, no directory can be backed up. The alternative
    /// default — any absolute path — would mean that enabling the feature quietly grants the ability
    /// to read <c>/etc</c>, or the panel's own data directory including its master key, and to
    /// download the result. Requiring the operator to name what may be read is a one-line setting
    /// and removes that entirely.
    /// </para>
    /// </summary>
    private ResolvedTarget ResolveDirectory(string path)
    {
        if (_options.AllowedSourceRoots.Count == 0)
            return new ResolvedTarget(false, Error:
                "No backup source directories are configured. Set Backups:Module:AllowedSourceRoots " +
                "to the directories this panel may read before creating a directory policy.");

        foreach (var root in _options.AllowedSourceRoots)
        {
            var check = PathGuard.ResolveWithin(root, path);
            if (!check.Allowed) continue;

            return Directory.Exists(check.ResolvedPath)
                ? new ResolvedTarget(true, check.ResolvedPath)
                : new ResolvedTarget(false, Error: $"There is no directory at {check.ResolvedPath}.");
        }

        return new ResolvedTarget(false, Error:
            $"'{path}' is not inside any configured backup source directory.");
    }
}
