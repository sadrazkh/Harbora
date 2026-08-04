using FluentAssertions;
using Harbora.Infrastructure.Ai;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Where a forwarded request is allowed to go.
///
/// An administrator types a provider's base URL and Harbora's server then calls it. Unchecked, that
/// is server-side request forgery: the classic exploit points it at a cloud metadata endpoint and
/// reads the platform's own credentials back out of the response body.
/// </summary>
public class AiUpstreamUrlTests
{
    [Fact]
    public void A_normal_provider_url_is_built()
    {
        AiUpstreamUrl.Build("https://openrouter.ai/api/v1", "chat/completions")
            .Should().Be("https://openrouter.ai/api/v1/chat/completions");
    }

    [Fact]
    public void Slashes_do_not_double_up()
    {
        AiUpstreamUrl.Build("https://api.example.com/v1/", "/chat/completions")
            .Should().Be("https://api.example.com/v1/chat/completions");
    }

    [Theory]
    [InlineData("http://api.example.com")]
    [InlineData("ftp://api.example.com")]
    [InlineData("file:///etc/passwd")]
    public void Anything_that_is_not_https_is_refused(string url)
    {
        // A provider token sent over plain HTTP is handed to everything on the path.
        AiUpstreamUrl.Build(url, "chat/completions").Should().BeNull();
    }

    [Theory]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://localhost")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://172.16.4.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://metadata.internal")]
    [InlineData("https://db.local")]
    public void An_address_inside_our_own_infrastructure_is_refused(string url)
    {
        // 169.254.169.254 is the cloud metadata service — the single most valuable SSRF target.
        AiUpstreamUrl.Build(url, "chat/completions").Should().BeNull();
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    public void Private_ipv6_is_refused_too(string host)
    {
        // An allowlist that only knows IPv4 is an allowlist with a documented way around it.
        AiUpstreamUrl.IsForbiddenHost(host).Should().BeTrue();
    }

    [Fact]
    public void A_public_address_is_allowed()
    {
        // The guard on all of the above: a check that refuses everything means no provider works.
        AiUpstreamUrl.IsForbiddenHost("openrouter.ai").Should().BeFalse();
        AiUpstreamUrl.IsForbiddenHost("8.8.8.8").Should().BeFalse();
    }

    [Fact]
    public void Nothing_and_nonsense_are_refused_rather_than_guessed_at()
    {
        AiUpstreamUrl.Build(null, "chat/completions").Should().BeNull();
        AiUpstreamUrl.Build("", "chat/completions").Should().BeNull();
        AiUpstreamUrl.Build("not a url", "chat/completions").Should().BeNull();
        AiUpstreamUrl.IsForbiddenHost("").Should().BeTrue();
    }
}
