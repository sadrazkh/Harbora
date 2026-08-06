﻿using FluentAssertions;
using Harbora.Infrastructure.Storage;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The key a bucket browser will act on.
///
/// These read like the volume-path tests because the failure is the same one: a key from a form is
/// pasted into an argument that names a path inside somebody's bucket, and on shared storage the
/// prefix next door belongs to another tenant.
/// </summary>
public class ObjectKeyTests
{
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("photos/2026/photo.jpg", "photos/2026/photo.jpg")]
    [InlineData("/photos/photo.jpg", "photos/photo.jpg")]
    [InlineData("photos/photo.jpg/", "photos/photo.jpg")]
    [InlineData("photos//photo.jpg", "photos/photo.jpg")]
    [InlineData("./photos/photo.jpg", "photos/photo.jpg")]
    public void An_ordinary_key_comes_back_tidied(string given, string expected) =>
        ObjectKey.Normalise(given).Should().Be(expected);

    [Theory]
    [InlineData("../secrets")]
    [InlineData("photos/../../secrets")]
    [InlineData("photos/..")]
    public void A_key_that_climbs_out_is_refused(string given) =>
        ObjectKey.Normalise(given).Should().BeNull(
            "resolving it quietly would turn a suspicious key into a working one");

    [Theory]
    [InlineData("photo..jpg")]
    [InlineData("archive..tar.gz")]
    [InlineData("photos/2026..old/a.jpg")]
    public void Two_dots_inside_a_name_are_a_name_not_a_way_out(string key) =>
        ObjectKey.Normalise(key).Should().Be(key,
            "refusing every key that contains two dots would refuse ordinary filenames — it is the " +
            "segment that is exactly \"..\" that climbs");

    [Fact]
    public void A_key_holding_a_nul_is_refused() =>
        ObjectKey.Normalise("photo\0.jpg").Should().BeNull();

    [Fact]
    public void A_key_holding_a_control_character_is_refused() =>
        ObjectKey.Normalise("photo\n.jpg").Should().BeNull();

    [Fact]
    public void A_backslash_is_refused_rather_than_translated() =>
        ObjectKey.Normalise("photos\\photo.jpg").Should().BeNull(
            "a backslash is a legitimate character in a key, so turning it into a separator " +
            "would address a different object than the one named");

    [Fact]
    public void A_segment_of_only_spaces_is_refused() =>
        ObjectKey.Normalise("photos/   /photo.jpg").Should().BeNull();

    [Fact]
    public void A_key_at_the_ceiling_is_allowed_and_one_past_it_is_not()
    {
        ObjectKey.Normalise(new string('a', ObjectKey.MaxLength)).Should().NotBeNull();
        ObjectKey.Normalise(new string('a', ObjectKey.MaxLength + 1)).Should().BeNull();
    }

    [Fact]
    public void Null_is_refused() => ObjectKey.Normalise(null).Should().BeNull();

    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("", false)]
    [InlineData("/", false)]
    [InlineData("../x", false)]
    [InlineData(null, false)]
    public void Naming_an_object_means_naming_something(string? key, bool usable) =>
        ObjectKey.IsUsableObject(key).Should().Be(usable);

    [Fact]
    public void The_root_is_a_legitimate_prefix_though_never_a_legitimate_key()
    {
        ObjectKey.NormalisePrefix(null).Should().Be("");
        ObjectKey.NormalisePrefix("").Should().Be("");
        ObjectKey.NormalisePrefix("/").Should().Be("");
        ObjectKey.IsUsableObject("").Should().BeFalse();
    }

    [Fact]
    public void A_prefix_that_climbs_out_is_refused_too() =>
        ObjectKey.NormalisePrefix("photos/..").Should().BeNull();

    [Theory]
    [InlineData("photos/2026/june", "photos/2026")]
    [InlineData("photos", "")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("/", null)]
    public void The_parent_is_what_the_up_one_level_link_needs(string? prefix, string? expected) =>
        ObjectKey.Parent(prefix).Should().Be(expected);

    [Fact]
    public void The_parent_of_a_refused_prefix_is_nothing_to_climb_to() =>
        ObjectKey.Parent("../x").Should().BeNull();
}
