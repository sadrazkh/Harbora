using System.Net.Http.Headers;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Git;

/// <summary>OAuth authorization-code helper for GitHub/GitLab/Gitea.</summary>
public sealed class GitOAuthService(IHttpClientFactory httpFactory) : IGitOAuthService
{
    public string DefaultOAuthBase(GitProviderType type) => type switch
    {
        GitProviderType.GitHub => "https://github.com",
        GitProviderType.GitLab => "https://gitlab.com",
        _ => "" // Gitea / custom: the instance base URL, supplied by the admin
    };

    public string BuildAuthorizeUrl(GitProviderType type, string oauthBase, string clientId, string redirectUri, string state)
    {
        var q = $"client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&response_type=code&state={Uri.EscapeDataString(state)}";
        return type switch
        {
            GitProviderType.GitHub => $"{Base(oauthBase, "https://github.com")}/login/oauth/authorize?{q}&scope=repo",
            GitProviderType.GitLab => $"{Base(oauthBase, "https://gitlab.com")}/oauth/authorize?{q}&scope=api",
            GitProviderType.Gitea => $"{oauthBase.TrimEnd('/')}/login/oauth/authorize?{q}",
            _ => $"{oauthBase.TrimEnd('/')}/login/oauth/authorize?{q}"
        };
    }

    public async Task<string> ExchangeCodeAsync(
        GitProviderType type, string oauthBase, string clientId, string clientSecret,
        string code, string redirectUri, CancellationToken ct)
    {
        var (url, form) = type switch
        {
            GitProviderType.GitHub => ($"{Base(oauthBase, "https://github.com")}/login/oauth/access_token",
                Form(clientId, clientSecret, code, redirectUri, grant: false)),
            GitProviderType.GitLab => ($"{Base(oauthBase, "https://gitlab.com")}/oauth/token",
                Form(clientId, clientSecret, code, redirectUri, grant: true)),
            _ => ($"{oauthBase.TrimEnd('/')}/login/oauth/access_token",
                Form(clientId, clientSecret, code, redirectUri, grant: true)),
        };

        var client = httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await client.SendAsync(request, ct);
        res.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("access_token", out var token))
            throw new InvalidOperationException("OAuth exchange returned no access token.");
        return token.GetString()!;
    }

    private static Dictionary<string, string> Form(string clientId, string clientSecret, string code, string redirectUri, bool grant)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        };
        if (grant) form["grant_type"] = "authorization_code";
        return form;
    }

    private static string Base(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.TrimEnd('/');
}
