using FluentAssertions;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Docker says <c>x86_64</c>; image manifests say <c>amd64</c>. Comparing the two directly matches
/// nothing, and a compatibility check that never matches either refuses everything or gets switched
/// off to make the page work again.
/// </summary>
public class HostArchitectureTests
{
    [Theory]
    [InlineData("x86_64", "amd64")]
    [InlineData("X86_64", "amd64")]
    [InlineData("amd64", "amd64")]
    [InlineData("aarch64", "arm64")]
    [InlineData("arm64", "arm64")]
    [InlineData("armv7l", "arm")]
    [InlineData("i686", "386")]
    public void A_kernel_name_becomes_the_name_an_image_manifest_uses(string reported, string expected)
    {
        HostArchitecture.Normalise(reported).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_reported_stays_nothing(string? reported)
    {
        // Not "amd64". A default here is a guess, and the guess refuses versions that would run.
        HostArchitecture.Normalise(reported).Should().BeNull();
    }

    [Fact]
    public void An_unfamiliar_name_is_passed_through_rather_than_dropped()
    {
        // A machine reporting something this list has not met still has an architecture, and a
        // template naming the same string should match it.
        HostArchitecture.Normalise("riscv64").Should().Be("riscv64");
    }

    [Fact]
    public void A_report_that_omits_a_fact_does_not_erase_it()
    {
        // The failure this prevents is intermittent: an agent that omits the field on one tick makes
        // a deployment refusable on that tick and fine on the next, with nothing in between to
        // explain it.
        Harbora.Infrastructure.Monitoring.ReportedFact.Keep("amd64", null).Should().Be("amd64");
        Harbora.Infrastructure.Monitoring.ReportedFact.Keep("amd64", "").Should().Be("amd64");
        Harbora.Infrastructure.Monitoring.ReportedFact.Keep("amd64", "   ").Should().Be("amd64");
    }

    [Fact]
    public void A_report_that_carries_a_fact_replaces_what_was_held()
    {
        // A machine really can change: a node is rebuilt on different hardware and keeps its row.
        Harbora.Infrastructure.Monitoring.ReportedFact.Keep("amd64", "arm64").Should().Be("arm64");
        Harbora.Infrastructure.Monitoring.ReportedFact.Keep(null, "arm64").Should().Be("arm64");
        Harbora.Infrastructure.Monitoring.ReportedFact.Keep(null, null).Should().BeNull();
    }

    [Fact]
    public void A_normalised_host_matches_a_version_that_names_the_same_platform()
    {
        // The join these two pieces exist to make. Tested together because each is convincing alone
        // and useless if the names they produce differ.
        var version = new AppTemplateVersion { SupportedArchitectures = "amd64,arm64" };

        VersionSelection.RunsOn(version, HostArchitecture.Normalise("x86_64")!).Should().BeTrue();
        VersionSelection.RunsOn(version, HostArchitecture.Normalise("aarch64")!).Should().BeTrue();
        VersionSelection.RunsOn(version, HostArchitecture.Normalise("armv7l")!).Should().BeFalse();
    }
}
