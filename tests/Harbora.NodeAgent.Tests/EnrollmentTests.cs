using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Enrollment;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tests.Fakes;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Section 14: enrollment success and failure, an expired token, and certificate rotation.
/// </summary>
public sealed class EnrollmentTests : IDisposable
{
    private readonly TempAgent _agent;
    private readonly TestCertificateAuthority _ca = new();
    private readonly FakeEnrollmentClient _client;
    private readonly NodeIdentityStore _identities;
    private readonly JsonFileStore<NodeState> _state;
    private readonly FakeHostFacts _host = new();
    private readonly FakeContainerRuntime _runtime = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly string _tokenPath;

    public EnrollmentTests()
    {
        _agent = new TempAgent();
        _tokenPath = Path.Combine(_agent.Root, "enrollment.token");
        File.WriteAllText(_tokenPath, "enr_short_lived_token");
        _agent.Options.EnrollmentTokenFile = _tokenPath;

        _client = new FakeEnrollmentClient(_ca) { NotBefore = _clock.GetUtcNow() };
        _identities = new NodeIdentityStore(_agent.Options.IdentityDirectory);
        _state = TestFactories.Store<NodeState>(_agent, "node.json");
    }

    private EnrollmentService Service() => new(
        _agent.Wrapped, _client, _identities, _state,
        new InventoryCollector(_agent.Wrapped, _host, _runtime, TestFactories.Log<InventoryCollector>()),
        _host, _clock, TestFactories.Log<EnrollmentService>());

