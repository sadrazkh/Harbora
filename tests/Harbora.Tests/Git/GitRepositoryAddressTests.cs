using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Infrastructure.Git;
using Xunit;

namespace Harbora.Tests.Git;

public sealed class GitRepositoryAddressTests
{
    [Theory]
    [InlineData("https://github.com/acme/shop.git")]
    [InlineData("https://github.com/acme/shop")]
    [InlineData("https://github.com/Acme/Shop/")]
    [InlineData("http://GITHUB.com/acme/shop.git")]
    public void Every_spelling_of_one_repository_is_one_key(string address) =>
        GitRepositoryAddress.Key(address).Should().Be("github.com/acme/shop");

    [Fact]
    public void A_port_and_a_nested_group_are_part_of_what_names_a_repository()
    {
        GitRepositoryAddress.Key("https://code.example.com:3000/me/app.git").Should().Be("code.example.com:3000/me/app");
        GitRepositoryAddress.FullNameOf("https://gitlab.com/group/sub/project.git").Should().Be("group/sub/project");
        GitRepositoryAddress.Key("https://github.com/").Should().BeNull("an address with no repository in it names none");
        GitRepositoryAddress.Key("git@github.com:acme/shop.git").Should().BeNull();
    }

    [Theory]
    [InlineData(GitProviderType.GitHub, "", "github.com")]
    [InlineData(GitProviderType.GitHub, "https://api.github.com", "github.com")]
    [InlineData(GitProviderType.GitHub, "https://ghe.example.com/api/v3", "ghe.example.com")]
    [InlineData(GitProviderType.GitLab, "", "gitlab.com")]
    [InlineData(GitProviderType.GitLab, "https://gitlab.example.com", "gitlab.example.com")]
    [InlineData(GitProviderType.Gitea, "https://code.example.com:3000/", "code.example.com:3000")]
    [InlineData(GitProviderType.Gitea, "", null)]
    [InlineData(GitProviderType.Custom, "https://example.com", null)]
    public void An_account_is_on_the_host_its_repositories_are_cloned_from(GitProviderType type, string apiBaseUrl, string? host) =>
        GitRepositoryAddress.HostOf(type, apiBaseUrl).Should().Be(host);

    [Fact]
    public void A_token_for_one_host_is_never_good_for_an_address_on_another()
    {
        var github = new GitProvider { Type = GitProviderType.GitHub, ApiBaseUrl = "" };

        GitRepositoryAddress.IsOn(github, "https://github.com/acme/shop.git").Should().BeTrue();
        GitRepositoryAddress.IsOn(github, "https://github.com.evil.example/acme/shop.git").Should().BeFalse();
        GitRepositoryAddress.IsOn(github, "https://gitlab.com/acme/shop.git").Should().BeFalse();
        GitRepositoryAddress.IsOn(new GitProvider { Type = GitProviderType.Custom }, "https://github.com/acme/shop.git").Should().BeFalse();
    }
}
