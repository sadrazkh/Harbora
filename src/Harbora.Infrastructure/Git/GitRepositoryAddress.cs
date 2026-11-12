using Harbora.Domain.Common;
using Harbora.Domain.Git;

namespace Harbora.Infrastructure.Git;

/// <summary>
/// Which repository an address names, and which connected account's host it is on. Pure, so every
/// spelling of one repository (with or without <c>.git</c>, a trailing slash, another case) can be
/// shown to be the same one without a network.
///
/// <para>
/// The host check is what keeps a connected account's token on its own provider: a launch or an app
/// only ever reads a repository through an account whose host the address is on, so a token for
/// github.com is never sent to an address somebody typed for somewhere else.
/// </para>
/// </summary>
public static class GitRepositoryAddress
{
    /// <summary>
    /// The repository an http(s) address names, as <c>host[:port]/path</c> in lower case with no
    /// <c>.git</c> and no slashes at either end. Null for anything else. GitHub, GitLab and Gitea all
    /// treat the owner and the name without regard to case, so two spellings of one are one key.
    /// </summary>
    public static string? Key(string? address)
    {
        if (!TryRead(address, out var uri)) return null;
        var path = PathOf(uri);
        return path.Length == 0 ? null : $"{uri.Authority.ToLowerInvariant()}/{path.ToLowerInvariant()}";
    }

    /// <summary><c>acme/shop</c>, or <c>group/sub/project</c> on GitLab: what each provider's API names a repository by.</summary>
    public static string? FullNameOf(string? address) =>
        TryRead(address, out var uri) && PathOf(uri) is { Length: > 0 } path ? path : null;

    /// <summary>The <c>host[:port]</c> of an address, lower case.</summary>
    public static string? HostOf(string? address) =>
        TryRead(address, out var uri) ? uri.Authority.ToLowerInvariant() : null;

    /// <summary>
    /// The host an account's repositories are cloned from: github.com for GitHub (whose API is on
    /// api.github.com), the instance itself for GitHub Enterprise (<c>https://ghe.example.com/api/v3</c>),
    /// GitLab and Gitea. Null when nothing says where a self-hosted one is.
    /// </summary>
    public static string? HostOf(GitProviderType type, string? apiBaseUrl)
    {
        var configured = string.IsNullOrWhiteSpace(apiBaseUrl) ? null : HostOf(apiBaseUrl.Trim());
        return type switch
        {
            GitProviderType.GitHub => configured is null or "api.github.com" ? "github.com" : configured,
            GitProviderType.GitLab => configured ?? "gitlab.com",
            GitProviderType.Gitea => configured,
            _ => null
        };
    }

    /// <summary>Whether <paramref name="address"/> is on the host <paramref name="account"/> belongs to.</summary>
    public static bool IsOn(GitProvider account, string? address) =>
        HostOf(account.Type, account.ApiBaseUrl) is { } host && HostOf(address) == host;

    private static bool TryRead(string? address, out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrWhiteSpace(address)
               && Uri.TryCreate(address.Trim(), UriKind.Absolute, out uri!)
               && uri.Scheme is "http" or "https"
               && uri.Host.Length > 0;
    }

    private static string PathOf(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return path.TrimEnd('/');
    }
}
