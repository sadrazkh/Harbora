using System.Security.Cryptography;
using System.Text;

namespace Harbora.Infrastructure.Projects;

/// <summary>
/// What a preview of a branch is called.
///
/// Branch names are the least disciplined strings in software: slashes, capitals, emoji, four
/// hundred characters of ticket title. Everything here ends up in a DNS label, a Docker network name
/// and a container name, each of which has its own limit and its own idea of a legal character — and
/// each of which fails in the middle of a deployment rather than when the branch was named.
///
/// A short hash of the original is always appended, so two branches that clean to the same text —
/// <c>feature/login</c> and <c>feature-login</c> — never collide and quietly share an environment.
/// </summary>
public static class PreviewNaming
{
    /// <summary>DNS labels stop at 63; the same bound keeps Docker network and container names legal.</summary>
    public const int MaxLabel = 63;

    /// <summary>Room left for the descriptive part once the hash and separators are accounted for.</summary>
    private const int HashLength = 6;

    /// <summary>The environment name a person reads, kept close to what they typed.</summary>
    public static string EnvironmentName(string branch) =>
        string.IsNullOrWhiteSpace(branch) ? "preview" : branch.Trim();

    /// <summary>The slug used for the environment, the network and everything derived from it.</summary>
    public static string Slug(string branch, int reserved = 0)
    {
        var cleaned = Clean(branch);
        var hash = ShortHash(branch);

        // The hash is never trimmed: it is the part that makes the name unique.
        var room = MaxLabel - reserved - HashLength - 1;
        if (room < 1) return hash;

        return cleaned.Length <= room ? $"{cleaned}-{hash}" : $"{cleaned[..room]}-{hash}";
    }

    /// <summary>
    /// The hostname a preview answers on. Built from the app and the branch together, because two
    /// apps previewing the same branch must not both claim one address.
    /// </summary>
    public static string? Host(string appSlug, string branch, string? rootDomain)
    {
        if (string.IsNullOrWhiteSpace(rootDomain)) return null;

        var app = Clean(appSlug);

        // The label has to hold the app name too, so the branch part gets what is left.
        var label = $"{app}-{Slug(branch, reserved: app.Length + 1)}";
        if (label.Length > MaxLabel) label = label[..MaxLabel].TrimEnd('-');

        return $"{label}.{rootDomain.Trim().Trim('.')}";
    }

    /// <summary>
    /// Lowercase letters, digits and dashes, never starting or ending with one. Anything else in a
    /// branch name becomes a dash rather than being dropped, so <c>fix/login</c> and <c>fixlogin</c>
    /// stay visibly different.
    /// </summary>
    private static string Clean(string value)
    {
        var text = new string((value ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

        while (text.Contains("--", StringComparison.Ordinal))
            text = text.Replace("--", "-", StringComparison.Ordinal);

        text = text.Trim('-');
        return text.Length == 0 ? "branch" : text;
    }

    /// <summary>Of the original branch name, so cleaning cannot make two branches look like one.</summary>
    private static string ShortHash(string branch) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(branch ?? "")))[..HashLength].ToLowerInvariant();
}
