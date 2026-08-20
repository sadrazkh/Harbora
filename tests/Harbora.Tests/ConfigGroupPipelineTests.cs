using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 9 (2026-08-20 platform-options plan): the plan itself warns that a merge proven right
/// in isolation is worse than no merge if the pipeline wires it up wrong — so these run the real
/// <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/> over the fake Docker engine
/// and assert on the <c>Env</c> dictionary the container actually received
/// (<see cref="FakeDockerEngine.RunRequests"/>), not on a helper's return value.
/// <see cref="ConfigGroupMergeTests"/> covers the precedence rules themselves at the same seam.
/// </summary>
public class ConfigGroupPipelineTests
{
    private static ConfigGroup GivenGroup(PipelineHarness h, string name, params (string Key, string Value, bool Secret)[] entries)
    {
        var group = new ConfigGroup { WorkspaceId = h.Workspace.Id, Name = name };
        h.Db.ConfigGroups.Add(group);
        foreach (var (key, value, secret) in entries)
            h.Db.ConfigGroupEntries.Add(new ConfigGroupEntry
            {
                ConfigGroupId = group.Id, Key = key,
                Value = secret ? h.Protector.Protect(value) : value, IsSecret = secret
            });
        h.Db.SaveChanges();
        return group;
    }

    private static AppConfigGroup Attach(PipelineHarness h, ConfigGroup group, int order, bool unpublished = true)
    {
        var join = new AppConfigGroup
        {
            AppId = h.App.Id, ConfigGroupId = group.Id, AttachOrder = order, HasUnpublishedChanges = unpublished
        };
        h.Db.AppConfigGroups.Add(join);
        h.Db.SaveChanges();
        return join;
    }

    [Fact]
    public async Task A_group_only_key_reaches_the_actual_container_environment()
    {
        using var h = new PipelineHarness();
        var group = GivenGroup(h, "shared", ("API_BASE", "https://api.example.com", false));
        Attach(h, group, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("API_BASE").WhoseValue.Should().Be("https://api.example.com");
    }

    [Fact]
    public async Task The_apps_own_variable_reaches_the_container_over_a_group_defining_the_same_key()
    {
        using var h = new PipelineHarness();
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable { AppId = h.App.Id, Key = "PORT", Value = "9000" });
        h.Db.SaveChanges();
        var group = GivenGroup(h, "shared", ("PORT", "8080", false));
        Attach(h, group, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["PORT"].Should().Be("9000", "the app's own variable must win over any group in the actual run request");
    }

    [Fact]
    public async Task Between_two_attached_groups_the_container_gets_the_later_ones_value()
    {
        using var h = new PipelineHarness();
        var first = GivenGroup(h, "first", ("LOG_LEVEL", "info", false));
        var second = GivenGroup(h, "second", ("LOG_LEVEL", "debug", false));
        Attach(h, first, order: 1);
        Attach(h, second, order: 2);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["LOG_LEVEL"].Should().Be("debug");
    }

    [Fact]
    public async Task A_secret_group_entry_reaches_the_container_decrypted_the_same_way_a_secret_env_var_does()
    {
        using var h = new PipelineHarness();
        var group = GivenGroup(h, "shared", ("DB_PASSWORD", "s3cret", true));
        Attach(h, group, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["DB_PASSWORD"].Should().Be("s3cret", "the container needs the plaintext, exactly like a secret EnvironmentVariable");
    }

    [Fact]
    public async Task A_successful_deploy_clears_the_unpublished_flag_on_every_attached_group()
    {
        using var h = new PipelineHarness();
        var group = GivenGroup(h, "shared", ("KEY", "value", false));
        var join = Attach(h, group, order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var stored = await h.Db.AppConfigGroups.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeFalse(
            "this deployment's container was built from the group's current entries, so it is applied");
    }

    [Fact]
    public async Task A_failed_deployment_leaves_the_unpublished_flag_set()
    {
        using var h = new PipelineHarness().WithDomain().WithHealthPath();
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;
        var group = GivenGroup(h, "shared", ("KEY", "value", false));
        var join = Attach(h, group, order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        var stored = await h.Db.AppConfigGroups.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeTrue(
            "nothing actually shipped with this group's entries, so the stale flag must not be cleared");
    }

    [Fact]
    public async Task Rolling_back_still_applies_current_group_entries_because_env_is_never_baked_into_the_image()
    {
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        var group = GivenGroup(h, "shared", ("KEY", "v1", false));
        var join = Attach(h, group, order: 1, unpublished: false);

        // The group changes after v1 shipped — same as editing it for real.
        var entry = await h.Db.ConfigGroupEntries.FirstAsync(e => e.ConfigGroupId == group.Id);
        entry.Value = "v2";
        join.HasUnpublishedChanges = true;
        h.Db.SaveChanges();

        var rollback = h.QueueDeployment(number: 2, rollbackTo: h.App.ActiveDeploymentId);
        var result = await h.RunAsync(rollback);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["KEY"].Should().Be("v2",
            "unlike function code, env is assembled fresh at run time regardless of which image is running");

        var stored = await h.Db.AppConfigGroups.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeFalse("the rollback's container really was built with v2's value");
    }
}
