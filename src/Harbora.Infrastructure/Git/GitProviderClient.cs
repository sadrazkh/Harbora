using System.Net.Http.Headers;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Git;

/// <summary>
/// Talks to a provider's REST API with the connected token: GitHub's v3, GitLab's v4 and Gitea's v1,
/// each as its documentation describes it. None of these calls has been made against a real provider
/// from this repository's tests; they are held to the documented shapes by fake handlers
/// (<c>GitProviderClientTests</c>).
/// </summary>
public sealed class GitProviderClient(IHttpClientFactory httpFactory) : IGitProviderClient
{
    /// <summary>A guard on top of the count, for a provider that keeps answering with full pages.</summary>
    private const int MaxPages = 25;

    public async Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(
        GitProviderType type, string apiBaseUrl, string token, CancellationToken ct)
    {
        using var client = ClientFor(type, token);

        return type switch
        {
            // Everything the account owns, collaborates on or reaches through an organisation.
            GitProviderType.GitHub => await PagesAsync(client, 100,
                page => $"{GitHubApi(apiBaseUrl)}/user/repos?per_page=100&sort=updated&page={page}", GitHubRepository, ct),
            GitProviderType.GitLab => await PagesAsync(client, 100,
                page => $"{GitLabBase(apiBaseUrl)}/api/v4/projects?membership=true&per_page=100&order_by=last_activity_at&page={page}",
                GitLabRepository, ct),
            // Gitea's own ceiling on a page is an install setting (MAX_RESPONSE_ITEMS, 50 out of the
            // box), which is why the total it reports is what decides whether there is another page.
            GitProviderType.Gitea => await PagesAsync(client, 50,
                page => $"{apiBaseUrl.TrimEnd('/')}/api/v1/user/repos?limit=50&page={page}", GiteaRepository, ct),
            _ => Array.Empty<RemoteRepository>()
        };
    }

    // ---- reading -------------------------------------------------------------------------------

    private static RemoteRepository GitHubRepository(JsonElement r) => new(
        r.GetProperty("full_name").GetString()!,
        r.GetProperty("clone_url").GetString()!,
        Str(r, "default_branch") ?? "main",
        r.GetProperty("private").GetBoolean());

    private static RemoteRepository GitLabRepository(JsonElement r) => new(
        r.GetProperty("path_with_namespace").GetString()!,
        r.GetProperty("http_url_to_repo").GetString()!,
        Str(r, "default_branch") ?? "main",
        Str(r, "visibility") != "public");

    private static RemoteRepository GiteaRepository(JsonElement r) => new(
        r.GetProperty("full_name").GetString()!,
        r.GetProperty("clone_url").GetString()!,
        Str(r, "default_branch") ?? "main",
        r.GetProperty("private").GetBoolean());

    /// <summary>
    /// Page after page until the provider runs out or <see cref="IGitProviderClient.MaxRepositories"/>
    /// is reached. The total a provider reports (Gitea's <c>X-Total-Count</c>, GitLab's <c>X-Total</c>)
    /// decides whether there is another; without one, a short page is the last.
    /// </summary>
    private static async Task<IReadOnlyList<RemoteRepository>> PagesAsync(
        HttpClient client, int pageSize, Func<int, string> url, Func<JsonElement, RemoteRepository> read, CancellationToken ct)
    {
        var all = new List<RemoteRepository>();
        for (var page = 1; page <= MaxPages && all.Count < IGitProviderClient.MaxRepositories; page++)
        {
            using var response = await client.GetAsync(url(page), ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            var items = doc.RootElement.EnumerateArray().Select(read).ToList();
            all.AddRange(items);

            var total = Total(response, "X-Total-Count") ?? Total(response, "X-Total");
            var more = items.Count > 0 && (total is { } t ? all.Count < t : items.Count >= pageSize);
            if (!more) break;
        }

        return all.Count > IGitProviderClient.MaxRepositories ? all.Take(IGitProviderClient.MaxRepositories).ToList() : all;
    }

    private static int? Total(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) && int.TryParse(values.FirstOrDefault(), out var total)
            ? total
            : null;

    // ---- the client ----------------------------------------------------------------------------

    /// <summary>One client per call, each with its own provider's way of carrying the token.</summary>
    private HttpClient ClientFor(GitProviderType type, string token)
    {
        var client = httpFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Harbora");
        switch (type)
        {
            case GitProviderType.GitHub:
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                break;
            case GitProviderType.GitLab:
                // Bearer, not PRIVATE-TOKEN: GitLab takes a personal access token either way, but the
                // token "Connect with GitLab" stores is an OAuth one, which only the bearer form carries.
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            default:
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
                break;
        }

        return client;
    }

    private static string GitHubApi(string? apiBaseUrl) => Fallback(apiBaseUrl, "https://api.github.com");
    private static string GitLabBase(string? apiBaseUrl) => Fallback(apiBaseUrl, "https://gitlab.com");

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.TrimEnd('/');

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
