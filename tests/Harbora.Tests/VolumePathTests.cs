using FluentAssertions;
using Harbora.Infrastructure.Storage;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A path inside one volume, and nothing outside it.
///
/// This is the only thing between a text box on a web page and the filesystem of the machine the
/// platform runs on. Everything here is a case where a path that looks harmless reaches somewhere
/// it should not, or a legitimate filename is refused because the check was written against the
/// raw string instead of against what the path means.
/// </summary>
public class VolumePathTests
{
    [Theory]
    [InlineData("data/uploads/logo.png", "data/uploads/logo.png")]
    [InlineData("data//uploads///x", "data/uploads/x")]
    [InlineData("./data/./x", "data/x")]
    [InlineData("data/uploads/", "data/uploads")]
    public void An_ordinary_path_is_normalised(string typed, string expected)
    {
        VolumePath.Normalise(typed).Should().Be(expected);
    }

    [Fact]
    public void The_root_is_a_path()
    {
        // Listing the root is the first thing the browser does. Refusing it would mean it never
        // opens at all. The root is spelled by leaving the path out — an empty string — not by
        // sending a slash; see below for why a leading slash is refused rather than treated as
        // another way of asking for the same thing.
        VolumePath.Normalise("").Should().BeEmpty();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/data/uploads")]
    [InlineData("/etc/passwd")]
    public void A_leading_slash_is_refused_rather_than_silently_stripped(string typed)
    {
        // Stripping it would answer "/etc/passwd" by quietly looking inside the volume's own
        // "etc/passwd" — a different question than the one that was typed, answered as if it were
        // the same one. No caller in this codebase ever sends a leading slash: every "path" this
        // reaches is either built from a value this function already normalised, or left out
        // entirely for the root, so refusing this breaks nothing that works today.
        VolumePath.Normalise(typed).Should().BeNull();
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("data/../../etc/passwd")]
    [InlineData("data/..")]
    [InlineData("/../x")]
    [InlineData("a//../../b")]
    public void Anything_that_climbs_out_is_refused(string typed)
    {
        // Refused rather than resolved. Resolving is how "a/../../b" escapes — it cancels a segment
        // that was never really there — and there is no legitimate use for it from a browser that
        // already shows somebody where they are.
        VolumePath.Normalise(typed).Should().BeNull();
    }

    [Fact]
    public void A_file_whose_name_merely_starts_with_dots_is_allowed()
    {
        // The reason the check is on segments rather than on the raw string. Refusing anything
        // containing ".." would refuse this, and dotfiles are exactly what somebody opens a file
        // browser on a config volume to edit.
        VolumePath.Normalise("..config").Should().Be("..config");
        VolumePath.Normalise("data/..keep").Should().Be("data/..keep");
    }

    [Fact]
    public void A_backslash_is_refused_rather_than_translated()
    {
        // A separator on the machine somebody is typing from, an ordinary filename character on the
        // host this runs against. Translating it would silently turn one intended filename into a
        // different path.
        VolumePath.Normalise("data\\uploads").Should().BeNull();
    }

    [Fact]
    public void A_null_byte_is_refused()
    {
        // It ends the string for the C library underneath, so the path this code checked, logged
        // and showed is not the path the kernel acted on. Deliberately with no ".." in it: the
        // segment check would catch that on its own, and this has to fail on the NUL alone.
        VolumePath.Normalise("data/logo.png\0extra").Should().BeNull();
    }

    [Fact]
    public void A_segment_of_only_whitespace_is_refused()
    {
        // "a/ /b" is not a path anybody meant, and on most filesystems it is a directory that is
        // very hard to see or remove afterwards.
        VolumePath.Normalise("a/ /b").Should().BeNull();
    }

    [Fact]
    public void An_absurdly_long_path_is_refused()
    {
        VolumePath.Normalise(new string('a', VolumePath.MaxLength + 1)).Should().BeNull();
    }

    [Fact]
    public void Nothing_at_all_is_not_the_root()
    {
        // Null input is a caller mistake, not a request for the root.
        VolumePath.Normalise(null).Should().BeNull();
    }

    [Theory]
    [InlineData("/data", "uploads/logo.png", "/data/uploads/logo.png")]
    [InlineData("/data/", "uploads", "/data/uploads")]
    [InlineData("/data", "", "/data")]
    public void The_absolute_path_is_built_under_the_mount(string root, string normalised, string expected)
    {
        VolumePath.Under(root, normalised).Should().Be(expected);
    }

    [Theory]
    [InlineData("data/uploads/logo.png", "logo.png")]
    [InlineData("logo.png", "logo.png")]
    [InlineData("", "")]
    public void The_name_is_the_last_segment(string normalised, string expected)
    {
        VolumePath.NameOf(normalised).Should().Be(expected);
    }

    [Fact]
    public void The_parent_of_the_root_is_nowhere()
    {
        // The "up" link at the root must not point above it, which is the one place this function
        // could produce an escape the normaliser never sees.
        VolumePath.ParentOf("").Should().BeNull();
    }

    [Theory]
    [InlineData("data/uploads/logo.png", "data/uploads")]
    [InlineData("logo.png", "")]
    public void The_parent_is_the_containing_directory(string normalised, string expected)
    {
        VolumePath.ParentOf(normalised).Should().Be(expected);
    }
}
