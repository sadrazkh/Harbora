using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A zip dropped on <c>/launch</c>: the same guards as the tar path in <see cref="SourceArchiveTests"/>,
/// plus what a browser upload has no client to filter out for it, and a size nobody may take on trust.
/// </summary>
public sealed class SourceArchiveZipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-zip-" + Guid.NewGuid().ToString("N"));

    public SourceArchiveZipTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp folder; the OS clears it */ }
    }

    private string Dest => Path.Combine(_root, "out");

    private static MemoryStream Zip(params (string Name, string Body)[] entries)
    {
        var raw = new MemoryStream();
        using (var zip = new ZipArchive(raw, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(body));
            }
        }
        raw.Position = 0;
        return raw;
    }

    private bool Written(params string[] path) => File.Exists(Path.Combine([Dest, .. path]));

    [Fact]
    public async Task A_zip_is_recognised_by_its_first_bytes_and_unpacked()
    {
        await using var archive = Zip(("package.json", "{}"), ("src/index.js", "console.log(1)"));

        var result = await SourceArchive.ExtractAsync(archive, Dest, default);

        result.Files.Should().Be(2);
        result.Bytes.Should().Be("{}".Length + "console.log(1)".Length);
        File.ReadAllText(Path.Combine(Dest, "src", "index.js")).Should().Be("console.log(1)");
    }

    [Fact]
    public async Task A_stream_that_cannot_seek_is_still_read()
    {
        await using var inner = Zip(("package.json", "{}"));
        await using var archive = new NonSeekable(inner);

        await SourceArchive.ExtractAsync(archive, Dest, default);

        Written("package.json").Should().BeTrue();
    }

    [Fact]
    public async Task An_entry_that_escapes_the_destination_is_refused()
    {
        await using var archive = Zip(("../../escaped.txt", "pwned"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*escapes*");
        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task An_escape_is_refused_even_when_it_is_named_like_something_we_would_skip()
    {
        await using var archive = Zip(("../../.env", "pwned"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*escapes*");
    }

    [Fact]
    public async Task An_absolute_entry_is_refused()
    {
        await using var archive = Zip(("/etc/harbora-owned", "pwned"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_symbolic_link_is_skipped_rather_than_written_as_a_file_holding_its_target()
    {
        var raw = new MemoryStream();
        using (var zip = new ZipArchive(raw, ZipArchiveMode.Create, leaveOpen: true))
        {
            var link = zip.CreateEntry("link");
            link.ExternalAttributes = 0xA1FF << 16; // S_IFLNK | 0777, how Info-ZIP records one
            await using (var stream = link.Open()) stream.Write(Encoding.UTF8.GetBytes("/etc/passwd"));

            var real = zip.CreateEntry("real.txt");
            await using (var stream = real.Open()) stream.Write(Encoding.UTF8.GetBytes("ok"));
        }
        raw.Position = 0;

        await SourceArchive.ExtractAsync(raw, Dest, default);

        Written("link").Should().BeFalse();
        Written("real.txt").Should().BeTrue();
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData("repo-main/.git/config")]
    [InlineData("node_modules/left-pad/index.js")]
    [InlineData("packages/api/node_modules/left-pad/index.js")]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("repo-main/.env")]
    [InlineData("repo-main/apps/api/.env.local")]
    public async Task What_the_cli_would_filter_on_the_client_is_left_out_at_any_depth(string name)
    {
        await using var archive = Zip((name, "AWS_SECRET=real"), ("package.json", "{}"));

        var result = await SourceArchive.ExtractAsync(archive, Dest, default);

        result.Files.Should().Be(1);
        File.Exists(Path.Combine(Dest, name.Replace('/', Path.DirectorySeparatorChar))).Should().BeFalse();
        Written("package.json").Should().BeTrue();
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".envrc")]
    [InlineData("config/env")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("my_node_modules/index.js")]
    public async Task Things_merely_named_like_them_are_kept(string name)
    {
        await using var archive = Zip((name, "x"), ("package.json", "{}"));

        await SourceArchive.ExtractAsync(archive, Dest, default);

        File.Exists(Path.Combine(Dest, name.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
    }

    // ---- the folder a zip wraps everything in -----------------------------------------------------

    [Fact]
    public async Task A_sole_top_level_folder_is_taken_off_so_nothing_downstream_ever_sees_it()
    {
        await using var archive = Zip(
            ("my-shop-main/", ""), ("my-shop-main/package.json", "{}"), ("my-shop-main/src/index.js", "1"), ("my-shop-main/src/deep/a.js", "2"));

        var result = await SourceArchive.ExtractAsync(archive, Dest, default);

        result.Files.Should().Be(3);
        Written("package.json").Should().BeTrue();
        Written("src", "index.js").Should().BeTrue();
        Written("src", "deep", "a.js").Should().BeTrue();
        Directory.Exists(Path.Combine(Dest, "my-shop-main")).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_beside_the_folder_means_it_is_a_real_folder_and_stays()
    {
        await using var archive = Zip(("my-shop/package.json", "{}"), ("README.md", "x"));

        await SourceArchive.ExtractAsync(archive, Dest, default);

        Written("my-shop", "package.json").Should().BeTrue();
        Written("README.md").Should().BeTrue();
    }

    [Fact]
    public async Task Two_folders_are_two_folders()
    {
        await using var archive = Zip(("api/package.json", "{}"), ("web/package.json", "{}"));

        await SourceArchive.ExtractAsync(archive, Dest, default);

        Written("api", "package.json").Should().BeTrue();
        Written("web", "package.json").Should().BeTrue();
    }

    [Fact]
    public async Task What_a_mac_adds_beside_the_folder_does_not_keep_it_and_is_not_kept_itself()
    {
        await using var archive = Zip(
            ("my-shop/package.json", "{}"), ("__MACOSX/my-shop/._package.json", "x"), (".DS_Store", "x"), ("my-shop/.DS_Store", "x"));

        var result = await SourceArchive.ExtractAsync(archive, Dest, default);

        result.Files.Should().Be(1);
        Written("package.json").Should().BeTrue();
        Directory.Exists(Path.Combine(Dest, "__MACOSX")).Should().BeFalse();
        Written(".DS_Store").Should().BeFalse();
    }

    [Fact]
    public async Task What_is_left_out_of_an_upload_does_not_count_as_beside_the_folder()
    {
        await using var archive = Zip(("my-shop/package.json", "{}"), (".env", "KEY=real"), ("node_modules/x/index.js", "x"));

        await SourceArchive.ExtractAsync(archive, Dest, default);

        Written("package.json").Should().BeTrue();
        Written(".env").Should().BeFalse();
    }

    [Fact]
    public async Task Backslashes_from_an_old_windows_zip_are_the_same_folder()
    {
        await using var archive = Zip(("my-shop\\package.json", "{}"), ("my-shop\\src\\a.js", "1"));

        await SourceArchive.ExtractAsync(archive, Dest, default);

        Written("package.json").Should().BeTrue();
        Written("src", "a.js").Should().BeTrue();
    }

    [Fact]
    public async Task Only_one_level_comes_off()
    {
        await using var archive = Zip(("outer/inner/package.json", "{}"));

        await SourceArchive.ExtractAsync(archive, Dest, default);

        Written("inner", "package.json").Should().BeTrue();
    }

    [Theory]
    [InlineData("shop/../../escaped.txt")]
    [InlineData("shop/../escaped.txt")]
    public async Task Taking_a_level_off_cannot_be_how_an_entry_climbs_out(string name)
    {
        await using var archive = Zip((name, "pwned"), ("shop/package.json", "{}"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*escapes*");
        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task A_folder_holding_only_what_is_left_out_is_still_empty()
    {
        await using var archive = Zip(("shop/.env", "KEY=real"), ("shop/node_modules/x/index.js", "x"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty or not a valid*");
    }

    // ---- what can be run ---------------------------------------------------------------------------

    private static MemoryStream ZipWithMode(string name, int mode, string body = "#!/bin/sh\n")
    {
        var raw = new MemoryStream();
        using (var zip = new ZipArchive(raw, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(name);
            entry.ExternalAttributes = mode << 16;
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(body));
        }
        raw.Position = 0;
        return raw;
    }

    [Theory]
    [InlineData(0b1000_000_111_101_101, true)]  // 100755, regular and executable: how Info-ZIP and GitHub record one
    [InlineData(0b1000_000_111_000_000, true)]  // 100700
    [InlineData(0b1000_000_110_100_100, false)] // 100644
    [InlineData(0b1000_000_110_000_000, false)] // 100600
    [InlineData(0, false)]                      // a zip made on Windows, which carries no mode at all
    public void Only_the_owners_executable_bit_is_read_from_a_zips_mode(int mode, bool executable)
    {
        SourceArchive.IsExecutableInZip(mode << 16).Should().Be(executable);
    }

    [Fact]
    public async Task An_entry_with_its_executable_bit_set_can_be_run_after_unpacking()
    {
        await using var archive = ZipWithMode("entrypoint.sh", 0b1000_000_111_101_101);

        await SourceArchive.ExtractAsync(archive, Dest, default);

        File.ReadAllText(Path.Combine(Dest, "entrypoint.sh")).Should().Be("#!/bin/sh\n");

        // Windows has no such bit to set, so this half runs on the Linux CI lane.
        if (OperatingSystem.IsWindows()) return;
        File.GetUnixFileMode(Path.Combine(Dest, "entrypoint.sh")).HasFlag(UnixFileMode.UserExecute).Should().BeTrue();
    }

    [Fact]
    public async Task A_mode_that_would_shut_out_the_web_server_is_not_copied()
    {
        await using var archive = ZipWithMode("index.php", 0b1000_000_110_000_000, "<?php");

        await SourceArchive.ExtractAsync(archive, Dest, default);

        if (OperatingSystem.IsWindows()) return;
        File.GetUnixFileMode(Path.Combine(Dest, "index.php")).HasFlag(UnixFileMode.OtherRead).Should().BeTrue(
            "a clone of the same repository would have it readable, and Apache runs as www-data");
    }

    // ---- the refusals, in both languages ------------------------------------------------------------

    [Fact]
    public void Being_too_large_is_said_in_both_languages_and_is_not_called_empty()
    {
        var refusal = SourceArchive.TooLarge();

        refusal.Message.Should().Contain("2048 MB");
        refusal.MessageFa.Should().Contain("2048").And.Contain("مگابایت");
        refusal.MessageFa.Should().NotContain("خالی");
    }

    [Theory]
    [InlineData("this is not an archive", "خالی")]
    public async Task Junk_is_refused_in_persian_too(string body, string persian)
    {
        await using var archive = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        (await act.Should().ThrowAsync<SourceArchiveRefusedException>()).Which.MessageFa.Should().Contain(persian);
    }

    [Theory]
    [InlineData("../../escaped.txt", "بیرون")]
    [InlineData("/etc/harbora-owned", "مطلق")]
    public async Task An_entry_that_cannot_be_written_is_refused_in_persian_too(string name, string persian)
    {
        await using var archive = Zip((name, "pwned"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        (await act.Should().ThrowAsync<SourceArchiveRefusedException>()).Which.MessageFa.Should().Contain(persian);
    }

    [Fact]
    public async Task Past_the_entry_ceiling_it_refuses_before_writing_anything()
    {
        await using var archive = File.Create(Path.Combine(_root, "many.zip"), 4096, FileOptions.DeleteOnClose);
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i <= SourceArchive.MaxEntries; i++)
                zip.CreateEntry($"{i}");
        }
        archive.Position = 0;

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*more than*entries*");
        Directory.GetFiles(Dest).Should().BeEmpty();
    }

    [Theory]
    [InlineData("this is not an archive")]
    [InlineData("PK this only starts like one")]
    public async Task Junk_gets_the_same_readable_message_as_the_tar_path(string body)
    {
        await using var archive = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty or not a valid*");
    }

    [Fact]
    public async Task A_zip_of_nothing_is_empty_whatever_it_is_technically()
    {
        await using var archive = Zip();

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty or not a valid*");
    }

    [Fact]
    public async Task A_zip_holding_only_what_is_left_out_is_empty_too()
    {
        await using var archive = Zip(("node_modules/x/index.js", "x"), (".env", "KEY=real"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty or not a valid*");
        Written(".env").Should().BeFalse();
    }

    [Fact]
    public async Task A_truncated_zip_gets_the_readable_message()
    {
        var whole = Zip(("package.json", "{}")).ToArray();
        await using var archive = new MemoryStream(whole[..(whole.Length / 2)]);

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty or not a valid*");
    }

    [Fact]
    public async Task A_gzipped_tar_still_goes_down_its_own_path()
    {
        await using var archive = new MemoryStream();
        await using (var gz = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        await using (var tar = new System.Formats.Tar.TarWriter(gz, leaveOpen: true))
        {
            await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, ".env")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("FROM_THE_CLI=kept"))
            });
        }
        archive.Position = 0;

        await SourceArchive.ExtractAsync(archive, Dest, default);

        Written(".env").Should().BeTrue("the tar path is the CLI's, which has already filtered what it wants");
    }

    /// <summary>A request body, as far as this code can tell.</summary>
    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
