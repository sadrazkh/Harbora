using FluentAssertions;
using Harbora.Infrastructure.Security;
using Xunit;

namespace Harbora.Tests;

/// <summary>SSRF guard for outbound webhook URLs (doc 10 §2.8).</summary>
public class UrlSafetyTests
{
    [Theory]
    [InlineData("https://hooks.slack.com/services/abc")]
    [InlineData("https://discord.com/api/webhooks/1/xyz")]
    [InlineData("http://example.com/webhook")]
    [InlineData("https://8.8.8.8/notify")]
    public void Allows_public_https_and_http_urls(string url)
        => UrlSafety.IsAllowedOutboundUrl(url, out _).Should().BeTrue();

    [Theory]
    [InlineData("http://localhost:8080/x")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://169.254.169.254/latest/meta-data")]   // cloud metadata
    [InlineData("http://10.0.0.5/x")]
    [InlineData("http://192.168.1.10/x")]
    [InlineData("http://172.16.0.9/x")]
    [InlineData("http://metadata.google.internal/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://service.localhost/x")]
    public void Blocks_internal_and_reserved_targets(string url)
    {
        UrlSafety.IsAllowedOutboundUrl(url, out var reason).Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("ftp://example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Blocks_non_http_schemes_and_garbage(string? url)
        => UrlSafety.IsAllowedOutboundUrl(url, out _).Should().BeFalse();
}
