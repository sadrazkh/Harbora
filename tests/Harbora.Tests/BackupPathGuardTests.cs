using Harbora.Shared;
using FluentAssertions;
using Harbora.Modules.Backup.Domain;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Restore extracts attacker-influenceable names onto a trusted filesystem — anyone who can write a
/// file into a backed-up volume chooses an entry name in the snapshot. These tests hold
/// <see cref="PathGuard"/> to the Zip-Slip defences described in THREAT_MODEL T2.
/// </summary>
public class BackupPathGuardTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "harbora-restore-root");

    [Fact]
    public void Allows_a_path_inside_the_root()
    {
        var check = PathGuard.ResolveWithin(Root, Path.Combine("data", "file.txt"));

        check.Allowed.Should().BeTrue();
        check.ResolvedPath.Should().StartWith(Path.GetFullPath(Root));
    }

    [Fact]
    public void Allows_the_root_itself()
    {
        PathGuard.ResolveWithin(Root, Root).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_relative_path_that_climbs_out()
    {
        var check = PathGuard.ResolveWithin(Root, Path.Combine("..", "..", "etc", "passwd"));

        check.Allowed.Should().BeFalse();
        check.Rejection.Should().Be(PathRejection.EscapesRoot);
    }

    /// <summary>
    /// The case a naive <c>StartsWith</c> gets wrong. "<c>…/harbora-restore-root-evil</c>" shares a
    /// string prefix with the root and is not inside it.
    /// </summary>
    [Fact]
    public void Rejects_a_sibling_directory_sharing_the_roots_prefix()
    {
        var sibling = Root + "-evil";

        var check = PathGuard.ResolveWithin(Root, Path.Combine(sibling, "loot.txt"));

        check.Allowed.Should().BeFalse();
        check.Rejection.Should().Be(PathRejection.EscapesRoot);
    }

    [Fact]
    public void Rejects_an_absolute_path_outside_the_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else", "file.txt");

        PathGuard.ResolveWithin(Root, outside).Rejection.Should().Be(PathRejection.EscapesRoot);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("nested/../../../etc/shadow")]
    public void Rejects_archive_entries_containing_parent_segments(string entry)
    {
        PathGuard.ValidateArchiveEntry(Root, entry).Rejection.Should().Be(PathRejection.ParentTraversal);
    }

    /// <summary>
    /// A backslash entry is inert on Linux and a traversal on Windows, and a restore artifact moves
    /// between the two. The stricter reading is the correct one.
    /// </summary>
    [Fact]
    public void Rejects_backslash_traversal_regardless_of_host()
    {
        PathGuard.ValidateArchiveEntry(Root, @"..\..\windows\system32\drivers\etc\hosts")
            .Rejection.Should().Be(PathRejection.ParentTraversal);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData(@"\windows\system32")]
    [InlineData("C:data")]
    public void Rejects_rooted_archive_entries(string entry)
    {
        PathGuard.ValidateArchiveEntry(Root, entry).Rejection.Should().Be(PathRejection.Rooted);
    }

    [Fact]
    public void Rejects_an_entry_containing_a_null_byte()
    {
        PathGuard.ValidateArchiveEntry(Root, "docs/read\0me.txt")
            .Rejection.Should().Be(PathRejection.InvalidCharacter);
    }

    [Fact]
    public void Allows_an_ordinary_nested_entry()
    {
        var check = PathGuard.ValidateArchiveEntry(Root, "docs/guides/readme.txt");

        check.Allowed.Should().BeTrue();
        check.ResolvedPath.Should().StartWith(Path.GetFullPath(Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_empty_entry(string entry)
    {
        PathGuard.ValidateArchiveEntry(Root, entry).Rejection.Should().Be(PathRejection.Empty);
    }
}
