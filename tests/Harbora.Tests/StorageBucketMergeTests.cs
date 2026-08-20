using FluentAssertions;
using Harbora.Domain.Apps;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="ConfigGroupMerge"/>'s bucket half (F5, 2026-08-21 functions-and-services plan) — the
/// exact code <c>DeploymentPipeline.BuildEnv</c> calls to fold an attached bucket's env in, one
/// precedence step below every config group (see the type's own doc comment: a bucket hands an app
/// default credentials, not something meant to override a value somebody deliberately set through a
/// group). Mirrors <see cref="ConfigGroupMergeTests"/>'s structure for the group half.
/// <see cref="StorageBucketPipelineTests"/> proves the same precedence survives to what a fake
/// container actually receives.
/// </summary>
public class StorageBucketMergeTests
{
    private static EnvironmentVariable OwnVar(string key, string value, bool isSecret = false) =>
        new() { Key = key, Value = value, IsSecret = isSecret };

    private static BucketEnvEntry BucketEntry(string key, string value, bool isSecret = false) =>
        new(key, value, isSecret);

    private static AttachedBucketEnv Bucket(int order, string name, params BucketEnvEntry[] entries) =>
        new(order, Guid.NewGuid(), name, entries);

    [Fact]
    public void A_key_only_a_bucket_defines_reaches_the_effective_set()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedBuckets: [Bucket(1, "uploads", BucketEntry("S3_BUCKET", "uploads"))]);

        result.Should().ContainSingle(e => e.Key == "S3_BUCKET" && e.Value == "uploads");
    }

    [Fact]
    public void The_apps_own_variable_wins_over_a_bucket_defining_the_same_key()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("S3_BUCKET", "hand-picked")],
            attachedGroups: [],
            attachedBuckets: [Bucket(1, "uploads", BucketEntry("S3_BUCKET", "uploads"))]);

        result.Should().ContainSingle(e => e.Key == "S3_BUCKET")
            .Which.Should().BeEquivalentTo(new { Value = "hand-picked", Source = ConfigSource.App });
    }

    [Fact]
    public void A_config_group_wins_over_a_bucket_defining_the_same_key()
    {
        // A bucket exists to hand an app default credentials, not to override a value somebody
        // deliberately set through a group — the reasoning ConfigGroupMerge's own doc comment states.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [new AttachedGroupEntries(1, Guid.NewGuid(), "overrides",
                [new ConfigGroupEntry { Key = "S3_ENDPOINT", Value = "https://from-group.example" }])],
            attachedBuckets: [Bucket(99, "uploads", BucketEntry("S3_ENDPOINT", "https://from-bucket.example"))]);

        result.Should().ContainSingle(e => e.Key == "S3_ENDPOINT")
            .Which.Should().BeEquivalentTo(new { Value = "https://from-group.example", Source = ConfigSource.Group },
                "a group must outrank a bucket regardless of either one's AttachOrder");
    }

    [Fact]
    public void Between_two_buckets_the_one_attached_later_wins()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedBuckets:
            [
                Bucket(1, "first", BucketEntry("S3_BUCKET", "first")),
                Bucket(2, "second", BucketEntry("S3_BUCKET", "second"))
            ]);

        var entry = result.Should().ContainSingle(e => e.Key == "S3_BUCKET").Which;
        entry.Value.Should().Be("second", "the bucket attached later (higher AttachOrder) outranks the earlier one");
        entry.Source.Should().Be(ConfigSource.Bucket);
        entry.SourceBucketName.Should().Be("second");
    }

    [Fact]
    public void Every_bucket_row_carries_where_it_came_from()
    {
        var bucketId = Guid.NewGuid();
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedBuckets: [new AttachedBucketEnv(1, bucketId, "Uploads",
                [BucketEntry("S3_ACCESS_KEY", "AKIA123")])]);

        result.Should().ContainSingle(e => e.Key == "S3_ACCESS_KEY")
            .Which.Should().BeEquivalentTo(new
            {
                Source = ConfigSource.Bucket, SourceBucketId = (Guid?)bucketId, SourceBucketName = "Uploads",
                SourceGroupId = (Guid?)null, SourceGroupName = (string?)null
            });
    }

    [Fact]
    public void The_buckets_secret_entry_keeps_its_flag_and_its_raw_ciphertext_value_through_the_merge()
    {
        // Merge never decrypts, for a bucket's secret exactly as for a group's — see
        // ConfigGroupMergeTests' equivalent for groups. BucketEnvKeys.EntriesFor is documented to
        // pass ciphertext through unchanged for the same reason.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedBuckets: [Bucket(1, "uploads", BucketEntry("S3_SECRET_KEY", "cipher:xyz", isSecret: true))]);

        result.Should().ContainSingle(e => e.Key == "S3_SECRET_KEY")
            .Which.Should().BeEquivalentTo(new { Value = "cipher:xyz", IsSecret = true });
    }

    [Fact]
    public void No_attached_buckets_at_all_does_not_error_and_adds_nothing()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("ONLY_MINE", "yes")],
            attachedGroups: []);

        result.Should().ContainSingle(e => e.Key == "ONLY_MINE");
    }
}
