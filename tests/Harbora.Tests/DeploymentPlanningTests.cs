using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Pure planning-logic tests for zero-downtime cutover + artifact rollback (P4 / ADR-006/007):
/// versioned naming, which containers get retired, per-deployment host ports, and never rebuilding
/// on rollback.
///
/// Also covers the cross-tenant container-retirement fix (2026-08-15-unique-app-names-design): a
/// container is only "this workspace's" when it is labelled for it, or — for a legacy container that
/// predates the label — when no other workspace could possibly hold this slug.
/// </summary>
public class DeploymentPlanningTests
{
    private static readonly Guid WorkspaceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkspaceB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ContainerInfo C(string name, string slug, Guid? workspaceId = null, string state = "running")
    {
        var labels = new Dictionary<string, string> { ["harbora.app"] = slug };
        if (workspaceId is { } w) labels["harbora.workspace"] = w.ToString();
        return new ContainerInfo("id-" + name, name, "img", state, "Up", labels);
    }

    [Fact]
    public void Container_name_is_versioned_and_carries_the_workspace()
    {
        DeploymentPlanning.ContainerName(WorkspaceA, "blog", 42)
            .Should().Be($"harbora-{WorkspaceA:N}-blog-42");
        DeploymentPlanning.LegacyContainerName("blog").Should().Be("harbora-blog");
    }

    [Fact]
    public void Two_workspaces_with_the_same_slug_get_different_container_names()
    {
        // The name itself cannot collide, independent of the label match below — belt and suspenders
        // for an install where the platform-wide slug index could not apply.
        DeploymentPlanning.ContainerName(WorkspaceA, "api", 1)
            .Should().NotBe(DeploymentPlanning.ContainerName(WorkspaceB, "api", 1));
    }

    [Fact]
    public void ContainersToRetire_keeps_new_and_ignores_other_apps()
    {
        var keep = DeploymentPlanning.ContainerName(WorkspaceA, "blog", 3);
        var all = new[]
        {
            C(keep, "blog", WorkspaceA),                                                     // the new one — keep
            C(DeploymentPlanning.ContainerName(WorkspaceA, "blog", 2), "blog", WorkspaceA),   // old version — retire
            C(DeploymentPlanning.LegacyContainerName("blog"), "blog", workspaceId: null),     // legacy — retire
            C(DeploymentPlanning.ContainerName(WorkspaceA, "shop", 9), "shop", WorkspaceA),   // other app — ignore
        };

        var retire = DeploymentPlanning.ContainersToRetire(
            all, WorkspaceA, "blog", keep, slugExclusiveToThisWorkspace: true);

        retire.Should().HaveCount(2);
        retire.Should().Contain("id-" + DeploymentPlanning.ContainerName(WorkspaceA, "blog", 2));
        retire.Should().Contain("id-" + DeploymentPlanning.LegacyContainerName("blog"));
        retire.Should().NotContain("id-" + keep);
        retire.Should().NotContain("id-" + DeploymentPlanning.ContainerName(WorkspaceA, "shop", 9));
    }

    // ---- the defect this fix closes: cross-tenant retirement ----

    [Fact]
    public void A_workspaces_deploy_never_retires_another_workspaces_identically_slugged_container()
    {
        // Workspace A deploys "api" for the second time; workspace B's own "api" container, labelled
        // for B, is sitting on the same host under the same harbora.app slug. Before the workspace
        // label existed, ContainersToRetire matched on the slug alone and force-removed it.
        var keepForA = DeploymentPlanning.ContainerName(WorkspaceA, "api", 2);
        var strangersContainer = DeploymentPlanning.ContainerName(WorkspaceB, "api", 1);
        var all = new[]
        {
            C(keepForA, "api", WorkspaceA),
            C(DeploymentPlanning.ContainerName(WorkspaceA, "api", 1), "api", WorkspaceA), // A's own old one — retire
            C(strangersContainer, "api", WorkspaceB, state: "running"),                   // B's live container
        };

        var retire = DeploymentPlanning.ContainersToRetire(
            all, WorkspaceA, "api", keepForA, slugExclusiveToThisWorkspace: false);

        retire.Should().Equal("id-" + DeploymentPlanning.ContainerName(WorkspaceA, "api", 1));
        retire.Should().NotContain("id-" + strangersContainer,
            "workspace A's deploy must never remove a container it does not own");
    }

    [Fact]
    public void CurrentContainerId_never_picks_a_strangers_container()
    {
        var strangersContainer = DeploymentPlanning.ContainerName(WorkspaceB, "api", 1);
        var all = new[] { C(strangersContainer, "api", WorkspaceB, state: "running") };

        DeploymentPlanning.CurrentContainerId(all, WorkspaceA, "api", slugExclusiveToThisWorkspace: false)
            .Should().BeNull("nothing on this host is workspace A's \"api\" container");
    }

    // ---- the legacy bridge: an unlabelled container predates the workspace label ----

    [Fact]
    public void An_unlabelled_container_is_retired_when_no_other_workspace_holds_the_slug()
    {
        var keep = DeploymentPlanning.ContainerName(WorkspaceA, "legacy-app", 2);
        var legacy = DeploymentPlanning.LegacyContainerName("legacy-app");
        var all = new[] { C(keep, "legacy-app", WorkspaceA), C(legacy, "legacy-app", workspaceId: null) };

        var retire = DeploymentPlanning.ContainersToRetire(
            all, WorkspaceA, "legacy-app", keep, slugExclusiveToThisWorkspace: true);

        retire.Should().Equal(new[] { "id-" + legacy },
            "the platform-wide slug index makes this true for every app created after the fix shipped, " +
            "and it is what lets a container from before the fix still get cleaned up");
    }

