namespace Harbora.Infrastructure.Templates;

/// <summary>
/// Pulling a container image reference apart.
///
/// It exists so somebody can name a release tag of their own — "run n8n 1.70.1" — and Harbora can
/// work out which repository to ask. The awkward part is that a colon means two different things:
/// the tag separator, and the port in <c>registry.example.com:5000/app</c>. Splitting on the last
/// colon without checking for a slash after it turns a private registry's port into a tag.
/// </summary>
public static class ImageReference
{
    /// <summary>The repository, with any tag or digest removed. Null when there is nothing usable.</summary>
    public static string? RepositoryOf(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var value = reference.Trim();

        // A digest is unambiguous: everything before the @ is the repository.
        var at = value.IndexOf('@');
        if (at > 0) value = value[..at];

        var colon = value.LastIndexOf(':');
        var slash = value.LastIndexOf('/');

        // A colon before the last slash is a port, not a tag.
        if (colon > 0 && colon > slash) value = value[..colon];

        value = value.Trim('/');
        return value.Length == 0 ? null : value;
    }

    /// <summary>The tag, or null when the reference carries a digest or nothing.</summary>
    public static string? TagOf(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var value = reference.Trim();
        if (value.Contains('@')) return null;

        var colon = value.LastIndexOf(':');
        var slash = value.LastIndexOf('/');
        if (colon <= 0 || colon < slash) return null;

        var tag = value[(colon + 1)..];
        return tag.Length == 0 ? null : tag;
    }

    /// <summary>
    /// Whether a tag is one a registry could have.
    ///
    /// Deliberately strict, because this value is typed by a person and then used to build a URL
    /// and a stored image reference. Docker's own rule: letters, digits, underscore, period and
    /// dash, not starting with a period or dash, at most 128 characters.
    /// </summary>
    public static bool IsUsableTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var value = tag.Trim();
        if (value.Length > 128) return false;
        if (value[0] is '.' or '-') return false;

        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
    }
}
