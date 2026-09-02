using FluentAssertions;
using Harbora.Shared;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 1.2 (2026-09 market-gaps round two): the root-directory setting an app builds from — normalisation,
/// entry-time validation (settable from the panel, refused by name for a traversing or absolute
/// path), and resolution against an actual unpacked source tree (settable at deploy time, refused by
/// name when the directory it names is not there).
/// </summary>
public class AppRootDirectoryTests
{
    // ---- Normalise -----------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("./", "")]
    [InlineData("api", "api")]
    [InlineData("./api", "api")]
    [InlineData("/api/", "api")]
    [InlineData("services\\worker", "services/worker")]
    public void Normalise_produces_the_one_stored_shape(string? input, string expected) =>
        AppRootDirectory.Normalise(input).Should().Be(expected);

    // ---- Validate: the entry-time refusals ------------------------------------------------------

    [Theory]
    [InlineData("api")]
    [InlineData("services/worker")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(null)]
    public void A_plain_sub_path_or_the_repository_root_is_accepted(string? value) =>
        AppRootDirectory.Validate(value).Should().Be(PathRejection.None);

    [Theory]
    [InlineData("../other")]
    [InlineData("api/../../etc")]
    [InlineData("..")]
    public void A_path_that_traverses_upward_is_refused_by_name(string value)
    {
        var rejection = AppRootDirectory.Validate(value);

        rejection.Should().Be(PathRejection.ParentTraversal);
        AppRootDirectory.Explain(value, rejection).Should()
            .Contain(value).And.Contain("..", "the explanation must say what specifically was wrong");
    }

    /// <summary>
    /// An absolute path — Unix-rooted or Windows-drive-qualified — is a different mistake to
    /// "../other": it does not merely escape the repository, it never named anything inside it at
    /// all. It must be refused for what it is, not quietly re-read as the equivalent relative path
    /// with its leading separator stripped — that would build a real, different, unannounced
    /// directory instead of the one that was actually asked for.
    /// </summary>
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/api")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share")]
    public void An_absolute_path_is_refused_by_name(string value)
    {
        var rejection = AppRootDirectory.Validate(value);

        rejection.Should().Be(PathRejection.Rooted,
            "an absolute path must be refused for being absolute, not silently reinterpreted as a relative " +
            "sub-path once its leading separator is trimmed off");
        AppRootDirectory.Explain(value, rejection).Should().Contain(value);
    }

    // ---- TryResolve: settable at deploy time, refused by name against a real source tree ---------

    [Fact]
    public void A_sub_directory_that_exists_resolves_to_it()
    {
        var root = MakeSourceTree("api", "web");

        AppRootDirectory.TryResolve(root, "api", out var resolved, out var error).Should().BeTrue();
        resolved.Should().Be(Path.Combine(root, "api"));
        error.Should().BeNull();
    }

    [Fact]
    public void No_root_directory_resolves_to_the_source_root_itself()
    {
        var root = MakeSourceTree("api");

        AppRootDirectory.TryResolve(root, null, out var resolved, out var error).Should().BeTrue();
        resolved.Should().Be(root);
        error.Should().BeNull();
    }

    [Fact]
    public void A_root_directory_that_does_not_exist_is_refused_by_name_and_lists_what_is_there()
    {
        var root = MakeSourceTree("api", "web");

        AppRootDirectory.TryResolve(root, "worker", out _, out var error).Should().BeFalse();

        error.Should().Contain("worker", "the missing directory must be named, not just \"not found\"");
        error.Should().Contain("api").And.Contain("web",
            "what IS at the top of the tree helps correct the typo without guessing");
    }

    [Fact]
    public void A_traversing_root_directory_is_refused_before_ever_touching_the_disk()
    {
        var root = MakeSourceTree("api");

        AppRootDirectory.TryResolve(root, "../elsewhere", out _, out var error).Should().BeFalse();

        error.Should().Contain("../elsewhere").And.Contain("..");
    }

    private static string MakeSourceTree(params string[] topLevelDirs)
    {
        var root = Path.Combine(Path.GetTempPath(), "harbora-root-dir-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var dir in topLevelDirs) Directory.CreateDirectory(Path.Combine(root, dir));
        return root;
    }
}
