using FluentAssertions;
using Harbora.Application.Abstractions;
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
    public void Host_port_is_in_range_deterministic_and_varies_by_number()
    {
        var p1 = DeploymentPlanning.HostPort("blog", 1);
        var p2 = DeploymentPlanning.HostPort("blog", 2);
        p1.Should().BeInRange(20000, 29999);
        p2.Should().BeInRange(20000, 29999);
        p1.Should().NotBe(p2, "consecutive deployments need distinct ports to overlap during cutover");
        DeploymentPlanning.HostPort("blog", 1).Should().Be(p1, "must be deterministic");
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
}
