using FluentAssertions;
using Harbora.Infrastructure.Storage;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The commands that read and write inside a volume.
///
/// A filename is attacker-controlled input in a multi-tenant platform. The property that keeps that
/// from mattering is that every script is a constant and every path travels as a positional
/// argument — a script assembled by string interpolation is a shell waiting for a filename with a
/// quote in it.
/// </summary>
public class VolumeFileCommandTests
{
    private const string Nasty = "a'; rm -rf /; echo '";

    [Fact]
    public void A_path_never_reaches_the_script_text()
    {
        // The whole safety argument in one assertion: whatever the path is, the script stays the
        // script and the path is a separate argv element the shell only ever sees through "$1".
        var argv = VolumeFileCommands.Read($"/data/{Nasty}");

        argv[2].Should().NotContain(Nasty);
        argv.Should().Contain($"/data/{Nasty}");
    }

    [Fact]
    public void File_contents_never_reach_the_script_text_either()
    {
        var argv = VolumeFileCommands.Write("/data/x", Nasty);

        argv[2].Should().NotContain(Nasty);
        argv.Last().Should().Be(Nasty);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("delete")]
    [InlineData("mkdir")]
    public void Every_command_runs_a_script_with_a_placeholder_argv_zero(string which)
    {
        // `sh -c script name arg1 …` — the element after the script becomes $0, not $1. Getting
        // this wrong shifts every argument by one, so the command silently operates on the wrong
        // path rather than failing.
        IReadOnlyList<string> argv = which switch
        {
            "list" => VolumeFileCommands.Listing("/data/x"),
            "read" => VolumeFileCommands.Read("/data/x"),
            "write" => VolumeFileCommands.Write("/data/x", "aGk="),
            "delete" => VolumeFileCommands.Delete("/data/x"),
            _ => VolumeFileCommands.MakeDirectory("/data/x")
        };

        argv[0].Should().Be("sh");
        argv[1].Should().Be("-c");
        argv[3].Should().Be("sh");
        argv[4].Should().Be("/data/x");
    }

    // --- reading what the listing printed ---

    [Fact]
    public void A_listing_is_read_into_entries()
    {
        var entries = VolumeFileCommands.ParseListing(
            "f|1024|1700000000|logo.png\nd|0|1700000001|uploads\n");

        entries.Should().HaveCount(2);
        entries[0].Name.Should().Be("uploads");
        entries[0].IsDirectory.Should().BeTrue();
        entries[1].Name.Should().Be("logo.png");
        entries[1].SizeBytes.Should().Be(1024);
    }

    [Fact]
    public void Directories_come_first_then_names()
    {
        // Decided here rather than in the view, so the list somebody sees cannot be ordered
        // differently from the links drawn beside it.
        var entries = VolumeFileCommands.ParseListing(
            "f|1|1|beta.txt\nd|0|1|zeta\nf|1|1|Alpha.txt\nd|0|1|admin\n");

        entries.Select(e => e.Name).Should().Equal("admin", "zeta", "Alpha.txt", "beta.txt");
    }

    [Fact]
    public void A_filename_containing_the_separator_survives_whole()
    {
        // "report|final.txt" is a legal filename. Splitting on every separator would truncate it to
        // "report", and the download link beside it would point at a file that does not exist.
        var entries = VolumeFileCommands.ParseListing("f|10|1700000000|report|final.txt\n");

        entries.Should().ContainSingle().Which.Name.Should().Be("report|final.txt");
    }

    [Theory]
    [InlineData("nonsense")]
    // A line cut off part-way — output truncated, or the helper killed mid-write. It has the right
    // shape as far as it goes, so a check on anything looser than "exactly four fields" walks off
    // the end of the array and throws instead of skipping it.
    [InlineData("f|10|1700000000")]
    [InlineData("f|10")]
    [InlineData("f|notanumber|1|x")]
    [InlineData("x|1|1|weird-type")]
    [InlineData("f|1|1|")]
    [InlineData("f|-5|1|negative")]
    public void A_line_that_makes_no_sense_is_skipped_rather_than_guessed_at(string line)
    {
        // The alternative is an entry with an invented name or size appearing in a file list, which
        // somebody then clicks.
        VolumeFileCommands.ParseListing(line).Should().BeEmpty();
    }

    [Fact]
    public void An_unreadable_timestamp_is_unknown_rather_than_1970()
    {
        // FromUnixTimeSeconds(0) shows every such file as modified in 1970 and sorts them all to
        // one end, which reads as data rather than as a gap.
        var entries = VolumeFileCommands.ParseListing("f|10|notatime|x.txt\nf|10|0|y.txt\n");

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.ModifiedAt == null);
    }

    [Fact]
    public void A_directory_reports_no_size_of_its_own()
    {
        // A directory's own inode size is not the size of what is in it, and showing it beside real
        // file sizes invites the reader to add them up.
        VolumeFileCommands.ParseListing("d|4096|1700000000|uploads\n")
            .Should().ContainSingle().Which.SizeBytes.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_directory_lists_nothing_rather_than_failing(string? output)
    {
        VolumeFileCommands.ParseListing(output).Should().BeEmpty();
    }

    [Fact]
    public void Dockers_framing_bytes_do_not_make_the_listing_read_as_empty()
    {
        // A container with no TTY has its output framed, so the type field arrives with control
        // bytes on the front and matches neither "d" nor "f" — every entry is skipped and the
        // folder reads as empty while the file is plainly there. Observed on a real server, after
        // the same trap had already been documented twice elsewhere.
        var header = new string([(char)1, (char)0, (char)0, (char)0, (char)0, (char)0, (char)0, (char)30]);

        var entries = VolumeFileCommands.ParseListing(header + "f|21|1700000000|note.txt");

        entries.Should().ContainSingle();
        entries[0].Name.Should().Be("note.txt");
        entries[0].SizeBytes.Should().Be(21);
    }

    [Fact]
    public void Windows_line_endings_do_not_become_part_of_a_filename()
    {
        VolumeFileCommands.ParseListing("f|10|1700000000|x.txt\r\n")
            .Should().ContainSingle().Which.Name.Should().Be("x.txt");
    }
}
