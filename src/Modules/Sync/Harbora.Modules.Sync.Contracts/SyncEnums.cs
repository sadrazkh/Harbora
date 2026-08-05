namespace Harbora.Modules.Sync.Contracts;

/// <summary>
/// How a device participates in a sync space. Persisted by value — never renumber.
/// </summary>
public enum SyncMode
{
    /// <summary>Changes flow both ways. The ordinary case, and the one that propagates deletions.</summary>
    SendAndReceive = 0,

    /// <summary>This device publishes changes and accepts none.</summary>
    SendOnly = 1,

    /// <summary>This device accepts changes and publishes none.</summary>
    ReceiveOnly = 2,

    /// <summary>
    /// This device stores ciphertext it cannot read.
    ///
    /// <para>
    /// The mode that makes an always-on relay node possible without trusting it. **Experimental**:
    /// the guarantee comes from the sync engine's untrusted-device support rather than from Harbora,
    /// and the failure mode — the node quietly holding plaintext — is invisible from the UI. Anything
    /// offering this must say so.
    /// </para>
    /// </summary>
    EncryptedReceiveOnly = 3
}

/// <summary>
/// What the engine keeps when a file is replaced or deleted.
///
/// <para>
/// Versioning limits the damage that sync propagates; it does not turn sync into backup. A folder
/// with versioning is still a folder where a deletion travels to every device — see THREAT_MODEL T9.
/// </para>
/// </summary>
public enum SyncVersioningMode
{
    /// <summary>Nothing is kept. A deletion is final everywhere.</summary>
    None = 0,

    /// <summary>Replaced files go to a bin and are cleaned up after a number of days.</summary>
    Trash = 1,

    /// <summary>A fixed number of old versions per file.</summary>
    Simple = 2,

    /// <summary>Denser recent history, thinning with age.</summary>
    Staggered = 3
}

public enum SyncSpaceStatus
{
    /// <summary>Created in Harbora but not yet accepted by the engine.</summary>
    Pending = 0,

    /// <summary>Every device is connected and up to date.</summary>
    UpToDate = 1,

    Syncing = 2,

    /// <summary>Files are waiting because a device that holds them is offline.</summary>
    WaitingForDevices = 3,

    /// <summary>Conflicts exist. Not an error — but not something to hide either.</summary>
    HasConflicts = 4,

    Paused = 5,

    /// <summary>The engine reported a problem with the folder itself.</summary>
    Error = 6
}

public enum SyncDeviceStatus
{
    /// <summary>Added, but the two ends have not accepted each other yet.</summary>
    PendingPairing = 0,

    Connected = 1,
    Disconnected = 2,

    /// <summary>Reachable only through a relay, so slower and via a third party.</summary>
    ConnectedViaRelay = 3,

    /// <summary>Access withdrawn. It cannot rejoin without being paired again.</summary>
    Revoked = 4
}

/// <summary>How two devices are talking, which is worth showing: a relay is someone else's server.</summary>
public enum SyncConnectionKind
{
    Unknown = 0,
    Direct = 1,
    Relay = 2
}

/// <summary>
/// What the user decided about a conflicting file.
///
/// <para>
/// There is deliberately no "resolve automatically". Whichever copy an automatic rule discards is
/// somebody's work, and the engine cannot know which one mattered.
/// </para>
/// </summary>
public enum SyncConflictResolution
{
    /// <summary>Seen by Harbora, not yet decided by anyone.</summary>
    Unresolved = 0,

    /// <summary>The local file was kept; the conflicting copy was removed by the user.</summary>
    KeptLocal = 1,

    /// <summary>The conflicting copy was promoted over the local file.</summary>
    KeptRemote = 2,

    /// <summary>Both were kept, under different names.</summary>
    KeptBoth = 3,

    /// <summary>The conflicting file is no longer present — resolved outside Harbora.</summary>
    Disappeared = 4
}
