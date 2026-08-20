namespace Harbora.Domain.Networking;

/// <summary>
/// The host names the platform answers on itself, which therefore cannot be bound to a tenant's app.
///
/// <para>
/// The node channel's host is the reason this rule exists, and it is the one that fails silently.
/// <c>deploy/traefik/dynamic/node-agent.yml</c> puts exactly one router on that name with
/// <c>options: harbora-node-mtls</c> and <c>clientAuthType: RequireAndVerifyClientCert</c>, and the
/// panel is configured to believe Traefik's <c>X-Forwarded-Tls-Client-Cert</c> header
/// <em>because</em> that router always overwrites it. A tenant route is rendered by
/// <c>TraefikProxyEngine.RenderRouter</c> as <c>tls: certResolver:</c> with no <c>options:</c> — the
/// default set, which asks for no client certificate. Traefik resolves TLS options per SNI host
/// name, so two routers with different options on one name make it fall back to the default: mTLS
/// stops being enforced on the single host where a client-settable header is trusted, and nothing
/// in the panel says so.
/// </para>
///
/// <para>
/// The panel's own host and the object-storage host are reserved for the same reason with a louder
/// symptom — claiming them breaks the thing visibly rather than quietly.
/// </para>
///
/// <para>
/// Deliberately exact names, never suffixes. Reserving <c>*.panel.example.com</c> would be simpler
/// and would take a whole namespace away from the tenants the platform exists to serve;
/// <c>my-nodes.panel.example.com</c> collides with nothing.
/// </para>
/// </summary>
public static class ReservedHosts
{
    /// <summary>
    /// Every host this platform serves itself, derived from the values the panel is configured with.
    /// </summary>
    /// <param name="panelDomain">
    /// <c>PANEL_DOMAIN</c> — a bare host name.
    /// </param>
    /// <param name="nodeChannelUrl">
    /// <c>NodeAgent:PublicUrl</c> — a URL, whose host is <c>NODE_DOMAIN</c>. The installer derives
    /// that as <c>nodes.$PANEL_DOMAIN</c> but an operator may set another, and both are claimed.
    /// </param>
    /// <param name="objectStorageUrl">
    /// <c>Storage:S3:PublicEndpoint</c> — the address customers are given for their buckets.
    /// </param>
    public static IReadOnlyList<string> ForPlatform(
        string? panelDomain, string? nodeChannelUrl, string? objectStorageUrl)
    {
        var hosts = new List<string>();

        void Add(string? candidate)
        {
            if (HostOf(candidate) is { Length: > 0 } host && !hosts.Contains(host, StringComparer.Ordinal))
                hosts.Add(host);
        }

        Add(panelDomain);
        Add(nodeChannelUrl);

        // The compose default for NodeAgent__PublicUrl is the empty string, and an install that
        // predates the key has nothing at all — which is precisely the broken state whose node host
        // would otherwise be the one name left claimable. The installer derives it from the panel's
        // domain, so this can derive it too rather than leave the hole open exactly when it matters.
        if (HostOf(panelDomain) is { Length: > 0 } panel)
            Add("nodes." + panel);

        Add(objectStorageUrl);

        return hosts;
    }

    /// <summary>True when <paramref name="host"/> is one of the platform's own names.</summary>
    public static bool IsReserved(string? host, IEnumerable<string> platformHosts) =>
        Normalise(host) is { Length: > 0 } candidate
        && platformHosts.Any(h => string.Equals(Normalise(h), candidate, StringComparison.Ordinal));

    /// <summary>The leftmost-label prefix the platform's own status pages claim under the tenant root domain.</summary>
    public const string StatusPagePrefix = "status-";

    /// <summary>
    /// True when <paramref name="host"/> is a leftmost label under <paramref name="rootDomain"/> that
    /// starts with <see cref="StatusPagePrefix"/> — <c>status-acme.apps.example.com</c> under a root
    /// domain of <c>apps.example.com</c>, never the root domain itself and never a label further down.
    ///
    /// <para>
    /// A prefix, not an exact list, because <see cref="ForPlatform"/>'s four names are known at boot
    /// from configuration and a status page's host is not: <c>status-{workspace.Slug}</c> is minted
    /// per workspace, on demand, and there is no fixed set to enumerate ahead of that. This still lives
    /// beside <see cref="IsReserved"/> rather than as a second reservation mechanism — both answer the
    /// same question ("can an app claim this host"), and <see cref="Harbora.Infrastructure.Networking.AppAddress.Decide"/>
    /// asks both of one host in one place.
    /// </para>
    ///
    /// <para>
    /// Scoped to the root domain apps already live under, deliberately: a customer's own domain
    /// starting with "status-" is that customer's business, not this rule's — see
    /// sub-project 8, which attaches a workspace's own status page to a domain the customer supplies.
    /// </para>
    /// </summary>
    public static bool IsReservedPrefix(string? host, string? rootDomain)
    {
        var candidate = Normalise(host);
        var root = Normalise(rootDomain);
        if (candidate is null || root is null) return false;
        if (!candidate.EndsWith("." + root, StringComparison.Ordinal)) return false;

        var label = candidate[..^(root.Length + 1)];
        return label.Length > 0
            && !label.Contains('.')
            && label.StartsWith(StatusPagePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host part of a configuration value, whether it arrives as a URL or as a bare name. The
    /// two shapes are mixed on purpose in <c>.env</c>: <c>PANEL_DOMAIN</c> is a name and
    /// <c>NodeAgent__PublicUrl</c> is a URL, and a comparison against a URL never matches anything a
    /// tenant can type into a domains form.
    /// </summary>
    internal static string? HostOf(string? urlOrHost)
    {
        var value = urlOrHost?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return Normalise(uri.Host);

        // Not a URL: it may still carry a port or a path if somebody wrote one into .env by hand.
        var host = value.Split('/', 2)[0];
        var colon = host.LastIndexOf(':');
        if (colon > 0) host = host[..colon];

        return Normalise(host);
    }

    /// <summary>
    /// A host name is case-insensitive and its root label is optional, so <c>NODES.Panel.Test.</c>
    /// and <c>nodes.panel.test</c> are the same name. A check that compares the typed string
    /// literally is a check with published bypasses.
    /// </summary>
    private static string? Normalise(string? host)
    {
        var value = host?.Trim().TrimEnd('.').ToLowerInvariant();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
