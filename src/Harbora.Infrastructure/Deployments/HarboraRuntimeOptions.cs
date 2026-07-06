namespace Harbora.Infrastructure.Deployments;

/// <summary>Filesystem + naming conventions the deployment engine uses on the host.</summary>
public sealed class HarboraRuntimeOptions
{
    /// <summary>Where sources are checked out and built.</summary>
    public string WorkDir { get; set; } = "/var/lib/harbora/builds";

    /// <summary>Shared docker network apps + Traefik join so the proxy can reach containers by name.</summary>
    public string Network { get; set; } = "harbora";

    /// <summary>Image repository prefix, e.g. "harbora/{slug}:build-{n}".</summary>
    public string ImagePrefix { get; set; } = "harbora";

    /// <summary>Root domain used to build default subdomains: {slug}.{RootDomain}.</summary>
    public string RootDomain { get; set; } = "localhost";
}
