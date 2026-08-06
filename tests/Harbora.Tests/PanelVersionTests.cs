using FluentAssertions;
using Harbora.Infrastructure.Maintenance;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whether a published release is actually newer than the running build.
///
/// The HTTP call is the boring half; this is the half that goes quietly wrong. String comparison
/// says "0.10.0" is older than "0.9.0"; a release candidate is not something to nudge an operator
/// toward; and a tag that means nothing must never become a banner urging an upgrade.
/// </summary>
public class PanelVersionTests
{
    [Theory]
    [InlineData("0.2.0", "v0.3.0")]
    [InlineData("0.2.0", "0.2.1")]
    [InlineData("0.9.0", "v0.10.0")]   // minor: the one string comparison gets backwards
    [InlineData("9.0.0", "v10.0.0")]   // major: same trap, one field up
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("0.2.0", "v1.0.0")]
    public void A_newer_release_is_newer(string running, string tag)
    {
        PanelVersion.IsNewer(running, tag).Should().BeTrue();
    }

    [Theory]
    [InlineData("0.3.0", "v0.2.0")]
    [InlineData("0.3.0", "v0.3.0")]
    [InlineData("0.10.0", "v0.9.0")]
    [InlineData("1.0.0", "0.9.9")]
    public void The_same_or_older_is_not_an_update(string running, string tag)
    {
        PanelVersion.IsNewer(running, tag).Should().BeFalse();
    }

    [Theory]
    [InlineData("v0.3.0-rc.1")]
    [InlineData("v0.3.0-beta")]
    public void A_pre_release_is_not_offered(string tag)
    {
        // It may well be "newer"; it is not something to nudge somebody toward unprompted.
        PanelVersion.IsNewer("0.2.0", tag).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "v0.3.0")]
    [InlineData("0.2.0", null)]
    [InlineData("0.2.0", "")]
    [InlineData("0.2.0", "latest")]
    [InlineData("0.2.0", "v")]
    [InlineData("0.2.0", "1.2.3.4")]
    [InlineData("not-a-version", "v0.3.0")]
    public void Anything_unreadable_announces_nothing(string? running, string? tag)
    {
        // A banner urging an upgrade to a tag nobody can parse is worse than no banner.
        PanelVersion.IsNewer(running, tag).Should().BeFalse();
    }

    [Fact]
    public void Build_metadata_is_ignored_the_way_semver_says()
    {
        // The running build carries "+sha" from CI; it is not part of precedence.
        PanelVersion.IsNewer("0.2.0+abc123", "v0.3.0").Should().BeTrue();
        PanelVersion.IsNewer("0.3.0+abc123", "v0.3.0+def456").Should().BeFalse();
    }

    [Theory]
    [InlineData("2", 2, 0, 0)]
    [InlineData("2.1", 2, 1, 0)]
    [InlineData("v2.1.3", 2, 1, 3)]
    public void A_short_tag_fills_the_missing_places_with_zero(string tag, int major, int minor, int patch)
    {
        PanelVersion.TryParse(tag, out var v).Should().BeTrue();
        (v.Major, v.Minor, v.Patch).Should().Be((major, minor, patch));
    }
}
