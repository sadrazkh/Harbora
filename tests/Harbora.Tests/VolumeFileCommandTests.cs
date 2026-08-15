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
            "f|1024|1700000000|logo.png\nd|0|1700000001|uploads\n").Entries;

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
            "f|1|1|beta.txt\nd|0|1|zeta\nf|1|1|Alpha.txt\nd|0|1|admin\n").Entries;

        entries.Select(e => e.Name).Should().Equal("admin", "zeta", "Alpha.txt", "beta.txt");
    }

    [Fact]
    public void A_filename_containing_the_separator_survives_whole()
    {
        // "report|final.txt" is a legal filename. Splitting on every separator would truncate it to
        // "report", and the download link beside it would point at a file that does not exist.
        var entries = VolumeFileCommands.ParseListing("f|10|1700000000|report|final.txt\n").Entries;

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
        // somebody then clicks. None of these shapes is the truncation marker either, so the listing
        // is not flagged as cut short just because a line was unreadable.
        var listing = VolumeFileCommands.ParseListing(line);

        listing.Entries.Should().BeEmpty();
        listing.Truncated.Should().BeFalse();
    }

    [Fact]
    public void A_truncation_marker_flags_the_listing_without_being_read_as_a_bogus_entry()
    {
        // What CapturingProgress appends once a remote one-off's captured output hits its 1 MiB
        // bound. It carries no "|" at all, so before this fix it fell into the same bucket as any
        // other unparseable line — skipped, with nothing left behind to say the listing was cut
        // short. That silence is exactly what this test rules out.
        var listing = VolumeFileCommands.ParseListing(
            "f|10|1700000000|kept.txt\n... [output truncated: exceeded 1048576 characters]\n");

        listing.Truncated.Should().BeTrue();
        listing.Entries.Should().ContainSingle().Which.Name.Should().Be("kept.txt");
    }

    [Fact]
    public void A_listing_with_no_truncation_marker_is_not_flagged_as_truncated()
    {
        // The other half of the same guarantee: a listing that always claimed to be partial would
        // be no more honest than one that never did.
        var listing = VolumeFileCommands.ParseListing("f|10|1700000000|whole.txt\n");

        listing.Truncated.Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_timestamp_is_unknown_rather_than_1970()
    {
        // FromUnixTimeSeconds(0) shows every such file as modified in 1970 and sorts them all to
        // one end, which reads as data rather than as a gap.
        var entries = VolumeFileCommands.ParseListing("f|10|notatime|x.txt\nf|10|0|y.txt\n").Entries;

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.ModifiedAt == null);
    }

    [Fact]
    public void A_directory_reports_no_size_of_its_own()
    {
        // A directory's own inode size is not the size of what is in it, and showing it beside real
        // file sizes invites the reader to add them up.
        VolumeFileCommands.ParseListing("d|4096|1700000000|uploads\n")
            .Entries.Should().ContainSingle().Which.SizeBytes.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_directory_lists_nothing_rather_than_failing(string? output)
    {
        var listing = VolumeFileCommands.ParseListing(output);

        listing.Entries.Should().BeEmpty();
        listing.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Dockers_framing_bytes_do_not_make_the_listing_read_as_empty()
    {
        // A container with no TTY has its output framed, so the type field arrives with control
        // bytes on the front and matches neither "d" nor "f" — every entry is skipped and the
        // folder reads as empty while the file is plainly there. Observed on a real server, after
        // the same trap had already been documented twice elsewhere.
        var header = new string([(char)1, (char)0, (char)0, (char)0, (char)0, (char)0, (char)0, (char)30]);

        var entries = VolumeFileCommands.ParseListing(header + "f|21|1700000000|note.txt").Entries;

        entries.Should().ContainSingle();
        entries[0].Name.Should().Be("note.txt");
        entries[0].SizeBytes.Should().Be(21);
    }

    // --- reading a file back out ---

    [Fact]
    public void A_framed_base64_stream_still_decodes()
    {
        // The same non-TTY header that broke the listing. Decoding the raw stream throws, the read
        // returns nothing, and the browser is told the file does not exist — which is how a file
        // plainly sitting in the volume becomes a 404.
        var header = new string([(char)1, (char)0, (char)0, (char)0, (char)0, (char)0, (char)0, (char)8]);

        var bytes = VolumeFileCommands.ParseBase64(header + "aGVsbG8=");

        System.Text.Encoding.UTF8.GetString(bytes!).Should().Be("hello");
    }

    [Fact]
    public void The_image_pull_chatter_does_not_swallow_the_file()
    {
        // RunOneOffAsync reports the image pull into the same buffer, and those words are almost
        // all inside the base64 alphabet — filtering the whole stream at once glues
        // "StatusImageisuptodateforalpine320" onto the front of the file and nothing decodes. This
        // was a real 404 on a file plainly sitting in the volume.
        var stream = string.Join('\n',
            "Pulling from library/alpine",
            "Digest: sha256:abc",
            "Status: Image is up to date for alpine:3.20",
            "aGVsbG8=");

        System.Text.Encoding.UTF8.GetString(VolumeFileCommands.ParseBase64(stream)!).Should().Be("hello");
    }

    [Fact]
    public void The_payload_is_taken_from_the_end_rather_than_the_start()
    {
        // Pull chatter comes first and the file last. A line early in the stream that happens to
        // decode is noise; the helper's own output is the final word.
        var stream = string.Join('\n', "abcd", "aGVsbG8=");

        System.Text.Encoding.UTF8.GetString(VolumeFileCommands.ParseBase64(stream)!).Should().Be("hello");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mc: <ERROR> no such file")]
    public void Nothing_usable_reads_as_no_file_rather_than_an_empty_one(string? output)
    {
        // An empty byte array would be written to disk on a download and saved as a corrupt file,
        // which is worse than the download failing.
        VolumeFileCommands.ParseBase64(output).Should().BeNull();
    }

    [Fact]
    public void An_empty_file_is_not_the_same_as_no_file()
    {
        // A genuinely empty file base64-encodes to nothing at all, so the two look identical on the
        // wire; the guard above deliberately treats that as no file rather than inventing one.
        VolumeFileCommands.ParseBase64("").Should().BeNull();
    }

    [Fact]
    public void Windows_line_endings_do_not_become_part_of_a_filename()
    {
        VolumeFileCommands.ParseListing("f|10|1700000000|x.txt\r\n")
            .Entries.Should().ContainSingle().Which.Name.Should().Be("x.txt");
    }
}
