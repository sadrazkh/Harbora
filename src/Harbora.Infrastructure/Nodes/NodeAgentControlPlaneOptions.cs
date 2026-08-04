namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// The control plane's half of the node contract, configured under <c>NodeAgent:</c>.
/// </summary>
public sealed class NodeAgentControlPlaneOptions
{
    public const string SectionName = "NodeAgent";

    /// <summary>
    /// The URL nodes should keep using after enrollment. Handed back in the enrollment response, so
    /// an installer given a redirect or an internal address still ends up on the right one.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>TCP gateway address for database tunnels, as <c>host:port</c>. Empty disables them.</summary>
    public string? TunnelGatewayUrl { get; set; }

    /// <summary>
    /// Below this, a node is told it is too old and refuses work itself. Empty means no floor.
    /// Raising it is how a fleet is forced forward after a protocol-relevant fix.
    /// </summary>
    public string? MinimumAgentVersion { get; set; }

    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// How long a freshly minted enrollment token lives. Short on purpose: the token's whole job is
    /// to survive a copy-paste into a terminal, and one that lives for a week lives in a wiki.
    /// </summary>
    public int EnrollmentTokenMinutes { get; set; } = 30;

    /// <summary>Default ceiling for a command with no explicit timeout.</summary>
    public int DefaultCommandTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Trust <c>X-Forwarded-Tls-Client-Cert</c> for node mTLS.
    ///
    /// <para>
    /// Off by default, and it must stay off unless Traefik is configured to <em>require</em> a client
    /// certificate on the node router and to overwrite that header. A forwarded certificate header
    /// that anyone can set is not authentication — it is a login form with no password field.
    /// </para>
    /// </summary>
    public bool TrustForwardedClientCertificate { get; set; }

    /// <summary>Node-facing listener for the TCP gateway. Zero disables the gateway entirely.</summary>
    public int GatewayListenPort { get; set; }

    /// <summary>Public port range the gateway allocates from, one per active database grant.</summary>
    public int GatewayPublicPortStart { get; set; } = 41000;
    public int GatewayPublicPortEnd { get; set; } = 41999;

    /// <summary>Hostname clients are told to connect to. Defaults to the gateway URL's host.</summary>
    public string? GatewayPublicHost { get; set; }

    /// <summary>Everything that must be true before the node subsystem is worth starting.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (!string.IsNullOrWhiteSpace(PublicUrl) &&
            !Uri.TryCreate(PublicUrl, UriKind.Absolute, out _))
            problems.Add($"NodeAgent:PublicUrl '{PublicUrl}' is not an absolute URL.");

        if (HeartbeatIntervalSeconds is < 5 or > 600)
            problems.Add("NodeAgent:HeartbeatIntervalSeconds must be between 5 and 600.");

        if (EnrollmentTokenMinutes is < 1 or > 1440)
            problems.Add("NodeAgent:EnrollmentTokenMinutes must be between 1 and 1440.");

        if (GatewayListenPort is < 0 or > 65535)
            problems.Add("NodeAgent:GatewayListenPort must be a valid port, or 0 to disable the gateway.");

        if (GatewayPublicPortStart >= GatewayPublicPortEnd)
            problems.Add("NodeAgent:GatewayPublicPortStart must be below GatewayPublicPortEnd.");

        return problems;
    }
}
