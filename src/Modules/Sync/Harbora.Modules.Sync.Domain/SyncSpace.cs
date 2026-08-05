using Harbora.Domain.Common;
using Harbora.Modules.Sync.Contracts;

namespace Harbora.Modules.Sync.Domain;

/// <summary>
/// A folder kept in step across devices.
///
/// <para>
/// <b>Not a backup, and deliberately modelled apart from one.</b> A sync space has no history to go
/// back to: a deletion or an encryption on one device is replicated to every other, usually within
/// seconds. Sharing a data model with <c>BackupSnapshot</c> would have made the two look
/// interchangeable in the UI, and the first person to rely on that would find out during a
/// ransomware incident (THREAT_MODEL T9).
/// </para>
/// </summary>
public class SyncSpace : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Path on this node. Confined to a configured root, like every path this platform writes.</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>The engine's own folder id, once it has accepted the folder.</summary>
    public string? EngineFolderId { get; set; }

    /// <summary>The mode this node itself uses. Each device's mode lives on its own membership row.</summary>
    public SyncMode Mode { get; set; } = SyncMode.SendAndReceive;

    public SyncVersioningMode VersioningMode { get; set; } = SyncVersioningMode.None;

    /// <summary>Days for Trash, versions kept for Simple. Ignored by the other modes.</summary>
    public int VersioningParameter { get; set; }

    /// <summary>Newline-separated ignore patterns, in the engine's own syntax.</summary>
    public string? IgnorePatterns { get; set; }

    public SyncSpaceStatus Status { get; set; } = SyncSpaceStatus.Pending;

    public bool IsPaused { get; set; }

    public DateTimeOffset? LastSyncAt { get; set; }

    public long PendingFiles { get; set; }
    public long PendingBytes { get; set; }
    public long TotalFiles { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>
    /// Never zeroed automatically. A conflict stops being counted when a person decides about it,
    /// not when the engine stops mentioning it.
    /// </summary>
    public int ConflictCount { get; set; }

    /// <summary>Redacted before storage.</summary>
    public string? LastError { get; set; }

    public List<SyncSpaceMember> Members { get; set; } = [];
}

/// <summary>
/// A device Harbora knows about, and can share folders with.
/// </summary>
public class SyncDevice : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The engine's device identity — a public-key fingerprint. It is not a secret, and it is the
    /// only thing that has to be exchanged out of band to pair two devices.
    /// </summary>
    public string EngineDeviceId { get; set; } = string.Empty;

    public SyncDeviceStatus Status { get; set; } = SyncDeviceStatus.PendingPairing;

    public SyncConnectionKind ConnectionKind { get; set; } = SyncConnectionKind.Unknown;

    /// <summary>Last observed address. Diagnostics only — the engine does its own discovery.</summary>
    public string? Address { get; set; }

    public string? ClientVersion { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// True for a device that is only ever sent ciphertext — the always-on relay case.
    ///
    /// <para>
    /// Recorded on the device rather than inferred from a mode, because it is a property of what
    /// this device is trusted with, and it must be visible wherever the device appears.
    /// </para>
    /// </summary>
    public bool IsUntrusted { get; set; }

    /// <summary>True for this Harbora node itself, which is a device like any other.</summary>
    public bool IsLocalNode { get; set; }
}

/// <summary>
/// One device's participation in one sync space.
///
/// <para>
/// A row of its own because the mode is per pair, not per folder: a laptop can send and receive
/// while the always-on node only ever receives ciphertext of the same space.
/// </para>
/// </summary>
public class SyncSpaceMember : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid SyncSpaceId { get; set; }
    public SyncSpace? SyncSpace { get; set; }

    public Guid SyncDeviceId { get; set; }
    public SyncDevice? SyncDevice { get; set; }

    public SyncMode Mode { get; set; } = SyncMode.SendAndReceive;

    /// <summary>
    /// False until the other end has accepted too. Pairing is mutual, and a membership that only one
    /// side agreed to syncs nothing while looking configured.
    /// </summary>
    public bool AcceptedByPeer { get; set; }

    /// <summary>
    /// Encryption password for an untrusted member, encrypted at rest.
    ///
    /// <para>
    /// Held here so trusted devices can be told what it is. It must never be given to the untrusted
    /// device itself — that would make it able to read what it stores, which is the entire point of
    /// the mode.
    /// </para>
    /// </summary>
    public string? EncryptedFolderPassword { get; set; }
}

/// <summary>
/// A conflicting copy the engine produced, and what was decided about it.
///
/// <para>
/// Persisted rather than read live from disk each time, so that a decision survives, and so a
/// conflict that appeared and was dealt with can still be seen afterwards. Nothing here deletes a
/// file on its own.
/// </para>
/// </summary>
public class SyncConflict : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid SyncSpaceId { get; set; }
    public SyncSpace? SyncSpace { get; set; }

    /// <summary>Path of the conflicting copy, relative to the folder root.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The file it conflicts with — the conflict suffix removed.</summary>
    public string OriginalRelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Which device's change lost, when the engine recorded it in the filename.</summary>
    public string? OriginatingDevice { get; set; }

    public SyncConflictResolution Resolution { get; set; } = SyncConflictResolution.Unresolved;

    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }

    public bool IsOpen => Resolution == SyncConflictResolution.Unresolved;
}
