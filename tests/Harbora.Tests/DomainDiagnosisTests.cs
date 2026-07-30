using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the panel tells a user about their custom domain.
///
/// Before this, the Domains list showed an "SSL" badge derived from a checkbox — the *intent*, not
/// reality. So a domain whose DNS was never pointed here displayed "SSL" while the browser showed a
/// certificate error. Every assertion below is about saying the true thing, and naming the one action
/// that fixes it.
/// </summary>
public class DomainDiagnosisTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ServerIps = ["91.99.205.231"];

    // `expires` uses a sentinel rather than null-coalescing: "no certificate at all" is a distinct
    // case from "unspecified", and collapsing them made one test silently assert the default.
    private const int NoCertificate = int.MinValue;

    private static DomainProbe Probe(
        string[]? resolved = null, bool https = true, int expiresInDays = 60,
        string? issuer = "Let's Encrypt", string? error = null)
        => new(resolved ?? ServerIps, ServerIps, https, "CN=app.example.com", issuer,
               expiresInDays == NoCertificate ? null : Now.AddDays(expiresInDays), error);

    private static DomainStatus Diagnose(DomainProbe probe) =>
        DomainDiagnosis.Diagnose("app.example.com", probe, Now);

    [Fact]
    public void A_working_domain_is_reported_ready_with_the_days_remaining()
    {
        var status = Diagnose(Probe());

        status.IsReady.Should().BeTrue();
        status.Summary.Should().Contain("60");
        status.Action.Should().BeNull("there is nothing for the user to do");
    }

    [Fact]
    public void A_name_that_does_not_resolve_is_told_to_add_an_A_record_with_the_address()
    {
        var status = Diagnose(Probe(resolved: []));

        status.Readiness.Should().Be(DomainReadiness.DnsMissing);
        status.Action.Should().Contain("A record").And.Contain("91.99.205.231",
            "the user needs the address, not just the word 'DNS'");
    }

    [Fact]
    public void A_domain_pointing_elsewhere_says_where_it_points_and_why_it_matters()
    {
        var status = Diagnose(Probe(resolved: ["203.0.113.10"]));

        status.Readiness.Should().Be(DomainReadiness.DnsNotPointingHere);
        status.Summary.Should().Contain("203.0.113.10");
        status.Action.Should().Contain("no certificate can be issued",
            "this is the part users don't work out for themselves");
    }

    [Fact]
    public void Dns_is_diagnosed_before_the_certificate()
    {
        // With DNS wrong, "waiting for a certificate" is a symptom and following it wastes an
        // afternoon on a certificate that can never be issued.
        var status = Diagnose(Probe(resolved: ["203.0.113.10"], https: false, expiresInDays: NoCertificate));

        status.Readiness.Should().Be(DomainReadiness.DnsNotPointingHere);
    }

    [Fact]
    public void Correct_dns_with_nothing_answering_points_at_ports_and_the_app()
    {
        var status = Diagnose(Probe(https: false));

        status.Readiness.Should().Be(DomainReadiness.Unreachable);
        status.Action.Should().Contain("443");
    }

    [Fact]
    public void Https_without_a_certificate_points_at_the_challenge_port()
    {
        var status = Diagnose(Probe(expiresInDays: NoCertificate));

        status.Readiness.Should().Be(DomainReadiness.AwaitingCertificate);
        status.Action.Should().Contain("port 80", "HTTP-01 is what needs it");
    }

    [Fact]
    public void An_expired_certificate_is_reported_with_its_date()
    {
        var status = Diagnose(Probe(expiresInDays: -3));

        status.Readiness.Should().Be(DomainReadiness.AwaitingCertificate);
        status.Summary.Should().Contain("expired").And.Contain("2026-07-27");
    }

    [Fact]
    public void A_certificate_near_renewal_still_counts_as_ready()
    {
        // It is serving correctly today; flagging it as broken would be crying wolf.
        var status = Diagnose(Probe(expiresInDays: 5));

        status.IsReady.Should().BeTrue();
        status.Summary.Should().Contain("renews soon");
    }

    [Fact]
    public void A_probe_that_failed_says_so_instead_of_guessing()
    {
        var status = Diagnose(Probe(error: "DNS lookup timed out"));

        status.Readiness.Should().Be(DomainReadiness.Unknown);
        status.Action.Should().Contain("timed out");
    }

    [Fact]
    public void Any_overlap_with_the_server_addresses_counts_as_pointing_here()
    {
        // Round-robin DNS and CDNs return several addresses; only one needs to be this server.
        var status = Diagnose(Probe(resolved: ["203.0.113.10", "91.99.205.231"]));

        status.IsReady.Should().BeTrue();
    }

    [Fact]
    public void When_the_servers_own_address_is_unknown_dns_is_not_called_wrong()
    {
        // Better to check the certificate than to accuse correct DNS of being wrong.
        var probe = new DomainProbe(["203.0.113.10"], [], true, "CN=x", "Let's Encrypt", Now.AddDays(30));

        DomainDiagnosis.Diagnose("app.example.com", probe, Now).IsReady.Should().BeTrue();
    }

    [Fact]
    public void The_issuer_is_shown_when_known()
    {
        Diagnose(Probe(issuer: "Let's Encrypt")).Summary.Should().Contain("Let's Encrypt");
    }

    // ---- reading the certificate the handshake presented ----

    [Fact]
    public void Traefiks_default_certificate_is_reported_as_no_certificate_yet()
    {
        // Traefik answers with a self-signed default for any host it has no real certificate for, so
        // the handshake succeeds and reading it naively would claim "valid for 1095 more days" on a
        // domain browsers reject.
        var (_, issuer, expires) = DomainInspector.Interpret(
            "CN=TRAEFIK DEFAULT CERT", "CN=TRAEFIK DEFAULT CERT", Now.AddDays(1095));

        expires.Should().BeNull();
        issuer.Should().BeNull();

        DomainDiagnosis.Diagnose("app.example.com",
            new DomainProbe(ServerIps, ServerIps, true, "CN=TRAEFIK DEFAULT CERT", issuer, expires), Now)
            .Readiness.Should().Be(DomainReadiness.AwaitingCertificate);
    }

    [Fact]
    public void A_real_certificate_keeps_its_issuer_and_expiry()
    {
        var expiry = Now.AddDays(89);

        var (subject, issuer, expires) = DomainInspector.Interpret(
            "CN=app.example.com", "C=US, O=Let's Encrypt, CN=R11", expiry);

        subject.Should().Be("app.example.com");
        issuer.Should().Be("R11");
        expires.Should().Be(expiry);
    }

    // ---- telling our failures apart from the domain's ----

    [Theory]
    [InlineData(typeof(System.Net.Sockets.SocketException))]
    [InlineData(typeof(System.Security.Authentication.AuthenticationException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(OperationCanceledException))]
    public void A_connection_that_did_not_answer_is_a_verdict_about_the_domain(Type failure)
        => ProbeFailures.IsConnectionFailure((Exception)Activator.CreateInstance(failure)!)
            .Should().BeTrue();

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(ArgumentException))]
    public void A_fault_in_our_own_probe_is_not_reported_as_a_broken_domain(Type ours)
    {
        // The bug this exists to prevent: SslStream threw InvalidOperationException on every check
        // because a callback was supplied twice, and the panel reported it as "nothing answered on
        // HTTPS" — sending users to check a firewall that was never the problem.
        ProbeFailures.IsConnectionFailure((Exception)Activator.CreateInstance(ours)!)
            .Should().BeFalse();
    }

    [Fact]
    public void A_probe_that_broke_is_reported_as_unknown_rather_than_unreachable()
    {
        var status = Diagnose(Probe(https: false, error: "The certificate check failed: boom"));

        status.Readiness.Should().Be(DomainReadiness.Unknown, "we cannot judge the domain from a failed check");
        status.Readiness.Should().NotBe(DomainReadiness.Unreachable);
    }

    [Theory]
    [InlineData("CN=app.example.com", "app.example.com")]
    [InlineData("C=US, O=Let's Encrypt, CN=R11", "R11")]
    [InlineData("O=Some Authority", "Some Authority")]  // no CN — fall back to the organisation
    [InlineData("OU=ops", "OU=ops")]                    // nothing usable — show it verbatim
    public void An_x500_name_is_reduced_to_something_readable(string dn, string expected)
        => DomainInspector.ShortName(dn).Should().Be(expected);
}
