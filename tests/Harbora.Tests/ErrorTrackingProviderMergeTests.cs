using FluentAssertions;
using Harbora.Domain.Apps;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="ConfigGroupMerge"/>'s error-tracking half (1.8, 2026-09 market-gaps round two) — the
/// exact code <c>DeploymentPipeline.BuildEnv</c> calls to fold an attached Sentry/GlitchTip DSN in,
/// one precedence step below every config group, mirroring <see cref="EmailProviderMergeTests"/>'s
/// structure for the SMTP half F6 added the same way. <see cref="ErrorTrackingProviderPipelineTests"/>
/// proves the same precedence survives to what a fake container actually receives.
/// </summary>
public class ErrorTrackingProviderMergeTests
{
    private static EnvironmentVariable OwnVar(string key, string value, bool isSecret = false) =>
        new() { Key = key, Value = value, IsSecret = isSecret };

    private static ErrorTrackingEnvEntry ProviderEntry(string key, string value, bool isSecret = false) =>
        new(key, value, isSecret);

    private static AttachedErrorTrackingEnv Provider(int order, string name, params ErrorTrackingEnvEntry[] entries) =>
        new(order, Guid.NewGuid(), name, entries);

    [Fact]
    public void A_key_only_a_provider_defines_reaches_the_effective_set()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedErrorTracking: [Provider(1, "glitchtip", ProviderEntry("SENTRY_DSN", "https://key@glitchtip.example/1"))]);

        result.Should().ContainSingle(e => e.Key == "SENTRY_DSN" && e.Value == "https://key@glitchtip.example/1");
    }

    [Fact]
    public void The_apps_own_sentry_dsn_wins_over_an_attached_provider_defining_the_same_key()
    {
        // The explicit requirement this sub-project turns on: a customer must be able to point an app
        // at their own external Sentry or GlitchTip instead of a Harbora-managed one — a plain
        // SENTRY_DSN they already set. ConfigGroupMerge's existing "the app's own row always wins"
        // rule already covers it; this pins that error tracking is not an exception.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("SENTRY_DSN", "https://own-key@sentry.example.com/42")],
            attachedGroups: [],
            attachedErrorTracking: [Provider(1, "glitchtip", ProviderEntry("SENTRY_DSN", "https://key@glitchtip.example/1"))]);

        result.Should().ContainSingle(e => e.Key == "SENTRY_DSN")
            .Which.Should().BeEquivalentTo(new
            {
                Value = "https://own-key@sentry.example.com/42",
                Source = ConfigSource.App
            });
    }

    [Fact]
    public void A_config_group_wins_over_a_provider_defining_the_same_key()
    {
        // A provider exists to hand an app default credentials, not to override a value somebody
        // deliberately set through a group — the same reasoning ConfigGroupMerge already applies to
        // buckets and email providers.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [new AttachedGroupEntries(1, Guid.NewGuid(), "overrides",
                [new ConfigGroupEntry { Key = "SENTRY_DSN", Value = "https://group-key@sentry.example.com/1" }])],
            attachedErrorTracking: [Provider(99, "glitchtip", ProviderEntry("SENTRY_DSN", "https://key@glitchtip.example/1"))]);

        result.Should().ContainSingle(e => e.Key == "SENTRY_DSN")
            .Which.Should().BeEquivalentTo(new { Value = "https://group-key@sentry.example.com/1", Source = ConfigSource.Group },
                "a group must outrank an error-tracking provider regardless of either one's AttachOrder");
    }

    [Fact]
    public void Between_two_providers_the_one_attached_later_wins()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedErrorTracking:
            [
                Provider(1, "first", ProviderEntry("SENTRY_DSN", "https://key@first.example/1")),
                Provider(2, "second", ProviderEntry("SENTRY_DSN", "https://key@second.example/1"))
            ]);

        var entry = result.Should().ContainSingle(e => e.Key == "SENTRY_DSN").Which;
        entry.Value.Should().Be("https://key@second.example/1", "the provider attached later (higher AttachOrder) outranks the earlier one");
        entry.Source.Should().Be(ConfigSource.ErrorTracking);
        entry.SourceErrorTrackingName.Should().Be("second");
    }

    [Fact]
    public void Every_provider_row_carries_where_it_came_from()
    {
        var providerId = Guid.NewGuid();
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedErrorTracking: [new AttachedErrorTrackingEnv(1, providerId, "GlitchTip",
                [ProviderEntry("SENTRY_DSN", "https://key@glitchtip.example/1")])]);

        result.Should().ContainSingle(e => e.Key == "SENTRY_DSN")
            .Which.Should().BeEquivalentTo(new
            {
                Source = ConfigSource.ErrorTracking,
                SourceErrorTrackingId = (Guid?)providerId, SourceErrorTrackingName = "GlitchTip",
                SourceBucketId = (Guid?)null, SourceBucketName = (string?)null,
                SourceEmailProviderId = (Guid?)null, SourceEmailProviderName = (string?)null,
                SourceDatabaseId = (Guid?)null, SourceDatabaseName = (string?)null,
                SourceGroupId = (Guid?)null, SourceGroupName = (string?)null
            });
    }

    [Fact]
    public void The_providers_dsn_keeps_its_secret_flag_and_its_raw_ciphertext_value_through_the_merge()
    {
        // Merge never decrypts, for a provider's DSN exactly as for a bucket's or an email provider's
        // secret — ErrorTrackingEnvKeys.EntriesFor is documented to pass ciphertext through unchanged
        // for the same reason.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedErrorTracking: [Provider(1, "glitchtip", ProviderEntry("SENTRY_DSN", "cipher:xyz", isSecret: true))]);

        result.Should().ContainSingle(e => e.Key == "SENTRY_DSN")
            .Which.Should().BeEquivalentTo(new { Value = "cipher:xyz", IsSecret = true });
    }

    [Fact]
    public void A_bucket_and_an_error_tracking_provider_coexist_without_either_shadowing_the_other()
    {
        // Different key namespaces (S3_* vs SENTRY_DSN), so the order between the loops in Merge must
        // not matter — this pins that it does not.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedBuckets: [new AttachedBucketEnv(1, Guid.NewGuid(), "uploads",
                [new BucketEnvEntry("S3_BUCKET", "uploads", false)])],
            attachedErrorTracking: [Provider(1, "glitchtip", ProviderEntry("SENTRY_DSN", "https://key@glitchtip.example/1"))]);

        result.Should().Contain(e => e.Key == "S3_BUCKET" && e.Source == ConfigSource.Bucket);
        result.Should().Contain(e => e.Key == "SENTRY_DSN" && e.Source == ConfigSource.ErrorTracking);
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
