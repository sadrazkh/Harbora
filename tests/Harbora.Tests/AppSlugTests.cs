using FluentAssertions;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A slug ends up in an image tag, a container name and a DNS label, and Docker refuses anything but
/// ASCII in the first two. It says so only after the database has been created and charged.
/// </summary>
public sealed class AppSlugTests
{
    [Theory]
    [InlineData("My Shop", "my-shop")]
    [InlineData("shop_2", "shop-2")]
    [InlineData("  --Shop--  ", "shop")]
    [InlineData("a...b", "a-b")]
    [InlineData("فروشگاه", "app")]
    [InlineData("فروشگاه shop", "shop")]
    [InlineData("shop فروشگاه ۲", "shop")]
    [InlineData("۱۲۳", "app")]
    [InlineData("中文", "app")]
    [InlineData("café", "caf")]
    [InlineData("", "app")]
    [InlineData("---", "app")]
    public void An_app_slug_holds_only_what_docker_and_dns_can(string name, string expected)
    {
        AppProvisioningService.Slugify(name).Should().Be(expected);
    }

    [Fact]
    public void A_long_name_is_cut_without_leaving_a_hyphen_on_the_end()
    {
        AppProvisioningService.Slugify(new string('a', 49) + "-b").Should().Be(new string('a', 49));
    }

    [Theory]
    [InlineData("فروشگاه-postgresql", "postgresql")]
    [InlineData("Shop DB", "shop-db")]
    public void A_databases_container_is_held_to_the_same_rule(string name, string expected)
    {
        DatabaseProvisioningService.Slugify(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("دیتابیس")]
    [InlineData("---")]
    public void A_database_with_nothing_left_of_its_name_still_gets_a_name_docker_can_hold(string name)
    {
        DatabaseProvisioningService.Slugify(name).Should().MatchRegex("^svc-[0-9a-f]{6}$");
    }
}
