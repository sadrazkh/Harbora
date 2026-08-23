namespace Harbora.Application.Abstractions;

/// <summary>
/// Reads and writes one file inside a container's own filesystem — the seam C2 (2026-08-22
/// config-delivery plan) applies file overrides through, and the same one the panel's "validate a
/// rule against the deployed app" feature reads through.
///
/// <para>
/// Behind a seam for the exact reason <see cref="IDockerEngine.ExecAsync"/> already states: an
/// engine that cannot honestly offer this must be able to say so, rather than silently mangling a
/// file or pretending nothing happened. The local engine implements this for real (<c>docker cp</c>
/// semantics via Docker.DotNet's container-archive endpoints); a remote node's engine throws
/// <see cref="NotSupportedException"/> today — see <c>RemoteDockerEngine</c>'s own doc for why that
/// is an honest v1 boundary rather than a silent gap.
/// </para>
///
/// <para>
/// Deliberately never touches the image: every write lands in a container's own writable layer, so a
/// rollback or redeploy that creates a fresh container always starts from the image's own (placeholder)
/// file and gets every rule re-applied fresh — never a value carried over from a previous run.
/// </para>
/// </summary>
public interface IContainerConfigFileWriter
{
    /// <summary>The file's bytes, or null when the path does not exist in this container. Works
    /// against a created-but-not-yet-started container (deploy-time application) and a running one
    /// (the panel's pre-deploy validation, which reads the currently deployed app).</summary>
    Task<byte[]?> ReadFileAsync(string containerNameOrId, string absolutePath, CancellationToken ct);

    /// <summary>Writes bytes at an absolute path inside a container's own filesystem, creating parent
    /// directories as needed. Overwrites whatever the image baked in at that path.</summary>
    Task WriteFileAsync(string containerNameOrId, string absolutePath, byte[] content, CancellationToken ct);

    /// <summary>
    /// The entry names directly inside an absolute directory, or null when the directory itself does
    /// not exist. Used only to make a "file not found" override failure debuggable — "here is what
    /// actually is in that directory" — never to browse a container generally.
    /// </summary>
    Task<IReadOnlyList<string>?> ListDirectoryAsync(string containerNameOrId, string absoluteDirectoryPath, CancellationToken ct);
}
