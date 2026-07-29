using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Unpacking a pushed source archive is the only place a user's bytes are written to the panel's
/// filesystem. A tar entry named "../../etc/whatever" would let any authenticated tenant write
/// wherever the panel can — so path containment is the property under test, not a nicety.
/// </summary>
public class SourceArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-arc-" + Guid.NewGuid().ToString("N"));

    public SourceArchiveTests() => Directory.CreateDirectory(_root);

    private string Dest => Path.Combine(_root, "out");

    private static Stream Archive(params (string Name, string Body)[] entries)
    {
        var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(body)) };
                tar.WriteEntry(entry);
            }
        }
        raw.Position = 0;
        return raw;
    }

    [Fact]
    public async Task Ordinary_files_are_written()
    {
        await using var archive = Archive(("index.js", "console.log(1)"), ("src/app.ts", "export {}"));

        var result = await SourceArchive.ExtractAsync(archive, Dest, default);

        File.Exists(Path.Combine(Dest, "index.js")).Should().BeTrue();
        File.Exists(Path.Combine(Dest, "src", "app.ts")).Should().BeTrue();
        result.Files.Should().Be(2);
    }

    [Fact]
    public async Task Leading_dot_slash_entries_are_handled()
    {
        // tar normally records "./package.json" for a directory-relative archive.
        await using var archive = Archive(("./package.json", "{}"));

        await SourceArchive.ExtractAsync(archive, Dest, default);

        File.Exists(Path.Combine(Dest, "package.json")).Should().BeTrue();
    }

    [Fact]
    public async Task An_entry_that_escapes_the_destination_is_refused()
    {
        await using var archive = Archive(("../../escaped.txt", "pwned"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*escapes*");
        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse();
        File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task A_deep_traversal_is_refused()
    {
        await using var archive = Archive(("a/b/../../../../outside.txt", "pwned"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task An_absolute_entry_is_refused()
    {
        await using var archive = Archive(("/etc/harbora-owned", "pwned"));

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Traversal_inside_a_path_that_still_lands_inside_is_allowed()
    {
        // "a/../b.txt" resolves to "b.txt" — inside the destination, so it is legitimate.
        var resolved = SourceArchive.ResolveSafePath(Path.GetFullPath(Dest), "a/../b.txt");

        resolved.Should().NotBeNull();
        resolved!.Should().EndWith("b.txt");
    }

    [Fact]
    public void A_sibling_directory_with_a_shared_prefix_is_not_mistaken_for_inside()
    {
        // Naive StartsWith without a separator would accept "<dest>-evil/x" as being inside "<dest>".
        var dest = Path.GetFullPath(Dest);

        var act = () => SourceArchive.ResolveSafePath(dest, "../out-evil/x.txt");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Symlinks_are_skipped_rather_than_followed()
    {
        var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc/passwd" });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "real.txt")
            { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("ok")) });
        }
        raw.Position = 0;

        await SourceArchive.ExtractAsync(raw, Dest, default);

        File.Exists(Path.Combine(Dest, "link")).Should().BeFalse("a link could point anywhere");
        File.Exists(Path.Combine(Dest, "real.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task An_empty_or_corrupt_archive_gets_a_readable_message()
    {
        // Pushing an empty folder, a truncated upload, or a body that isn't a gzipped tar all land
        // here. Surfacing "unable to read beyond the end of the stream" as a deploy error tells the
        // user nothing about what to do.
        await using var archive = Archive();

        var act = async () => await SourceArchive.ExtractAsync(archive, Dest, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*empty or not a valid*");
    }

    [Fact]
    public async Task A_body_that_is_not_an_archive_at_all_is_rejected_clearly()
    {
        await using var notAnArchive = new MemoryStream(Encoding.UTF8.GetBytes("this is plain text"));

        var act = async () => await SourceArchive.ExtractAsync(notAnArchive, Dest, default);

        await act.Should().ThrowAsync<Exception>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }
}
