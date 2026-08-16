namespace Harbora.Cli;

/// <summary>
/// Which app a deploy is for. Kept separate from the command, and free of any prompt, so the rules
/// can be tested without a terminal — they are the part users will argue with, and getting them
/// wrong means deploying to something other than what was asked for.
///
/// The one rule everything else serves: a resolved app carries the server's spelling of its slug.
/// A name that was typed is only ever used to *find* an app, never to address one. `Kousar-kolie`
/// matched `kousar-kolie` here and was then sent to the server verbatim, which compares ordinally —
/// a 404 the CLI never showed, because it arrived while a 3.1 MB upload was still being written.
/// </summary>
public static class AppChoice
{
    /// <param name="Current">What the typed name resolved to, or null when nothing matched.</param>
    /// <param name="NeedsPrompt">Whether the caller should offer the list.</param>
    /// <param name="Problem">
    /// What was wrong with the name given. Printed either way; fatal only when there is nothing to
    /// prompt with and nothing was resolved.
    /// </param>
    public sealed record Choice(RemoteApp? Current, bool NeedsPrompt, string? Problem);

    public static Choice Resolve(
        string? typedSlug, IReadOnlyList<RemoteApp> apps, bool interactive, bool yes)
    {
        if (apps.Count == 0)
            return new(null, false, "This account has no apps yet. Create one in the panel first.");

        var typed = (typedSlug ?? "").Trim();

        // Case-insensitively, because that is how people type — but what comes back is the app, and
        // the app knows its own slug.
        var current = typed.Length == 0
            ? null
            : apps.FirstOrDefault(a => a.Slug.Equals(typed, StringComparison.OrdinalIgnoreCase));

        var unknown = typed.Length > 0 && current is null;
        var problem = unknown ? $"No app called {typed} on this account." : null;

        var canAsk = interactive && !yes;

        // A single app is not a question — unless a name was given and it was not that one, in which
        // case answering the question nobody asked would deploy to the wrong app.
        if (!unknown && apps.Count == 1) return new(apps[0], false, null);

        if (canAsk) return new(current, true, problem);
        if (current is not null) return new(current, false, null);

        return new(null, false,
            problem ?? "No app specified — pass one, or add app: to harbora.yml.");
    }

    /// <summary>
    /// The apps in the order they should be offered. Spectre's <c>SelectionPrompt</c> cannot
    /// pre-highlight a choice, so position carries that meaning: the current app is first, and
    /// pressing Enter accepts it.
    /// </summary>
    public static IReadOnlyList<RemoteApp> Order(IReadOnlyList<RemoteApp> apps, RemoteApp? current) =>
        current is null ? apps : [current, .. apps.Where(a => !ReferenceEquals(a, current))];
}
