namespace Harbora.Infrastructure.Templates;

/// <summary>Where a repository lives and what to call it when asking.</summary>
/// <param name="Host">The registry's API host — <c>registry-1.docker.io</c>, not <c>docker.io</c>.</param>
/// <param name="Path">The repository path the API wants, e.g. <c>library/postgres</c>.</param>
public sealed record RegistryReference(string Host, string Path);

/// <summary>
/// Turning <c>postgres</c> or <c>ghcr.io/n8n-io/n8n</c> into a registry host and an API path.
///
/// The allowlist is the point. The host comes from a template's stored repository and is then called
/// by our own server, so whatever can write that field chooses who we talk to. Restricting it to
/// registries we deliberately support means a template naming <c>169.254.169.254/foo</c> is refused
/// here rather than discovered as a repository.
/// </summary>
public static class RegistryReferences
{
    /// <summary>
    /// Registries Harbora will talk to. Adding one is a deliberate act, which is the safeguard: an
    /// arbitrary host here would let a template decide where our server sends requests.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Allowed =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker.io"] = "registry-1.docker.io",
            ["index.docker.io"] = "registry-1.docker.io",
            ["registry-1.docker.io"] = "registry-1.docker.io",
            ["ghcr.io"] = "ghcr.io",
            ["quay.io"] = "quay.io",
            ["registry.k8s.io"] = "registry.k8s.io",
            ["mcr.microsoft.com"] = "mcr.microsoft.com"
        };

    /// <summary>The registry to ask, or null when the repository names one we do not talk to.</summary>
    public static RegistryReference? Parse(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return null;

        var value = repository.Trim().Trim('/');
        if (value.Length == 0) return null;

        // A digest or tag has no business in a repository field, and silently stripping one would
        // hide the fact that something wrote the wrong thing there.
        if (value.Contains('@') || value.Contains(' ')) return null;

        var firstSegment = value.Split('/')[0];

        // A registry host is the first segment only when it looks like a host. Docker's own naming
        // makes "myorg/myapp" a Docker Hub repository and "ghcr.io/myorg/myapp" a GHCR one, and the
        // only thing telling them apart is the dot.
        var hasHost = firstSegment.Contains('.') || firstSegment.Contains(':') || firstSegment == "localhost";

        if (!hasHost)
        {
            // Docker Hub. A bare name is an official image and lives under library/.
            var path = value.Contains('/') ? value : $"library/{value}";
            return new RegistryReference(Allowed["docker.io"], path);
        }

        if (!Allowed.TryGetValue(firstSegment, out var host)) return null;

        // A repository that is only a host names nothing. Slicing past the end would throw, and a
        // parser that throws on a stored value takes the whole discovery run with it.
        if (value.Length <= firstSegment.Length + 1) return null;

        var remainder = value[(firstSegment.Length + 1)..].Trim('/');
        if (remainder.Length == 0) return null;

        // Docker Hub reached through its own name still needs the library prefix for official images.
        if (host == "registry-1.docker.io" && !remainder.Contains('/'))
            remainder = $"library/{remainder}";

        return new RegistryReference(host, remainder);
    }
}
