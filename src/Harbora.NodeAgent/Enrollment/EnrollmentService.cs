using System.Security.Cryptography;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Enrollment;

/// <summary>
/// Owns the node's credential lifecycle: first enrollment, renewal, and the decision that a
/// credential is beyond saving and the node must be re-enrolled by an admin.
/// </summary>
public sealed class EnrollmentService(
    IOptions<NodeAgentOptions> options,
    IEnrollmentClient client,
    NodeIdentityStore identities,
    JsonFileStore<NodeState> stateStore,
    InventoryCollector inventory,
    IHostFacts host,
    TimeProvider clock,
    ILogger<EnrollmentService> log)
{
    private readonly NodeAgentOptions _options = options.Value;

    /// <summary>
    /// Return the current identity, enrolling first if there is none.
    ///
    /// <para>
    /// A node with a certificate never re-enrolls on its own, even when it also has a token lying
    /// around. Re-enrollment mints a second identity for the same machine, and a machine with two
    /// identities is one the control plane will schedule onto twice.
    /// </para>
    /// </summary>
    public async Task<EnrollmentOutcome<NodeIdentity>> EnsureEnrolledAsync(CancellationToken ct)
    {
        if (identities.Load() is { } existing)
        {
            WarnIfKeyIsReadable();
            return EnrollmentOutcome<NodeIdentity>.Ok(existing);
        }

        var token = ReadEnrollmentToken();
        if (string.IsNullOrWhiteSpace(token))
            return EnrollmentOutcome<NodeIdentity>.Fail(
                NodeErrorCode.EnrollmentTokenInvalid,
                "This node is not enrolled and no enrollment token is configured. Create a token in the panel and re-run the installer.");

        if (!inventory.ArchitectureIsSupported())
            return EnrollmentOutcome<NodeIdentity>.Fail(
                NodeErrorCode.UnsupportedArchitecture,
                $"Architecture '{host.Architecture}' is not supported. Harbora nodes run on amd64 or arm64.");

        log.LogInformation("Enrolling node {NodeName} with {ControlPlaneUrl}.", _options.NodeName, _options.ControlPlaneUrl);

        var csr = identities.CreateSigningRequest(_options.NodeName, newKey: true);

        var request = new EnrollmentRequest
        {
            EnrollmentToken = token,
            NodeName = _options.NodeName,
            CertificateSigningRequestPem = csr,
            AgentVersion = AgentVersion.Current,
            SupportedProtocolVersions = NodeContract.SupportedProtocolVersions,
            Region = _options.Region,
            Environment = _options.Environment,
            Labels = _options.Labels,
            Inventory = await inventory.CollectAsync(ct),
            Capabilities = inventory.Capabilities(),
            MachineFingerprint = host.MachineFingerprint(),
        };

        var outcome = await client.EnrollAsync(_options.ControlPlaneUrl, token, request, ct);
        if (!outcome.Success)
        {
            log.LogError("Enrollment refused: {Code} — {Message}", outcome.Error!.Code, outcome.Error.Message);
            return EnrollmentOutcome<NodeIdentity>.Fail(outcome.Error.Code, outcome.Error.Message, outcome.Error.Retryable);
        }

        var response = outcome.Value!;

        if (!NodeContract.SupportedProtocolVersions.Contains(response.ProtocolVersion))
            return EnrollmentOutcome<NodeIdentity>.Fail(
                NodeErrorCode.UnsupportedProtocolVersion,
                $"The control plane speaks protocol v{response.ProtocolVersion}; this agent speaks {string.Join(", ", NodeContract.SupportedProtocolVersions)}. Update the agent.");

        identities.StoreCertificate(response.CertificatePem, response.CaCertificatePem);

        stateStore.Update(current => (current ?? new NodeState()) with
        {
            NodeId = response.NodeId,
            NegotiatedProtocolVersion = response.ProtocolVersion,
            GrantedScopes = response.GrantedScopes ?? NodeScopes.Default,
            ControlPlaneUrl = response.ControlPlaneUrl,
            TunnelGatewayUrl = response.TunnelGatewayUrl,
            MinimumAgentVersion = response.MinimumAgentVersion,
            HeartbeatIntervalSeconds = response.HeartbeatIntervalSeconds,
            EnrolledAt = clock.GetUtcNow(),
            // A fresh identity invalidates any session the previous one held.
            ResumeToken = null,
            LastReceivedSequence = 0,
            LastSentSequence = 0,
        });

        ShredEnrollmentToken();

        var identity = identities.Load();
        if (identity is null)
            return EnrollmentOutcome<NodeIdentity>.Fail(
                NodeErrorCode.Internal, "The signed certificate was stored but could not be read back.");

        log.LogInformation(
            "Enrolled as node {NodeId}. Credential valid until {NotAfter:u}.",
            response.NodeId, identity.NotAfter);

        return EnrollmentOutcome<NodeIdentity>.Ok(identity);
    }

    /// <summary>
    /// True when enough of the certificate's life has been spent to start renewing. Deliberately
    /// well before expiry: renewal that begins at the last moment has one attempt, and the first
    /// attempt is the one most likely to hit the outage that caused the delay.
    /// </summary>
    public bool NeedsRenewal(NodeIdentity identity) =>
        identity.LifetimeElapsed(clock.GetUtcNow()) >= _options.CertificateRenewalThreshold;

    /// <summary>Rotate the certificate, keeping the existing key.</summary>
    public async Task<EnrollmentOutcome<NodeIdentity>> RenewAsync(NodeIdentity current, CancellationToken ct)
    {
        var state = stateStore.Load();
        if (state?.NodeId is not { Length: > 0 } nodeId)
            return EnrollmentOutcome<NodeIdentity>.Fail(
                NodeErrorCode.CredentialRevoked, "No node id on record; the agent must be re-enrolled.");

        log.LogInformation("Renewing node credential; current one expires {NotAfter:u}.", current.NotAfter);

        var csr = identities.CreateSigningRequest(_options.NodeName, newKey: false);

        var outcome = await client.RenewAsync(
            state.ControlPlaneUrl ?? _options.ControlPlaneUrl,
            current,
            new CredentialRenewalRequest
            {
                NodeId = nodeId,
                CertificateSigningRequestPem = csr,
                AgentVersion = AgentVersion.Current,
            },
            ct);

        if (!outcome.Success)
        {
            var error = outcome.Error!;
            log.LogWarning("Credential renewal failed: {Code} — {Message}", error.Code, error.Message);
            return EnrollmentOutcome<NodeIdentity>.Fail(error.Code, error.Message, error.Retryable);
        }

        var response = outcome.Value!;
        identities.StoreCertificate(response.CertificatePem, response.CaCertificatePem);

        if (response.GrantedScopes is { Count: > 0 } scopes)
            stateStore.Update(s => (s ?? new NodeState()) with { GrantedScopes = scopes });

        var renewed = identities.Load();
        if (renewed is null)
            return EnrollmentOutcome<NodeIdentity>.Fail(
                NodeErrorCode.Internal, "The renewed certificate was stored but could not be read back.");

        log.LogInformation("Credential renewed; valid until {NotAfter:u}.", renewed.NotAfter);
        return EnrollmentOutcome<NodeIdentity>.Ok(renewed);
    }

    /// <summary>
    /// Whether a renewal failure is terminal. A revoked node must stop trying and say so loudly:
    /// an agent that keeps retrying a revoked credential is an agent whose operator believes it is
    /// still connected.
    /// </summary>
    public static bool IsTerminal(NodeErrorCode code) =>
        code is NodeErrorCode.CredentialRevoked
             or NodeErrorCode.EnrollmentTokenAlreadyUsed
             or NodeErrorCode.EnrollmentTokenExpired
             or NodeErrorCode.EnrollmentTokenInvalid
             or NodeErrorCode.UnsupportedProtocolVersion
             or NodeErrorCode.UnsupportedArchitecture;

    private string? ReadEnrollmentToken()
    {
        if (!string.IsNullOrWhiteSpace(_options.EnrollmentToken)) return _options.EnrollmentToken.Trim();

        if (_options.EnrollmentTokenFile is { Length: > 0 } path && File.Exists(path))
        {
            var token = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        return null;
    }

    /// <summary>
    /// Destroy the token file once the token has been spent. It is single-use at the control plane
    /// too, so leaving it costs nothing in theory — but a credential-shaped file on disk is one
    /// more thing to explain to whoever finds it, and one more thing to leak in a support bundle.
    /// </summary>
    private void ShredEnrollmentToken()
    {
        if (_options.EnrollmentTokenFile is not { Length: > 0 } path || !File.Exists(path)) return;

        try
        {
            var length = new FileInfo(path).Length;
            if (length > 0)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                var noise = new byte[length];
                RandomNumberGenerator.Fill(noise);
                stream.Write(noise);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(path);
            log.LogInformation("Enrollment token consumed and removed from {Path}.", path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Could not remove the spent enrollment token at {Path}. Delete it manually.", path);
        }
    }

    private void WarnIfKeyIsReadable()
    {
        if (identities.KeyIsProtected()) return;

        log.LogWarning(
            "The node private key at {Path} is readable beyond its owner. Run: chmod 600 {Path}",
            identities.KeyPath, identities.KeyPath);
    }
}
