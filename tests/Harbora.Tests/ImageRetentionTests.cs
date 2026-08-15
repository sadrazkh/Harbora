using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Image retention (doc 15, Phase C). Artifact rollback re-releases a stored image rather than
/// rebuilding, so retention decides how far back "instant rollback" actually reaches — and a
/// too-eager prune silently destroys the ability to recover.
/// </summary>
public class ImageRetentionTests
{
    private static ImageInfo Img(string tag) => new($"sha256:{tag}", tag, DateTimeOffset.UnixEpoch, 1024);

    private static Deployment Dep(int number, string? image, DeploymentStatus status = DeploymentStatus.Succeeded, Guid? id = null)
        => new() { Id = id ?? Guid.NewGuid(), Number = number, ImageTag = image, Status = status };

    private static IReadOnlyList<string> Prune(
        IEnumerable<ImageInfo> onNode, IEnumerable<Deployment> history, Guid? active, int keep)
        => DeploymentPlanning.ImagesToPrune(onNode, history, active, "harbora", "blog", keep);

    [Fact]
    public void The_build_image_prefix_is_scoped_to_one_app()
    {
        DeploymentPlanning.BuildImagePrefix("harbora", "blog").Should().Be("harbora/blog:build-");
    }

    [Fact]
    public void Images_beyond_the_retention_window_are_pruned()
    {
        var history = Enumerable.Range(1, 5).Select(n => Dep(n, $"harbora/blog:build-{n}")).ToList();
        var onNode = history.Select(d => Img(d.ImageTag!)).ToList();

        var prunable = Prune(onNode, history, active: history[4].Id, keep: 2);

        prunable.Should().BeEquivalentTo(["harbora/blog:build-1", "harbora/blog:build-2", "harbora/blog:build-3"]);
    }

    [Fact]
    public void The_active_image_is_never_pruned_even_outside_the_window()
    {
        // The user pinned an old version by rolling back to it: it is live but far down the history.
        var history = Enumerable.Range(1, 6).Select(n => Dep(n, $"harbora/blog:build-{n}")).ToList();
        var onNode = history.Select(d => Img(d.ImageTag!)).ToList();

        var prunable = Prune(onNode, history, active: history[0].Id, keep: 2);

        prunable.Should().NotContain("harbora/blog:build-1", "deleting the image that is serving traffic would be catastrophic");
    }

    [Fact]
    public void Images_of_other_apps_are_never_touched()
    {
        var history = new[] { Dep(1, "harbora/blog:build-1"), Dep(2, "harbora/blog:build-2") };
        var onNode = new[]
        {
            Img("harbora/blog:build-1"), Img("harbora/blog:build-2"),
            Img("harbora/shop:build-1"), Img("harbora/shop:build-9")
        };

        var prunable = Prune(onNode, history, active: history[1].Id, keep: 1);

        prunable.Should().NotContain(t => t.Contains("shop"));
    }

    [Fact]
    public void User_supplied_images_are_never_pruned()
    {
        // Prebuilt-image and template apps run images the user brought. Deleting nginx:1.27 because
        // an app happened to reference it would break every other app using it.
        var history = new[] { Dep(1, "nginx:1.27"), Dep(2, "harbora/blog:build-2") };
        var onNode = new[] { Img("nginx:1.27"), Img("postgres:16"), Img("harbora/blog:build-2") };

        var prunable = Prune(onNode, history, active: history[1].Id, keep: 1);

        prunable.Should().BeEmpty();
    }

    [Fact]
    public void Failed_deployments_do_not_consume_the_retention_window()
    {
        // Only Succeeded/RolledBack deployments are rollback targets. If failures counted, a burst
        // of broken builds would silently push every working version out of reach.
        var history = new[]
        {
            Dep(1, "harbora/blog:build-1"),
            Dep(2, "harbora/blog:build-2"),
            Dep(3, "harbora/blog:build-3", DeploymentStatus.Failed),
            Dep(4, "harbora/blog:build-4", DeploymentStatus.Failed)
        };
        var onNode = history.Select(d => Img(d.ImageTag!)).ToList();

        var prunable = Prune(onNode, history, active: history[1].Id, keep: 2);

        prunable.Should().BeEquivalentTo(["harbora/blog:build-3", "harbora/blog:build-4"]);
    }

    [Fact]
    public void A_rollback_reusing_an_image_does_not_shrink_the_window()
    {
        // The most common rollback: #3 returns to #2, re-releasing the SAME artifact. The two newest
        // deployments now share one image. Counting deployments instead of distinct images would
        // spend the whole window on that one artifact and prune build-1 — leaving a "keep 2" setting
        // with only one recoverable version.
        var d1 = Dep(1, "harbora/blog:build-1");
        var d2 = Dep(2, "harbora/blog:build-2");
        var d3 = Dep(3, "harbora/blog:build-2");   // rollback: re-releases #2's artifact
        var history = new[] { d1, d2, d3 };
        var onNode = new[] { Img("harbora/blog:build-1"), Img("harbora/blog:build-2") };

        var prunable = Prune(onNode, history, active: d3.Id, keep: 2);

        prunable.Should().BeEmpty("two distinct images exist and two are meant to be kept");
    }

    [Fact]
    public void Rolled_back_deployments_remain_rollback_targets()
    {
        var history = new[]
        {
            Dep(1, "harbora/blog:build-1", DeploymentStatus.RolledBack),
            Dep(2, "harbora/blog:build-2")
        };
        var onNode = history.Select(d => Img(d.ImageTag!)).ToList();

        var prunable = Prune(onNode, history, active: history[1].Id, keep: 2);

        prunable.Should().BeEmpty("a superseded version is exactly what a user wants to return to");
    }

