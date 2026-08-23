using FluentAssertions;
using Harbora.Domain.Apps;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="ConfigGroupMerge"/>'s database half (C1, 2026-08-22 config-delivery plan) — the exact
/// code <c>DeploymentPipeline.BuildEnv</c> calls to fold an attached database's connection string in,
/// one precedence step below every config group, the same tier <see cref="StorageBucketMergeTests"/>
/// already proves for buckets. Mirrors that file's structure. <see cref="AppManagedServicePipelineTests"/>
/// proves the same precedence survives to what a fake container actually receives.
/// </summary>
public class AppManagedServiceMergeTests
{
    private static EnvironmentVariable OwnVar(string key, string value, bool isSecret = false) =>
        new() { Key = key, Value = value, IsSecret = isSecret };

    private static DatabaseEnvEntry DbEntry(string key, string value, bool isSecret = false) =>
        new(key, value, isSecret);

    private static AttachedDatabaseEnv Database(int order, string name, params DatabaseEnvEntry[] entries) =>
        new(order, Guid.NewGuid(), name, entries);

    [Fact]
    public void A_key_only_a_database_defines_reaches_the_effective_set()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedDatabases: [Database(1, "orders", DbEntry("DATABASE_URL", "postgres://orders"))]);

        result.Should().ContainSingle(e => e.Key == "DATABASE_URL" && e.Value == "postgres://orders");
    }

    [Fact]
    public void The_apps_own_variable_wins_over_a_database_defining_the_same_key()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("DATABASE_URL", "hand-picked")],
            attachedGroups: [],
            attachedDatabases: [Database(1, "orders", DbEntry("DATABASE_URL", "postgres://orders"))]);

        result.Should().ContainSingle(e => e.Key == "DATABASE_URL")
            .Which.Should().BeEquivalentTo(new { Value = "hand-picked", Source = ConfigSource.App });
    }

    [Fact]
    public void A_config_group_wins_over_a_database_defining_the_same_key()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [new AttachedGroupEntries(1, Guid.NewGuid(), "overrides",
                [new ConfigGroupEntry { Key = "DATABASE_URL", Value = "postgres://from-group" }])],
            attachedDatabases: [Database(99, "orders", DbEntry("DATABASE_URL", "postgres://from-database"))]);

        result.Should().ContainSingle(e => e.Key == "DATABASE_URL")
            .Which.Should().BeEquivalentTo(new { Value = "postgres://from-group", Source = ConfigSource.Group },
                "a group must outrank a database regardless of either one's AttachOrder");
    }

    [Fact]
    public void Between_two_databases_sharing_the_magic_name_the_one_attached_later_wins()
    {
        // The magic/unprefixed name (DATABASE_URL, PGHOST, …) is the one a zero-config app reads —
        // exactly the "second one wins" rule StorageBucketMergeTests proves for buckets. The
        // alias-prefixed names never reach this situation because AppManagedServiceAlias.Resolve
        // already made them unique before either attachment was created.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedDatabases:
            [
                Database(1, "orders", DbEntry("DATABASE_URL", "postgres://orders")),
                Database(2, "customers", DbEntry("DATABASE_URL", "postgres://customers"))
            ]);

        var entry = result.Should().ContainSingle(e => e.Key == "DATABASE_URL").Which;
        entry.Value.Should().Be("postgres://customers", "the database attached later (higher AttachOrder) outranks the earlier one");
        entry.Source.Should().Be(ConfigSource.Database);
        entry.SourceDatabaseName.Should().Be("customers");
    }

    [Fact]
    public void Every_database_row_carries_where_it_came_from()
    {
        var databaseId = Guid.NewGuid();
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedDatabases: [new AttachedDatabaseEnv(1, databaseId, "Orders",
                [DbEntry("PGHOST", "harbora-svc-orders")])]);

        result.Should().ContainSingle(e => e.Key == "PGHOST")
            .Which.Should().BeEquivalentTo(new
            {
                Source = ConfigSource.Database, SourceDatabaseId = (Guid?)databaseId, SourceDatabaseName = "Orders",
                SourceGroupId = (Guid?)null, SourceGroupName = (string?)null,
                SourceBucketId = (Guid?)null, SourceEmailProviderId = (Guid?)null
            });
    }

    [Fact]
    public void The_databases_secret_entry_keeps_its_flag_and_its_raw_ciphertext_value_through_the_merge()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedDatabases: [Database(1, "orders", DbEntry("PGPASSWORD", "cipher:xyz", isSecret: true))]);

        result.Should().ContainSingle(e => e.Key == "PGPASSWORD")
            .Which.Should().BeEquivalentTo(new { Value = "cipher:xyz", IsSecret = true });
    }

    [Fact]
    public void Two_databases_alias_prefixed_names_both_survive_side_by_side()
    {
        // The concrete proof of the "make collisions impossible" requirement: two databases, each
        // contributing both its magic name and its alias-prefixed name, end up with FOUR distinct
        // keys in the effective set rather than one overwriting the other.
        var result = ConfigGroupMerge.Merge(
            ownVariables: [],
            attachedGroups: [],
            attachedDatabases:
            [
                Database(1, "orders", DbEntry("DATABASE_URL", "postgres://orders"), DbEntry("ORDERS_DATABASE_URL", "postgres://orders")),
                Database(2, "customers", DbEntry("DATABASE_URL", "postgres://customers"), DbEntry("CUSTOMERS_DATABASE_URL", "postgres://customers"))
            ]);

        result.Should().Contain(e => e.Key == "ORDERS_DATABASE_URL" && e.Value == "postgres://orders");
        result.Should().Contain(e => e.Key == "CUSTOMERS_DATABASE_URL" && e.Value == "postgres://customers");
        result.Should().Contain(e => e.Key == "DATABASE_URL" && e.Value == "postgres://customers");
    }

    [Fact]
    public void No_attached_databases_at_all_does_not_error_and_adds_nothing()
    {
        var result = ConfigGroupMerge.Merge(
            ownVariables: [OwnVar("ONLY_MINE", "yes")],
            attachedGroups: []);

        result.Should().ContainSingle(e => e.Key == "ONLY_MINE");
    }
}
