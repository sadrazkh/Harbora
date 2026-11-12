using System.Text;
using Harbora.Domain.Common;

namespace Harbora.Application.Abstractions;

/// <summary>
/// A connected provider's (GitHub/GitLab/Gitea) API, through the stored token: the repositories the
/// account can reach, so a person picks one instead of pasting a clone URL, and the push webhooks on
/// one of them, so a push deploys without anybody pasting an address and a secret into the provider.
///
/// <para>
/// Every call throws <see cref="HttpRequestException"/> when the provider refuses (its status code
/// says why) or cannot be reached. None of them puts the token anywhere but a request header.
/// </para>
/// </summary>
public interface IGitProviderClient
{
    /// <summary>
    /// How many repositories a list stops at. An account with more is shown its most recently active
    /// ones, and can still paste the address of any other: a list that never ends is not one anybody
    /// chooses from.
    /// </summary>
    public const int MaxRepositories = 1000;

    /// <summary>
    /// Every repository the account can reach, most recently active first where the provider can sort,
    /// up to <see cref="MaxRepositories"/>.
    /// </summary>
    Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(GitProviderType type, string apiBaseUrl, string token, CancellationToken ct);

    /// <param name="fullName"><c>owner/name</c>, or <c>group/sub/project</c> on GitLab.</param>
    Task<IReadOnlyList<RemoteHook>> ListHooksAsync(GitProviderType type, string apiBaseUrl, string token, string fullName, CancellationToken ct);

    /// <summary>Adds a hook that sends pushes and tags as JSON to <see cref="HookSpec.Url"/>, signed (or, on GitLab, tokened) with the secret. Returns it as the provider recorded it.</summary>
    Task<RemoteHook> CreateHookAsync(GitProviderType type, string apiBaseUrl, string token, string fullName, HookSpec spec, CancellationToken ct);

    /// <summary>Sets an existing hook to exactly what <paramref name="spec"/> says, secret included, and turns it on.</summary>
    Task UpdateHookAsync(GitProviderType type, string apiBaseUrl, string token, string fullName, string hookId, HookSpec spec, CancellationToken ct);
}

public sealed record RemoteRepository(string FullName, string CloneUrl, string DefaultBranch, bool Private);

/// <param name="Active">Whether the provider will deliver to it. A hook GitLab disabled after failures, or one turned off by hand, deploys nothing.</param>
public sealed record RemoteHook(string Id, string Url, bool Active);

/// <summary>What a push hook is to be: where it posts, and the secret it proves itself with.</summary>
public sealed record HookSpec(string Url, string Secret)
{
    /// <summary>A record prints every property by default, and this one carries the webhook secret.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Url = {Url}");
        return true;
    }
}
