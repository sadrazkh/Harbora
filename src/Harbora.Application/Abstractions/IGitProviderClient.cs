using Harbora.Domain.Common;

namespace Harbora.Application.Abstractions;

/// <summary>
/// Lists repositories from a connected provider (GitHub/GitLab/Gitea) using the stored token,
/// so users can import a repo instead of pasting a clone URL.
/// </summary>
public interface IGitProviderClient
{
    Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(GitProviderType type, string apiBaseUrl, string token, CancellationToken ct);
}

public sealed record RemoteRepository(string FullName, string CloneUrl, string DefaultBranch, bool Private);
