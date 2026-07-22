using FluentAssertions;
using Harbora.Infrastructure.Security;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Fail-closed master-key policy (ADR-009 / doc 10 §2.2). These tests lock in that Production can
/// never boot with the insecure default and that a configured key is always honored.
/// </summary>
public class MasterKeyResolverTests
{
    [Fact]
    public void Production_without_key_throws()
    {
        var act = () => MasterKeyResolver.Resolve(configuredKey: null, isProduction: true);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*HARBORA_MASTER_KEY*");
    }

    [Fact]
    public void Production_with_blank_key_throws()
    {
        var act = () => MasterKeyResolver.Resolve("   ", isProduction: true);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("dev-insecure-master-key-change-me")]
    [InlineData("dev-insecure-master-key-change-me-in-production")]
    public void Production_with_known_insecure_default_throws(string insecure)
    {
        // Defense in depth: even if an old appsettings default is inherited, Production must refuse.
        var act = () => MasterKeyResolver.Resolve(insecure, isProduction: true);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Production_with_key_is_honored_without_fallback()
    {
        var result = MasterKeyResolver.Resolve("a-real-key", isProduction: true);
        result.Key.Should().Be("a-real-key");
        result.UsedDevFallback.Should().BeFalse();
    }

    [Fact]
    public void Development_without_key_uses_dev_fallback()
    {
        var result = MasterKeyResolver.Resolve(configuredKey: null, isProduction: false);
        result.UsedDevFallback.Should().BeTrue();
        result.Key.Should().Be(MasterKeyResolver.DevFallbackKey);
    }

    [Fact]
    public void Development_with_key_prefers_the_configured_key()
    {
        var result = MasterKeyResolver.Resolve("my-dev-key", isProduction: false);
        result.Key.Should().Be("my-dev-key");
        result.UsedDevFallback.Should().BeFalse();
    }
}
