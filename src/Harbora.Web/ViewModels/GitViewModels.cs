using Harbora.Application.Abstractions;
using Harbora.Domain.Git;

namespace Harbora.Web.ViewModels;

public sealed class GitPageViewModel
{
    public List<GitProvider> Providers { get; set; } = new();
    /// <summary>Base URL used to build per-repo webhook endpoints (scheme://host).</summary>
    public string WebhookBase { get; set; } = string.Empty;
}

public sealed class RemoteReposViewModel
{
    public GitProvider Provider { get; set; } = null!;
    public IReadOnlyList<RemoteRepository> Repositories { get; set; } = Array.Empty<RemoteRepository>();
    public string? Error { get; set; }
}