    [Fact]
    public async Task Successful_enrollment_stores_an_identity_and_the_node_id()
    {
        var outcome = await Service().EnsureEnrolledAsync(CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Value!.Certificate.Subject.Should().Contain("test-node");

        var state = _state.Load()!;
        state.NodeId.Should().Be("node-test-1");
        state.IsEnrolled.Should().BeTrue();
        state.GrantedScopes.Should().BeEquivalentTo(NodeScopes.Default);
        state.EnrolledAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task Enrollment_sends_a_csr_and_never_the_private_key()
    {
        await Service().EnsureEnrolledAsync(CancellationToken.None);

        var request = _client.EnrollRequests.Single();

        request.CertificateSigningRequestPem.Should().Contain("BEGIN CERTIFICATE REQUEST");
        request.CertificateSigningRequestPem.Should().NotContain("PRIVATE KEY",
            "the key must never leave the node — a control plane that holds it can impersonate the node");

        // And the key really is on disk locally.
        File.Exists(_identities.KeyPath).Should().BeTrue();
        File.ReadAllText(_identities.KeyPath).Should().Contain("PRIVATE KEY");
    }

    [Fact]
    public async Task Enrollment_reports_inventory_and_capabilities()
    {
        await Service().EnsureEnrolledAsync(CancellationToken.None);

        var request = _client.EnrollRequests.Single();

        request.Inventory.Architecture.Should().Be("amd64");
        request.Inventory.KernelVersion.Should().Be("6.1.0-test");
        request.Inventory.ContainerRuntimeVersion.Should().Be(_runtime.Version);
        request.Capabilities.SupportedCommands.Should().BeEquivalentTo(NodeCommandCatalog.All);
        request.Capabilities.PrivilegedModeEnabled.Should().BeFalse();
        request.MachineFingerprint.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Spent_enrollment_token_file_is_shredded()
    {
        await Service().EnsureEnrolledAsync(CancellationToken.None);

        File.Exists(_tokenPath).Should().BeFalse(
            "a spent token left on disk is a credential-shaped file that looks valid to whoever finds it");
    }

    [Fact]
    public async Task An_already_enrolled_node_does_not_enroll_again()
    {
        var service = Service();
        await service.EnsureEnrolledAsync(CancellationToken.None);

        // A leftover token must not tempt it into minting a second identity for the same machine.
        File.WriteAllText(_tokenPath, "enr_another_token");

        var second = await service.EnsureEnrolledAsync(CancellationToken.None);

        second.Success.Should().BeTrue();
        _client.EnrollRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Expired_token_is_a_terminal_failure()
    {
        _client.EnrollFailure = NodeError.From(NodeErrorCode.EnrollmentTokenExpired, "token expired at 11:00");

        var outcome = await Service().EnsureEnrolledAsync(CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error!.Code.Should().Be(NodeErrorCode.EnrollmentTokenExpired);
        EnrollmentService.IsTerminal(outcome.Error.Code).Should().BeTrue("retrying an expired token can never work");

        _identities.HasIdentity.Should().BeFalse();
        File.Exists(_tokenPath).Should().BeTrue("a token that was never spent must not be destroyed");
    }

    [Fact]
    public async Task Already_used_token_is_terminal()
    {
        _client.EnrollFailure = NodeError.From(NodeErrorCode.EnrollmentTokenAlreadyUsed, "spent");

        var outcome = await Service().EnsureEnrolledAsync(CancellationToken.None);

        EnrollmentService.IsTerminal(outcome.Error!.Code).Should().BeTrue();
    }

    [Fact]
    public async Task A_transport_failure_is_retryable_rather_than_terminal()
    {
        _client.EnrollFailure = NodeError.From(NodeErrorCode.Internal, "connection refused", retryable: true);

        var outcome = await Service().EnsureEnrolledAsync(CancellationToken.None);

        outcome.Success.Should().BeFalse();
        EnrollmentService.IsTerminal(outcome.Error!.Code).Should().BeFalse();
        outcome.Error.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task Missing_token_fails_with_an_actionable_message()
    {
        File.Delete(_tokenPath);

        var outcome = await Service().EnsureEnrolledAsync(CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error!.Code.Should().Be(NodeErrorCode.EnrollmentTokenInvalid);
        outcome.Error.Message.Should().Contain("installer");
    }

    [Fact]
    public async Task An_unsupported_architecture_refuses_to_enroll()
    {
        _host.Architecture = "riscv64";

        var outcome = await Service().EnsureEnrolledAsync(CancellationToken.None);

        outcome.Error!.Code.Should().Be(NodeErrorCode.UnsupportedArchitecture);
        _client.EnrollRequests.Should().BeEmpty("a node that cannot run workloads must not occupy a slot in the panel");
    }

    [Fact]
    public async Task A_control_plane_on_an_unknown_protocol_version_is_refused()
    {
        _client.ProtocolVersion = 99;

        var outcome = await Service().EnsureEnrolledAsync(CancellationToken.None);

        outcome.Error!.Code.Should().Be(NodeErrorCode.UnsupportedProtocolVersion);
        outcome.Error.Message.Should().Contain("Update the agent");
    }

    [Fact]
    public async Task Renewal_is_due_only_after_two_thirds_of_the_certificate_lifetime()
    {
        _client.CertificateLifetime = TimeSpan.FromDays(90);
        var service = Service();
        var identity = (await service.EnsureEnrolledAsync(CancellationToken.None)).Value!;

        service.NeedsRenewal(identity).Should().BeFalse();

        _clock.Advance(TimeSpan.FromDays(59));
        service.NeedsRenewal(identity).Should().BeFalse();

        _clock.Advance(TimeSpan.FromDays(2));
        service.NeedsRenewal(identity).Should().BeTrue();
    }

    [Fact]
    public async Task Rotation_issues_a_new_certificate_and_keeps_the_same_key()
    {
        var service = Service();
        var original = (await service.EnsureEnrolledAsync(CancellationToken.None)).Value!;
        var keyBefore = File.ReadAllText(_identities.KeyPath);

        _clock.Advance(TimeSpan.FromDays(61));
        _client.NotBefore = _clock.GetUtcNow();

        var renewed = await service.RenewAsync(original, CancellationToken.None);

        renewed.Success.Should().BeTrue();
        renewed.Value!.NotAfter.Should().BeAfter(original.NotAfter);
        renewed.Value.Certificate.Thumbprint.Should().NotBe(original.Certificate.Thumbprint);

        File.ReadAllText(_identities.KeyPath).Should().Be(keyBefore,
            "rotating the certificate does not need a new key, and rotating the key adds a window where it can be half-written");

        _client.RenewRequests.Single().NodeId.Should().Be("node-test-1");
    }

    [Fact]
    public async Task Rotation_updates_the_granted_scopes_when_the_control_plane_narrows_them()
    {
        var service = Service();
        var identity = (await service.EnsureEnrolledAsync(CancellationToken.None)).Value!;

        _client.GrantedScopes = [NodeScopes.WorkloadsRead];
        await service.RenewAsync(identity, CancellationToken.None);

        _state.Load()!.GrantedScopes.Should().BeEquivalentTo([NodeScopes.WorkloadsRead]);
    }

    [Fact]
    public async Task A_revoked_node_gets_a_terminal_renewal_failure()
    {
        var service = Service();
        var identity = (await service.EnsureEnrolledAsync(CancellationToken.None)).Value!;

        _client.RenewFailure = NodeError.From(NodeErrorCode.CredentialRevoked, "revoked by admin");

        var outcome = await service.RenewAsync(identity, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error!.Code.Should().Be(NodeErrorCode.CredentialRevoked);
        EnrollmentService.IsTerminal(outcome.Error.Code).Should().BeTrue();
    }

    [Fact]
    public async Task A_transient_renewal_failure_leaves_the_existing_credential_in_place()
    {
        var service = Service();
        var identity = (await service.EnsureEnrolledAsync(CancellationToken.None)).Value!;
        var thumbprint = identity.Certificate.Thumbprint;

        _client.RenewFailure = NodeError.From(NodeErrorCode.Internal, "panel restarting", retryable: true);
        await service.RenewAsync(identity, CancellationToken.None);

        _identities.Load()!.Certificate.Thumbprint.Should().Be(thumbprint,
            "a failed renewal must not damage a certificate that is still valid");
    }

    [Fact]
    public async Task Restarting_the_agent_recovers_the_identity_and_state_from_disk()
    {
        await Service().EnsureEnrolledAsync(CancellationToken.None);

        // A fresh object graph over the same directory is exactly what a service restart is.
        var afterRestart = new NodeIdentityStore(_agent.Options.IdentityDirectory);
        var stateAfterRestart = TestFactories.Store<NodeState>(_agent, "node.json");

        afterRestart.Load().Should().NotBeNull();
        stateAfterRestart.Load()!.NodeId.Should().Be("node-test-1");
    }

    [Fact]
    public async Task Key_material_is_written_owner_only()
    {
        await Service().EnsureEnrolledAsync(CancellationToken.None);

        _identities.KeyIsProtected().Should().BeTrue(
            "another account on the box must not be able to read the node's identity");
    }

    [Fact]
    public void Erasing_the_identity_removes_every_file()
    {
        _identities.CreateSigningRequest("test-node", newKey: true);
        _identities.StoreCertificate(_ca.CertificatePem, _ca.CertificatePem);

        _identities.Erase();

        _identities.HasIdentity.Should().BeFalse();
        File.Exists(_identities.KeyPath).Should().BeFalse();
        File.Exists(_identities.CertificatePath).Should().BeFalse();
        File.Exists(_identities.CaPath).Should().BeFalse();
    }

    [Fact]
    public void A_node_name_cannot_inject_extra_subject_fields()
    {
        var store = new NodeIdentityStore(Path.Combine(_agent.Root, "hostile"));

        var csr = store.CreateSigningRequest("evil,O=SomeoneElse,CN=admin", newKey: true);

        var request = System.Security.Cryptography.X509Certificates.CertificateRequest
            .LoadSigningRequestPem(csr, System.Security.Cryptography.HashAlgorithmName.SHA256);

        // The whole hostile string must land inside one common-name component. Asserting on the
        // structure rather than on the rendered text is the point: the rendered form quotes the
        // value, so a substring check would pass even if the injection had worked.
        var components = request.SubjectName.EnumerateRelativeDistinguishedNames()
            .Select(rdn => (Oid: rdn.GetSingleElementType().Value, Value: rdn.GetSingleElementValue()))
            .ToList();

        components.Should().HaveCount(3, "exactly the three components the agent builds");
        components.Should().Contain(c => c.Oid == "2.5.4.10" && c.Value == "Harbora");
        components.Should().Contain(c => c.Oid == "2.5.4.3" && c.Value == "evil,O=SomeoneElse,CN=admin");
        components.Should().NotContain(c => c.Oid == "2.5.4.10" && c.Value == "SomeoneElse");
    }

    public void Dispose()
    {
        _ca.Dispose();
        _agent.Dispose();
    }
}
