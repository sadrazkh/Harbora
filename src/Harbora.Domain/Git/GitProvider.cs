using Harbora.Domain.Common;

namespace Harbora.Domain.Git;

/// <summary>A connected Git provider account (or a bare custom endpoint).</summary>
public class GitProvider : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public GitProviderType Type { get; set; }

    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>Encrypted at rest (personal access token / OAuth token). Never returned in plaintext.</summary>
    public string? EncryptedCredential { get; set; }

    public ICollection<GitRepository> Repositories { get; set; } = new List<GitRepository>();
}

public class GitRepository : BaseEntity
{
    public Guid GitProviderId { get; set; }
    public GitProvider? Provider { get; set; }

    public string FullName { get; set; } = string.Empty;   // owner/name
    public string CloneUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";

    /// <summary>Random per-repo secret used to verify inbound webhook HMAC signatures.</summary>
    public string WebhookSecret { get; set; } = string.Empty;
}
