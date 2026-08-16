using System.Text;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// Turns what somebody typed into the identifier a generated project can carry.
///
/// <para>
/// A function's slug is not cosmetic: it becomes a folder name, a C# type name, a JavaScript import
/// binding and a URL segment all at once. Anything that survives all four is deliberately narrow —
/// lowercase letters, digits and single hyphens — because a name that is legal in three of them and
/// not the fourth fails at image build time, in a log, long after the person typed it.
/// </para>
/// </summary>
public static class FunctionSlug
{
    public const int MaxLength = 48;

    public static string Normalise(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var sb = new StringBuilder(input.Length);
        foreach (var c in input.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxLength) slug = slug[..MaxLength].Trim('-');

        // A slug has to start with a letter: it becomes a type name in the C# host and an import
        // binding in the JavaScript one, and neither may begin with a digit.
        if (slug.Length > 0 && !char.IsAsciiLetter(slug[0])) slug = "fn-" + slug;
        return slug;
    }

    public static bool IsValid(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug == Normalise(slug);

    /// <summary>The slug as a type name: <c>send-email</c> becomes <c>SendEmail</c>.</summary>
    public static string ToPascalCase(string slug)
    {
        var sb = new StringBuilder(slug.Length);
        var upperNext = true;
        foreach (var c in slug)
        {
            if (c == '-') { upperNext = true; continue; }
            sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
            upperNext = false;
        }
        return sb.ToString();
    }

    /// <summary>The slug as an identifier a JavaScript import or a Python module can bind to.</summary>
    public static string ToIdentifier(string slug) => slug.Replace('-', '_');
}
