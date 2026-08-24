using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What `harbora deploy` puts in the tarball. Exclusions are not only about size: a local
/// <c>.env</c> or a <c>.git</c> directory swept into a push sends credentials and history to the
/// server, and <c>node_modules</c> turns a two-second push into a hundred-megabyte one.
/// </summary>
public class SourcePackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-pack-" + Guid.NewGuid().ToString("N"));

    public SourcePackerTests() => Directory.CreateDirectory(_root);

    private static bool Excluded(string path, params string[] ignore) =>
        SourcePacker.IsExcluded(path, ignore);

    [Theory]
    [InlineData("node_modules/express/index.js")]
    [InlineData(".git/config")]
    [InlineData("src/node_modules/dep/a.js")]     // nested, not just at the root
    [InlineData("bin/Debug/app.dll")]
    [InlineData("__pycache__/mod.pyc")]
    [InlineData(".venv/lib/python3/site.py")]
    public void Heavy_and_machine_local_paths_are_excluded_without_any_ignore_file(string path)
    {
        Excluded(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    public void Local_secret_files_never_leave_the_machine(string path)
    {
        // These routinely hold database URLs and API keys for local development.
        Excluded(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("index.js")]
    [InlineData("src/app/main.ts")]
    [InlineData("Dockerfile")]
    [InlineData("package.json")]
    [InlineData(".dockerignore")]
    public void Real_source_files_are_kept(string path)
    {
        Excluded(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("Scripts/build/copy-fonts.mjs")]     // the DriveUnion regression: a source helper folder
    [InlineData("src/vendor/CustomVendorCode.php")]
    [InlineData("app/target/README.md")]
    [InlineData("packages/api/dist/index.ts")]
    [InlineData("web/.output/notes.txt")]
    public void Build_output_names_are_ordinary_source_folders_anywhere_but_the_project_root(string path)
    {
        // "build", "vendor", "target", "dist" and ".output" are common build-output directory names
        // for an unconfigured project — but they are equally common as ordinary source directory
        // names anywhere deeper in a tree. Excluding them at every depth (as node_modules is)
        // silently drops real source files; DriveUnion's own Scripts/build/copy-fonts.mjs is exactly
        // that, and its absence broke `npm run prebuild` inside the image with no error surfaced.
        Excluded(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("build/app.js")]
    [InlineData("dist/bundle.js")]
    [InlineData("target/release/app")]
    [InlineData("vendor/autoload.php")]
    [InlineData(".output/server/index.mjs")]
    public void Build_output_names_are_still_excluded_at_the_project_root_with_no_ignore_file(string path)
    {
        // The backstop this set exists for: an unconfigured project whose build output lands directly
        // under the folder being packed (npm's default ./build or ./dist, cargo's ./target, a PHP/Go
        // ./vendor, Nuxt's ./.output) must still not be shipped even with no .dockerignore/.gitignore.
        Excluded(path).Should().BeTrue();
    }

    [Fact]
    public async Task Packing_keeps_a_source_folder_that_happens_to_be_named_build()
    {
        // The exact regression this task exists to close: a build *helper script* living under a
        // folder literally called "build", nested under an ordinary source directory. The old rule
        // matched "build" at any depth and silently dropped this file, which broke `npm run prebuild`
        // inside the image with no error anywhere in the deployment log.
        Directory.CreateDirectory(Path.Combine(_root, "Scripts", "build"));
        File.WriteAllText(Path.Combine(_root, "Scripts", "build", "copy-fonts.mjs"), "// copies fonts");
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");

        var packed = await SourcePacker.PackAsync(_root);
        try
        {
            var dest = Path.Combine(_root, "..", "unpacked-" + Guid.NewGuid().ToString("N"));
            await using (var stream = File.OpenRead(packed.ArchivePath))
                await Harbora.Infrastructure.Deployments.SourceArchive.ExtractAsync(stream, dest, default);

            File.Exists(Path.Combine(dest, "Scripts", "build", "copy-fonts.mjs")).Should().BeTrue(
                "a source folder named 'build' is not the same thing as a project's build output");

            Directory.Delete(dest, recursive: true);
        }
        finally { File.Delete(packed.ArchivePath); }
    }

    [Fact]
    public void An_ignore_entry_excludes_the_directory_and_everything_under_it()
    {
        Excluded("coverage/report.html", "coverage").Should().BeTrue();
        Excluded("coverage", "coverage").Should().BeTrue();
    }

    [Fact]
    public void Wildcard_ignore_entries_are_honoured()
    {
        Excluded("debug.log", "*.log").Should().BeTrue();
        Excluded("logs/app.log", "*.log").Should().BeTrue();
        Excluded("app.js", "*.log").Should().BeFalse();
    }

    [Fact]
    public void Dockerignore_wins_over_gitignore()
    {
        // The build uses .dockerignore, so the push should match what the build would see.
        File.WriteAllText(Path.Combine(_root, ".dockerignore"), "only-docker");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "only-git");

        var patterns = SourcePacker.LoadIgnorePatterns(_root);

        patterns.Should().Contain("only-docker").And.NotContain("only-git");
    }

    [Fact]
    public void Comments_blank_lines_and_negations_are_skipped()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"),
            "# a comment\n\n  \nbuild-output\n!keep-me\n/leading-slash/\n");

        var patterns = SourcePacker.LoadIgnorePatterns(_root);

        patterns.Should().BeEquivalentTo(["build-output", "leading-slash"]);
    }

    [Fact]
    public async Task Packing_produces_an_archive_containing_the_source()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "dep"));
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "src", "index.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET=hunter2");
        File.WriteAllText(Path.Combine(_root, "node_modules", "dep", "big.js"), new string('x', 5000));

        var packed = await SourcePacker.PackAsync(_root);

        try
        {
            packed.Files.Should().Be(2, "only package.json and src/index.js belong in the push");

            // A customer who only sees "Unpacked 215 entries" has no way to find out that 130 files
            // never arrived. The count of what was dropped, and why, must be discoverable from the
            // same result the file count came from.
            packed.Excluded.Should().HaveCount(2);
            packed.Excluded.Select(e => e.Path).Should().BeEquivalentTo(
                [".env", "node_modules/dep/big.js"]);
            packed.Excluded.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Reason));

            // Round-trip through the server's own extractor: what was packed is what arrives.
            var dest = Path.Combine(_root, "..", "unpacked-" + Guid.NewGuid().ToString("N"));
            await using (var stream = File.OpenRead(packed.ArchivePath))
                await Harbora.Infrastructure.Deployments.SourceArchive.ExtractAsync(stream, dest, default);

            File.Exists(Path.Combine(dest, "package.json")).Should().BeTrue();
            File.Exists(Path.Combine(dest, "src", "index.js")).Should().BeTrue();
            File.Exists(Path.Combine(dest, ".env")).Should().BeFalse("the secret must not have been sent");
            Directory.Exists(Path.Combine(dest, "node_modules")).Should().BeFalse();

            Directory.Delete(dest, recursive: true);
        }
        finally { File.Delete(packed.ArchivePath); }
    }

    [Fact]
    public async Task Excluded_files_report_which_rule_dropped_them()
    {
        Directory.CreateDirectory(Path.Combine(_root, "coverage"));
        File.WriteAllText(Path.Combine(_root, "coverage", "report.html"), "x");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "coverage");
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");

        var packed = await SourcePacker.PackAsync(_root);
        try
        {
            var entry = packed.Excluded.Should().ContainSingle().Which;
            entry.Path.Should().Be("coverage/report.html");
            entry.Reason.Should().Contain("coverage", "the reason should name the rule, not just say 'excluded'");
        }
        finally { File.Delete(packed.ArchivePath); }
    }

    [Fact]
    public void DescribeExclusion_names_the_built_in_rule_that_matched()
    {
        SourcePacker.DescribeExclusion("node_modules/express/index.js", [])
            .Should().NotBeNull().And.Subject.ToString()!.Should().Contain("node_modules");
    }

    [Fact]
    public void DescribeExclusion_returns_null_for_a_file_that_is_not_excluded()
    {
        SourcePacker.DescribeExclusion("src/app/main.ts", []).Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }
}
