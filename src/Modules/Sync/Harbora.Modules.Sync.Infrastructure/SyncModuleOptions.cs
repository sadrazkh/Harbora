namespace Harbora.Modules.Sync.Infrastructure;

/// <summary>
/// The sync module's view of the platform's <c>Features</c> section.
///
/// <para>
/// Its own class binding the same section as the backup module's, rather than sharing one. Two
/// small classes over one config section keeps the modules independent — a sync module that
/// referenced the backup module to read a boolean would be a dependency between two things that are
/// deliberately unrelated.
/// </para>
/// </summary>
public sealed class SyncFeatureOptions
{
    public const string SectionName = "Features";

    public bool Sync { get; set; }

    /// <summary>
    /// Always-on node holding ciphertext it cannot read. **Experimental** — the guarantee comes from
    /// the sync engine's untrusted-device support, not from Harbora, and the failure mode is silent.
    /// </summary>
    public bool EncryptedSyncNode { get; set; }
}

/// <summary>Where Syncthing is and how Harbora talks to it.</summary>
public sealed class SyncthingOptions
{
    public const string SectionName = "Sync:Syncthing";

    /// <summary>
    /// Syncthing's REST endpoint.
    ///
    /// <para>
    /// Loopback or a private network only. Syncthing's own API is a direct path to every file it
    /// holds and to its configuration, bypassing Harbora's authentication entirely — it must never
    /// be published (THREAT_MODEL T7).
    /// </para>
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8384";

    /// <summary>
    /// The API key, supplied through configuration or an environment variable — never committed.
    /// Sent as <c>X-API-Key</c>. Empty means the module cannot talk to the engine and says so.
    /// </summary>
    public string ApiKey { get; set; } = "";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often folder status is refreshed. A poll rather than a subscription: Syncthing's event
    /// stream is long-polling, and a timer that cannot wedge is worth more here than freshness
    /// measured in seconds.
    /// </summary>
    public TimeSpan StatusRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Module-level settings for sync spaces on this node.</summary>
public sealed class SyncModuleOptions
{
    public const string SectionName = "Sync:Module";

    /// <summary>
    /// Directories a sync space may live in. **Empty means none.**
    ///
    /// <para>
    /// Fails closed, exactly like the backup module's source roots. A sync folder is a directory this
    /// node will both read from and WRITE to on a remote device's instruction, which is a stronger
    /// capability than backup's read — so the default must not be "anywhere the panel user can
    /// reach".
    /// </para>
    /// </summary>
    public List<string> AllowedRoots { get; set; } = [];

    /// <summary>
    /// Whether this node may be added to a space as an untrusted, ciphertext-only device.
    ///
    /// <para>
    /// Off by default and surfaced as experimental: the guarantee comes from the sync engine, and
    /// its failure mode — the node quietly holding plaintext — is not something Harbora can detect.
    /// </para>
    /// </summary>
    public bool AllowEncryptedNode { get; set; }
}
