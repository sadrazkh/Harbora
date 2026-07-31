using FluentAssertions;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Two questions a managed database could not answer about itself: what version is really running,
/// and how much disk it is using.
/// </summary>
public class DatabaseHardeningTests
{
    // ---- version ----

    [Fact]
    public void A_container_running_a_different_version_than_the_one_chosen_is_drift()
    {
        // The failure worth catching: a container recreated from a moving tag comes back on a newer
        // major, refuses to start on the data directory it already has, and the panel goes on showing
        // the version that was originally picked.
        VersionDrift.HasDrifted("15-alpine", "postgres:16-alpine").Should().BeTrue();
        VersionDrift.HasDrifted("16-alpine", "postgres:16-alpine").Should().BeFalse();
    }

    [Fact]
    public void Not_knowing_what_is_running_is_not_the_same_as_drift()
    {
        // Before a container exists there is nothing to disagree with, and a warning there would
        // train people to ignore the one that matters.
        VersionDrift.HasDrifted("16-alpine", null).Should().BeFalse();
        VersionDrift.HasDrifted("16-alpine", "").Should().BeFalse();
        VersionDrift.HasDrifted(null, "postgres:16-alpine").Should().BeFalse();
    }

    [Theory]
    [InlineData("postgres:16-alpine", "16-alpine")]
    [InlineData("postgres", "latest")]
    [InlineData("registry.example.com:5000/postgres:16", "16")]
    [InlineData("registry.example.com:5000/postgres", "latest")]
    public void The_tag_is_read_without_mistaking_a_registry_port_for_one(string image, string expected)
    {
        // A colon in a registry host is not a tag separator, and reading it as one would report drift
        // on every database in a private-registry install.
        VersionDrift.TagOf(image).Should().Be(expected);
    }

    [Fact]
    public void A_version_that_does_not_stay_put_is_recognised_as_such()
    {
        // Worth saying before the data is written rather than after: this is the setting that lets a
        // database change major version on its own.
        VersionDrift.IsMoving("latest").Should().BeTrue();
        VersionDrift.IsMoving("").Should().BeTrue();
        VersionDrift.IsMoving(null).Should().BeTrue();

        VersionDrift.IsMoving("16-alpine").Should().BeFalse();
    }

    // ---- storage ----

    [Fact]
    public void The_size_is_read_out_of_the_commands_output()
    {
        StorageMeasurement.Parse("48271360").Should().Be(48271360);
    }

    [Fact]
    public void The_size_is_found_among_the_noise_the_pull_writes()
    {
        // The image pull reports on the same stream, so the number is looked for rather than assumed
        // to be the first thing said.
        var output = "Pulling from library/alpine\nDigest: sha256:abc\nStatus: Downloaded\n48271360\n";

        StorageMeasurement.Parse(output).Should().Be(48271360);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("du: /data: Permission denied")]
    public void An_answer_that_cannot_be_trusted_is_no_answer(string? output)
    {
        // Not zero. "0 B" is a plausible-looking figure that would be read as fact, while nothing at
        // all is honest about not knowing.
        StorageMeasurement.Parse(output).Should().BeNull();
    }

    [Fact]
    public void The_size_survives_dockers_stream_framing()
    {
        // Observed on a real server: a container with no TTY has its output framed, so the digits
        // arrive with control bytes stuck to them and the size came back as unknown.
        StorageMeasurement.Parse("\0\0\0\0\0\0\b48271360\n").Should().Be(48271360);
    }

    [Fact]
    public void A_digest_line_is_not_mistaken_for_a_size()
    {
        // "sha256:16…" is full of digits. Only a line that is nothing but digits is a size.
        StorageMeasurement.Parse("Digest: sha256:1234567890abcdef\nStatus: Downloaded\n").Should().BeNull();
    }

    [Fact]
    public void The_measuring_command_asks_for_bytes_and_only_the_total()
    {
        var command = string.Join(" ", StorageMeasurement.Command);

        command.Should().Contain("du -sb");
        command.Should().Contain("/data");
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(512L, "512 B")]
    [InlineData(48271360L, "46 MB")]
    [InlineData(3221225472L, "3 GB")]
    public void A_size_is_shown_in_a_unit_someone_can_read(long? bytes, string expected)
    {
        StorageMeasurement.Describe(bytes).Should().Be(expected);
    }

    [Fact]
    public void An_unmeasured_database_does_not_claim_to_be_empty()
    {
        // The guard on the line above: showing "0 B" for "never measured" is a lie a screen tells
        // without anyone deciding to tell it.
        StorageMeasurement.Describe(null).Should().NotContain("0");
    }
}
