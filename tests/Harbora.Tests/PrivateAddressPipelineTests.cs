using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Deployments;
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
    /// It still has to prove the deployment actually reached that branch rather than failing before
    /// it — an empty <c>RunRequests</c> list looks the same either way — and that the branch records
    /// the outcome on the app, since that is the field the page reads and nothing else sets it for a
    /// one-shot kind.
    /// </summary>
    [Fact]
    public async Task A_release_task_never_starts_a_container_and_records_that_its_kind_does_not_join()
    {
        using var harness = new PipelineHarness();
        harness.App.Kind = ServiceKind.ReleaseTask;
        await harness.Db.SaveChangesAsync();

        var deployment = harness.QueueDeployment();
        var result = await harness.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded,
            "an empty RunRequests list also describes a deployment that failed before reaching the " +
            "run call — this is the assertion that tells the two apart");
        harness.Docker.RunRequests.Should().BeEmpty(
            "a release task runs once and exits — ServicePlan.IsLongRunning excludes it, so this " +
            "deployment never reaches the container-run call the alias would have ridden along with");

        var app = await harness.Db.Apps.FindAsync(harness.App.Id);
        app!.PrivateAddressState.Should().Be(PrivateAddressOutcome.KindDoesNotJoin,
            "the early-return branch has to set this itself — PrivateAddress.Decide is never called " +
            "for a kind that takes this branch, so nothing else would record it, and the page would " +
            "otherwise say \"a name will be assigned on the next deploy\" for ever");
    }

    /// <summary>
    /// The other kind that takes the same early-return branch, for the same reason: neither starts a
    /// long-lived container, so neither can answer to a private name.
    /// </summary>
    [Fact]
    public async Task A_scheduled_jobs_deploy_also_records_that_its_kind_does_not_join()
    {
        using var harness = new PipelineHarness();
        harness.App.Kind = ServiceKind.Cron;
        harness.App.CronExpression = "0 3 * * *";
        await harness.Db.SaveChangesAsync();

        var deployment = harness.QueueDeployment();
        var result = await harness.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);

        var app = await harness.Db.Apps.FindAsync(harness.App.Id);
        app!.PrivateAddressState.Should().Be(PrivateAddressOutcome.KindDoesNotJoin,
            "a scheduled job's container exits by design and nothing is left running between ticks " +
            "to answer to a name");
    }

    /// <summary>
    /// The compose path has registered its own per-service aliases since before this branch existed
    /// (spec's first testing bullet); this pins that it still does, so a later change that folds the
    /// two alias paths into one cannot quietly collapse them into the app-slug alias. It also pins
    /// what the app-level state says: not a name that "will be assigned on the next deploy" — that is
    /// a false promise for a kind of app that will never get a single app-level alias at all.
    /// </summary>
    [Fact]
    public async Task A_compose_stacks_own_service_aliases_are_unchanged_by_the_apps_new_alias()
    {
        using var harness = new PipelineHarness();
        harness.WithComposeFile("""
            services:
              web:
                image: nginx:alpine
                ports:
                  - "8080:80"
            """);

        var deployment = harness.QueueDeployment();
        var result = await harness.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);

        var containerName = DeploymentPlanning.ComposeContainerName(harness.App.Slug, "web", deployment.Number);
        AliasesOf(harness, containerName).Should().Equal(["web", $"web-{deployment.Number}"],
            "StartComposeStackAsync has always registered exactly these two; a change that reused " +
            "the ordinary path's single-alias logic here would break every stack silently");

        var app = await harness.Db.Apps.FindAsync(harness.App.Id);
        app!.PrivateAddressState.Should().Be(PrivateAddressOutcome.ComposeManaged,
            "each service already carries its own name — the app's own slug may not even match any " +
            "of them, so this is not the same claim Registered makes");
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

    /// <summary>
    /// A real sibling sits in this environment too, so <c>siblingIds.Count == 0</c> never short-
    /// circuits the query before Docker is ever consulted — without that, this test would still pass
    /// with the environment filter deleted outright, because there would be nothing to find either
    /// way. The stranger below is what actually exercises the filter.
    /// </summary>
    [Fact]
    public async Task A_compose_service_outside_this_environment_does_not_block_the_name()
    {
        using var harness = new PipelineHarness();
        SeedSiblingRunning(harness, "sibling", harness.Environment.Id, composeService: "sibling-svc");
        // No EnvironmentId: on another network entirely, so it cannot collide. The collision query
        // filters on environment for exactly this reason.
        SeedSiblingRunning(harness, "stranger", environmentId: null, composeService: harness.App.Slug);

        var deployment = harness.QueueDeployment();
        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number))
            .Should().ContainSingle().Which.Should().Be(harness.App.Slug,
                "a real sibling in the environment proves Docker really was asked; the stranger " +
                "outside it must still not be able to withhold the name");
    }

    /// <summary>
    /// <c>App.Slug</c> is unique only per workspace (<c>HasIndex(WorkspaceId, Slug).IsUnique()</c>),
    /// but containers are listed host-wide. So a same-slugged app in an unrelated workspace, whose
    /// compose stack happens to run a service named after THIS app's slug, must not be mistaken for
    /// the legitimate sibling that shares the collision-triggering slug in this environment.
    /// </summary>
    [Fact]
    public async Task A_same_slugged_app_in_a_different_workspace_does_not_block_the_name()
    {
        using var harness = new PipelineHarness();

        // The real sibling: this workspace's own "api" app, in this app's environment. Its slug is
        // what the pre-fix code matched containers by — the bug this test pins is that a container
        // is not "owned" by every app that happens to share that string.
        var siblingHere = new App
        {
            WorkspaceId = harness.Workspace.Id, ServerId = harness.Server.Id,
            EnvironmentId = harness.Environment.Id, Name = "api", Slug = "api",
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/api:1.0"
        };
        harness.Db.Apps.Add(siblingHere);
        harness.Db.SaveChanges();

        // A different app entirely — different workspace, no relation to this environment — that is
        // legally allowed to be slugged "api" too. Its compose stack runs a service named after this
        // app's own slug, which is the actual collision string the old, slug-only match would have
        // reached through siblingHere to find.
        var strangerWorkspaceApp = new App
        {
            WorkspaceId = Guid.NewGuid(), ServerId = harness.Server.Id,
            EnvironmentId = null, Name = "api", Slug = "api",
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/api:1.0"
        };
        harness.Db.Apps.Add(strangerWorkspaceApp);
        harness.Db.SaveChanges();
        harness.Docker.SeedContainer("harbora-api-1-svc", "api",
            composeService: harness.App.Slug, appId: strangerWorkspaceApp.Id);

        var deployment = harness.QueueDeployment();
        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number))
            .Should().ContainSingle().Which.Should().Be(harness.App.Slug,
                "the stranger's container answers to the label \"api\", which happens to equal a " +
                "real sibling's slug — but it is a different app in a different workspace, and " +
                "matching by id rather than slug must not let it cost this app its name");
    }

    /// <summary>
    /// <c>ListContainersAsync</c> lists every container regardless of state, and Stop leaves a
    /// container behind rather than removing it — so an app a customer stopped months ago can still
    /// be listed. A stopped container answers no DNS query, so it must not be able to withhold a
    /// name from a live app for ever.
    /// </summary>
    [Fact]
    public async Task An_exited_sibling_container_does_not_block_the_name()
    {
        using var harness = new PipelineHarness();
        SeedSiblingRunning(harness, "sibling", harness.Environment.Id, composeService: harness.App.Slug,
            state: "exited");

        var deployment = harness.QueueDeployment();
        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number))
            .Should().ContainSingle().Which.Should().Be(harness.App.Slug,
                "nothing actually answers to the name of a container that is not running");
    }

    /// <summary>Another app in the workspace, with a compose-stack container of its own.</summary>
    private static App SeedSiblingRunning(
        PipelineHarness harness, string slug, Guid? environmentId, string composeService,
        string state = "running")
    {
        var sibling = new App
        {
            WorkspaceId = harness.Workspace.Id,
            ServerId = harness.Server.Id,
            EnvironmentId = environmentId,
            Name = slug,
            Slug = slug,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = $"ghcr.io/example/{slug}:1.0"
        };
        harness.Db.Apps.Add(sibling);
        harness.Db.SaveChanges();
        harness.Docker.SeedContainer($"harbora-{slug}-1-svc", slug, state: state,
            composeService: composeService, appId: sibling.Id);
        return sibling;
    }
}
