using System.Net;
using FluentAssertions;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The panel runs behind Traefik, so without forwarded-header handling every request reports the
/// proxy's IP — collapsing the per-IP rate limits into one platform-wide bucket and making the
/// audit trail's IP column worthless. Trust must be narrow enough that a client can't forge it.
/// </summary>
public class TrustedProxySetupTests
{
    private static ForwardedHeadersOptions Configured(params string[] cidrs)
    {
        var options = new ForwardedHeadersOptions();
        TrustedProxySetup.Configure(options, cidrs.Length > 0 ? cidrs : TrustedProxySetup.DefaultProxyNetworks);
        return options;
    }

    [Fact]
    public void Configure_unwinds_exactly_one_proxy_hop()
    {
        // With more than one hop a client could prepend its own X-Forwarded-For entry and be believed.
        Configured().ForwardLimit.Should().Be(1);
    }

    [Fact]
    public void Configure_forwards_ip_and_proto()
    {
        var options = Configured();
        options.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedFor);
        options.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedProto);
    }

    [Fact]
    public void Configure_replaces_the_framework_defaults_with_the_configured_networks()
    {
        var options = Configured("10.0.0.0/8");

        options.KnownProxies.Should().BeEmpty("only network ranges are trusted, not individual hosts");
        options.KnownIPNetworks.Should().ContainSingle();
    }

    [Fact]
    public void Configure_accepts_the_docker_defaults()
    {
        // Every shipped default must parse — a typo here would silently drop a trusted range.
        var accepted = TrustedProxySetup.Configure(new ForwardedHeadersOptions(), TrustedProxySetup.DefaultProxyNetworks);
        accepted.Should().HaveCount(TrustedProxySetup.DefaultProxyNetworks.Length);
    }

    [Fact]
    public void Configure_skips_malformed_entries_rather_than_crashing_startup()
    {
        var accepted = TrustedProxySetup.Configure(
            new ForwardedHeadersOptions(), ["10.0.0.0/8", "not-a-cidr", "192.168.0.0"]);

        accepted.Should().ContainSingle();
    }

    /// <summary>Runs the real middleware over a request and returns the resulting client IP.</summary>
    private static async Task<string?> RemoteIpAfterMiddlewareAsync(string peerIp, string forwardedFor)
    {
        var options = new ForwardedHeadersOptions();
        TrustedProxySetup.Configure(options, TrustedProxySetup.DefaultProxyNetworks);

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peerIp);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;

        await middleware.Invoke(context);
        return context.Connection.RemoteIpAddress?.ToString();
    }

    [Fact]
    public async Task Header_from_a_trusted_proxy_becomes_the_client_ip()
    {
        // Traefik on the Docker bridge network appended the real client — believe it.
        var ip = await RemoteIpAfterMiddlewareAsync(peerIp: "172.18.0.2", forwardedFor: "203.0.113.9");
        ip.Should().Be("203.0.113.9");
    }

    [Fact]
    public async Task Header_from_an_untrusted_peer_is_ignored()
    {
        // A direct connection from the public internet claiming to be someone else must not be
        // believed — otherwise every rate limit and audit entry is trivially forgeable.
        var ip = await RemoteIpAfterMiddlewareAsync(peerIp: "203.0.113.50", forwardedFor: "10.1.2.3");
        ip.Should().Be("203.0.113.50");
    }

    [Fact]
    public async Task Only_the_hop_the_proxy_added_is_trusted()
    {
        // The client prepended a forged entry; with ForwardLimit = 1 only the rightmost (the one
        // Traefik appended) is unwound, so the forgery is never adopted.
        var ip = await RemoteIpAfterMiddlewareAsync(
            peerIp: "172.18.0.2", forwardedFor: "1.1.1.1, 203.0.113.9");
        ip.Should().Be("203.0.113.9");
    }

    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("172.16.0.0/12", true)]
    [InlineData("::1/128", true)]
    [InlineData("fc00::/7", true)]
    [InlineData("10.0.0.0", false)]        // no prefix
    [InlineData("10.0.0.0/33", false)]     // prefix out of range for IPv4
    [InlineData("::1/129", false)]         // prefix out of range for IPv6
    [InlineData("10.0.0.0/abc", false)]    // non-numeric prefix
    [InlineData("nonsense/8", false)]
    [InlineData("10.1.2.3/8", true)]       // host bits set — masked to the prefix, same range as 10.0.0.0/8
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseNetwork_accepts_only_well_formed_cidrs(string? cidr, bool expected)
    {
        TrustedProxySetup.TryParseNetwork(cidr, out _).Should().Be(expected);
    }
}
