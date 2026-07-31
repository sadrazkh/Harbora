namespace Harbora.Infrastructure.Backups;

/// <summary>An SFTP command to run in a one-off container, with what it needs on stdin.</summary>
/// <param name="Command">Runs through a shell, with the staging volume mounted at /backup.</param>
/// <param name="Env">Carries the password. Never a command-line argument.</param>
public sealed record SftpCommand(IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Env);

/// <summary>
/// Copying backup artifacts to and from an SFTP server.
///
/// Done through a one-off container running <c>sftp</c> rather than by adding an SSH library: the
/// platform already runs one-off containers for every other file-level job, that path is well
/// exercised now, and it keeps a network client with its own key handling out of the panel process.
///
/// The host key is the part worth being careful about. Accepting whatever the server presents —
/// which is what most quick implementations do — means a backup, and the credentials used to send
/// it, can be handed to anyone who can answer on that address. So a destination either pins the host
/// key it expects, or Harbora refuses and says why rather than trusting silently.
/// </summary>
public static class SftpTransfer
{
    /// <summary>Image with an SSH client. Alpine's is small and present in every registry mirror.</summary>
    public const string ClientImage = "alpine:3.20";

    /// <summary>
    /// Why this destination cannot be used yet, or null when it is usable. Refusing is the point: a
    /// destination with no pinned host key is one an attacker can impersonate.
    /// </summary>
    public static string? WhyUnusable(string? host, string? username, string? hostKey)
    {
        if (string.IsNullOrWhiteSpace(host)) return "This destination has no server address.";
        if (string.IsNullOrWhiteSpace(username)) return "This destination has no username.";

        if (string.IsNullOrWhiteSpace(hostKey))
            return "This destination has no host key, so Harbora cannot tell the real server from " +
                   "anything else answering on that address. Add the server's host key " +
                   "(`ssh-keyscan your-host`) before using it.";

        return null;
    }

    /// <summary>Uploads one staged file. The remote directory is created first — it usually is not there.</summary>
    public static SftpCommand Upload(string host, int port, string username, string password,
                                     string? remoteDirectory, string fileName)
    {
        var directory = NormaliseDirectory(remoteDirectory);
        var script = string.Join("\n", [
            directory is null ? "" : $"-mkdir {Quote(directory)}",   // leading - - keep going if it exists
            directory is null ? "" : $"cd {Quote(directory)}",
            $"put {Quote($"/backup/{fileName}")}",
            "bye"
        ]).Replace("\n\n", "\n");

        return Build(host, port, username, password, script);
    }

    /// <summary>Fetches one artifact back into the staging directory.</summary>
    public static SftpCommand Download(string host, int port, string username, string password,
                                       string? remoteDirectory, string fileName)
    {
        var directory = NormaliseDirectory(remoteDirectory);
        var remote = directory is null ? fileName : $"{directory}/{fileName}";
        var script = string.Join("\n", [$"get {Quote(remote)} {Quote($"/backup/{fileName}")}", "bye"]);

        return Build(host, port, username, password, script);
    }

    /// <summary>Removes an artifact, for retention.</summary>
    public static SftpCommand Delete(string host, int port, string username, string password,
                                     string? remoteDirectory, string fileName)
    {
        var directory = NormaliseDirectory(remoteDirectory);
        var remote = directory is null ? fileName : $"{directory}/{fileName}";
        return Build(host, port, username, password, string.Join("\n", [$"rm {Quote(remote)}", "bye"]));
    }

    private static SftpCommand Build(string host, int port, string username, string password, string script)
    {
        // sshpass, because sftp reads a password from a terminal and there is none here. The
        // password reaches it through the environment: -p would put it in the process list.
        var command =
            "set -e; apk add --no-cache openssh-client sshpass >/dev/null; " +
            "mkdir -p ~/.ssh; printf '%s\\n' \"$SFTP_HOST_KEY\" > ~/.ssh/known_hosts; " +
            $"sshpass -e sftp -oBatchMode=no -oStrictHostKeyChecking=yes " +
            $"-oUserKnownHostsFile=~/.ssh/known_hosts -P {port} " +
            $"-b - {Shell(username)}@{Shell(host)} <<'SFTP_SCRIPT'\n{script}\nSFTP_SCRIPT";

        return new SftpCommand(
            ["sh", "-c", command],
            new Dictionary<string, string> { ["SSHPASS"] = password });
    }

    /// <summary>Trailing and leading slashes make the difference between a path and a mistake.</summary>
    private static string? NormaliseDirectory(string? directory)
    {
        var trimmed = directory?.Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>Quoting inside an sftp batch script, where the escape is a backslash.</summary>
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
