using System.Runtime.InteropServices;
using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Knowing when the CLI is out of date, and what would replace it.
///
/// A stale CLI does not announce itself — it fails in ways that look like server bugs. But a version
/// check that cries wolf is worse than none, so "is this newer?" has to be right in both directions,
/// and silent whenever it cannot tell.
/// </summary>
public class SelfUpdateTests
{
    [Theory]
    [InlineData("0.3.0", "0.2.0")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.2.1", "0.2.0")]
    [InlineData("v0.3.0", "0.2.0")]   // release tags carry the v
    public void A_higher_version_is_newer(string candidate, string current)
        => SelfUpdate.IsNewer(candidate, current).Should().BeTrue();

    [Theory]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.1.0", "0.2.0")]
    [InlineData("v0.2.0", "0.2.0")]
    public void The_same_or_older_version_is_not_newer(string candidate, string current)
        => SelfUpdate.IsNewer(candidate, current).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nightly")]
    [InlineData("2026-07-30")]
    public void An_unreadable_version_never_triggers_a_warning(string? candidate)
    {
        // Telling someone to update on the strength of a string we did not understand is worse than
        // staying quiet — and an older panel may not report a version at all.
        SelfUpdate.IsNewer(candidate, "0.2.0").Should().BeFalse();
    }

    [Fact]
    public void A_build_suffix_does_not_make_a_version_look_older()
    {
        // The CLI stamps InformationalVersion, which carries "+<commit>" on local builds.
        SelfUpdate.IsNewer("0.3.0", "0.2.0+abc123").Should().BeTrue();
        SelfUpdate.IsNewer("0.2.0", "0.2.0+abc123").Should().BeFalse();
    }

    [Fact]
    public void A_prerelease_is_compared_on_its_numbers()
        => SelfUpdate.IsNewer("0.3.0-rc.1", "0.2.0").Should().BeTrue();

    // ---- which file replaces this one ----

    [Theory]
    [InlineData("Windows", Architecture.X64, "harbora-win-x64.exe")]
    [InlineData("Windows", Architecture.Arm64, "harbora-win-arm64.exe")]
    [InlineData("Linux", Architecture.X64, "harbora-linux-x64")]
    [InlineData("Linux", Architecture.Arm64, "harbora-linux-arm64")]
    [InlineData("OSX", Architecture.Arm64, "harbora-osx-arm64")]
    public void The_asset_matches_the_names_the_release_publishes(string os, Architecture arch, string expected)
    {
        // These strings are the contract with .github/workflows/release-cli.yml. Getting one wrong
        // downloads a binary for another architecture, which only reveals itself when it is run —
        // exactly the "exec format error" this project has already hit once on an ARM server.
        var platform = os switch
        {
            "Windows" => OSPlatform.Windows,
            "OSX" => OSPlatform.OSX,
            _ => OSPlatform.Linux
        };

        SelfUpdate.AssetNameFor(platform, arch).Should().Be(expected);
    }

    [Fact]
    public void An_unsupported_architecture_has_no_asset()
    {
        // Better to say there is no build than to hand back a plausible-looking name for a file that
        // does not exist, or worse, one that does and is wrong.
        SelfUpdate.AssetNameFor(OSPlatform.Linux, Architecture.X86).Should().BeNull();
        SelfUpdate.AssetNameFor(OSPlatform.FreeBSD, Architecture.X64).Should().BeNull();
    }

    [Fact]
    public void This_machine_has_an_asset()
        => SelfUpdate.AssetNameForThisMachine().Should().NotBeNull();

    [Fact]
    public void The_retired_binary_sits_beside_the_one_it_replaced()
    {
        // Windows will not overwrite a running executable, so the old one is renamed aside first.
        SelfUpdate.RetiredPathFor("/usr/local/bin/harbora").Should().Be("/usr/local/bin/harbora.old");
    }

    [Fact]
    public void Cleaning_up_a_missing_previous_binary_is_not_an_error()
    {
        // This runs at the start of every command, so it must be silent about a file that is simply
        // not there — which is the normal case.
        var clean = () => SelfUpdate.CleanUpPreviousBinary(
            Path.Combine(Path.GetTempPath(), "harbora-not-here-" + Guid.NewGuid()));

        clean.Should().NotThrow();
    }
}
