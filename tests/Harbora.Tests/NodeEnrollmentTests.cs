using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Nodes;
using Harbora.Infrastructure.Nodes;
using Harbora.NodeAgent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The control plane's half of node enrollment: minting a token, spending it exactly once, signing
/// a CSR without trusting anything in it, and rotating or withdrawing the result.
/// </summary>
public sealed class NodeEnrollmentTests : IDisposable
{
    private readonly HarboraDbContext _db;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingAudit _audit = new();
    private readonly NodeCertificateAuthority _ca;

    public NodeEnrollmentTests()
    {
        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("nodes-" + Guid.NewGuid()).Options);

        _ca = new NodeCertificateAuthority(_db, new PassthroughProtector(), NullLogger<NodeCertificateAuthority>.Instance);
    }

    private NodeEnrollmentService Service(Action<NodeAgentControlPlaneOptions>? configure = null)
    {
        var options = new NodeAgentControlPlaneOptions { PublicUrl = "https://panel.test" };
        configure?.Invoke(options);

        return new NodeEnrollmentService(
            _db, _ca, _audit, Options.Create(options), _clock, NullLogger<NodeEnrollmentService>.Instance);
    }

    /// <summary>A node-side keypair and CSR, the way the agent produces one.</summary>
    private static (string Csr, ECDsa Key) NewCsr(string commonName = "web-01")
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(commonName);

        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        return (request.CreateSigningRequestPem(), key);
    }

    private static EnrollmentRequest Request(string csr, string? fingerprint = "fp-1", string architecture = "amd64") => new()
    {
        EnrollmentToken = "ignored — the service is given the token separately",
        NodeName = "web-01",
        CertificateSigningRequestPem = csr,
        AgentVersion = "0.2.0",
        SupportedProtocolVersions = [NodeContract.ProtocolVersion],
        MachineFingerprint = fingerprint,
        Inventory = new NodeInventory
        {
            NodeName = "web-01",
            Hostname = "web-01",
            OsName = "Debian GNU/Linux",
            OsVersion = "12",
            KernelVersion = "6.1.0",
            Architecture = architecture,
            ContainerRuntime = "docker",
            ContainerRuntimeVersion = "27.3.1",
            CpuCores = 4,
            TotalMemoryBytes = 8L * 1024 * 1024 * 1024,
        },
        Capabilities = new NodeCapabilities
        {
            AgentVersion = "0.2.0",
            SupportedProtocolVersions = [NodeContract.ProtocolVersion],
            SupportedCommands = NodeCommandCatalog.All.ToList(),
        },
    };

    private async Task<(NodeEnrollmentService Service, string Token)> EnrolledTokenAsync(
        IReadOnlyList<string>? scopes = null, TimeSpan? lifetime = null)
    {
        var service = Service();
        var token = await service.MintTokenAsync(
            Guid.NewGuid(), "web-01", "eu-central", "production", null, scopes, lifetime, default);

        return (service, token.Token);
    }

    // --- minting ---

    [Fact]
    public async Task A_minted_token_stores_only_a_hash()
    {
        var (_, token) = await EnrolledTokenAsync();

        var stored = await _db.NodeEnrollmentTokens.SingleAsync();

        stored.TokenHash.Should().NotBe(token, "a database dump must not be a list of working credentials");
        stored.TokenHash.Should().HaveLength(64);
        token.Should().StartWith(stored.Prefix);
    }

    [Fact]
    public async Task An_unknown_scope_is_refused_at_mint_time()
    {
        var service = Service();

        var act = async () => await service.MintTokenAsync(
            Guid.NewGuid(), null, null, null, null, ["workloads:everything"], null, default);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // --- enrollment ---

    [Fact]
    public async Task A_valid_token_produces_a_node_and_a_certificate()
    {
        var (service, token) = await EnrolledTokenAsync();
        var (csr, key) = NewCsr();
        using var _ = key;

        var outcome = await service.EnrollAsync(token, Request(csr), "203.0.113.10", default);

        outcome.Success.Should().BeTrue();
        outcome.Value!.NodeId.Should().StartWith("nd_");
        outcome.Value.CertificatePem.Should().Contain("BEGIN CERTIFICATE");
        outcome.Value.CaCertificatePem.Should().Contain("BEGIN CERTIFICATE");
        outcome.Value.ControlPlaneUrl.Should().Be("https://panel.test");
        outcome.Value.GrantedScopes.Should().BeEquivalentTo(NodeScopes.Default);

        var node = await _db.Nodes.SingleAsync();
        node.Status.Should().Be(NodeStatus.Pending);
        node.Architecture.Should().Be("amd64");
        node.CertificateThumbprint.Should().NotBeNullOrEmpty();
        node.Region.Should().Be("eu-central");
    }

    [Fact]
    public async Task The_issued_certificate_matches_the_key_the_node_kept()
    {
        var (service, token) = await EnrolledTokenAsync();
        var (csr, key) = NewCsr();
        using var _ = key;

        var outcome = await service.EnrollAsync(token, Request(csr), null, default);

        using var issued = X509Certificate2.CreateFromPem(outcome.Value!.CertificatePem);
        using var publicKey = issued.GetECDsaPublicKey()!;

        publicKey.ExportSubjectPublicKeyInfo()
            .Should().Equal(key.ExportSubjectPublicKeyInfo(),
                "the panel signs the node's key; it never sees or generates the private half");
    }

    [Fact]
    public async Task A_token_can_be_spent_exactly_once()
    {
        var (service, token) = await EnrolledTokenAsync();

        var (firstCsr, firstKey) = NewCsr();
        using var _ = firstKey;
        (await service.EnrollAsync(token, Request(firstCsr), null, default)).Success.Should().BeTrue();

        var (secondCsr, secondKey) = NewCsr();
        using var __ = secondKey;
        var second = await service.EnrollAsync(token, Request(secondCsr, fingerprint: "fp-2"), null, default);

        second.Success.Should().BeFalse();
        second.Error.Should().Be(NodeErrorCode.EnrollmentTokenAlreadyUsed);
        (await _db.Nodes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (service, token) = await EnrolledTokenAsync(lifetime: TimeSpan.FromMinutes(5));
        _clock.Advance(TimeSpan.FromMinutes(6));

        var (csr, key) = NewCsr();
        using var _ = key;

        var outcome = await service.EnrollAsync(token, Request(csr), null, default);

        outcome.Error.Should().Be(NodeErrorCode.EnrollmentTokenExpired);
        (await _db.Nodes.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_token_is_refused_without_saying_why()
    {
        var service = Service();

        var (csr, key) = NewCsr();
        using var _ = key;

        var outcome = await service.EnrollAsync("hbr_node_" + new string('0', 48), Request(csr), null, default);

        outcome.Error.Should().Be(NodeErrorCode.EnrollmentTokenInvalid);
        // Deliberately the same answer as a wrong token: distinguishing them turns this into a
        // prefix-enumeration endpoint.
        outcome.Message.Should().Be("That enrollment token is not valid.");
    }

    [Fact]
    public async Task An_unsupported_architecture_is_refused_and_the_token_survives()
    {
        var (service, token) = await EnrolledTokenAsync();

        var (csr, key) = NewCsr();
        using var _ = key;

        var outcome = await service.EnrollAsync(token, Request(csr, architecture: "riscv64"), null, default);

        outcome.Error.Should().Be(NodeErrorCode.UnsupportedArchitecture);
        (await _db.NodeEnrollmentTokens.SingleAsync()).UsedAt
            .Should().BeNull("a refused enrollment must not burn the operator's token");
    }

    [Fact]
    public async Task A_protocol_version_this_panel_does_not_speak_is_refused()
    {
        var (service, token) = await EnrolledTokenAsync();

        var (csr, key) = NewCsr();
        using var _ = key;

        var request = Request(csr) with { SupportedProtocolVersions = [99] };
        var outcome = await service.EnrollAsync(token, request, null, default);

        outcome.Error.Should().Be(NodeErrorCode.UnsupportedProtocolVersion);
        outcome.Message.Should().Contain("Update one of them");
    }

    [Fact]
    public async Task Re_enrolling_the_same_machine_reuses_its_node_id()
    {
        // Two node rows for one machine would compete for the same containers while the panel
        // showed both as healthy.
        var (service, firstToken) = await EnrolledTokenAsync();

        var (firstCsr, firstKey) = NewCsr();
        using var _ = firstKey;
        var first = await service.EnrollAsync(firstToken, Request(firstCsr), null, default);

        var secondToken = await service.MintTokenAsync(Guid.NewGuid(), "web-01", null, null, null, null, null, default);
        var (secondCsr, secondKey) = NewCsr();
        using var __ = secondKey;
        var second = await service.EnrollAsync(secondToken.Token, Request(secondCsr), null, default);

        second.Value!.NodeId.Should().Be(first.Value!.NodeId);
        (await _db.Nodes.CountAsync()).Should().Be(1);
        (await _db.Nodes.SingleAsync()).CertificateGeneration.Should().Be(2);
    }

    [Fact]
    public async Task A_machine_with_no_fingerprint_becomes_a_new_node()
    {
        var (service, firstToken) = await EnrolledTokenAsync();

        var (firstCsr, firstKey) = NewCsr();
        using var _ = firstKey;
        await service.EnrollAsync(firstToken, Request(firstCsr, fingerprint: null), null, default);

        var secondToken = await service.MintTokenAsync(Guid.NewGuid(), null, null, null, null, null, null, default);
        var (secondCsr, secondKey) = NewCsr();
        using var __ = secondKey;
        await service.EnrollAsync(secondToken.Token, Request(secondCsr, fingerprint: null), null, default);

        (await _db.Nodes.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Scopes_come_from_the_token_the_admin_minted()
    {
        // The admin who creates the token decides what the node may be asked to do; deciding it at
        // enrollment would let a machine's arrival change the answer.
        var (service, token) = await EnrolledTokenAsync([NodeScopes.WorkloadsRead]);

        var (csr, key) = NewCsr();
        using var _ = key;

        var outcome = await service.EnrollAsync(token, Request(csr), null, default);

        outcome.Value!.GrantedScopes.Should().BeEquivalentTo([NodeScopes.WorkloadsRead]);
    }

    [Fact]
    public async Task Enrollment_is_audited_either_way()
    {
        var (service, token) = await EnrolledTokenAsync();

        var (csr, key) = NewCsr();
        using var _ = key;
        await service.EnrollAsync(token, Request(csr), "203.0.113.10", default);
        await service.EnrollAsync(token, Request(csr, fingerprint: "fp-2"), "203.0.113.10", default);

        _audit.Actions.Should().Contain("node.enrollment_token_created");
        _audit.Actions.Should().Contain("node.enrolled");
        _audit.Actions.Should().Contain("node.enroll_failed");
    }

    // --- renewal ---

    [Fact]
    public async Task Renewal_rotates_the_certificate_and_bumps_the_generation()
    {
        var (service, token) = await EnrolledTokenAsync();

        var (csr, key) = NewCsr();
        using var _ = key;
        var enrolled = await service.EnrollAsync(token, Request(csr), null, default);

        using var current = X509Certificate2.CreateFromPem(enrolled.Value!.CertificatePem);

        var (renewalCsr, renewalKey) = NewCsr();
        using var __ = renewalKey;

        var renewed = await service.RenewAsync(current, new CredentialRenewalRequest
        {
            NodeId = enrolled.Value.NodeId,
            CertificateSigningRequestPem = renewalCsr,
            AgentVersion = "0.2.1",
        }, null, default);

        renewed.Success.Should().BeTrue();

        using var issued = X509Certificate2.CreateFromPem(renewed.Value!.CertificatePem);
        issued.Thumbprint.Should().NotBe(current.Thumbprint);

        var node = await _db.Nodes.SingleAsync();
        node.CertificateThumbprint.Should().Be(issued.Thumbprint);
        node.CertificateGeneration.Should().Be(2);
        node.AgentVersion.Should().Be("0.2.1");
    }

    [Fact]
    public async Task One_node_cannot_renew_another_nodes_credential()
    {
        // Every node's certificate chains to the CA, so the chain alone cannot be the check.
        var (service, firstToken) = await EnrolledTokenAsync();

        var (csrA, keyA) = NewCsr();
        using var _ = keyA;
        var nodeA = await service.EnrollAsync(firstToken, Request(csrA, fingerprint: "fp-a"), null, default);

        var secondToken = await service.MintTokenAsync(Guid.NewGuid(), null, null, null, null, null, null, default);
        var (csrB, keyB) = NewCsr();
        using var __ = keyB;
        var nodeB = await service.EnrollAsync(secondToken.Token, Request(csrB, fingerprint: "fp-b"), null, default);

        using var certificateA = X509Certificate2.CreateFromPem(nodeA.Value!.CertificatePem);

        var (attempt, attemptKey) = NewCsr();
        using var ___ = attemptKey;

        var outcome = await service.RenewAsync(certificateA, new CredentialRenewalRequest
        {
            NodeId = nodeB.Value!.NodeId,
            CertificateSigningRequestPem = attempt,
            AgentVersion = "0.2.0",
        }, null, default);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Be(NodeErrorCode.Unauthorized);
    }

    [Fact]
    public async Task A_superseded_certificate_cannot_renew()
    {
        var (service, token) = await EnrolledTokenAsync();

        var (csr, key) = NewCsr();
        using var _ = key;
        var enrolled = await service.EnrollAsync(token, Request(csr), null, default);
        using var original = X509Certificate2.CreateFromPem(enrolled.Value!.CertificatePem);

        var (firstRenewal, firstRenewalKey) = NewCsr();
        using var __ = firstRenewalKey;
        await service.RenewAsync(original, new CredentialRenewalRequest
        {
            NodeId = enrolled.Value.NodeId,
            CertificateSigningRequestPem = firstRenewal,
            AgentVersion = "0.2.0",
        }, null, default);

        var (secondRenewal, secondRenewalKey) = NewCsr();
        using var ___ = secondRenewalKey;

        var replay = await service.RenewAsync(original, new CredentialRenewalRequest
        {
            NodeId = enrolled.Value.NodeId,
            CertificateSigningRequestPem = secondRenewal,
            AgentVersion = "0.2.0",
        }, null, default);

        replay.Error.Should().Be(NodeErrorCode.Unauthorized,
            "the old certificate is no longer the one on record");
    }

    [Fact]
    public async Task A_revoked_node_cannot_renew()
    {
        var (service, token) = await EnrolledTokenAsync();

        var (csr, key) = NewCsr();
        using var _ = key;
        var enrolled = await service.EnrollAsync(token, Request(csr), null, default);
        using var certificate = X509Certificate2.CreateFromPem(enrolled.Value!.CertificatePem);

        (await service.RevokeAsync(enrolled.Value.NodeId, "decommissioned", Guid.NewGuid(), null, default))
            .Should().BeTrue();

        var (renewalCsr, renewalKey) = NewCsr();
        using var __ = renewalKey;

        var outcome = await service.RenewAsync(certificate, new CredentialRenewalRequest
        {
            NodeId = enrolled.Value.NodeId,
            CertificateSigningRequestPem = renewalCsr,
            AgentVersion = "0.2.0",
        }, null, default);

        outcome.Error.Should().Be(NodeErrorCode.CredentialRevoked);
        outcome.Message.Should().Contain("decommissioned");
    }

    [Fact]
    public async Task Re_enrolling_a_revoked_node_readmits_it()
    {
        // An admin minting a fresh token for this machine is exactly the act of re-admitting it.
        var (service, token) = await EnrolledTokenAsync();

        var (csr, key) = NewCsr();
        using var _ = key;
        var enrolled = await service.EnrollAsync(token, Request(csr), null, default);
        await service.RevokeAsync(enrolled.Value!.NodeId, "mistake", null, null, default);

        var fresh = await service.MintTokenAsync(Guid.NewGuid(), null, null, null, null, null, null, default);
        var (newCsr, newKey) = NewCsr();
        using var __ = newKey;

        var again = await service.EnrollAsync(fresh.Token, Request(newCsr), null, default);

        again.Success.Should().BeTrue();
        (await _db.Nodes.SingleAsync()).IsRevoked.Should().BeFalse();
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Encryption is exercised by the platform's own tests; here it would only make the CA's
    /// storage harder to read when one of these fails.
    /// </summary>
    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
        public byte[] DeriveKey(string purpose) => new byte[32];
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<string> Actions { get; } = [];

        public Task LogAsync(
            string action, string? targetType = null, string? targetId = null, string? ipAddress = null,
            string? actorEmailOverride = null, Guid? userIdOverride = null, string? metadataJson = null,
            CancellationToken ct = default)
        {
            lock (Actions) Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
