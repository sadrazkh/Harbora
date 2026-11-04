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
        await using var archive = Zip((name, "x"));

        await SourceArchive.ExtractAsync(archive, Dest, default);

        File.Exists(Path.Combine(Dest, name.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
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
