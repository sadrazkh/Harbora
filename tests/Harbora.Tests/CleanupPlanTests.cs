using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Maintenance;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which build images belong to nobody.
///
/// Per-app retention runs inside a deployment, so the app that no longer exists is exactly the app
/// whose images nothing ever visits again — and on a build-heavy server those orphans are the
/// disk. Every boundary here is a way of deleting something that is not ours: a customer's image,
/// a living app's rollback window, a compose service that merely shares a prefix.
/// </summary>
public class CleanupPlanTests
{
    private static ImageInfo Img(string tag) => new("sha256:x", tag, DateTimeOffset.UtcNow, 1024);

    [Fact]
    public void An_orphans_build_images_are_deletable()
    {
        var images = new[] { Img("harbora/old-shop:build-1"), Img("harbora/old-shop:build-2") };

        CleanupPlan.OrphanedBuildImages(images, "harbora", ["blog"])
            .Should().BeEquivalentTo("harbora/old-shop:build-1", "harbora/old-shop:build-2");
    }

    [Fact]
    public void A_living_apps_images_are_untouchable()
    {
        // Its rollback depth is per-app retention's decision, not this rule's.
        var images = new[] { Img("harbora/blog:build-1"), Img("harbora/blog:build-9") };

        CleanupPlan.OrphanedBuildImages(images, "harbora", ["blog"]).Should().BeEmpty();
    }

    [Fact]
    public void A_customers_image_is_never_even_a_candidate()
    {
        // nginx:1.27 belongs to the user. So does anything else outside the build prefix,
        // including something that merely mentions it later in the tag.
        var images = new[] { Img("nginx:1.27"), Img("postgres:16-alpine"), Img("someone/harbora/x:1") };

        CleanupPlan.OrphanedBuildImages(images, "harbora", []).Should().BeEmpty();
    }

    [Fact]
    public void The_prefix_matches_as_a_path_segment_not_a_substring()
    {
        // "harborax/app:1" is not ours; "harbora/…" is. A StartsWith without the slash would
        // claim the neighbour's registry.
        var images = new[] { Img("harborax/app:build-1"), Img("harbora/app:build-1") };

        CleanupPlan.OrphanedBuildImages(images, "harbora", [])
            .Should().BeEquivalentTo("harbora/app:build-1");
    }

    [Fact]
    public void A_compose_service_is_protected_by_its_apps_slug()
    {
        // App "shop" builds "harbora/shop-api:build-N" for its compose services. "shopx" is a
        // different app entirely — the dash is the boundary.
        var images = new[]
        {
            Img("harbora/shop-api:build-1"),
            Img("harbora/shop:build-1"),
            Img("harbora/shopx:build-1")
        };

        CleanupPlan.OrphanedBuildImages(images, "harbora", ["shop"])
            .Should().BeEquivalentTo("harbora/shopx:build-1");
    }

    [Fact]
    public void Slugs_compare_case_sensitively_because_tags_do()
    {
        // "Shop" and "shop" are two different tags to a registry, so they are two different apps
        // here. Folding case would let one protect the other's leftovers forever.
        var images = new[] { Img("harbora/shop:build-1") };

        CleanupPlan.OrphanedBuildImages(images, "harbora", ["Shop"])
            .Should().BeEquivalentTo("harbora/shop:build-1");
    }

    [Fact]
    public void A_tag_this_rule_cannot_read_is_left_alone()
    {
        // "harbora/:build-1" has no name part. Unknown is not "delete it".
        var images = new[] { Img("harbora/:build-1") };

        CleanupPlan.OrphanedBuildImages(images, "harbora", ["blog"]).Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_tags_come_back_once()
    {
        // One image listed under two entries must not be deleted twice — the second attempt fails
        // and counts as an error somebody then investigates.
        var images = new[] { Img("harbora/old:build-1"), Img("harbora/old:build-1") };

        CleanupPlan.OrphanedBuildImages(images, "harbora", []).Should().ContainSingle();
    }

    [Fact]
    public void Blank_slugs_protect_nothing()
    {
        // An empty slug plus the compose rule is "protect every name that starts with '-'" — an
        // accidental amnesty for exactly the malformed tags nothing else will ever clean. The
        // dash-leading name is the case that tells the two behaviours apart.
        var images = new[] { Img("harbora/old:build-1"), Img("harbora/-stray:build-1") };

        CleanupPlan.OrphanedBuildImages(images, "harbora", ["", "  "])
            .Should().BeEquivalentTo("harbora/old:build-1", "harbora/-stray:build-1");
    }
}
