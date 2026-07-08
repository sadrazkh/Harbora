using Harbora.Domain.Common;

namespace Harbora.Application.Abstractions;

/// <summary>
/// OAuth authorization-code flow for Git providers. The admin registers an OAuth app once
/// (client id/secret); users then "Connect with GitHub/GitLab/Gitea" instead of pasting a token.
/// </summary>
public interface IGitOAuthService
{
    /// <summary>Default OAuth host for a provider (where the login/authorize pages live).</summary>
    string DefaultOAuthBase(GitProviderType type);

    string BuildAuthorizeUrl(GitProviderType type, string oauthBase, string clientId, string redirectUri, string state);

    Task<string> ExchangeCodeAsync(
        GitProviderType type, string oauthBase, string clientId, string clientSecret,
        string code, string redirectUri, CancellationToken ct);
}
