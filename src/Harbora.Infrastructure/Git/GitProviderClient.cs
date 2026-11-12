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

    // ---- push webhooks -------------------------------------------------------------------------
    //
    // GitHub:  GET/POST /repos/{owner}/{repo}/hooks, PATCH /repos/{owner}/{repo}/hooks/{id}
    // GitLab:  GET/POST /api/v4/projects/{url-encoded path}/hooks, PUT /api/v4/projects/{path}/hooks/{id}
    // Gitea:   GET/POST /api/v1/repos/{owner}/{repo}/hooks, PATCH /api/v1/repos/{owner}/{repo}/hooks/{id}
    //
    // One page of hooks is read, a hundred on GitHub and GitLab and fifty on Gitea: a repository with
    // more webhooks than that is not one this was written for, and Harbora's would then go unseen.

    public async Task<IReadOnlyList<RemoteHook>> ListHooksAsync(
        GitProviderType type, string apiBaseUrl, string token, string fullName, CancellationToken ct)
    {
        using var client = ClientFor(type, token);
        var page = type == GitProviderType.Gitea ? "?limit=50" : "?per_page=100";
        using var response = await client.GetAsync($"{HooksUrl(type, apiBaseUrl, fullName)}{page}", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray().Select(h => Hook(type, h)).ToList();
    }

    public async Task<RemoteHook> CreateHookAsync(
        GitProviderType type, string apiBaseUrl, string token, string fullName, HookSpec spec, CancellationToken ct)
    {
        using var client = ClientFor(type, token);
        using var response = await client.PostAsync(HooksUrl(type, apiBaseUrl, fullName), Body(type, spec, creating: true), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Hook(type, doc.RootElement);
    }

    public async Task UpdateHookAsync(
        GitProviderType type, string apiBaseUrl, string token, string fullName, string hookId, HookSpec spec, CancellationToken ct)
    {
        using var client = ClientFor(type, token);
        using var request = new HttpRequestMessage(type == GitProviderType.GitLab ? HttpMethod.Put : HttpMethod.Patch,
            $"{HooksUrl(type, apiBaseUrl, fullName)}/{Uri.EscapeDataString(hookId)}")
        {
            Content = Body(type, spec, creating: false)
        };
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string HooksUrl(GitProviderType type, string apiBaseUrl, string fullName)
    {
        // Each segment escaped for GitHub and Gitea; the whole path as one escaped id for GitLab.
        var path = string.Join("/", fullName.Trim('/').Split('/').Select(Uri.EscapeDataString));
        return type switch
        {
            GitProviderType.GitHub => $"{GitHubApi(apiBaseUrl)}/repos/{path}/hooks",
            GitProviderType.GitLab => $"{GitLabBase(apiBaseUrl)}/api/v4/projects/{Uri.EscapeDataString(fullName.Trim('/'))}/hooks",
            GitProviderType.Gitea => $"{apiBaseUrl.TrimEnd('/')}/api/v1/repos/{path}/hooks",
            _ => throw new NotSupportedException($"{type} has no webhook API this client speaks.")
        };
    }

    /// <summary>
    /// Pushes and tags, as JSON, to Harbora's address, proven by the secret the way
    /// <see cref="GitWebhookProcessor"/> checks it: an HMAC signature from GitHub and Gitea, the secret
    /// itself as GitLab's token header. A tag on GitHub and Gitea arrives as a push of <c>refs/tags/…</c>.
    /// </summary>
    private static StringContent Body(GitProviderType type, HookSpec spec, bool creating)
    {
        object body = type switch
        {
            GitProviderType.GitLab => new Dictionary<string, object>
            {
                ["url"] = spec.Url,
                ["token"] = spec.Secret,
                ["push_events"] = true,
                ["tag_push_events"] = true,
                ["enable_ssl_verification"] = true
            },
            GitProviderType.GitHub => creating
                ? new Dictionary<string, object>
                {
                    ["name"] = "web", ["active"] = true, ["events"] = new[] { "push" },
                    ["config"] = new Dictionary<string, string> { ["url"] = spec.Url, ["content_type"] = "json", ["secret"] = spec.Secret, ["insecure_ssl"] = "0" }
                }
                : new Dictionary<string, object>
                {
                    ["active"] = true, ["events"] = new[] { "push" },
                    ["config"] = new Dictionary<string, string> { ["url"] = spec.Url, ["content_type"] = "json", ["secret"] = spec.Secret, ["insecure_ssl"] = "0" }
                },
            _ => creating
                ? new Dictionary<string, object>
                {
                    ["type"] = "gitea", ["active"] = true, ["events"] = new[] { "push" },
                    ["config"] = new Dictionary<string, string> { ["url"] = spec.Url, ["content_type"] = "json", ["secret"] = spec.Secret }
                }
                : new Dictionary<string, object>
                {
                    ["active"] = true, ["events"] = new[] { "push" },
                    ["config"] = new Dictionary<string, string> { ["url"] = spec.Url, ["content_type"] = "json", ["secret"] = spec.Secret }
                }
        };

        return new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
    }

    /// <summary>GitHub and Gitea keep the address under <c>config.url</c>, GitLab at the top, and GitLab says a hook is off through <c>alert_status</c>.</summary>
    private static RemoteHook Hook(GitProviderType type, JsonElement h)
    {
        var id = h.GetProperty("id").ToString();
        if (type == GitProviderType.GitLab)
            return new RemoteHook(id, Str(h, "url") ?? "", Str(h, "alert_status") is null or "executable");

        var url = h.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object ? Str(config, "url") : null;
        var active = !h.TryGetProperty("active", out var on) || on.ValueKind != JsonValueKind.False;
        return new RemoteHook(id, url ?? "", active);
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
