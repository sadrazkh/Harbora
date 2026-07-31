namespace Harbora.Infrastructure.Docker;

/// <summary>
/// How a one-off command — a release task, a scheduled job, a database dump — is handed to Docker.
///
/// The rule that matters: <b>the image's own ENTRYPOINT must be replaced, not prepended to.</b>
/// Nearly every application image has one (<c>ENTRYPOINT ["dotnet", "App.dll"]</c>), and Docker
/// treats a container's command as <i>arguments</i> to that entrypoint. Sending
/// <c>["sh", "-c", "dotnet ef database update"]</c> without replacing it does not run a migration:
/// it starts the application with three arguments it ignores, and the caller waits for a container
/// that never exits. Observed exactly that way — a deployment sat "in progress" indefinitely while
/// the release command it was supposedly running had not executed at all.
///
/// Replacing the entrypoint also stops the image's own CMD being appended as stray arguments: Docker
/// only inherits CMD from the image when the entrypoint is left alone.
/// </summary>
public static class OneOffLaunch
{
    /// <summary>
    /// Splits a command into the entrypoint and arguments to send. Both are null for an empty
    /// command, which means "run the image as its author intended".
    /// </summary>
    public static (IList<string>? Entrypoint, IList<string>? Arguments) From(IReadOnlyList<string> command)
    {
        if (command.Count == 0) return (null, null);

        return ([command[0]], command.Count > 1 ? command.Skip(1).ToList() : null);
    }
}
