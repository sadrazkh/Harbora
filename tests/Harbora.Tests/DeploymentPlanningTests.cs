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
/// </summary>
public class DeploymentPlanningTests
{
    private static ContainerInfo C(string name, string slug, string state = "running")
        => new("id-" + name, name, "img", state, "Up", new Dictionary<string, string>
        {
            ["harbora.app"] = slug
        });

    [Fact]
    public void Container_name_is_versioned()
    {
        DeploymentPlanning.ContainerName("blog", 42).Should().Be("harbora-blog-42");
        DeploymentPlanning.LegacyContainerName("blog").Should().Be("harbora-blog");
    }

    [Fact]
    public void ContainersToRetire_keeps_new_and_ignores_other_apps()
    {
        var keep = DeploymentPlanning.ContainerName("blog", 3);
        var all = new[]
        {
            C(keep, "blog"),                                   // the new one — keep
            C(DeploymentPlanning.ContainerName("blog", 2), "blog"),  // old version — retire
            C(DeploymentPlanning.LegacyContainerName("blog"), "blog"), // legacy — retire
            C(DeploymentPlanning.ContainerName("shop", 9), "shop"),  // other app — ignore
        };

        var retire = DeploymentPlanning.ContainersToRetire(all, "blog", keep);

        retire.Should().HaveCount(2);
        retire.Should().Contain("id-" + DeploymentPlanning.ContainerName("blog", 2));
        retire.Should().Contain("id-" + DeploymentPlanning.LegacyContainerName("blog"));
        retire.Should().NotContain("id-" + keep);
        retire.Should().NotContain("id-" + DeploymentPlanning.ContainerName("shop", 9));
    }

    [Fact]
    public void CurrentContainerId_prefers_running()
    {
        var all = new[]
        {
            C("harbora-blog-1", "blog", state: "exited"),
            C("harbora-blog-2", "blog", state: "running"),
        };
        DeploymentPlanning.CurrentContainerId(all, "blog").Should().Be("id-harbora-blog-2");
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
            C("harbora-shop-web-1", "shop"), C("harbora-shop-db-1", "shop"),
            C("harbora-shop-web-2", "shop"), C("harbora-shop-db-2", "shop")
        };

        var retire = DeploymentPlanning.ContainersToRetire(
            containers, "shop", new[] { "harbora-shop-web-2", "harbora-shop-db-2" });

        retire.Should().HaveCount(2);
        retire.Should().BeEquivalentTo(["id-harbora-shop-web-1", "id-harbora-shop-db-1"]);
    }

    [Fact]
    public void A_compose_cutover_still_ignores_other_apps()
    {
        var containers = new[] { C("harbora-shop-web-1", "shop"), C("harbora-blog-1", "blog") };

        var retire = DeploymentPlanning.ContainersToRetire(containers, "shop", new[] { "harbora-shop-web-2" });

        retire.Should().Equal("id-harbora-shop-web-1");
    }

    [Fact]
    public void Compose_container_names_are_versioned_per_service()
    {
        // Old and new must coexist during the cutover, so the number is part of the name.
        DeploymentPlanning.ComposeContainerName("shop", "web", 7).Should().Be("harbora-shop-web-7");
        DeploymentPlanning.ComposeContainerName("shop", "db", 7).Should().Be("harbora-shop-db-7");
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
