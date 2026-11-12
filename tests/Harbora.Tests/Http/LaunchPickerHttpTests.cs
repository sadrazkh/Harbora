using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Domain.Launch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The repository picker on <c>/launch</c>, as requests: a workspace of its own per test, with a
/// connected account in it, so the shared workspace's page stays the paste field the rest of this class
/// describes.
/// </summary>
public partial class LaunchHttpTests
{
    private async Task<(HttpClient Client, GitProvider Account)> WithAccountAsync(string token, params RemoteRepository[] repositories)
    {
        var workspaceId = _fixture.GivenEmptyWorkspace($"picker-{Guid.NewGuid():N}"[..20]);
        string encrypted;
        using (var scope = _fixture.Panel.Services.CreateScope())
            encrypted = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect(token);

        var account = new GitProvider
        {
            WorkspaceId = workspaceId, Name = "GitHub (OAuth)", Type = GitProviderType.GitHub, ApiBaseUrl = "", EncryptedCredential = encrypted
        };
        _fixture.Seed(db => db.GitProviders.Add(account));
        _fixture.GitProviders.Holding(token, repositories);

        var client = await _fixture.SignedInAs(workspaceId, $"picker-{Interlocked.Increment(ref _nextUser)}@example.com");
        return (client, account);
    }

    private static readonly RemoteRepository ShopRepository = new("acme/shop", "https://github.com/acme/shop.git", "main", true);
    private static readonly RemoteRepository BlogRepository = new("acme/blog", "https://github.com/acme/blog.git", "trunk", false);

    [Fact]
    public async Task A_connected_account_s_repositories_are_listed_to_pick_from_and_pasting_stays()
    {
        var (client, account) = await WithAccountAsync("gho_listed", ShopRepository, BlogRepository);

        var html = await PageAsync(client, "/launch");

        html.Should().Contain("data-launch-repos").And.Contain("یکی از مخزن‌هایت را انتخاب کن");
        html.Should().Contain("acme/shop").And.Contain("acme/blog").And.Contain("trunk");
        html.Should().Contain($"name=\"Repository\" value=\"{account.Id:D}|https://github.com/acme/shop.git\"");
        html.Should().Contain("data-lucide=\"lock\"", "a private repository says so");
        html.Should().Contain("name=\"GitUrl\"", "pasting is still there beside the list");
        html.Should().NotContain("gho_listed");

        var picker = Regex.Match(html, "<form[^>]*data-launch-pick.*?</form>", RegexOptions.Singleline).Value;
        Regex.Matches(picker, "__RequestVerificationToken").Count.Should().Be(1, "one form for the whole list, however long");
    }

    [Fact]
    public async Task Searching_without_a_script_narrows_the_list_on_the_server()
    {
        var (client, _) = await WithAccountAsync("gho_searched", ShopRepository, BlogRepository);

        var html = await PageAsync(client, "/launch?q=SHOP");

        html.Should().Contain("acme/shop").And.NotContain("acme/blog");
        html.Should().Contain("value=\"SHOP\"", "the box keeps what was searched for");

        var none = await PageAsync(client, "/launch?q=nothing-like-it");
        none.Should().Contain("مخزنی با این نام نیست.");
    }

    [Fact]
    public async Task A_list_that_cannot_be_fetched_is_one_sentence_and_the_paste_field_still_works()
    {
        var (client, _) = await WithAccountAsync("gho_expired");
        _fixture.GitProviders.Failing["gho_expired"] = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        var html = await PageAsync(client, "/launch");

        html.Should().Contain("data-launch-account-problem");
        html.Should().Contain("توکن این حساب را نپذیرفت");
        html.Should().Contain("href=\"/git\"");
        html.Should().Contain("name=\"GitUrl\"");
    }

    [Fact]
    public async Task Pressing_a_repository_starts_a_launch_through_that_account_with_no_token_and_no_branch()
    {
        var (client, account) = await WithAccountAsync("gho_pressed", ShopRepository);
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostFormAsync("/launch", token,
            ("Repository", $"{account.Id:D}|https://github.com/acme/shop.git"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var started = Launches.Started.Should().ContainSingle().Subject;
        started.Source.Should().Be(LaunchSourceKind.Git);
        started.GitProviderId.Should().Be(account.Id);
        started.CloneUrl.Should().Be("https://github.com/acme/shop.git");
        started.GitToken.Should().BeNull();
        started.GitRef.Should().BeNull("a picked repository is read from its default branch");
    }

    [Fact]
    public async Task A_workspace_with_nothing_connected_sees_the_page_it_always_did()
    {
        var html = await PageAsync(await Owner(), "/launch");

        html.Should().NotContain("data-launch-repos");
    }

    [Fact]
    public void A_picked_value_that_is_not_one_reads_as_nothing_picked()
    {
        Harbora.Web.ViewModels.LaunchStartForm.ReadPicked("not-a-guid|https://github.com/a/b").Should().BeNull();
        Harbora.Web.ViewModels.LaunchStartForm.ReadPicked($"{Guid.Empty}|").Should().BeNull();
        Harbora.Web.ViewModels.LaunchStartForm.ReadPicked(null).Should().BeNull();
        var id = Guid.CreateVersion7();
        Harbora.Web.ViewModels.LaunchStartForm.ReadPicked(Harbora.Web.ViewModels.LaunchStartForm.PickedValue(id, "https://x.example/a/b"))
            .Should().Be((id, "https://x.example/a/b"));
    }
}
