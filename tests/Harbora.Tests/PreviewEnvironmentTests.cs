using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Infrastructure.Projects;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Giving a branch an environment of its own.
///
/// Branch names are the least disciplined strings in software, and each one ends up in a DNS label,
/// a Docker network name and a container name — every one of which has its own limit and fails in
/// the middle of a deployment rather than when the branch was named.
/// </summary>
public class PreviewNamingTests
{
    [Fact]
    public void A_slash_in_a_branch_name_does_not_reach_a_hostname()
    {
        var slug = PreviewNaming.Slug("feature/login-page");

        slug.Should().MatchRegex("^[a-z0-9-]+$");
        slug.Should().StartWith("feature-login-page");
    }

    [Fact]
    public void Two_branches_that_clean_to_the_same_text_never_share_an_environment()
    {
        // The reason a hash is always appended: without it these are one environment, and one
        // branch's deployment silently replaces the other's.
        PreviewNaming.Slug("feature/login").Should().NotBe(PreviewNaming.Slug("feature-login"));
    }

    [Fact]
    public void The_same_branch_always_gets_the_same_name()
    {
        // Otherwise every push builds a new environment and the old one is stranded.
        PreviewNaming.Slug("feature/login").Should().Be(PreviewNaming.Slug("feature/login"));
    }

    [Fact]
    public void A_very_long_branch_name_still_produces_a_legal_label()
    {
        // A ticket title pasted as a branch name is normal, and 64 characters is where DNS stops.
        var slug = PreviewNaming.Slug(new string('a', 300));

        slug.Length.Should().BeLessThanOrEqualTo(PreviewNaming.MaxLabel);
    }

    [Fact]
    public void The_unique_part_survives_being_trimmed()
    {
        // Trimming the hash instead of the description would reintroduce the collision the hash
        // exists to prevent.
        var one = PreviewNaming.Slug(new string('a', 300) + "one");
        var two = PreviewNaming.Slug(new string('a', 300) + "two");

        one.Should().NotBe(two);
    }

    [Fact]
    public void A_hostname_carries_the_app_as_well_as_the_branch()
    {
        // Two apps previewing the same branch must not both claim one address.
        var shop = PreviewNaming.Host("shop", "feature/login", "apps.example.com");
        var blog = PreviewNaming.Host("blog", "feature/login", "apps.example.com");

        shop.Should().NotBe(blog);
        shop.Should().StartWith("shop-").And.EndWith(".apps.example.com");
    }

    [Fact]
    public void A_hostname_label_stays_within_what_dns_allows()
    {
        var host = PreviewNaming.Host(new string('s', 40), new string('b', 300), "apps.example.com");

        host!.Split('.')[0].Length.Should().BeLessThanOrEqualTo(PreviewNaming.MaxLabel);
        host.Should().MatchRegex(@"^[a-z0-9-]+\.apps\.example\.com$");
    }

    [Fact]
    public void With_no_root_domain_configured_a_preview_gets_no_address()
    {
        // Same as every other service: there is nothing to derive one from.
        PreviewNaming.Host("shop", "feature/login", null).Should().BeNull();
        PreviewNaming.Host("shop", "feature/login", "  ").Should().BeNull();
    }

    [Fact]
    public void An_empty_branch_name_still_produces_something_usable()
    {
        PreviewNaming.Slug("").Should().NotBeNullOrWhiteSpace();
        PreviewNaming.EnvironmentName("").Should().Be("preview");
    }

    [Fact]
    public void The_environment_keeps_the_name_a_person_typed()
    {
        // The slug is for machines; a screen showing "feature-login-a1b2c3" instead of the branch
        // makes people hunt for which preview is theirs.
        PreviewNaming.EnvironmentName("feature/login").Should().Be("feature/login");
    }
}

/// <summary>
/// Which branches get a preview, and what goes in it.
///
/// A preview is created by anybody who can push a branch. Copying the parent's secrets into it hands
/// production's database password to every branch in the repository.
/// </summary>
public class PreviewPolicyTests
{
    private static EnvironmentVariable Var(string key, string value, bool secret = false) =>
        new() { Key = key, Value = value, IsSecret = secret };

    [Fact]
    public void A_branch_that_is_not_the_tracked_one_gets_a_preview()
    {
        PreviewPolicy.ShouldPreview(true, "feature/login", isTag: false, trackedBranch: "main").Should().BeTrue();
    }

    [Fact]
    public void The_tracked_branch_does_not_get_one()
    {
        // It already has a real environment; previewing it would deploy the same commit twice.
        PreviewPolicy.ShouldPreview(true, "main", false, "main").Should().BeFalse();
        PreviewPolicy.ShouldPreview(true, "MAIN", false, "main").Should().BeFalse("branch names differ in case, not in meaning");
    }

    [Fact]
    public void A_tag_does_not_get_one()
    {
        // A tag is a release, not work in progress.
        PreviewPolicy.ShouldPreview(true, "v2.1.0", isTag: true, trackedBranch: "main").Should().BeFalse();
    }

    [Fact]
    public void Nothing_happens_unless_it_was_turned_on()
    {
        // Every branch quietly becoming a running service is a surprise, and a bill.
        PreviewPolicy.ShouldPreview(false, "feature/login", false, "main").Should().BeFalse();
    }

    [Fact]
    public void Secrets_are_not_copied_into_a_preview()
    {
        // The decision the whole feature turns on.
        var config = PreviewPolicy.ConfigFor([
            Var("LOG_LEVEL", "debug"),
            Var("DB_PASSWORD", "hunter2", secret: true)
        ]);

        config.Copied.Should().ContainSingle().Which.Key.Should().Be("LOG_LEVEL");
        config.Copied.Should().NotContain(v => v.Value == "hunter2");
        config.SkippedSecrets.Should().BeEquivalentTo(["DB_PASSWORD"]);
    }

    [Fact]
    public void The_secrets_left_behind_are_named()
    {
        // A preview that will not start because it has no database password should say which one it
        // is missing, rather than looking like a broken build.
        var config = PreviewPolicy.ConfigFor([Var("DB_PASSWORD", "x", true), Var("API_KEY", "y", true)]);

        var advice = PreviewPolicy.Advice(config);

        advice.Should().Contain("DB_PASSWORD").And.Contain("API_KEY");
        advice.Should().NotContain("x").And.NotContain("y");
    }

    [Fact]
    public void An_app_with_no_secrets_gets_no_warning()
    {
        // The guard on the message above: advice shown always is advice nobody reads.
        PreviewPolicy.Advice(PreviewPolicy.ConfigFor([Var("LOG_LEVEL", "debug")])).Should().BeNull();
    }

    [Fact]
    public void A_preview_that_has_gone_quiet_is_removed()
    {
        // A branch nobody deletes would otherwise leave a service running for ever, quietly eating
        // the tenant's quota.
        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        PreviewPolicy.HasExpired(now - PreviewPolicy.IdleLifetime, now).Should().BeTrue();
        PreviewPolicy.HasExpired(now - TimeSpan.FromDays(1), now).Should().BeFalse();
    }
}