    [Fact]
    public void An_unlabelled_container_is_left_alone_when_another_workspace_could_hold_the_slug()
    {
        // The one narrow bridge: treating "no label" as "mine" unconditionally is the exact defect
        // this fix closes, so an unlabelled container must NOT be claimed while the slug could still
        // belong to somebody else (an install whose unique-index migration could not apply).
        var keep = DeploymentPlanning.ContainerName(WorkspaceA, "api", 2);
        var legacy = DeploymentPlanning.LegacyContainerName("api");
        var all = new[] { C(keep, "api", WorkspaceA), C(legacy, "api", workspaceId: null) };

        var retire = DeploymentPlanning.ContainersToRetire(
            all, WorkspaceA, "api", keep, slugExclusiveToThisWorkspace: false);

        retire.Should().BeEmpty(
            "stranding it is the safer failure than a false positive that could be a stranger's container");
    }

    [Fact]
    public void CurrentContainerId_prefers_running()
    {
        var all = new[]
        {
            C("harbora-blog-1", "blog", WorkspaceA, state: "exited"),
            C("harbora-blog-2", "blog", WorkspaceA, state: "running"),
        };
        DeploymentPlanning.CurrentContainerId(all, WorkspaceA, "blog", slugExclusiveToThisWorkspace: true)
            .Should().Be("id-harbora-blog-2");
    }

    [Fact]
    public void ResolveRollbackImage_returns_prior_image()
    {
        var target = new Deployment { Number = 5, ImageTag = "harbora/blog:build-5" };
        DeploymentPlanning.ResolveRollbackImage(target).Should().Be("harbora/blog:build-5");
    }

    [Fact]
    public void ResolveRollbackImage_throws_when_target_missing_or_imageless()
    {
        var actNull = () => DeploymentPlanning.ResolveRollbackImage(null);
        actNull.Should().Throw<InvalidOperationException>();

        var actNoImage = () => DeploymentPlanning.ResolveRollbackImage(new Deployment { Number = 5, ImageTag = null });
        actNoImage.Should().Throw<InvalidOperationException>().WithMessage("*no retained image*");
    }

    // ---- compose: a stack replaces several containers at once ----

    [Fact]
    public void A_compose_cutover_keeps_every_container_of_the_new_stack()
    {
        // Retiring per-service would tear down half the stack it had just built.
        var containers = new[]
        {
            C("harbora-shop-web-1", "shop", WorkspaceA), C("harbora-shop-db-1", "shop", WorkspaceA),
            C("harbora-shop-web-2", "shop", WorkspaceA), C("harbora-shop-db-2", "shop", WorkspaceA)
        };

        var retire = DeploymentPlanning.ContainersToRetire(
            containers, WorkspaceA, "shop", new[] { "harbora-shop-web-2", "harbora-shop-db-2" },
            slugExclusiveToThisWorkspace: true);

        retire.Should().HaveCount(2);
        retire.Should().BeEquivalentTo(["id-harbora-shop-web-1", "id-harbora-shop-db-1"]);
    }

    [Fact]
    public void A_compose_cutover_still_ignores_other_apps()
    {
        var containers = new[] { C("harbora-shop-web-1", "shop", WorkspaceA), C("harbora-blog-1", "blog", WorkspaceA) };

        var retire = DeploymentPlanning.ContainersToRetire(
            containers, WorkspaceA, "shop", new[] { "harbora-shop-web-2" }, slugExclusiveToThisWorkspace: true);

        retire.Should().Equal("id-harbora-shop-web-1");
    }

    [Fact]
    public void Compose_container_names_are_versioned_per_service_and_carry_the_workspace()
    {
        // Old and new must coexist during the cutover, so the number is part of the name.
        DeploymentPlanning.ComposeContainerName(WorkspaceA, "shop", "web", 7)
            .Should().Be($"harbora-{WorkspaceA:N}-shop-web-7");
        DeploymentPlanning.ComposeContainerName(WorkspaceA, "shop", "db", 7)
            .Should().Be($"harbora-{WorkspaceA:N}-shop-db-7");
    }

    // ---- superseding a live deployment on rollback ----

    [Fact]
    public void The_live_deployment_a_rollback_displaces_is_marked_rolled_back()
    {
        var live = new Deployment { Id = Guid.NewGuid(), Number = 7, Status = DeploymentStatus.Succeeded };

        DeploymentPlanning.ShouldMarkRolledBack(live, Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void A_rollback_never_marks_itself_rolled_back()
    {
        var id = Guid.NewGuid();
        var self = new Deployment { Id = id, Number = 7, Status = DeploymentStatus.Succeeded };

        DeploymentPlanning.ShouldMarkRolledBack(self, id).Should().BeFalse();
    }

    [Fact]
    public void Nothing_is_marked_when_the_app_had_no_live_deployment()
    {
        DeploymentPlanning.ShouldMarkRolledBack(null, Guid.NewGuid()).Should().BeFalse();
    }

    [Theory]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Cancelled)]
    [InlineData(DeploymentStatus.RolledBack)]
    [InlineData(DeploymentStatus.Building)]
    public void Only_a_succeeded_deployment_can_be_superseded(DeploymentStatus status)
    {
        // Anything else would be an illegal transition — the state machine is the authority.
        var other = new Deployment { Id = Guid.NewGuid(), Number = 7, Status = status };

        DeploymentPlanning.ShouldMarkRolledBack(other, Guid.NewGuid()).Should().BeFalse();
    }
}