    [Fact]
    public void An_image_on_the_node_with_no_deployment_row_is_prunable()
    {
        // Orphan from an interrupted build — nothing can roll back to it.
        var history = new[] { Dep(1, "harbora/blog:build-1") };
        var onNode = new[] { Img("harbora/blog:build-1"), Img("harbora/blog:build-77") };

        var prunable = Prune(onNode, history, active: history[0].Id, keep: 3);

        prunable.Should().Equal("harbora/blog:build-77");
    }

    [Fact]
    public void Keep_is_clamped_to_at_least_one()
    {
        // A misconfigured keep=0 must not wipe the only rollback target; disabling pruning entirely
        // is expressed by not calling this at all (ImageRetentionCount <= 0).
        var history = new[] { Dep(1, "harbora/blog:build-1"), Dep(2, "harbora/blog:build-2") };
        var onNode = history.Select(d => Img(d.ImageTag!)).ToList();

        var prunable = Prune(onNode, history, active: null, keep: 0);

        prunable.Should().Equal("harbora/blog:build-1");
    }

    [Fact]
    public void Nothing_is_pruned_when_history_fits_the_window()
    {
        var history = new[] { Dep(1, "harbora/blog:build-1"), Dep(2, "harbora/blog:build-2") };
        var onNode = history.Select(d => Img(d.ImageTag!)).ToList();

        Prune(onNode, history, active: history[1].Id, keep: 5).Should().BeEmpty();
    }

    // ---- RollbackEligibleDeploymentIds (sub-project F: showing the depth ImagesToPrune enforces) ----

    private static IReadOnlySet<Guid> Eligible(IEnumerable<Deployment> history, Guid? active, int keep) =>
        DeploymentPlanning.RollbackEligibleDeploymentIds(history, active, keep);

    [Fact]
    public void A_deployment_inside_the_retention_window_is_rollback_eligible()
    {
        var history = Enumerable.Range(1, 5).Select(n => Dep(n, $"harbora/blog:build-{n}")).ToList();

        var eligible = Eligible(history, active: history[4].Id, keep: 2);

        eligible.Should().Contain(history[3].Id, "#4 is one of the newest two rollback targets");
    }

    [Fact]
    public void A_deployment_outside_the_retention_window_is_not_rollback_eligible()
    {
        var history = Enumerable.Range(1, 5).Select(n => Dep(n, $"harbora/blog:build-{n}")).ToList();

        var eligible = Eligible(history, active: history[4].Id, keep: 2);

        eligible.Should().NotContain(history[0].Id, "#1 fell out of a keep-2 window three deployments ago");
    }

    [Fact]
    public void The_eligible_set_matches_exactly_what_ImagesToPrune_would_keep()
    {
        // The property the design doc insists on: the two must not be able to disagree. Built from
        // distinct tags so counting deployments and counting tags agree too.
        var history = Enumerable.Range(1, 6).Select(n => Dep(n, $"harbora/blog:build-{n}")).ToList();
        var onNode = history.Select(d => Img(d.ImageTag!)).ToList();

        var eligible = Eligible(history, active: history[5].Id, keep: 3);
        var kept = onNode.Select(i => i.Tag).Except(Prune(onNode, history, active: history[5].Id, keep: 3));

        eligible.Select(id => history.Single(d => d.Id == id).ImageTag)
            .Should().BeEquivalentTo(kept, "the marker and the pruner are reading the same rule");
    }

    [Fact]
    public void Widening_the_configured_retention_moves_which_deployments_are_marked_eligible()
    {
        // The line has to follow ImageRetentionCount rather than a literal number: #2 is out of reach
        // at keep=2 and back in reach the moment the configured depth grows to 4.
        var history = Enumerable.Range(1, 5).Select(n => Dep(n, $"harbora/blog:build-{n}")).ToList();

        Eligible(history, active: history[4].Id, keep: 2).Should().NotContain(history[1].Id);
        Eligible(history, active: history[4].Id, keep: 4).Should().Contain(history[1].Id);
    }

    [Fact]
    public void A_deployment_with_no_retained_image_is_never_rollback_eligible()
    {
        var history = new[] { Dep(1, image: null, DeploymentStatus.Failed), Dep(2, "harbora/blog:build-2") };

        Eligible(history, active: history[1].Id, keep: 5).Should().NotContain(history[0].Id);
    }

    [Fact]
    public void A_rolled_back_deployment_still_reachable_by_its_own_shared_tag_is_rollback_eligible()
    {
        // Same artifact as A_rollback_reusing_an_image_does_not_shrink_the_window: two rows, one tag.
        var d1 = Dep(1, "harbora/blog:build-1", DeploymentStatus.RolledBack);
        var d2 = Dep(2, "harbora/blog:build-1");
        var history = new[] { d1, d2 };

        Eligible(history, active: d2.Id, keep: 1).Should().Contain(d1.Id,
            "d1's image is the same tag the active deployment is protecting");
    }

    [Fact]
    public void Zero_retention_disables_pruning_entirely_so_every_imaged_deployment_stays_eligible()
    {
        // HarboraRuntimeOptions.ImageRetentionCount: "0 disables pruning entirely" — unlike
        // ImagesToPrune itself (which clamps a direct keep=0 call to "keep the newest one"), the
        // eligibility marker must mirror what the two real pruning callers actually do when the
        // option is 0: they never call ImagesToPrune at all, so nothing this deep in history is ever
        // pruned.
        var history = Enumerable.Range(1, 9).Select(n => Dep(n, $"harbora/blog:build-{n}")).ToList();

        var eligible = Eligible(history, active: history[8].Id, keep: 0);

        eligible.Should().BeEquivalentTo(history.Select(d => d.Id), "retention is off — nothing is ever pruned");
    }
}
