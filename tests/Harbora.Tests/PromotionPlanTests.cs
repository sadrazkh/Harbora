using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Moving a release from one environment to the next.
///
/// The point of promoting rather than deploying again is that it is the <b>same artifact</b>.
/// Building twice from one commit does not reliably produce the same image — a floating base tag, a
/// dependency published in between — so "we tested this in staging" only means something if the
/// bytes that reach production are the bytes that passed.
/// </summary>
public class PromotionPlanTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid OtherProject = Guid.NewGuid();
    private static readonly Guid Server = Guid.NewGuid();
    private static readonly Guid OtherServer = Guid.NewGuid();
    private static readonly Guid StagingApp = Guid.NewGuid();
    private static readonly Guid ProductionApp = Guid.NewGuid();

    private static PromotionSource Source(
        DeploymentStatus status = DeploymentStatus.Succeeded,
        string? image = "harbora/shop:build-7",
        Guid? project = null, Guid? server = null) =>
        new(status, image, StagingApp, project ?? Project, server ?? Server);

    private static PromotionTarget Target(Guid? project = null, Guid? server = null) =>
        new(ProductionApp, project ?? Project, server ?? Server);

    [Fact]
    public void A_release_that_passed_can_be_promoted()
    {
        PromotionPlan.Refuse(Source(), Target()).Should().BeNull();
    }

    [Fact]
    public void A_deployment_that_did_not_succeed_cannot_be_promoted()
    {
        // Promoting a failed release is how the thing that broke staging reaches production.
        foreach (var status in new[] { DeploymentStatus.Failed, DeploymentStatus.Building, DeploymentStatus.Cancelled })
            PromotionPlan.Refuse(Source(status), Target()).Should().NotBeNull($"{status} is not a release");
    }

    [Fact]
    public void A_deployment_with_no_image_has_nothing_to_promote()
    {
        PromotionPlan.Refuse(Source(image: null), Target()).Should().Contain("no image");
        PromotionPlan.Refuse(Source(image: "  "), Target()).Should().Contain("no image");
    }

    [Fact]
    public void A_release_cannot_be_promoted_onto_itself()
    {
        var target = new PromotionTarget(StagingApp, Project, Server);

        PromotionPlan.Refuse(Source(), target).Should().Contain("same service");
    }

    [Fact]
    public void A_release_cannot_cross_from_one_project_to_another()
    {
        // Different projects are different applications that happen to share a platform.
        PromotionPlan.Refuse(Source(), Target(project: OtherProject)).Should().Contain("same project");
    }

    [Fact]
    public void A_service_belonging_to_no_project_cannot_take_part()
    {
        // Nothing places it, so "the same project" cannot be established either way. Built inline
        // rather than through the helper, whose defaulting would quietly fill the null back in.
        var unplacedSource = new PromotionSource(
            DeploymentStatus.Succeeded, "harbora/shop:build-7", StagingApp, null, Server);

        PromotionPlan.Refuse(unplacedSource, Target()).Should().Contain("same project");
        PromotionPlan.Refuse(Source(), new PromotionTarget(ProductionApp, null, Server)).Should().Contain("same project");

        // And both unplaced: comparing two nulls says "equal", which would read as "same project"
        // and quietly allow a promotion between two services nothing connects.
        PromotionPlan.Refuse(unplacedSource, new PromotionTarget(ProductionApp, null, Server))
            .Should().Contain("same project");
    }

    [Fact]
    public void A_built_image_cannot_be_promoted_to_a_different_server()
    {
        // It exists only on the node that built it, so this would fail at pull time, halfway
        // through a deployment, with an error about a missing image.
        var refusal = PromotionPlan.Refuse(Source(image: "harbora/shop:build-7"), Target(server: OtherServer));

        refusal.Should().Contain("different server");
        refusal.Should().Contain("Deploy the target service from source instead", "a refusal should say what to do");
    }

    [Fact]
    public void A_registry_image_can_be_promoted_anywhere()
    {
        // It was pulled, so any node can pull it too.
        PromotionPlan.Refuse(Source(image: "nginx:1.27"), Target(server: OtherServer)).Should().BeNull();
        PromotionPlan.Refuse(Source(image: "ghcr.io/acme/shop:2026.7.31"), Target(server: OtherServer)).Should().BeNull();
    }

    [Theory]
    [InlineData("harbora/shop:build-7", true)]
    [InlineData("harbora/shop:compose-3", true)]
    [InlineData("nginx:1.27", false)]
    [InlineData("ghcr.io/acme/shop:build-it-yourself", false)]
    public void An_image_harbora_built_is_told_apart_from_one_it_pulled(string image, bool expected)
    {
        // The tag Harbora produces is the signal. A registry tag that merely contains the word
        // "build" is not one of ours.
        PromotionPlan.IsLocallyBuilt(image).Should().Be(expected);
    }

    [Fact]
    public void The_description_says_what_does_not_travel()
    {
        // The important half: people expect a promotion to bring the settings with it, and copying
        // staging's environment into production is how this feature becomes an outage.
        var text = PromotionPlan.Describe("harbora/shop:build-7", "Production");

        text.Should().Contain("harbora/shop:build-7");
        text.Should().Contain("Nothing is rebuilt");
        text.Should().Contain("own variables");
    }
}
