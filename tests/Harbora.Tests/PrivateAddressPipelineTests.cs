using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The alias reaches the container, and an ambiguous one does not.
///
/// Asserted against the recorded <c>DockerRunRequest.NetworkAliases</c> specifically, never against
/// the request as a whole: the container name is <c>harbora-{slug}-{number}</c> and contains the slug
/// too, so "the request mentions shop" is true whether or not the alias was ever passed.
/// </summary>
public class PrivateAddressPipelineTests
{
    /// <summary>The aliases the just-started container was given, or an empty list.</summary>
    private static IReadOnlyList<string> AliasesOf(PipelineHarness harness, string containerName) =>
        harness.Docker.RunRequests.Single(r => r.ContainerName == containerName).NetworkAliases ?? [];

    [Fact]
    public async Task A_deployed_app_answers_to_its_slug()
    {
        using var harness = new PipelineHarness();
        var deployment = harness.QueueDeployment();

        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number))
            .Should().ContainSingle().Which.Should().Be(harness.App.Slug,
                "the compose path has always registered its service names; the ordinary path never " +
                "did, so an app was reachable only at a name carrying the deployment number");
    }

    /// <summary>
    /// Corrected from the brief's original assertion, which expected a run request carrying no
    /// alias. <c>ServiceKind.ReleaseTask</c> is also excluded from <c>ServicePlan.IsLongRunning</c>,
    /// so the pipeline takes the early-return branch at DeploymentPipeline.cs:302 and never calls
    /// <c>docker.RunContainerAsync</c> at all for this kind — there is no run request to inspect.
    /// <c>PrivateAddress.Decide</c>'s own refusal for this kind (<c>KindDoesNotJoin</c>) is already
    /// covered directly in PrivateAddressTests.cs; what the pipeline can actually promise is that a
    /// one-shot kind never reaches the alias machinery in the first place.
    /// </summary>
    [Fact]
    public async Task A_release_task_never_starts_a_container_so_there_is_nothing_to_register()
    {
        using var harness = new PipelineHarness();
        harness.App.Kind = ServiceKind.ReleaseTask;
        await harness.Db.SaveChangesAsync();

        var deployment = harness.QueueDeployment();
        await harness.RunAsync(deployment);

        harness.Docker.RunRequests.Should().BeEmpty(
            "a release task runs once and exits — ServicePlan.IsLongRunning excludes it, so this " +
            "deployment never reaches the container-run call the alias would have ridden along with");
    }

    [Fact]
    public async Task A_slug_a_neighbours_compose_service_already_answers_to_is_not_registered()
    {
        using var harness = new PipelineHarness();
        SeedSiblingRunning(harness, "sibling", harness.Environment.Id, composeService: harness.App.Slug);

        var deployment = harness.QueueDeployment();
        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number)).Should().BeEmpty(
            "docker balances between every container holding a name, so registering this one would " +
            "send a share of the calls to a stranger's service");

        var app = await harness.Db.Apps.FindAsync(harness.App.Id);
        app!.PrivateAddressState.Should().Be(PrivateAddressOutcome.Ambiguous,
            "the page has to be able to say why there is no address, rather than showing a blank");
    }

    [Fact]
    public async Task A_name_clash_does_not_fail_the_deployment()
    {
        using var harness = new PipelineHarness();
        SeedSiblingRunning(harness, "sibling", harness.Environment.Id, composeService: harness.App.Slug);

        var deployment = harness.QueueDeployment();
        var result = await harness.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded,
            "a convenience must never cost somebody a release — this is the assertion that matters most");
    }

    [Fact]
    public async Task A_compose_service_outside_this_environment_does_not_block_the_name()
    {
        using var harness = new PipelineHarness();
        // No EnvironmentId: on another network entirely, so it cannot collide. The collision query
        // filters on environment for exactly this reason.
        SeedSiblingRunning(harness, "stranger", environmentId: null, composeService: harness.App.Slug);

        var deployment = harness.QueueDeployment();
        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number))
            .Should().ContainSingle().Which.Should().Be(harness.App.Slug);
    }

    /// <summary>Another app in the workspace, with a running compose-stack container of its own.</summary>
    private static void SeedSiblingRunning(
        PipelineHarness harness, string slug, Guid? environmentId, string composeService)
    {
        harness.Db.Apps.Add(new App
        {
            WorkspaceId = harness.Workspace.Id,
            ServerId = harness.Server.Id,
            EnvironmentId = environmentId,
            Name = slug,
            Slug = slug,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = $"ghcr.io/example/{slug}:1.0"
        });
        harness.Db.SaveChanges();
        harness.Docker.SeedContainer($"harbora-{slug}-1-svc", slug, composeService: composeService);
    }
}
