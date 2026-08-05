using FluentAssertions;
using Harbora.Infrastructure.Storage;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Where a volume may be mounted inside a container.
///
/// Volumes could only arrive from a template, so nobody had ever typed one of these. Now they can,
/// and the dangerous answers look ordinary: an empty volume over /etc replaces the image's
/// configuration with nothing and the container stops resolving DNS, over / it does not start at
/// all, and over /proc the runtime refuses in a way that reads as a platform fault rather than as
/// a choice somebody made.
/// </summary>
public class MountPathTests
{
    [Theory]
    [InlineData("/data")]
    [InlineData("/var/lib/myapp")]
    [InlineData("/app/uploads")]
    [InlineData("/srv")]
    public void An_ordinary_mount_is_accepted(string path)
    {
        MountPath.Check(path).Should().Be(MountPathRefusal.None);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("./data")]
    public void A_relative_path_is_refused(string path)
    {
        // Resolved against whatever the image's workdir happens to be, which is not something the
        // person choosing the path can see.
        MountPath.Check(path).Should().Be(MountPathRefusal.NotAbsolute);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("/proc")]
    [InlineData("/usr")]
    public void A_directory_the_container_needs_is_refused(string path)
    {
        MountPath.Check(path).Should().Be(MountPathRefusal.Reserved);
    }

    [Fact]
    public void A_subdirectory_of_a_reserved_one_is_refused_too()
    {
        // Mounting over /usr/bin breaks the image just as thoroughly as mounting over /usr, and an
        // exact-match check would let it through.
        MountPath.Check("/usr/bin").Should().Be(MountPathRefusal.Reserved);
        MountPath.Check("/etc/nginx").Should().Be(MountPathRefusal.Reserved);
    }

    [Fact]
    public void A_path_that_merely_starts_with_the_same_letters_is_not_reserved()
    {
        // "/etcetera" is not "/etc". A prefix check without the separator refuses ordinary paths.
        MountPath.Check("/etcetera").Should().Be(MountPathRefusal.None);
        MountPath.Check("/developer").Should().Be(MountPathRefusal.None);
    }

    [Theory]
    [InlineData("/data/../etc")]
    [InlineData("/data/./x")]
    [InlineData("/data\\x")]
    public void Anything_that_walks_or_is_not_a_separator_is_refused(string path)
    {
        MountPath.Check(path).Should().Be(MountPathRefusal.Unsafe);
    }

    [Fact]
    public void Walking_back_into_a_reserved_directory_is_refused()
    {
        // The check is on the normalised form, so a path cannot be dressed up to walk past the list.
        MountPath.Check("/data/../../etc").Should().NotBe(MountPathRefusal.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_is_refused_as_nothing_typed(string? path)
    {
        MountPath.Check(path).Should().Be(MountPathRefusal.Missing);
    }

    [Fact]
    public void An_overlong_path_is_refused()
    {
        MountPath.Check("/" + new string('a', MountPath.MaxLength)).Should().Be(MountPathRefusal.TooLong);
    }

    [Fact]
    public void A_trailing_separator_does_not_make_a_second_mount()
    {
        // "/data" and "/data/" are one place. Stored differently they collide at deploy time, where
        // the message is about a duplicate bind rather than about what somebody typed.
        MountPath.Normalise("/data/").Should().Be("/data");
        MountPath.Normalise("  /data  ").Should().Be("/data");
    }

    [Fact]
    public void The_root_survives_normalising()
    {
        MountPath.Normalise("/").Should().Be("/");
    }

    // --- the volume behind the mount ---

    [Fact]
    public void The_volume_is_named_after_the_application_and_the_path()
    {
        MountPath.VolumeNameFor("shop", "/var/lib/uploads")
            .Should().Be("harbora-vol-shop-var-lib-uploads");
    }

    [Fact]
    public void Two_applications_mounting_the_same_path_get_different_volumes()
    {
        // Sharing one would hand an application another's files, silently, on the next deploy.
        MountPath.VolumeNameFor("shop", "/data")
            .Should().NotBe(MountPath.VolumeNameFor("blog", "/data"));
    }

    [Fact]
    public void One_path_written_two_ways_is_one_volume()
    {
        MountPath.VolumeNameFor("shop", "/data/")
            .Should().Be(MountPath.VolumeNameFor("shop", "/data"));
    }

    [Fact]
    public void A_volume_name_never_runs_separators_together()
    {
        // Docker accepts it, but "harbora-vol-shop--x" and "harbora-vol-shop-x" reading as two
        // different volumes for one intent is exactly the confusion this naming exists to avoid.
        MountPath.VolumeNameFor("shop", "/a//b").Should().Be("harbora-vol-shop-a-b");
    }
}
