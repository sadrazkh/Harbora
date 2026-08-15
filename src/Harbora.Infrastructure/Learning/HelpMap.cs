namespace Harbora.Infrastructure.Learning;

/// <summary>
/// Maps the screen a person is looking at to the tutorial chapter that explains it — the piece that
/// turns the Help control from a link to a filing cabinet into one that opens something relevant: the
/// applications chapter from an app page, the storage chapter from the volumes tab, the networking
/// chapter from domains.
///
/// <para>
/// Matching is longest-prefix over path <em>segments</em>, not a plain string prefix. A route in
/// <see cref="Routes"/> can hold the literal segment <c>{id}</c> where a real request carries a
/// resource's GUID, so <c>/apps/{id}/volumes</c> and the bare <c>/apps</c> are two different entries
/// even though every request under either one starts with <c>/apps</c>. Whenever more than one entry
/// matches, the longer — more specific — one wins, which is what lets the volumes tab send someone to
/// the storage chapter while the rest of the app page sends them to applications.
/// </para>
///
/// <para>
/// A few entries look wrong next to the sidebar's own grouping, and are not: <c>/networks</c> answers
/// <c>02-projects-and-environments</c> rather than the networking chapter, because
/// <c>06-networking.md</c> says of itself that the networks screen "is fully explained in section 2"
/// rather than repeating that explanation. The map follows where a screen is actually documented, not
/// where the sidebar happens to file it.
/// </para>
///
/// <para>
/// A route with no entry answers null — deliberately, rather than the first chapter or a guess at the
/// closest one. The topbar's Help control (<c>Views/Shared/Design/_Topbar.cshtml</c>) turns that null
/// into the index with an honest "no chapter for this screen yet" rather than opening something
/// unrelated: an unhelpful Help button is worse than a missing one, because it costs a click to find
/// out it was unhelpful.
/// </para>
/// </summary>
public static class HelpMap
{
    /// <summary>
    /// Route pattern → chapter slug, in the order checked. Internal rather than private so
    /// <c>LearningCensusTests</c> can read the real table directly instead of guessing at every route
    /// <see cref="ChapterFor"/> might ever be asked about — the same reasoning that keeps that census
    /// off a hand-kept list of screens, aimed instead at the one table this class already has to
    /// maintain for its own job.
    /// </summary>
    internal static readonly (string Pattern, string Slug)[] Routes =
    [
        // The app page's own sub-screens that belong to a different chapter than the rest of it —
        // longer than the bare "/apps" entry below, so they win over it on a path that matches both.
        ("/apps/{id}/volumes", "05-storage"),
        ("/apps/{id}/data", "05-storage"),

        ("/apps", "03-applications"),
        // Ready-made stacks are documented as part of the applications chapter ("## قالب‌ها" in
        // 03-applications.md), not as a doc-site page of their own.
        ("/templates", "03-applications"),

        ("/databases", "04-databases-and-brokers"),

        ("/storage", "05-storage"),

        // See the class docstring: fully explained in chapter 2, not chapter 6.
        ("/networks", "02-projects-and-environments"),
        ("/projects", "02-projects-and-environments"),

        ("/domains", "06-networking"),
        ("/routes", "06-networking"),

        ("/deployments", "07-operations"),
        ("/monitoring", "07-operations"),
        ("/backups", "07-operations"),
        ("/audit", "07-operations"),
        // The two optional modules chapter 7 names by their feature flag and describes as having "no
        // page, and their address 404s" until turned on.
        ("/backup-center", "07-operations"),
        ("/sync", "07-operations"),

        ("/admin/ai", "08-ai"),
        ("/ai", "08-ai"),

        ("/users", "09-administration"),
        ("/servers", "09-administration"),
        ("/nodes", "09-administration"),
        ("/plans", "09-administration"),
        ("/admin/templates", "09-administration"),
        ("/git", "09-administration"),
        ("/tenants", "09-administration"),
        ("/admin/settings", "09-administration"),
        ("/settings", "09-administration"),
    ];

    private static readonly (string[] Segments, string Slug)[] Entries =
        BuildEntries();

    private static (string[] Segments, string Slug)[] BuildEntries()
    {
        var entries = new (string[] Segments, string Slug)[Routes.Length];
        for (var i = 0; i < Routes.Length; i++)
            entries[i] = (Segments(Routes[i].Pattern), Routes[i].Slug);
        return entries;
    }

    /// <summary>
    /// The chapter that explains <paramref name="routePath"/>, or null when nothing in
    /// <see cref="Routes"/> answers to it. The dashboard root is the one exception handled outside the
    /// table: every account lands there, and it is the screen chapter 1 opens with.
    /// </summary>
    public static string? ChapterFor(string routePath)
    {
        var actual = Segments(routePath);
        if (actual.Length == 0) return "01-first-steps";

        string? best = null;
        var bestLength = 0;

        foreach (var (segments, slug) in Entries)
        {
            if (segments.Length == 0 || segments.Length > actual.Length || segments.Length <= bestLength)
                continue;

            if (!MatchesFromStart(segments, actual)) continue;

            best = slug;
            bestLength = segments.Length;
        }

        return best;
    }

    private static bool MatchesFromStart(string[] pattern, string[] actual)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == "{id}") continue;
            if (!string.Equals(pattern[i], actual[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static string[] Segments(string? path) =>
        (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
}
