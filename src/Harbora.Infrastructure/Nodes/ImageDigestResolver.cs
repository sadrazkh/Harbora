using System.Net.Http.Headers;
using Docker.DotNet;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// Turns an image reference into one pinned by digest.
///
/// <para>
/// A v1 node refuses an unpinned image, and it is right to: a tag cannot express "deploy the thing
/// that was tested". So the control plane has to resolve the tag, once, at deploy time — which also
/// means every node in a fleet gets the same bytes, rather than each resolving <c>:latest</c> at
/// whatever moment it happened to pull.
/// </para>
///
/// <para>
/// The panel's own Docker is asked first because it is free and usually right: the image was almost
/// certainly just built or pulled here. The registry is the fallback for an image the panel has
/// never seen, which is the ordinary case for a prebuilt-image app.
/// </para>
/// </summary>
public sealed class ImageDigestResolver(
    IDockerClient docker,
    IHttpClientFactory httpFactory,
    ILogger<ImageDigestResolver> log)
{
    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    public sealed class UnresolvableImageException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>
    /// The reference a node should be told to pull: <c>repository@sha256:…</c>.
    /// Throws when neither the panel nor the registry can say what the tag currently points at.
    /// </summary>
    public async Task<string> ResolveAsync(string imageReference, CancellationToken ct)
    {
        // Already pinned. Re-resolving would be a chance to change the answer.
        if (imageReference.Contains("@sha256:", StringComparison.Ordinal)) return imageReference;

        var (registry, repository, tag) = Parse(imageReference);

        if (await FromLocalDockerAsync(imageReference, repository, ct) is { } local)
        {
            log.LogDebug("Resolved {Image} to {Digest} from the panel's own Docker.", imageReference, local);
            return $"{Qualified(registry, repository)}@{local}";
        }

        var remote = await FromRegistryAsync(registry, repository, tag, ct);

        log.LogInformation("Resolved {Image} to {Digest} from {Registry}.", imageReference, remote, registry);
        return $"{Qualified(registry, repository)}@{remote}";
    }

    /// <summary>The digest of a local copy, or null when the panel does not have this image.</summary>
    private async Task<string?> FromLocalDockerAsync(string imageReference, string repository, CancellationToken ct)
    {
        try
        {
            var image = await docker.Images.InspectImageAsync(imageReference, ct);

            // RepoDigests entries look like "repo@sha256:…". An image tagged into several
            // repositories carries several, and the one for the repository we asked about is the
            // only one that means anything to the node.
            var match = image.RepoDigests?.FirstOrDefault(d =>
                d.StartsWith(repository, StringComparison.Ordinal) ||
                d.Contains("/" + repository, StringComparison.Ordinal));

            var digest = match ?? image.RepoDigests?.FirstOrDefault();

            return digest?.Split('@') is [_, { Length: > 0 } sha] ? sha : null;
        }
        catch (Exception e) when (e is DockerApiException or DockerImageNotFoundException or HttpRequestException or TimeoutException)
        {
            // No local copy, or no Docker here at all. Both mean "ask the registry".
            return null;
        }
    }

    /// <summary>
    /// The digest the registry currently serves for a tag, read from the <c>Docker-Content-Digest</c>
    /// header of a HEAD against the manifest.
    /// </summary>
    private async Task<string> FromRegistryAsync(string registry, string repository, string tag, CancellationToken ct)
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var host = registry == "docker.io" ? "registry-1.docker.io" : registry;
        var url = $"https://{host}/v2/{repository}/manifests/{tag}";

        var response = await SendAsync(client, url, token: null, ct);

        // A registry that wants a token says so in Www-Authenticate rather than just refusing.
        // Docker Hub does this for every request, including anonymous ones.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            response.Headers.WwwAuthenticate.FirstOrDefault() is { } challenge)
        {
            response.Dispose();

            var token = await FetchTokenAsync(client, challenge, ct)
                ?? throw new UnresolvableImageException(
                    $"The registry at {host} demanded authentication for {repository} and did not offer a token endpoint. " +
                    "A private registry needs credentials the panel does not have yet.");

            response = await SendAsync(client, url, token, ct);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new UnresolvableImageException(
                    $"The registry at {host} answered {(int)response.StatusCode} for {repository}:{tag}. " +
                    "A node cannot deploy an image whose digest the panel could not resolve.");

            var digest = response.Headers.TryGetValues("Docker-Content-Digest", out var values)
                ? values.FirstOrDefault()
                : null;

            if (string.IsNullOrWhiteSpace(digest))
                throw new UnresolvableImageException(
                    $"{host} served a manifest for {repository}:{tag} with no Docker-Content-Digest header.");

            return digest;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string url, string? token, CancellationToken ct)
    {
        // HEAD rather than GET: the digest is a header, and the manifest body can be megabytes for
        // a multi-architecture index nobody here is going to read.
        using var request = new HttpRequestMessage(HttpMethod.Head, url);

        foreach (var mediaType in ManifestMediaTypes)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));

        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Follow a bearer challenge to the registry's token endpoint, anonymously.</summary>
    private async Task<string?> FetchTokenAsync(
        HttpClient client, AuthenticationHeaderValue challenge, CancellationToken ct)
    {
        if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)) return null;

        var parameters = ParseChallenge(challenge.Parameter);

        if (!parameters.TryGetValue("realm", out var realm)) return null;

        var query = new List<string>();
        if (parameters.TryGetValue("service", out var service)) query.Add($"service={Uri.EscapeDataString(service)}");
        if (parameters.TryGetValue("scope", out var scope)) query.Add($"scope={Uri.EscapeDataString(scope)}");

        var url = query.Count > 0 ? $"{realm}?{string.Join('&', query)}" : realm;

        try
        {
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            // Docker Hub calls it "token"; the OCI spec calls it "access_token". Registries differ
            // about which they send, and some send both.
            foreach (var name in (string[])["token", "access_token"])
                if (document.RootElement.TryGetProperty(name, out var value) &&
                    value.GetString() is { Length: > 0 } issued)
                    return issued;

            return null;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            log.LogDebug(e, "Could not obtain a registry token from {Url}.", url);
            return null;
        }
    }

    /// <summary>Split <c>realm="…",service="…",scope="…"</c> into its parts.</summary>
    public static Dictionary<string, string> ParseChallenge(string? parameter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(parameter)) return result;

        foreach (var part in parameter.Split(','))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;

            result[part[..separator].Trim()] = part[(separator + 1)..].Trim().Trim('"');
        }

        return result;
    }

    /// <summary>
    /// Split an image reference into registry, repository and tag, applying Docker's defaults.
    ///
    /// <para>
    /// The awkward case is telling a registry host from the first path segment: <c>nginx</c> means
    /// <c>docker.io/library/nginx</c>, <c>acme/app</c> means <c>docker.io/acme/app</c>, and
    /// <c>ghcr.io/acme/app</c> means what it says. The rule everyone uses is that the first segment
    /// is a host only if it contains a dot or a colon, or is exactly "localhost".
    /// </para>
    /// </summary>
    public static (string Registry, string Repository, string Tag) Parse(string imageReference)
    {
        var reference = imageReference.Trim();

        var tag = "latest";
        var lastColon = reference.LastIndexOf(':');
        var lastSlash = reference.LastIndexOf('/');

        if (lastColon > lastSlash && lastColon >= 0)
        {
            tag = reference[(lastColon + 1)..];
            reference = reference[..lastColon];
        }

        var registry = "docker.io";
        var slash = reference.IndexOf('/');

        if (slash > 0)
        {
            var first = reference[..slash];

            if (first.Contains('.') || first.Contains(':') || first == "localhost")
            {
                registry = first;
                reference = reference[(slash + 1)..];
            }
        }

        // Docker Hub's official images live under the implicit "library" namespace.
        if (registry == "docker.io" && !reference.Contains('/')) reference = "library/" + reference;

        return (registry, reference, tag);
    }

    private static string Qualified(string registry, string repository) =>
        registry == "docker.io" ? $"docker.io/{repository}" : $"{registry}/{repository}";
}
