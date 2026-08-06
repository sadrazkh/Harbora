using Harbora.Application.Abstractions;
using Harbora.Domain.Git;

namespace Harbora.Web.ViewModels;

public sealed class GitPageViewModel
{
    public List<GitProvider> Providers { get; set; } = new();
    /// <summary>Base URL used to build per-repo webhook endpoints (scheme://host).</summary>
    public string WebhookBase { get; set; } = string.Empty;

    /// <summary>
    /// The one repository whose webhook secret is being shown, if any.
    ///
    /// The page used to print every secret it had, always. A webhook secret is what proves a push
    /// notification came from the provider, so anyone who reads one off a shared screen can forge a
    /// deploy — and this is a page an operator has every reason to be showing somebody while they
    /// set a repository up. Storage answers the same question with the same shape: one at a time,
    /// and only when asked.
    /// </summary>
    public Guid? RevealedRepositoryId { get; set; }
}

public sealed class RemoteReposViewModel
{
    public GitProvider Provider { get; set; } = null!;
    public IReadOnlyList<RemoteRepository> Repositories { get; set; } = Array.Empty<RemoteRepository>();
    public string? Error { get; set; }
}
