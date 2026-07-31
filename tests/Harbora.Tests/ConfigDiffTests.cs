using System.Security.Cryptography;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Answering "it worked yesterday, what changed?".
///
/// A deployment recorded its commit and its image and nothing about how the app was configured, so
/// the most common question after a bad release had no answer anywhere in the platform: someone
/// edits a variable, redeploys, the app breaks, and the history shows two identical-looking rows.
/// </summary>
public class ConfigDiffTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static App AppWith(Action<App> configure)
    {
        var app = new App
        {
            Name = "shop", Slug = "shop",
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "nginx:1.27",
            ContainerPort = 8080, HealthCheckPath = "/healthz", InstanceSizeKey = "nano"
        };
        configure(app);
        return app;
    }

    private static DeploymentConfig Snapshot(App app) => DeploymentConfig.From(app, v => v.Value, Key);

    [Fact]
    public void A_changed_variable_is_named_with_both_values()
    {
        var before = Snapshot(AppWith(a => a.EnvironmentVariables.Add(new EnvironmentVariable { Key = "LOG_LEVEL", Value = "info" })));
        var after = Snapshot(AppWith(a => a.EnvironmentVariables.Add(new EnvironmentVariable { Key = "LOG_LEVEL", Value = "debug" })));

        ConfigDiff.Between(before, after).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConfigChange("Variable LOG_LEVEL", "info → debug"));
    }

    [Fact]
    public void A_changed_secret_is_reported_as_changed_and_nothing_more()
    {
        // Showing the old value to explain a break would put every rotated password permanently in
        // the deployment history — the sort of convenience that becomes an incident.
        var before = Snapshot(AppWith(a => a.EnvironmentVariables.Add(
            new EnvironmentVariable { Key = "DB_PASSWORD", Value = "hunter2", IsSecret = true })));
        var after = Snapshot(AppWith(a => a.EnvironmentVariables.Add(
            new EnvironmentVariable { Key = "DB_PASSWORD", Value = "correct-horse", IsSecret = true })));

        var change = ConfigDiff.Between(before, after).Should().ContainSingle().Subject;
        change.Detail.Should().Be("changed (secret)");
        change.Detail.Should().NotContain("hunter2").And.NotContain("correct-horse");
    }

    [Fact]
    public void A_secret_that_did_not_change_produces_no_noise()
    {
        // The guard on the fingerprint: if it were random per snapshot, every release would look
        // like every secret had been rotated.
        var app = AppWith(a => a.EnvironmentVariables.Add(
            new EnvironmentVariable { Key = "DB_PASSWORD", Value = "hunter2", IsSecret = true }));

        ConfigDiff.Between(Snapshot(app), Snapshot(app)).Should().BeEmpty();
    }

    [Fact]
    public void A_secrets_value_never_appears_in_the_snapshot_at_all()
    {
        // Not just in the diff: the snapshot itself is stored, so it must not carry the value.
        var app = AppWith(a => a.EnvironmentVariables.Add(
            new EnvironmentVariable { Key = "DB_PASSWORD", Value = "hunter2", IsSecret = true }));

        var json = Snapshot(app).ToJson();

        json.Should().NotContain("hunter2");
        json.Should().Contain("DB_PASSWORD", "the name is what makes the record useful");
    }

    [Fact]
    public void The_fingerprint_depends_on_the_platform_key()
    {
        // Otherwise it is a plain hash of a short secret, which is guessable by trying candidates.
        var app = AppWith(a => a.EnvironmentVariables.Add(
            new EnvironmentVariable { Key = "T", Value = "hunter2", IsSecret = true }));

        var one = DeploymentConfig.From(app, v => v.Value, RandomNumberGenerator.GetBytes(32));
        var two = DeploymentConfig.From(app, v => v.Value, RandomNumberGenerator.GetBytes(32));

        one.Variables[0].Fingerprint.Should().NotBe(two.Variables[0].Fingerprint);
    }

    [Fact]
    public void Added_and_removed_variables_are_both_reported()
    {
        var before = Snapshot(AppWith(a => a.EnvironmentVariables.Add(new EnvironmentVariable { Key = "OLD", Value = "1" })));
        var after = Snapshot(AppWith(a => a.EnvironmentVariables.Add(new EnvironmentVariable { Key = "NEW", Value = "2" })));

        var changes = ConfigDiff.Between(before, after);

        changes.Should().Contain(c => c.What == "Variable NEW" && c.Detail.Contains("added"));
        changes.Should().Contain(c => c.What == "Variable OLD" && c.Detail == "removed");
    }

    [Fact]
    public void A_variable_that_became_a_secret_is_worth_saying()
    {
        // Its value stops being visible from that release on, which explains a lot of later confusion.
        var before = Snapshot(AppWith(a => a.EnvironmentVariables.Add(new EnvironmentVariable { Key = "TOKEN", Value = "abc" })));
        var after = Snapshot(AppWith(a => a.EnvironmentVariables.Add(new EnvironmentVariable { Key = "TOKEN", Value = "abc", IsSecret = true })));

        ConfigDiff.Between(before, after).Should().ContainSingle()
            .Which.Detail.Should().Be("became a secret");
    }

    [Fact]
    public void Settings_outside_the_environment_are_compared_too()
    {
        var before = Snapshot(AppWith(a => { }));
        var after = Snapshot(AppWith(a => { a.ContainerPort = 3000; a.HealthCheckPath = "/up"; a.InstanceSizeKey = "small"; }));

        var changes = ConfigDiff.Between(before, after);

        changes.Should().Contain(c => c.What == "Port" && c.Detail == "8080 → 3000");
        changes.Should().Contain(c => c.What == "Health check path" && c.Detail == "/healthz → /up");
        changes.Should().Contain(c => c.What == "Instance size" && c.Detail == "nano → small");
    }

    [Fact]
    public void A_cleared_setting_says_what_it_used_to_be()
    {
        var before = Snapshot(AppWith(a => a.ReleaseCommand = "dotnet ef database update"));
        var after = Snapshot(AppWith(a => a.ReleaseCommand = null));

        ConfigDiff.Between(before, after).Should().ContainSingle()
            .Which.Detail.Should().Contain("cleared").And.Contain("dotnet ef database update");
    }

    [Fact]
    public void Blank_and_absent_are_the_same_thing()
    {
        // Treating them apart produces changes nobody made, which is how a diff stops being read.
        var before = Snapshot(AppWith(a => a.HealthCheckPath = null));
        var after = Snapshot(AppWith(a => a.HealthCheckPath = "   "));

        ConfigDiff.Between(before, after).Should().BeEmpty();
    }

    [Fact]
    public void Volumes_and_domains_are_tracked()
    {
        var before = Snapshot(AppWith(a => a.Volumes.Add(new Volume { Name = "v", MountPath = "/data" })));
        var after = Snapshot(AppWith(a => a.Domains.Add(new Harbora.Domain.Networking.DomainName { Host = "shop.example.com" })));

        var changes = ConfigDiff.Between(before, after);

        changes.Should().Contain(c => c.What == "Volume /data" && c.Detail == "removed");
        changes.Should().Contain(c => c.What == "Domain shop.example.com" && c.Detail == "added");
    }

    [Fact]
    public void Two_identical_releases_say_so_rather_than_showing_an_empty_list()
    {
        var app = AppWith(a => { });

        ConfigDiff.AreIdentical(Snapshot(app), Snapshot(app)).Should().BeTrue();
        ConfigDiff.Between(Snapshot(app), Snapshot(app)).Should().BeEmpty();
    }

    [Fact]
    public void A_deployment_from_before_snapshots_existed_compares_to_nothing()
    {
        // Most of the history predates this. Inventing differences against a missing record would be
        // worse than saying nothing.
        ConfigDiff.Between(null, Snapshot(AppWith(a => { }))).Should().BeEmpty();
        ConfigDiff.AreIdentical(null, Snapshot(AppWith(a => { }))).Should().BeFalse();
    }

    [Fact]
    public void A_stored_snapshot_reads_back_as_what_was_stored()
    {
        var app = AppWith(a =>
        {
            a.EnvironmentVariables.Add(new EnvironmentVariable { Key = "A", Value = "1" });
            a.EnvironmentVariables.Add(new EnvironmentVariable { Key = "B", Value = "s", IsSecret = true });
            a.Volumes.Add(new Volume { Name = "v", MountPath = "/data" });
        });

        var restored = DeploymentConfig.FromJson(Snapshot(app).ToJson());

        restored.Should().BeEquivalentTo(Snapshot(app));
    }

    [Fact]
    public void Unreadable_stored_json_is_treated_as_no_snapshot()
    {
        DeploymentConfig.FromJson("not json").Should().BeNull();
        DeploymentConfig.FromJson(null).Should().BeNull();
    }
}
