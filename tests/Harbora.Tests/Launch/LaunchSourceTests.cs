using FluentAssertions;
using Harbora.Infrastructure.Launch;
using Xunit;

namespace Harbora.Tests.Launch;

/// <summary>Every way a person might paste where their code is, and what is safe to keep of it.</summary>
public sealed class LaunchSourceTests
{
    [Theory]
    [InlineData("https://github.com/acme/shop.git", "https://github.com/acme/shop.git")]
    [InlineData("  https://github.com/acme/shop  ", "https://github.com/acme/shop")]
    [InlineData("http://git.internal:3000/acme/shop.git", "http://git.internal:3000/acme/shop.git")]
    public void An_ordinary_url_is_kept_with_no_token(string raw, string expected)
    {
        var reading = LaunchSource.ReadCloneUrl(raw);

        reading.Refusal.Should().BeNull();
        reading.CloneUrl.Should().Be(expected);
        reading.EmbeddedToken.Should().BeNull();
    }

    [Theory]
    [InlineData("https://x-access-token:ghp_abc123@github.com/acme/shop.git", "ghp_abc123")]
    [InlineData("https://ghp_abc123@github.com/acme/shop.git", "ghp_abc123")]
    [InlineData("https://ghp_abc123:@github.com/acme/shop.git", "ghp_abc123")]
    [InlineData("https://oauth2:p%40ss%2Fword@gitlab.com/acme/shop.git", "p@ss/word")]
    public void Credentials_in_the_url_move_to_the_token_and_leave_the_url(string raw, string token)
    {
        var reading = LaunchSource.ReadCloneUrl(raw);

        reading.Refusal.Should().BeNull();
        reading.EmbeddedToken.Should().Be(token);
        reading.CloneUrl.Should().NotContain("@").And.NotContain("abc123").And.NotContain("ss");
        reading.CloneUrl.Should().EndWith("/acme/shop.git");
    }

    [Fact]
    public void A_query_string_is_dropped_because_that_is_another_place_a_token_can_sit()
    {
        LaunchSource.ReadCloneUrl("https://git.example.com/shop.git?private_token=abc123#frag")
            .CloneUrl.Should().Be("https://git.example.com/shop.git");
    }

    [Theory]
    [InlineData("git@github.com:acme/shop.git", "https://github.com/acme/shop.git")]
    [InlineData("ssh://git@github.com/acme/shop.git", "https://github.com/acme/shop.git")]
    public void An_ssh_address_is_refused_with_the_https_form_to_use_instead(string raw, string suggestion)
    {
        var reading = LaunchSource.ReadCloneUrl(raw);

        reading.CloneUrl.Should().BeNull();
        reading.Refusal!.Message.Should().Contain("SSH").And.Contain(suggestion);
        reading.Refusal.MessageFa.Should().Contain(suggestion);
    }

    [Theory]
    [InlineData("ftp://example.com/shop.git")]
    [InlineData("file:///etc/passwd")]
    [InlineData("github.com/acme/shop")]
    [InlineData("not a url at all")]
    public void Anything_but_http_and_https_is_refused_in_both_languages(string raw)
    {
        var refusal = LaunchSource.ReadCloneUrl(raw).Refusal;

        refusal.Should().NotBeNull();
        refusal!.Message.Should().Contain("https");
        refusal.MessageFa.Should().Contain("https");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_url_is_refused(string? raw)
    {
        LaunchSource.ReadCloneUrl(raw).Refusal.Should().NotBeNull();
    }

    [Fact]
    public void A_url_longer_than_its_column_is_refused_rather_than_failing_the_save()
    {
        LaunchSource.ReadCloneUrl("https://example.com/" + new string('a', 2100)).Refusal.Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://github.com/acme/my-shop.git", "my-shop")]
    [InlineData("https://github.com/acme/my-shop", "my-shop")]
    [InlineData("https://github.com/acme/my-shop/", "my-shop")]
    [InlineData("https://github.com", "app")]
    public void The_app_is_named_after_the_repository(string url, string name)
    {
        LaunchSource.NameFromCloneUrl(url).Should().Be(name);
    }

    [Theory]
    [InlineData("/var/uploads/my-shop.zip", "my-shop")]
    [InlineData("/var/uploads/my-shop.tar.gz", "my-shop")]
    [InlineData("/var/uploads/my-shop.tgz", "my-shop")]
    [InlineData("/var/uploads/my-shop-0123456789abcdef0123456789abcdef.zip", "my-shop")]
    [InlineData("/var/uploads/my-shop-01234567-89ab-cdef-0123-456789abcdef.zip", "my-shop")]
    [InlineData("/var/uploads/launch-0123456789abcdef0123456789abcdef.zip", "app")]
    [InlineData("/var/uploads/0123456789abcdef0123456789abcdef.zip", "app")]
    public void An_archive_gives_its_own_name_without_the_part_that_only_keeps_uploads_apart(string path, string name)
    {
        LaunchSource.NameFromArchive(path).Should().Be(name);
    }
}
