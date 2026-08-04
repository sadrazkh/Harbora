using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Templates;

/// <summary>
/// Reads public container registries over the OCI distribution API.
///
/// Anonymous, read-only, and against an allowlist of hosts. Registries answer an unauthenticated
/// request with 401 and a <c>WWW-Authenticate</c> header naming where to get a token; that dance is
/// the same at Docker Hub, GHCR and Quay, so it is done once here rather than per registry.
///
/// Every failure returns nothing rather than throwing. A registry being unreachable is an ordinary
/// Tuesday, and a discovery run that throws on the first bad repository never reaches the rest.
/// </summary>
public sealed class ContainerRegistryClient(
    IHttpClientFactory httpClientFactory,
    ILogger<ContainerRegistryClient> logger) : IContainerRegistry
{
    /// <summary>
    /// Manifest types worth asking for, most modern first. Without these headers a registry returns
    /// a v1 manifest whose digest is not the one anybody else means by that tag.
    /// </summary>
    private static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json"
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct)
    {
        var reference = RegistryReferences.Parse(repository);
        if (reference is null)
        {
            logger.LogWarning("Repository {Repository} names a registry Harbora does not read.", repository);
            return [];
        }

        var response = await SendAsync(reference, $"/v2/{reference.Path}/tags/list", HttpMethod.Get, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            logger.LogInformation("Could not list tags for {Repository} ({Status}).",
                repository, response?.StatusCode.ToString() ?? "no response");
            return [];
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!document.RootElement.TryGetProperty("tags", out var tags) ||
                tags.ValueKind != JsonValueKind.Array)
                return [];

            return tags.EnumerateArray()
                .Select(t => t.GetString())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "The tag list for {Repository} could not be read.", repository);
            return [];
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<string?> ResolveDigestAsync(string repository, string tag, CancellationToken ct)
    {
        var reference = RegistryReferences.Parse(repository);
        if (reference is null) return null;

        // HEAD, because the digest is in a header and the body can be megabytes. A registry that
        // refuses HEAD is handled by falling back to GET below.
        var response = await SendAsync(reference, $"/v2/{reference.Path}/manifests/{tag}", HttpMethod.Head, ct)
                       ?? await SendAsync(reference, $"/v2/{reference.Path}/manifests/{tag}", HttpMethod.Get, ct);

        if (response is null) return null;

        try
        {
            if (!response.IsSuccessStatusCode) return null;

            if (!response.Headers.TryGetValues("Docker-Content-Digest", out var values)) return null;

            var digest = values.FirstOrDefault()?.Trim();

            // Only sha256. Anything else is a form we have not checked, and a digest we cannot
            // verify the shape of is one we should not pin a customer's deployment to.
            return digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest : null;
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// One request, acquiring an anonymous token if the registry asks for one.
    ///
    /// The token is fetched from the realm the registry itself names, and only after checking that
    /// realm is on the same allowlist — a registry that answers 401 with a realm pointing somewhere
    /// else would otherwise redirect our server wherever it likes.
    /// </summary>
    private async Task<HttpResponseMessage?> SendAsync(
        RegistryReference reference, string path, HttpMethod method, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(nameof(ContainerRegistryClient));
        client.Timeout = Timeout;

        try
        {
            var uri = new Uri($"https://{reference.Host}{path}");

            var first = await client.SendAsync(Build(method, uri), HttpCompletionOption.ResponseHeadersRead, ct);
            if (first.StatusCode != HttpStatusCode.Unauthorized) return first;

            var challenge = first.Headers.WwwAuthenticate.FirstOrDefault();
            first.Dispose();

            var token = challenge is null ? null : await TokenAsync(client, challenge, ct);
            if (token is null) return null;

            var retry = Build(method, uri);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            logger.LogInformation(ex, "Registry request to {Host}{Path} failed.", reference.Host, path);
            return null;
        }
    }

    private static HttpRequestMessage Build(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        foreach (var type in ManifestTypes)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));

        // Registries rate-limit anonymous traffic by client. Saying who we are is what lets an
        // operator recognise Harbora's requests in their own logs.
        request.Headers.UserAgent.ParseAdd("Harbora/1.0 (+https://github.com/sadrazkh/Harbora)");
        return request;
    }

    private async Task<string?> TokenAsync(
        HttpClient client, AuthenticationHeaderValue challenge, CancellationToken ct)
    {
        var parameters = ParseChallenge(challenge.Parameter);
        if (!parameters.TryGetValue("realm", out var realm)) return null;

        if (!Uri.TryCreate(realm, UriKind.Absolute, out var realmUri)) return null;

        // The realm is a value the remote side chose. Checked against the same allowlist as the
        // registry itself, or a 401 becomes an instruction telling our server where to send
        // requests next.
        if (realmUri.Scheme != Uri.UriSchemeHttps) return null;
        if (!RegistryReferences.Allowed.ContainsKey(realmUri.Host)
            && !IsKnownTokenHost(realmUri.Host)) return null;

        var query = new List<string>();
        if (parameters.TryGetValue("service", out var service))
            query.Add($"service={Uri.EscapeDataString(service)}");
        if (parameters.TryGetValue("scope", out var scope))
            query.Add($"scope={Uri.EscapeDataString(scope)}");

        var tokenUri = new Uri(query.Count == 0 ? realm : $"{realm}?{string.Join("&", query)}");

        try
        {
            using var response = await client.GetAsync(tokenUri, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // Docker Hub returns "token"; some registries return "access_token" for the same thing.
            foreach (var name in new[] { "token", "access_token" })
                if (document.RootElement.TryGetProperty(name, out var value) &&
                    value.GetString() is { Length: > 0 } text)
                    return text;

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation(ex, "Could not obtain an anonymous registry token.");
            return null;
        }
    }

    /// <summary>Token endpoints that are not themselves registries — Docker Hub's is separate.</summary>
    private static bool IsKnownTokenHost(string host) =>
        host.Equals("auth.docker.io", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseChallenge(string? parameter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(parameter)) return result;

        foreach (var part in parameter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0) continue;

            var key = part[..equals].Trim();
            var value = part[(equals + 1)..].Trim().Trim('"');
            if (key.Length > 0 && value.Length > 0) result[key] = value;
        }

        return result;
    }
}
