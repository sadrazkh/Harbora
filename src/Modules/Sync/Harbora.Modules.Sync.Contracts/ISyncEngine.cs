namespace Harbora.Modules.Sync.Contracts;

/// <summary>
/// The file-synchronisation engine behind a sync space.
///
/// <para>
/// Kept behind an interface for the same reason the backup engine is: Harbora orchestrates, and the
/// thing doing the work should be replaceable without the business logic noticing. The first
/// implementation drives Syncthing.
/// </para>
/// <para>
/// Nothing here restores. A sync engine has no concept of "the state last Tuesday" — deletions and
/// corruption propagate, which is exactly why this module is separate from Backup
/// (<c>docs/backup-sync/THREAT_MODEL.md</c> T9).
/// </para>
/// </summary>
public interface ISyncEngine
{
    /// <summary>Introduce a device so folders can later be shared with it.</summary>
    Task<SyncDeviceResult> RegisterDeviceAsync(
        RegisterSyncDeviceRequest request,
        CancellationToken cancellationToken);

    /// <summary>Create a synchronised folder on this node.</summary>
    Task<SyncFolderResult> CreateFolderAsync(
        CreateSyncFolderRequest request,
        CancellationToken cancellationToken);

    /// <summary>Share a folder with a device, in a given mode.</summary>
    Task<PairDeviceResult> PairDeviceAsync(
        PairSyncDeviceRequest request,
        CancellationToken cancellationToken);

    /// <summary>Live status: how far behind, how many devices, and whether anything conflicts.</summary>
    Task<SyncFolderStatusResult> GetFolderStatusAsync(
        Guid folderId,
        CancellationToken cancellationToken);

    /// <summary>Stop or resume synchronising a folder without removing it.</summary>
    Task<SyncOperationResult> SetPausedAsync(
        Guid folderId, bool paused, CancellationToken cancellationToken);

    /// <summary>Remove a device from a folder. Its copy of the files is not touched.</summary>
    Task<SyncOperationResult> UnpairDeviceAsync(
        PairSyncDeviceRequest request, CancellationToken cancellationToken);

    /// <summary>Conflicting copies the engine has produced in a folder.</summary>
    Task<IReadOnlyList<SyncConflictFile>> ListConflictsAsync(
        Guid folderId, CancellationToken cancellationToken);

    /// <summary>Connection state of every known device.</summary>
    Task<IReadOnlyList<SyncDeviceConnection>> ListConnectionsAsync(CancellationToken cancellationToken);

    /// <summary>This node's own device id, which other devices must be given to pair with it.</summary>
    Task<string?> GetLocalDeviceIdAsync(CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------------------------

public sealed record RegisterSyncDeviceRequest(
    Guid DeviceId,
    string EngineDeviceId,
    string Name,

    // Empty lets the engine discover the device. Addresses are a hint, not a requirement.
    IReadOnlyList<string>? Addresses = null,

    // An untrusted device is sent ciphertext only. Requires a password the trusted devices share
    // and this node must never hold.
    bool Untrusted = false);

public sealed record CreateSyncFolderRequest(
    Guid FolderId,
    string Label,
    string Path,
    SyncMode Mode,
    SyncVersioningMode Versioning = SyncVersioningMode.None,

    // Interpretation depends on the mode: days for Trash, versions kept for Simple.
    int VersioningParameter = 0,

    IReadOnlyList<string>? IgnorePatterns = null);

public sealed record PairSyncDeviceRequest(
    Guid FolderId,
    Guid DeviceId,
    SyncMode Mode,

    /// <summary>
    /// Only for <see cref="SyncMode.EncryptedReceiveOnly"/>: the password the receiving device's
    /// copy is encrypted with. Held by trusted devices, never by the untrusted one.
    /// </summary>
    string? EncryptionPassword = null);

// ---------------------------------------------------------------------------------------------
// Results
// ---------------------------------------------------------------------------------------------

public sealed record SyncDeviceResult(
    bool Succeeded, Guid DeviceId, string? EngineDeviceId = null, string? Error = null);

public sealed record SyncFolderResult(
    bool Succeeded, Guid FolderId, string? EngineFolderId = null, string? Error = null);

public sealed record PairDeviceResult(
    bool Succeeded,

    // False while the other end has not accepted. Pairing is mutual; one side agreeing is half of it.
    bool AcceptedByPeer = false,

    string? Error = null);

public sealed record SyncOperationResult(bool Succeeded, string? Error = null);

/// <summary>
/// Live folder state.
///
/// <para>
/// <paramref name="PendingFiles"/> being zero is not the same as "safe". It means every device that
/// is currently connected has everything — a device that has been offline for a week has none of it.
/// <paramref name="ConnectedDevices"/> is what tells them apart.
/// </para>
/// </summary>
public sealed record SyncFolderStatusResult(
    bool Reachable,
    SyncSpaceStatus Status,
    long PendingFiles = 0,
    long PendingBytes = 0,
    long TotalFiles = 0,
    long TotalBytes = 0,
    int ConnectedDevices = 0,
    int TotalDevices = 0,
    int ConflictCount = 0,
    DateTimeOffset? LastSyncAt = null,
    string? Error = null);

/// <summary>
/// A conflicting copy the engine wrote alongside the original.
///
/// <para>
/// Surfaced rather than resolved. Whichever copy an automatic rule would discard is somebody's work.
/// </para>
/// </summary>
public sealed record SyncConflictFile(
    string RelativePath,

    // The file the conflict is against, with the conflict suffix removed.
    string OriginalRelativePath,

    long SizeBytes,
    DateTimeOffset DetectedAt,

    // Which device's change lost, when the engine recorded it in the name.
    string? OriginatingDevice = null);

public sealed record SyncDeviceConnection(
    string EngineDeviceId,
    bool Connected,
    SyncConnectionKind Kind,
    string? Address = null,
    DateTimeOffset? LastSeenAt = null,
    string? ClientVersion = null);
