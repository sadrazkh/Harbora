using Harbora.Shared.Inspection;

namespace Harbora.Cli;

/// <summary>
/// The names a service's attachment sets. A table about attachments, not a reading of anybody's source,
/// so it does not give this CLI a second opinion on what the code needs: that stays the proposal's. It
/// is here because a person with their own database passes <c>--set DATABASE_URL=…</c>, and a project
/// with no example file has that name nowhere in its proposal, though nobody could call it a typo.
/// </summary>
public static class ConnectionNames
{
    /// <summary>What no attachment sets is still an ordinary variable, and may be a mistyped one.</summary>
    public static bool IsKnown(string key) => AttachEnvNames.All.Values.Any(names => names.Contains(key));

    /// <summary>
    /// The API names an engine by the domain's enum (<c>PostgreSql</c>) and this table by the inspector's
    /// (<c>Postgres</c>), and each of the second begins the first, give or take a capital.
    /// <c>CliLaunchPlanTests</c> holds that for every engine, because the day one does not, a service
    /// would stop answering its own names.
    /// </summary>
    public static bool IsSetBy(string serviceType, string key) => AttachEnvNames.All.Any(entry =>
        serviceType.StartsWith(entry.Key.ToString(), StringComparison.OrdinalIgnoreCase) && entry.Value.Contains(key));

    /// <summary>
    /// A connection's name that plainly holds nothing secret. Anything else somebody gives for one is
    /// kept as a secret, because their own <c>DATABASE_URL</c> has their password in it.
    /// </summary>
    public static bool IsPlainPart(string key) =>
        PlainParts.Any(part => key.EndsWith(part, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] PlainParts = ["HOST", "PORT", "USER", "USERNAME", "NAME", "DATABASE", "DB"];
}
