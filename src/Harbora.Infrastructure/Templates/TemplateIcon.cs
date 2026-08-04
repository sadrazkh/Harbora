namespace Harbora.Infrastructure.Templates;

/// <summary>What to draw for a template: an image, or a readable fallback.</summary>
/// <param name="ImagePath">A path under wwwroot, or null when there is no asset.</param>
/// <param name="Initials">Shown only when there is no image — never alongside one.</param>
public sealed record TemplateIconView(string? ImagePath, string Initials)
{
    public bool HasImage => ImagePath is not null;
}

/// <summary>
/// Resolves a template's icon.
///
/// The catalogue used to draw the first letters of a name — "W" for WordPress — which reads as a
/// placeholder for a missing image even when the app is fully supported. Real marks are shipped in
/// this repository, so the letters are now what they always should have been: the thing shown when
/// there genuinely is no logo, not the normal case.
/// </summary>
public static class TemplateIcon
{
    private const string Root = "/img/apps";

    /// <summary>
    /// Where a template's logo lives by convention. Kept as a rule rather than a stored path so a
    /// template that gains an asset file picks it up without a data migration.
    /// </summary>
    public static string PathFor(string key) => $"{Root}/{key}.svg";

    /// <summary>
    /// The icon to draw. <paramref name="assetExists"/> is asked of the caller — this class does not
    /// touch the filesystem, so it stays a rule that can be reasoned about and tested.
    /// </summary>
    public static TemplateIconView For(string? key, string? name, Func<string, bool> assetExists)
    {
        var slug = (key ?? "").Trim().ToLowerInvariant();

        if (slug.Length > 0)
        {
            var path = PathFor(slug);
            if (assetExists(path)) return new TemplateIconView(path, Initials(name, slug));
        }

        return new TemplateIconView(null, Initials(name, slug));
    }

    /// <summary>
    /// One or two letters, from the display name rather than the key: the key is a slug and often
    /// starts with a word nobody reads, so "uptime-kuma" would give "UP" where the name gives "UK".
    /// </summary>
    public static string Initials(string? name, string? fallback = null)
    {
        var source = string.IsNullOrWhiteSpace(name) ? fallback ?? "" : name;

        var words = source
            .Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => char.IsLetterOrDigit(w[0]))
            .ToList();

        if (words.Count == 0) return "?";
        if (words.Count == 1)
            return words[0].Length == 1
                ? words[0].ToUpperInvariant()
                : words[0][..2].ToUpperInvariant();

        return $"{words[0][0]}{words[1][0]}".ToUpperInvariant();
    }
}
