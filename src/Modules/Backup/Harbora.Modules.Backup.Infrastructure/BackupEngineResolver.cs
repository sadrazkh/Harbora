using Harbora.Modules.Backup.Contracts;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Hands back the adapter that owns a repository's format.
///
/// <para>
/// Resolution is per repository, never global. A repository's bytes are written in its engine's
/// format — a Kopia repository is only readable by Kopia — so a global engine setting would strand
/// every existing artifact the first time someone changed it, and the failure would only appear at
/// the next restore.
/// </para>
/// </summary>
public sealed class BackupEngineResolver(IEnumerable<IBackupEngine> engines) : IBackupEngineResolver
{
    private readonly Dictionary<BackupEngineKind, IBackupEngine> _engines =
        engines.ToDictionary(e => e.Kind);

    public IReadOnlyCollection<BackupEngineKind> Available => _engines.Keys;

    public IBackupEngine Resolve(BackupEngineKind kind)
    {
        if (_engines.TryGetValue(kind, out var engine)) return engine;

        // A repository whose engine is not registered is a configuration problem, and saying which
        // engine is missing is the difference between a one-line fix and an afternoon.
        throw new InvalidOperationException(
            $"No backup engine is registered for {kind}. Repositories created with it cannot be read " +
            $"until it is available. Registered: {string.Join(", ", _engines.Keys)}.");
    }
}
