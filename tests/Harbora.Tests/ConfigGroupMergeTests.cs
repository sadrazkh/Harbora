using FluentAssertions;
using Harbora.Domain.Apps;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="ConfigGroupMerge"/> is the exact code <c>DeploymentPipeline.BuildEnv</c> calls to
/// decide app-over-group precedence (Sub-project 9, 2026-08-20 platform-options plan) — not a
/// parallel implementation of the same idea. These tests exercise it directly at that seam;
/// <c>ConfigGroupPipelineTests</c> proves the same precedence survives all the way to what a fake
/// container actually receives.
/// </summary>
public class ConfigGroupMergeTests
{
    private static EnvironmentVariable OwnVar(string key, string value, bool isSecret = false) =>
        new() { Key = key, Value = value, IsSecret = isSecret };

    private static ConfigGroupEntry GroupEntry(string key, string value, bool isSecret = false) =>
        new() { Key = key, Value = value, IsSecret = isSecret };

    private static AttachedGroupEntries Group(int order, string name, params ConfigGroupEntry[] entries) =>
        new(order, Guid.NewGuid(), name, entries);

    [Fact]
    public void A_key_only_a_group_defines_reaches_the_effective_set()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [Group(1, "shared", GroupEntry("API_BASE", "https://api.example.com"))]);

        result.Should().ContainSingle(e => e.Key == "API_BASE" && e.Value == "https://api.example.com");
    }

    [Fact]
    public void The_apps_own_variable_wins_over_a_group_defining_the_same_key()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("PORT", "9000")],
            attachedGroups: [Group(1, "shared", GroupEntry("PORT", "8080"))]);

        result.Should().ContainSingle(e => e.Key == "PORT")
            .Which.Should().BeEquivalentTo(new { Value = "9000", Source = ConfigSource.App });
    }

    [Fact]
    public void Between_two_groups_the_one_attached_later_wins()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups:
            [
                Group(1, "first", GroupEntry("LOG_LEVEL", "info")),
                Group(2, "second", GroupEntry("LOG_LEVEL", "debug"))
            ]);

        var entry = result.Should().ContainSingle(e => e.Key == "LOG_LEVEL").Which;
        entry.Value.Should().Be("debug", "the group attached later (higher AttachOrder) outranks the earlier one");
        entry.Source.Should().Be(ConfigSource.Group);
        entry.SourceGroupName.Should().Be("second");
    }

    [Fact]
    public void Attachment_order_in_the_input_sequence_does_not_matter_only_AttachOrder_does()
    {
        // The second group is passed FIRST here — Merge must still resolve precedence by AttachOrder,
        // not by whatever order the caller happened to enumerate the collection in.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups:
            [
                Group(5, "attached-later", GroupEntry("FLAG", "on")),
                Group(1, "attached-first", GroupEntry("FLAG", "off"))
            ]);

        result.Should().ContainSingle(e => e.Key == "FLAG").Which.Value.Should().Be("on");
    }

    [Fact]
    public void Every_row_carries_where_it_came_from()
    {
        var groupId = Guid.NewGuid();
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("OWN_KEY", "mine")],
            attachedGroups: [new AttachedGroupEntries(1, groupId, "Shared Defaults", [GroupEntry("GROUP_KEY", "theirs")])]);

        result.Should().ContainSingle(e => e.Key == "OWN_KEY")
            .Which.Should().BeEquivalentTo(new { Source = ConfigSource.App, SourceGroupId = (Guid?)null, SourceGroupName = (string?)null });

        result.Should().ContainSingle(e => e.Key == "GROUP_KEY")
            .Which.Should().BeEquivalentTo(new { Source = ConfigSource.Group, SourceGroupId = (Guid?)groupId, SourceGroupName = "Shared Defaults" });
    }

    [Fact]
    public void Secret_entries_keep_their_flag_and_their_raw_ciphertext_value_through_the_merge()
    {
        // Merge never decrypts — that decision belongs to the caller (BuildEnv unprotects for a
        // container; the env page masks instead). Proving the flag and the untouched value survive is
        // what keeps that division of responsibility honest.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [Group(1, "shared", GroupEntry("DB_PASSWORD", "cipher:xyz", isSecret: true))]);

        result.Should().ContainSingle(e => e.Key == "DB_PASSWORD")
            .Which.Should().BeEquivalentTo(new { Value = "cipher:xyz", IsSecret = true });
    }

    [Fact]
    public void Three_or_more_groups_resolve_by_the_highest_AttachOrder_among_them()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups:
            [
                Group(1, "a", GroupEntry("K", "1")),
                Group(2, "b", GroupEntry("K", "2")),
                Group(3, "c", GroupEntry("K", "3"))
            ]);

        result.Should().ContainSingle(e => e.Key == "K").Which.Value.Should().Be("3");
    }

    [Fact]
    public void Keys_only_the_app_defines_are_unaffected_by_any_group()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("ONLY_MINE", "yes")],
            attachedGroups: [Group(1, "shared", GroupEntry("SOMETHING_ELSE", "value"))]);

        result.Should().HaveCount(2);
        result.Should().Contain(e => e.Key == "ONLY_MINE" && e.Source == ConfigSource.App);
        result.Should().Contain(e => e.Key == "SOMETHING_ELSE" && e.Source == ConfigSource.Group);
    }
}
