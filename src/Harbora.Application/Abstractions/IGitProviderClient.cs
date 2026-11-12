using Harbora.Domain.Common;

namespace Harbora.Application.Abstractions;

/// <summary>
/// A connected provider's (GitHub/GitLab/Gitea) API, through the stored token: the repositories the
/// account can reach, so a person picks one instead of pasting a clone URL.
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
    /// up to <see cref="MaxRepositories"/>. Throws <see cref="HttpRequestException"/> when the provider
    /// refuses (its status code says why) or cannot be reached.
    /// </summary>
    Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(GitProviderType type, string apiBaseUrl, string token, CancellationToken ct);
}

public sealed record RemoteRepository(string FullName, string CloneUrl, string DefaultBranch, bool Private);
