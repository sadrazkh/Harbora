using FluentAssertions;
using Harbora.Domain.Apps;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="ConfigGroupMerge"/>'s email-provider half (F6, 2026-08-21 functions-and-services
/// plan) — the exact code <c>DeploymentPipeline.BuildEnv</c> calls to fold an attached SMTP
/// provider's env in, one precedence step below every config group, mirroring
/// <see cref="StorageBucketMergeTests"/>'s structure for the bucket half added by F5 the same day.
/// <c>EmailProviderPipelineTests</c> proves the same precedence survives to what a fake container
/// actually receives.
/// </summary>
public class EmailProviderMergeTests
{
    private static EnvironmentVariable OwnVar(string key, string value, bool isSecret = false) =>
        new() { Key = key, Value = value, IsSecret = isSecret };

    private static EmailProviderEnvEntry ProviderEntry(string key, string value, bool isSecret = false) =>
        new(key, value, isSecret);

    private static AttachedEmailProviderEnv Provider(int order, string name, params EmailProviderEnvEntry[] entries) =>
        new(order, Guid.NewGuid(), name, entries);

    [Fact]
    public void A_key_only_a_provider_defines_reaches_the_effective_set()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedEmailProviders: [Provider(1, "sendgrid", ProviderEntry("SMTP_HOST", "smtp.sendgrid.net"))]);

        result.Should().ContainSingle(e => e.Key == "SMTP_HOST" && e.Value == "smtp.sendgrid.net");
    }

    [Fact]
    public void The_apps_own_variable_wins_over_a_provider_defining_the_same_key()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("SMTP_HOST", "hand-picked.example")],
            attachedGroups: [],
            attachedEmailProviders: [Provider(1, "sendgrid", ProviderEntry("SMTP_HOST", "smtp.sendgrid.net"))]);

        result.Should().ContainSingle(e => e.Key == "SMTP_HOST")
            .Which.Should().BeEquivalentTo(new { Value = "hand-picked.example", Source = ConfigSource.App });
    }

    [Fact]
    public void A_config_group_wins_over_a_provider_defining_the_same_key()
    {
        // A provider exists to hand an app default credentials, not to override a value somebody
        // deliberately set through a group — the same reasoning ConfigGroupMerge already applies to
        // buckets.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [new AttachedGroupEntries(1, Guid.NewGuid(), "overrides",
                [new ConfigGroupEntry { Key = "SMTP_FROM", Value = "from-group@example.com" }])],
            attachedEmailProviders: [Provider(99, "sendgrid", ProviderEntry("SMTP_FROM", "from-provider@example.com"))]);

        result.Should().ContainSingle(e => e.Key == "SMTP_FROM")
            .Which.Should().BeEquivalentTo(new { Value = "from-group@example.com", Source = ConfigSource.Group },
                "a group must outrank a provider regardless of either one's AttachOrder");
    }

    [Fact]
    public void Between_two_providers_the_one_attached_later_wins()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedEmailProviders:
            [
                Provider(1, "first", ProviderEntry("SMTP_HOST", "first.example")),
                Provider(2, "second", ProviderEntry("SMTP_HOST", "second.example"))
            ]);

        var entry = result.Should().ContainSingle(e => e.Key == "SMTP_HOST").Which;
        entry.Value.Should().Be("second.example", "the provider attached later (higher AttachOrder) outranks the earlier one");
        entry.Source.Should().Be(ConfigSource.EmailProvider);
        entry.SourceEmailProviderName.Should().Be("second");
    }

    [Fact]
    public void Every_provider_row_carries_where_it_came_from()
    {
        var providerId = Guid.NewGuid();
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedEmailProviders: [new AttachedEmailProviderEnv(1, providerId, "SendGrid",
                [ProviderEntry("SMTP_USER", "apikey")])]);

        result.Should().ContainSingle(e => e.Key == "SMTP_USER")
            .Which.Should().BeEquivalentTo(new
            {
                Source = ConfigSource.EmailProvider,
                SourceEmailProviderId = (Guid?)providerId, SourceEmailProviderName = "SendGrid",
                SourceBucketId = (Guid?)null, SourceBucketName = (string?)null,
                SourceGroupId = (Guid?)null, SourceGroupName = (string?)null
            });
    }

    [Fact]
    public void The_providers_secret_entry_keeps_its_flag_and_its_raw_ciphertext_value_through_the_merge()
    {
        // Merge never decrypts, for a provider's secret exactly as for a bucket's or a group's —
        // EmailProviderEnvKeys.EntriesFor is documented to pass ciphertext through unchanged for the
        // same reason.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedEmailProviders: [Provider(1, "sendgrid", ProviderEntry("SMTP_PASSWORD", "cipher:xyz", isSecret: true))]);

        result.Should().ContainSingle(e => e.Key == "SMTP_PASSWORD")
            .Which.Should().BeEquivalentTo(new { Value = "cipher:xyz", IsSecret = true });
    }

    [Fact]
    public void A_bucket_and_a_provider_coexist_without_either_shadowing_the_other()
    {
        // Different key namespaces (S3_* vs SMTP_*), so the order between the two loops in Merge
        // must not matter — this pins that it does not.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedBuckets: [new AttachedBucketEnv(1, Guid.NewGuid(), "uploads",
                [new BucketEnvEntry("S3_BUCKET", "uploads", false)])],
            attachedEmailProviders: [Provider(1, "sendgrid", ProviderEntry("SMTP_HOST", "smtp.sendgrid.net"))]);

        result.Should().Contain(e => e.Key == "S3_BUCKET" && e.Source == ConfigSource.Bucket);
        result.Should().Contain(e => e.Key == "SMTP_HOST" && e.Source == ConfigSource.EmailProvider);
    }

    [Fact]
    public void No_attached_providers_at_all_does_not_error_and_adds_nothing()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("ONLY_MINE", "yes")],
            attachedGroups: []);

        result.Should().ContainSingle(e => e.Key == "ONLY_MINE");
    }
}
