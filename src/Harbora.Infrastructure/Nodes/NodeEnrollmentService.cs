using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Nodes;
using Harbora.NodeAgent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Two unrelated types share this name: the control plane's own summary of a node it reaches over
// HTTP (Application.Abstractions), and what a v1 node reports about itself in its hello frame. This
// file is about the second. Aliasing rather than renaming either keeps the contract type matching
// the wire schema and leaves the older abstraction alone.
using NodeCapabilities = Harbora.NodeAgent.Contracts.NodeCapabilities;

namespace Harbora.Infrastructure.Nodes;

/// <summary>A freshly minted enrollment token. The plaintext exists only in this object.</summary>
public sealed record NewEnrollmentToken(string Token, string Prefix, DateTimeOffset ExpiresAt);

/// <summary>Enrollment or renewal outcome, with the failure typed the way the contract defines it.</summary>
public sealed record NodeEnrollmentResult<T>(T? Value, NodeErrorCode? Error, string? Message) where T : class
{
    public bool Success => Error is null && Value is not null;

    public static NodeEnrollmentResult<T> Ok(T value) => new(value, null, null);
    public static NodeEnrollmentResult<T> Fail(NodeErrorCode code, string message) => new(null, code, message);
}

/// <summary>
/// Turns a short-lived ticket into a permanent node identity, and keeps that identity current.
///
/// <para>
/// The control plane never sees a node's private key. It receives a CSR, signs it, and stores the
/// resulting certificate's thumbprint — so a stolen panel database lets an attacker revoke nodes,
/// which is noisy and recoverable, but not impersonate one.
/// </para>
/// </summary>
public sealed class NodeEnrollmentService(
    HarboraDbContext db,
    NodeCertificateAuthority ca,
    IAuditLogger audit,
    IOptions<NodeAgentControlPlaneOptions> options,
    TimeProvider clock,
    ILogger<NodeEnrollmentService> log)
{
    private const string TokenPrefixLabel = "hbr_node_";

    private readonly NodeAgentControlPlaneOptions _options = options.Value;

    // --- minting ---

    /// <summary>
    /// Create a token an admin hands to one installer.
    ///
    /// <para>
    /// The lifetime is short by default because the token's whole job is to survive a copy-paste
    /// into a terminal. A token that lives for a week is a token that lives in a wiki.
    /// </para>
    /// </summary>
    public async Task<NewEnrollmentToken> MintTokenAsync(
        Guid createdByUserId,
        string? nodeNameHint,
        string? region,
        string? environment,
        IReadOnlyDictionary<string, string>? labels,
        IReadOnlyList<string>? scopes,
        TimeSpan? lifetime,
        CancellationToken ct)
    {
        var secret = RandomNumberGenerator.GetHexString(48, lowercase: true);
        var token = TokenPrefixLabel + secret;
        var prefix = token[..Math.Min(20, token.Length)];

        var granted = scopes is { Count: > 0 } ? scopes : NodeScopes.Default;
        var invalid = granted.Where(s => !NodeScopes.Default.Contains(s, StringComparer.Ordinal)).ToList();

        if (invalid.Count > 0)
            throw new ArgumentException($"Unknown node scope(s): {string.Join(", ", invalid)}.", nameof(scopes));

        var expiresAt = clock.GetUtcNow() + (lifetime ?? TimeSpan.FromMinutes(_options.EnrollmentTokenMinutes));

        db.NodeEnrollmentTokens.Add(new NodeEnrollmentToken
        {
            Prefix = prefix,
            TokenHash = Hash(token),
            ExpiresAt = expiresAt,
            CreatedByUserId = createdByUserId,
            NodeNameHint = nodeNameHint,
            Region = region,
            Environment = environment,
            LabelsJson = JsonSerializer.Serialize(labels ?? new Dictionary<string, string>(), NodeContract.Json),
            ScopesJson = JsonSerializer.Serialize(granted, NodeContract.Json),
        });

        await db.SaveChangesAsync(ct);

        // Platform infrastructure, not a workspace's own act: the token authorises enrolling a
        // server into the platform before any workspace's workloads run on it.
        await audit.LogAsync("node.enrollment_token_created", "node-token", prefix,
            metadataJson: JsonSerializer.Serialize(new { expiresAt, scopes = granted }, NodeContract.Json),
            workspaceId: null, ct: ct);

        log.LogInformation("Minted node enrollment token {Prefix}…, valid until {ExpiresAt:u}.", prefix, expiresAt);

        return new NewEnrollmentToken(token, prefix, expiresAt);
    }

    // --- enrollment ---

    public async Task<NodeEnrollmentResult<EnrollmentResponse>> EnrollAsync(
        string presentedToken, EnrollmentRequest request, string? sourceIp, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var token = await FindTokenAsync(presentedToken, ct);

        if (token is null)
        {
            // One audit line either way, and the same wording for "no such token" and "wrong token",
            // so this cannot be used to probe which prefixes exist.
            await audit.LogAsync("node.enroll_failed", "node", null, sourceIp,
                metadataJson: JsonSerializer.Serialize(new { reason = "invalid token" }),
                workspaceId: null, ct: ct);

            return NodeEnrollmentResult<EnrollmentResponse>.Fail(
                NodeErrorCode.EnrollmentTokenInvalid, "That enrollment token is not valid.");
        }

        if (token.IsSpent)
            return await RefusedAsync(token, NodeErrorCode.EnrollmentTokenAlreadyUsed,
                "That enrollment token has already been used. Create a new one in the panel.", sourceIp, ct);

        if (token.IsRevoked)
            return await RefusedAsync(token, NodeErrorCode.EnrollmentTokenInvalid,
                "That enrollment token was revoked.", sourceIp, ct);

        if (token.IsExpired(now))
            return await RefusedAsync(token, NodeErrorCode.EnrollmentTokenExpired,
                $"That enrollment token expired at {token.ExpiresAt:u}. Create a new one in the panel.", sourceIp, ct);

        if (!request.SupportedProtocolVersions.Intersect(NodeContract.SupportedProtocolVersions).Any())
            return await RefusedAsync(token, NodeErrorCode.UnsupportedProtocolVersion,
                $"This panel speaks protocol v{string.Join(", v", NodeContract.SupportedProtocolVersions)}; " +
                $"the agent offered v{string.Join(", v", request.SupportedProtocolVersions)}. Update one of them.",
                sourceIp, ct);

        if (request.Inventory.Architecture is not ("amd64" or "arm64"))
            return await RefusedAsync(token, NodeErrorCode.UnsupportedArchitecture,
                $"Architecture '{request.Inventory.Architecture}' is not supported.", sourceIp, ct);

        // A re-install of a machine that is already enrolled reuses its node id rather than becoming
        // a second node. Two node rows for one machine would compete for the same containers, and
        // the panel would show both as healthy while each undid the other's work.
        var existing = request.MachineFingerprint is { Length: > 0 } fingerprint
            ? await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.MachineFingerprint == fingerprint, ct)
            : null;

        var node = existing ?? new Node { NodeId = NewNodeId() };
        var scopes = Deserialize<List<string>>(token.ScopesJson) ?? NodeScopes.Default.ToList();

        SignedNodeCertificate signed;
        try
        {
            signed = await ca.SignAsync(request.CertificateSigningRequestPem, node.NodeId, request.NodeName, ct);
        }
        catch (NodeCertificateException e)
        {
            return await RefusedAsync(token, NodeErrorCode.ValidationFailed, e.Message, sourceIp, ct);
        }

        node.Name = string.IsNullOrWhiteSpace(request.NodeName) ? token.NodeNameHint ?? node.NodeId : request.NodeName;
        node.MachineFingerprint = request.MachineFingerprint;
        node.Status = NodeStatus.Pending;
        node.AgentVersion = request.AgentVersion;
        node.ProtocolVersion = NodeContract.ProtocolVersion;
        node.Region = request.Region ?? token.Region;
        node.Environment = request.Environment ?? token.Environment;
        node.LabelsJson = JsonSerializer.Serialize(request.Labels ?? Deserialize<Dictionary<string, string>>(token.LabelsJson) ?? [], NodeContract.Json);
        node.GrantedScopesJson = JsonSerializer.Serialize(scopes, NodeContract.Json);
        node.CertificateThumbprint = signed.Thumbprint;
        node.CertificateSerial = signed.SerialNumber;
        node.CertificateNotAfter = signed.NotAfter;
        node.CertificateGeneration = existing is null ? 1 : existing.CertificateGeneration + 1;
        node.EnrolledAt = now;

        // A re-enrollment clears a previous revocation — an admin minting a fresh token for this
        // machine is exactly the act of re-admitting it.
        node.RevokedAt = null;
        node.RevokedReason = null;
        node.RevokedByUserId = null;

        // The old session cannot be resumed against a new credential.
        node.ResumeToken = null;
        node.LastReceivedSequence = 0;
        node.LastSentSequence = 0;

        ApplyInventory(node, request.Inventory, request.Capabilities);

        if (existing is null) db.Nodes.Add(node);

        token.UsedAt = now;
        token.UsedByNodeId = node.NodeId;

        await db.SaveChangesAsync(ct);

        await audit.LogAsync("node.enrolled", "node", node.NodeId, sourceIp,
            workspaceId: null,
            metadataJson: JsonSerializer.Serialize(new
            {
                node.Name, node.Architecture, node.AgentVersion,
                reEnrollment = existing is not null,
                tokenPrefix = token.Prefix,
            }, NodeContract.Json), ct: ct);

        log.LogInformation(
            "Node {NodeId} ({Name}) enrolled from {Ip}; certificate valid until {NotAfter:u}.",
            node.NodeId, node.Name, sourceIp ?? "unknown", signed.NotAfter);

        return NodeEnrollmentResult<EnrollmentResponse>.Ok(new EnrollmentResponse
        {
            NodeId = node.NodeId,
            CertificatePem = signed.CertificatePem,
            CaCertificatePem = signed.CaCertificatePem,
            CertificateNotAfter = signed.NotAfter,
            ControlPlaneUrl = _options.PublicUrl,
            TunnelGatewayUrl = _options.TunnelGatewayUrl,
            ProtocolVersion = NodeContract.ProtocolVersion,
            GrantedScopes = scopes,
            MinimumAgentVersion = _options.MinimumAgentVersion,
            HeartbeatIntervalSeconds = _options.HeartbeatIntervalSeconds,
        });
    }

    // --- renewal ---

    /// <summary>
    /// Rotate a node's certificate, authenticated by the one being replaced.
    ///
    /// <para>
    /// The presented certificate must both chain to the CA and be the one on record for that node.
    /// The chain alone is not enough: any node's certificate chains to the CA, so accepting it would
    /// let one node renew another's credential.
    /// </para>
    /// </summary>
    public async Task<NodeEnrollmentResult<CredentialRenewalResponse>> RenewAsync(
        X509Certificate2 presented, CredentialRenewalRequest request, string? sourceIp, CancellationToken ct)
    {
        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == request.NodeId, ct);

        if (node is null)
            return NodeEnrollmentResult<CredentialRenewalResponse>.Fail(
                NodeErrorCode.CredentialRevoked, "No such node. Re-enroll it with a fresh token.");

        if (node.IsRevoked)
        {
            await audit.LogAsync("node.renew_refused", "node", node.NodeId, sourceIp,
                metadataJson: JsonSerializer.Serialize(new { reason = "revoked" }),
                workspaceId: null, ct: ct);

            return NodeEnrollmentResult<CredentialRenewalResponse>.Fail(
                NodeErrorCode.CredentialRevoked,
                $"This node was revoked{(node.RevokedReason is null ? "" : $": {node.RevokedReason}")}. Re-enroll it with a fresh token.");
        }

        if (!string.Equals(presented.Thumbprint, node.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            return NodeEnrollmentResult<CredentialRenewalResponse>.Fail(
                NodeErrorCode.Unauthorized,
                "The presented certificate is not the one on record for this node.");

        if (!await ca.ValidatesAsync(presented, ct))
            return NodeEnrollmentResult<CredentialRenewalResponse>.Fail(
                NodeErrorCode.Unauthorized, "The presented certificate does not chain to this panel's node CA.");

        SignedNodeCertificate signed;
        try
        {
            signed = await ca.SignAsync(request.CertificateSigningRequestPem, node.NodeId, node.Name, ct);
        }
        catch (NodeCertificateException e)
        {
            return NodeEnrollmentResult<CredentialRenewalResponse>.Fail(NodeErrorCode.ValidationFailed, e.Message);
        }

        node.CertificateThumbprint = signed.Thumbprint;
        node.CertificateSerial = signed.SerialNumber;
        node.CertificateNotAfter = signed.NotAfter;
        node.CertificateGeneration++;
        node.AgentVersion = request.AgentVersion;

        await db.SaveChangesAsync(ct);

        await audit.LogAsync("node.credential_renewed", "node", node.NodeId, sourceIp,
            metadataJson: JsonSerializer.Serialize(new { generation = node.CertificateGeneration, signed.NotAfter }, NodeContract.Json),
            workspaceId: null, ct: ct);

        log.LogInformation(
            "Renewed the credential for node {NodeId} (generation {Generation}), valid until {NotAfter:u}.",
            node.NodeId, node.CertificateGeneration, signed.NotAfter);

        return NodeEnrollmentResult<CredentialRenewalResponse>.Ok(new CredentialRenewalResponse
        {
            CertificatePem = signed.CertificatePem,
            CaCertificatePem = signed.CaCertificatePem,
            CertificateNotAfter = signed.NotAfter,
            GrantedScopes = Deserialize<List<string>>(node.GrantedScopesJson),
        });
    }

    // --- revocation ---

    /// <summary>
    /// Withdraw a node's credential. It cannot renew afterwards and its next connection is refused.
    ///
    /// <para>
    /// No CRL is published, deliberately. A revocation list is a thing the node has to fetch and
    /// might not; the node row is consulted on every connection and every renewal, so revocation
    /// takes effect at the next contact rather than at the next refresh interval.
    /// </para>
    /// </summary>
    public async Task<bool> RevokeAsync(string nodeId, string? reason, Guid? userId, string? sourceIp, CancellationToken ct)
    {
        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (node is null || node.IsRevoked) return false;

        node.RevokedAt = clock.GetUtcNow();
        node.RevokedReason = reason;
        node.RevokedByUserId = userId;
        node.Status = NodeStatus.Revoked;
        node.ResumeToken = null;

        await db.SaveChangesAsync(ct);

        await audit.LogAsync("node.revoked", "node", nodeId, sourceIp,
            metadataJson: JsonSerializer.Serialize(new { reason }), workspaceId: null, ct: ct);

        log.LogWarning("Node {NodeId} revoked{Reason}.", nodeId, reason is null ? "" : $": {reason}");
        return true;
    }

    // --- helpers ---

    /// <summary>
    /// Look up a token by its prefix, then compare the hash in constant time.
    ///
    /// <para>
    /// The prefix is indexed and not secret; the comparison that decides the answer does not leak
    /// how many leading characters matched.
    /// </para>
    /// </summary>
    private async Task<NodeEnrollmentToken?> FindTokenAsync(string presented, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presented) || presented.Length < 20) return null;

        var prefix = presented[..20];
        var candidate = await db.NodeEnrollmentTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Prefix == prefix, ct);

        if (candidate is null) return null;

        var expected = Encoding.UTF8.GetBytes(candidate.TokenHash);
        var actual = Encoding.UTF8.GetBytes(Hash(presented));

        return CryptographicOperations.FixedTimeEquals(expected, actual) ? candidate : null;
    }

    private async Task<NodeEnrollmentResult<EnrollmentResponse>> RefusedAsync(
        NodeEnrollmentToken token, NodeErrorCode code, string message, string? sourceIp, CancellationToken ct)
    {
        await audit.LogAsync("node.enroll_failed", "node-token", token.Prefix, sourceIp,
            metadataJson: JsonSerializer.Serialize(new { code = code.ToString(), message }),
            workspaceId: null, ct: ct);

        log.LogWarning("Enrollment refused for token {Prefix}…: {Code} — {Message}", token.Prefix, code, message);

        return NodeEnrollmentResult<EnrollmentResponse>.Fail(code, message);
    }

    internal static void ApplyInventory(Node node, NodeInventory inventory, NodeCapabilities capabilities)
    {
        node.OsName = inventory.OsName;
        node.OsVersion = inventory.OsVersion;
        node.KernelVersion = inventory.KernelVersion;
        node.Architecture = inventory.Architecture;
        node.ContainerRuntime = inventory.ContainerRuntime;
        node.ContainerRuntimeVersion = inventory.ContainerRuntimeVersion;
        node.CpuCores = inventory.CpuCores;
        node.TotalMemoryBytes = inventory.TotalMemoryBytes;
        node.TotalDiskBytes = inventory.TotalDiskBytes;
        node.FreeDiskBytes = inventory.FreeDiskBytes;
        node.IpAddressesJson = JsonSerializer.Serialize(inventory.IpAddresses, NodeContract.Json);
        node.InventoryJson = JsonSerializer.Serialize(inventory, NodeContract.Json);
        node.CapabilitiesJson = JsonSerializer.Serialize(capabilities, NodeContract.Json);

        if (inventory.Region is { Length: > 0 }) node.Region = inventory.Region;
        if (inventory.Environment is { Length: > 0 }) node.Environment = inventory.Environment;
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Node ids are opaque to everything except equality, so a random one is the honest shape.</summary>
    private static string NewNodeId() => "nd_" + RandomNumberGenerator.GetHexString(20, lowercase: true);

    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, NodeContract.Json); }
        catch (JsonException) { return null; }
    }
}
