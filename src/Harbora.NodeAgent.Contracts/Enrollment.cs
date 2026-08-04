namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// What the installer sends once, holding a short-lived enrollment token. The token is spent here
/// and never used again: everything afterwards is authenticated by the certificate issued in
/// response, so a token leaked from a shell history or a CI log grants nothing after first use.
/// </summary>
public sealed record EnrollmentRequest
{
    /// <summary>Short-lived, single-use token minted by an admin in the control plane.</summary>
    public required string EnrollmentToken { get; init; }

    /// <summary>Operator-chosen name. Not unique by itself; the control plane assigns the real id.</summary>
    public required string NodeName { get; init; }

    /// <summary>
    /// PEM-encoded PKCS#10 certificate request. The private key never leaves the node — the
    /// control plane sees only the public half, so a compromised panel database cannot impersonate
    /// a node it once enrolled.
    /// </summary>
    public required string CertificateSigningRequestPem { get; init; }

    public required string AgentVersion { get; init; }
    public required IReadOnlyList<int> SupportedProtocolVersions { get; init; }

    public string? Region { get; init; }
    public string? Environment { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    public required NodeInventory Inventory { get; init; }
    public required NodeCapabilities Capabilities { get; init; }

    /// <summary>
    /// Stable machine fingerprint (hash of machine-id + primary MAC). Lets a re-enrollment of the
    /// same box be recognised as such instead of silently creating a duplicate node.
    /// </summary>
    public string? MachineFingerprint { get; init; }
}

/// <summary>The node's permanent identity plus the material needed to keep it.</summary>
public sealed record EnrollmentResponse
{
    /// <summary>Permanent node id. Survives re-enrollment, credential rotation and agent updates.</summary>
    public required string NodeId { get; init; }

    /// <summary>PEM certificate signed by the control plane's node CA. Presented on every later call.</summary>
    public required string CertificatePem { get; init; }

    /// <summary>PEM CA chain the node uses to verify the control plane in return.</summary>
    public required string CaCertificatePem { get; init; }

    public required DateTimeOffset CertificateNotAfter { get; init; }

    /// <summary>Absolute base URL of the control plane, in case the installer was given a redirect.</summary>
    public required string ControlPlaneUrl { get; init; }

    /// <summary>Optional dedicated TCP gateway host for database tunnels.</summary>
    public string? TunnelGatewayUrl { get; init; }

    public required int ProtocolVersion { get; init; }
    public IReadOnlyList<string>? GrantedScopes { get; init; }
    public string? MinimumAgentVersion { get; init; }
    public int HeartbeatIntervalSeconds { get; init; } = 30;
}

/// <summary>
/// A renewal, authenticated by the certificate being replaced. Rotation is routine rather than
/// exceptional: the node starts asking well before expiry so a failed renewal has many retries
/// left before it becomes an outage.
/// </summary>
public sealed record CredentialRenewalRequest
{
    public required string NodeId { get; init; }
    public required string CertificateSigningRequestPem { get; init; }
    public required string AgentVersion { get; init; }
}

public sealed record CredentialRenewalResponse
{
    public required string CertificatePem { get; init; }
    public required string CaCertificatePem { get; init; }
    public required DateTimeOffset CertificateNotAfter { get; init; }
    public IReadOnlyList<string>? GrantedScopes { get; init; }
}

/// <summary>Machine-readable rejection of an enrollment or renewal, returned with a 4xx.</summary>
public sealed record EnrollmentFailure
{
    public required NodeErrorCode Code { get; init; }
    public required string Message { get; init; }
}
