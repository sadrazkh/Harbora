using System.Net.Http.Headers;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Git;

/// <summary>Lists repositories from a provider's REST API using the connected token.</summary>
public sealed class GitProviderClient(IHttpClientFactory httpFactory) : IGitProviderClient
{
    public async Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(
        GitProviderType type, string apiBaseUrl, string token, CancellationToken ct)
    {
        var client = httpFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Harbora");

        return type switch
        {
            GitProviderType.GitHub => await GitHub(client, Fallback(apiBaseUrl, "https://api.github.com"), token, ct),
            GitProviderType.GitLab => await GitLab(client, Fallback(apiBaseUrl, "https://gitlab.com"), token, ct),
            GitProviderType.Gitea => await Gitea(client, apiBaseUrl.TrimEnd('/'), token, ct),
            _ => Array.Empty<RemoteRepository>()
        };
    }

    private static async Task<IReadOnlyList<RemoteRepository>> GitHub(HttpClient client, string baseUrl, string token, CancellationToken ct)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
        using var doc = await GetJson(client, $"{baseUrl}/user/repos?per_page=100&sort=updated", ct);
        return doc.RootElement.EnumerateArray().Select(r => new RemoteRepository(
            r.GetProperty("full_name").GetString()!,
            r.GetProperty("clone_url").GetString()!,
            Str(r, "default_branch") ?? "main",
            r.GetProperty("private").GetBoolean())).ToList();
    }

    private static async Task<IReadOnlyList<RemoteRepository>> GitLab(HttpClient client, string baseUrl, string token, CancellationToken ct)
    {
        client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
        using var doc = await GetJson(client, $"{baseUrl}/api/v4/projects?membership=true&per_page=100&order_by=last_activity_at", ct);
        return doc.RootElement.EnumerateArray().Select(r => new RemoteRepository(
            r.GetProperty("path_with_namespace").GetString()!,
            r.GetProperty("http_url_to_repo").GetString()!,
            Str(r, "default_branch") ?? "main",
            Str(r, "visibility") != "public")).ToList();
    }

    private static async Task<IReadOnlyList<RemoteRepository>> Gitea(HttpClient client, string baseUrl, string token, CancellationToken ct)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
        using var doc = await GetJson(client, $"{baseUrl}/api/v1/user/repos?limit=50", ct);
        return doc.RootElement.EnumerateArray().Select(r => new RemoteRepository(
            r.GetProperty("full_name").GetString()!,
            r.GetProperty("clone_url").GetString()!,
            Str(r, "default_branch") ?? "main",
            r.GetProperty("private").GetBoolean())).ToList();
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url, CancellationToken ct)
    {
        var res = await client.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.TrimEnd('/');

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
