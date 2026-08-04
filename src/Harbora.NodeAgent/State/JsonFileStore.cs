using System.Text.Json;
using Harbora.NodeAgent.Contracts;

namespace Harbora.NodeAgent.State;

/// <summary>
/// A single JSON document on disk, written atomically and readable only by the agent's user.
///
/// <para>
/// Atomicity matters more than it looks: the agent is a service on a machine that can lose power
/// mid-deploy, and a half-written state file is worse than a missing one — a missing one restarts
/// clean, a truncated one restarts confidently wrong. Writes go to a sibling temp file, are
/// flushed to the device, and only then replace the original.
/// </para>
/// </summary>
public sealed class JsonFileStore<T>(string path) where T : class
{
    private readonly Lock _gate = new();

    public string Path { get; } = path;

    /// <summary>The document, or null when it has never been written or cannot be parsed.</summary>
    public T? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return null;

            try
            {
                var json = File.ReadAllText(Path);
                return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, NodeContract.Json);
            }
            catch (JsonException)
            {
                // A corrupt file is quarantined rather than deleted: whatever wrote it is a bug
                // worth being able to look at, and the agent can rebuild its state from the
                // control plane anyway.
                Quarantine();
                return null;
            }
        }
    }

    public T LoadOrDefault(Func<T> factory) => Load() ?? factory();

    public void Save(T value)
    {
        lock (_gate)
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(directory);
            FilePermissions.RestrictDirectory(directory);

            var temp = Path + ".tmp";
            var json = JsonSerializer.Serialize(value, NodeContract.Json);

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            FilePermissions.RestrictFile(temp);
            File.Move(temp, Path, overwrite: true);
        }
    }

    /// <summary>
    /// Read, mutate and write back under one lock, so two callers cannot clobber each other.
    /// <see cref="Lock"/> is reentrant, so the nested Load/Save calls take the lock they already hold.
    /// </summary>
    public T Update(Func<T?, T> mutate)
    {
        lock (_gate)
        {
            var next = mutate(Load());
            Save(next);
            return next;
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }

    private void Quarantine()
    {
        try
        {
            File.Move(Path, Path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // Best effort. Losing the quarantine copy must not stop the agent from starting.
        }
    }
}

/// <summary>
/// Unix file modes for everything the agent persists. The identity key, the grant credentials and
/// the state file are all things a second account on the box has no business reading.
/// </summary>
public static class FilePermissions
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        File.SetUnixFileMode(path, OwnerOnlyFile);
    }

    public static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(path)) return;
        File.SetUnixFileMode(path, OwnerOnlyDirectory);
    }

    /// <summary>True when the file is not readable by group or other. Used by the startup self-check.</summary>
    public static bool IsOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        if (!File.Exists(path)) return false;

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode others =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        return (mode & others) == 0;
    }
}
