using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Nodes;
using Harbora.Infrastructure.Nodes;
using Harbora.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The node CA, and what it deliberately refuses to take from a certificate signing request.
/// </summary>
public sealed class NodeCertificateAuthorityTests : IDisposable
{
    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("node-ca-" + Guid.NewGuid()).Options);

    private NodeCertificateAuthority Ca() =>
        new(_db, new PassthroughProtector(), NullLogger<NodeCertificateAuthority>.Instance);

    private static (string Csr, ECDsa Key) NewCsr(Action<CertificateRequest>? decorate = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName("whatever-the-node-asked-for");

        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        decorate?.Invoke(request);

        return (request.CreateSigningRequestPem(), key);
    }

    [Fact]
    public async Task The_ca_is_created_once_and_reused()
    {
        using var first = await Ca().GetOrCreateAsync(default);
        using var second = await Ca().GetOrCreateAsync(default);

        first.Thumbprint.Should().Be(second.Thumbprint);
        (await _db.Settings.CountAsync(s => s.Key.StartsWith("nodeagent.ca."))).Should().Be(2);
    }

    [Fact]
    public async Task The_ca_private_key_is_stored_as_a_secret()
    {
        await Ca().GetOrCreateAsync(default);

        var key = await _db.Settings.SingleAsync(s => s.Key == "nodeagent.ca.key");
        key.IsSecret.Should().BeTrue("the settings screen must never render it");
    }

    [Fact]
    public async Task A_csr_asking_to_be_a_certificate_authority_is_signed_as_a_leaf_anyway()
    {
        // The whole reason the signer rebuilds the request from the public key: copying extensions
        // through would hand a node an authority certificate for the entire fleet.
        var (csr, key) = NewCsr(request =>
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(certificateAuthority: true, true, 5, critical: true)));

        using var _ = key;

        var signed = await Ca().SignAsync(csr, "nd_test", "web-01", default);
        using var issued = X509Certificate2.CreateFromPem(signed.CertificatePem);

        var constraints = issued.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        constraints.CertificateAuthority.Should().BeFalse();
    }

    [Fact]
    public async Task A_csr_asking_for_certificate_signing_does_not_get_it()
    {
        var (csr, key) = NewCsr(request =>
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, critical: true)));

        using var _ = key;

        var signed = await Ca().SignAsync(csr, "nd_test", "web-01", default);
        using var issued = X509Certificate2.CreateFromPem(signed.CertificatePem);

        var usage = issued.Extensions.OfType<X509KeyUsageExtension>().Single();
        usage.KeyUsages.Should().NotHaveFlag(X509KeyUsageFlags.KeyCertSign);
    }

    [Fact]
    public async Task The_subject_is_the_node_id_the_panel_assigned_not_the_one_requested()
    {
        var (csr, key) = NewCsr();
        using var _ = key;

        var signed = await Ca().SignAsync(csr, "nd_assigned_by_panel", "web-01", default);
        using var issued = X509Certificate2.CreateFromPem(signed.CertificatePem);

        issued.Subject.Should().Contain("nd_assigned_by_panel");
        issued.Subject.Should().NotContain("whatever-the-node-asked-for");
    }

    [Fact]
    public async Task A_node_certificate_is_client_auth_only()
    {
        // A node certificate that could also serve TLS would be a credential for standing up
        // something that impersonates the control plane to other nodes.
        var (csr, key) = NewCsr();
        using var _ = key;

        var signed = await Ca().SignAsync(csr, "nd_test", "web-01", default);
        using var issued = X509Certificate2.CreateFromPem(signed.CertificatePem);

        var usages = issued.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single()
            .EnhancedKeyUsages.OfType<Oid>().Select(o => o.Value).ToList();

        usages.Should().ContainSingle().Which.Should().Be("1.3.6.1.5.5.7.3.2");
    }

    [Fact]
    public async Task The_gateway_certificate_is_server_auth_only()
    {
        using var gateway = await Ca().IssueGatewayCertificateAsync("gw.harbora.test", default);

        var usages = gateway.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single()
            .EnhancedKeyUsages.OfType<Oid>().Select(o => o.Value).ToList();

        usages.Should().ContainSingle().Which.Should().Be("1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public async Task A_certificate_this_ca_issued_validates_and_a_stranger_does_not()
    {
        var ca = Ca();

        var (csr, key) = NewCsr();
        using var _ = key;
        var signed = await ca.SignAsync(csr, "nd_test", "web-01", default);

        using var mine = X509Certificate2.CreateFromPem(signed.CertificatePem);
        (await ca.ValidatesAsync(mine, default)).Should().BeTrue();

        using var strangerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var strangerRequest = new CertificateRequest("CN=not-ours", strangerKey, HashAlgorithmName.SHA256);
        using var stranger = strangerRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        (await ca.ValidatesAsync(stranger, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_signing_request_that_is_not_one_is_refused_clearly()
    {
        var ca = Ca();

        var act = async () => await ca.SignAsync("-----BEGIN CERTIFICATE REQUEST-----\nnope\n-----END CERTIFICATE REQUEST-----",
            "nd_test", "web-01", default);

        (await act.Should().ThrowAsync<NodeCertificateException>())
            .Which.Message.Should().Contain("could not be read");
    }

    public void Dispose() => _db.Dispose();

    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
        public byte[] DeriveKey(string purpose) => new byte[32];
    }
}

/// <summary>The gateway's allowlist rule, which decides who reaches a customer's database.</summary>
public class NodeTunnelGatewayTests
{
    [Theory]
    [InlineData("203.0.113.44", "203.0.113.44", true)]
    [InlineData("203.0.113.44", "203.0.113.45", false)]
    [InlineData("203.0.113.44/32", "203.0.113.44", true)]
    [InlineData("203.0.113.44/32", "203.0.113.45", false)]
    [InlineData("203.0.113.0/24", "203.0.113.200", true)]
    [InlineData("203.0.113.0/24", "203.0.114.1", false)]
    [InlineData("10.0.0.0/8", "10.55.12.9", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("192.168.1.128/25", "192.168.1.200", true)]
    [InlineData("192.168.1.128/25", "192.168.1.127", false)]
    public void An_ipv4_allowlist_entry_matches_exactly_what_it_says(string entry, string client, bool expected) =>
        NodeTunnelGateway.AddressMatches(entry, IPAddress.Parse(client)).Should().Be(expected);

    [Theory]
    [InlineData("2001:db8::1", "2001:db8::1", true)]
    [InlineData("2001:db8::/32", "2001:db8:1234::9", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    public void An_ipv6_allowlist_entry_works_the_same_way(string entry, string client, bool expected) =>
        NodeTunnelGateway.AddressMatches(entry, IPAddress.Parse(client)).Should().Be(expected);

    [Fact]
    public void Address_families_do_not_match_each_other()
    {
        NodeTunnelGateway.AddressMatches("10.0.0.0/8", IPAddress.Parse("2001:db8::1")).Should().BeFalse();
        NodeTunnelGateway.AddressMatches("2001:db8::/32", IPAddress.Parse("10.0.0.1")).Should().BeFalse();
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("203.0.113.0/abc")]
    [InlineData("203.0.113.0/33")]
    [InlineData("203.0.113.0/24/8")]
    [InlineData("")]
    public void A_malformed_entry_matches_nothing(string entry) =>
        NodeTunnelGateway.AddressMatches(entry, IPAddress.Parse("203.0.113.44")).Should().BeFalse();

    [Fact]
    public void A_zero_prefix_still_matches_everything_which_is_why_the_node_refuses_to_create_one()
    {
        // The gateway's matcher is honest about what /0 means; refusing it is the grant validator's
        // job, and it does — see the node agent's DatabaseAccessManager.
        NodeTunnelGateway.AddressMatches("0.0.0.0/0", IPAddress.Parse("198.51.100.7")).Should().BeTrue();
    }
}

/// <summary>Nodes that stop heartbeating must stop being scheduling targets.</summary>
public sealed class NodeHeartbeatMonitorTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly NodeChannelRegistry _registry = new(NullLogger<NodeChannelRegistry>.Instance);
    private readonly string _database = "node-hb-" + Guid.NewGuid();

    public NodeHeartbeatMonitorTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_database));
        _services = services.BuildServiceProvider();
    }

    private HarboraDbContext Db() => _services.GetRequiredService<IServiceScopeFactory>()
        .CreateScope().ServiceProvider.GetRequiredService<HarboraDbContext>();

    private NodeHeartbeatMonitor Monitor() => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        _registry,
        Options.Create(new NodeAgentControlPlaneOptions { HeartbeatIntervalSeconds = 30 }),
        _clock,
        NullLogger<NodeHeartbeatMonitor>.Instance);

    private async Task SeedAsync(string nodeId, DateTimeOffset lastHeartbeat, NodeStatus status = NodeStatus.Online)
    {
        var db = Db();
        db.Nodes.Add(new Node
        {
            NodeId = nodeId,
            Name = nodeId,
            Status = status,
            Health = "healthy",
            LastHeartbeatAt = lastHeartbeat,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_node_that_stopped_answering_is_marked_offline()
    {
        await SeedAsync("nd_gone", _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await Monitor().SweepAsync(default)).Should().Be(1);

        var node = await Db().Nodes.SingleAsync();
        node.Status.Should().Be(NodeStatus.Offline);
        node.Health.Should().Be("unknown");
    }

    [Fact]
    public async Task One_missed_heartbeat_is_not_enough()
    {
        // A single missed beat is a network hiccup; marking a node offline for one would flap the
        // whole fleet every time the panel's own network blinked.
        await SeedAsync("nd_slow", _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(45));

        (await Monitor().SweepAsync(default)).Should().Be(0);
        (await Db().Nodes.SingleAsync()).Status.Should().Be(NodeStatus.Online);
    }

    [Fact]
    public async Task A_node_that_never_heartbeat_at_all_is_stale()
    {
        var db = Db();
        db.Nodes.Add(new Node { NodeId = "nd_new", Name = "nd_new", Status = NodeStatus.Online });
        await db.SaveChangesAsync();

        (await Monitor().SweepAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task A_node_already_offline_is_left_alone()
    {
        await SeedAsync("nd_known", _clock.GetUtcNow() - TimeSpan.FromDays(1), NodeStatus.Offline);

        (await Monitor().SweepAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task A_draining_node_that_goes_quiet_still_goes_offline()
    {
        await SeedAsync("nd_draining", _clock.GetUtcNow(), NodeStatus.Draining);
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await Monitor().SweepAsync(default)).Should().Be(1);
    }

    public void Dispose() => _services.Dispose();

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

/// <summary>
/// Reading a client certificate that Traefik forwarded, and refusing to read one when nobody said
/// Traefik was in front.
/// </summary>
public class NodeClientCertificateTests
{
    private static X509Certificate2 SampleCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=nd_test", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static NodeClientCertificateResolver Resolver(bool trustForwarded) =>
        new(Options.Create(new NodeAgentControlPlaneOptions { TrustForwardedClientCertificate = trustForwarded }),
            NullLogger<NodeClientCertificateResolver>.Instance);

    [Fact]
    public void A_forwarded_certificate_is_ignored_unless_the_operator_opted_in()
    {
        // A header anyone can set is not authentication.
        using var certificate = SampleCertificate();

        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers[NodeClientCertificateResolver.ForwardedHeader] = certificate.ExportCertificatePem();

        Resolver(trustForwarded: false).Resolve(context).Should().BeNull();
    }

    [Fact]
    public void A_forwarded_certificate_is_read_when_it_was()
    {
        using var certificate = SampleCertificate();

        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers[NodeClientCertificateResolver.ForwardedHeader] = certificate.ExportCertificatePem();

        using var resolved = Resolver(trustForwarded: true).Resolve(context);

        resolved.Should().NotBeNull();
        resolved!.Thumbprint.Should().Be(certificate.Thumbprint);
    }

    [Fact]
    public void A_missing_header_resolves_to_nothing()
    {
        Resolver(trustForwarded: true).Resolve(new Microsoft.AspNetCore.Http.DefaultHttpContext())
            .Should().BeNull();
    }

    [Fact]
    public void An_unreadable_header_resolves_to_nothing_rather_than_throwing()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers[NodeClientCertificateResolver.ForwardedHeader] = "not a certificate";

        Resolver(trustForwarded: true).Resolve(context).Should().BeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Every_shape_traefik_has_ever_sent_is_accepted(bool urlEncoded, bool bareBase64)
    {
        // Traefik has used URL-encoded PEM, plain PEM and bare base64 DER across versions. Pinning
        // one would turn a Traefik upgrade into a fleet-wide authentication outage.
        using var certificate = SampleCertificate();

        var value = bareBase64
            ? Convert.ToBase64String(certificate.RawData)
            : certificate.ExportCertificatePem();

        if (urlEncoded) value = Uri.EscapeDataString(value);

        using var parsed = NodeClientCertificateResolver.Parse(value);
        parsed.Thumbprint.Should().Be(certificate.Thumbprint);
    }
}

/// <summary>Configuration that must fail loudly rather than half-work.</summary>
public class NodeControlPlaneOptionsTests
{
    [Fact]
    public void A_sound_configuration_has_no_problems() =>
        new NodeAgentControlPlaneOptions { PublicUrl = "https://panel.example.com" }
            .Validate().Should().BeEmpty();

    [Fact]
    public void A_public_url_that_is_not_a_url_is_refused() =>
        new NodeAgentControlPlaneOptions { PublicUrl = "panel.example.com" }
            .Validate().Should().ContainMatch("*not an absolute URL*");

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(601)]
    public void An_absurd_heartbeat_interval_is_refused(int seconds) =>
        new NodeAgentControlPlaneOptions { HeartbeatIntervalSeconds = seconds }
            .Validate().Should().ContainMatch("*HeartbeatIntervalSeconds*");

    [Fact]
    public void An_enrollment_token_that_lives_for_a_week_is_refused()
    {
        // A token that lives that long lives in a wiki.
        new NodeAgentControlPlaneOptions { EnrollmentTokenMinutes = 60 * 24 * 7 }
            .Validate().Should().ContainMatch("*EnrollmentTokenMinutes*");
    }

    [Fact]
    public void An_inverted_gateway_port_range_is_refused() =>
        new NodeAgentControlPlaneOptions { GatewayPublicPortStart = 42000, GatewayPublicPortEnd = 41000 }
            .Validate().Should().ContainMatch("*below GatewayPublicPortEnd*");

    [Fact]
    public void Forwarded_client_certificates_are_off_by_default() =>
        new NodeAgentControlPlaneOptions().TrustForwardedClientCertificate.Should().BeFalse();

    [Fact]
    public void The_gateway_is_off_by_default() =>
        new NodeAgentControlPlaneOptions().GatewayListenPort.Should().Be(0);
}
