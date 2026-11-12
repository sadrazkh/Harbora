using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Launch;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Harbora.Tests.Launch.LaunchFixtures;

namespace Harbora.Tests.Launch;

/// <summary>
/// A launch whose repository belongs to a connected Git account: picked from its list, or pasted and
/// found in it. Read with that account's token, never with it written onto the launch, and created under
/// that account rather than under a provider of the app's own.
/// </summary>
public sealed class LaunchPickerTests
{
    private const string Shop = "https://github.com/acme/shop.git";
    private const string AccountToken = "gho_account-token";

    private static GitProvider Connect(LaunchHarness h, Guid? workspaceId = null, GitProviderType type = GitProviderType.GitHub,
        string apiBaseUrl = "", string token = AccountToken)
    {
        var account = new GitProvider
        {
            WorkspaceId = workspaceId ?? h.WorkspaceId, Name = $"{type} (OAuth)", Type = type, ApiBaseUrl = apiBaseUrl,
            EncryptedCredential = h.Protector.Protect(token)
        };
        var db = h.Store();
        db.GitProviders.Add(account);
        db.SaveChanges();
        return account;
    }

    private static async Task<Guid> PickedAsync(LaunchHarness h, GitProvider account, string url = Shop)
    {
        h.Reader.Inspection = Bare();
        var started = await h.Request().StartAsync(h.GitRequest(url) with { GitProviderId = account.Id }, default);
        started.Ok.Should().BeTrue(started.Refusals.FirstOrDefault()?.Message);
        await h.Worker().InspectAsync(started.Value, default);
        return started.Value;
    }

    private static GitRepository RepositoryOf(LaunchHarness h, Guid launchId)
    {
        var appId = h.Row(launchId).AppId!.Value;
        var db = h.Store();
        var app = db.Apps.IgnoreQueryFilters().AsNoTracking().Single(a => a.Id == appId);
        return db.GitRepositories.IgnoreQueryFilters().AsNoTracking().Include(r => r.Provider).Single(r => r.Id == app.GitRepositoryId);
    }

    [Fact]
    public async Task A_picked_repository_is_read_with_the_accounts_token_and_the_app_is_made_under_that_account()
    {
        using var h = new LaunchHarness();
        var account = Connect(h);

        var id = await PickedAsync(h, account);

        var row = h.Row(id);
        row.EncryptedGitToken.Should().BeNull("the account's token stays on the account's row");
        row.InspectionJson.Should().NotContain(AccountToken);
        LaunchJson.ReadStored(row.InspectionJson).GitProviderId.Should().Be(account.Id);
        row.Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        h.Git.Checkouts.Single().Token.Should().Be(AccountToken);

        await h.ConfirmAsync(id);

        var repository = RepositoryOf(h, id);
        repository.GitProviderId.Should().Be(account.Id);
        repository.FullName.Should().Be("acme/shop", "the full path is what the provider's API names it by");
        h.Store().GitProviders.IgnoreQueryFilters().Should().ContainSingle("no provider of the app's own was added");
        h.Audit.Entries.Should().Contain(e => e.Action == "launch.started" && e.MetadataJson!.Contains(account.Id.ToString()));
    }

    [Fact]
    public async Task A_second_app_on_the_same_repository_shares_the_accounts_row_and_so_its_webhook_address()
    {
        using var h = new LaunchHarness();
        var account = Connect(h);

        var first = await PickedAsync(h, account);
        await h.ConfirmAsync(first);
        var second = await PickedAsync(h, account, "https://github.com/Acme/Shop");
        await h.ConfirmAsync(second);

        RepositoryOf(h, second).Id.Should().Be(RepositoryOf(h, first).Id);
        h.Store().GitRepositories.IgnoreQueryFilters().Should().ContainSingle();
    }

    [Fact]
    public async Task Another_workspaces_account_cannot_be_picked()
    {
        using var h = new LaunchHarness();
        var theirs = Connect(h, h.OtherWorkspaceId);

        var started = await h.Request().StartAsync(h.GitRequest(Shop) with { GitProviderId = theirs.Id }, default);

        started.Ok.Should().BeFalse();
        started.Refusals.Single().Message.Should().Contain("no longer connected");
        started.Refusals.Single().MessageFa.Should().Contain("دیگر وصل نیست");
        h.Store().AppLaunches.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task An_address_on_another_host_is_never_read_with_a_picked_accounts_token()
    {
        using var h = new LaunchHarness();
        var account = Connect(h);

        var started = await h.Request().StartAsync(
            h.GitRequest("https://git.example.com/acme/shop.git") with { GitProviderId = account.Id }, default);

        started.Ok.Should().BeFalse();
        started.Refusals.Single().MessageFa.Should().NotBeNullOrWhiteSpace();
        h.Store().AppLaunches.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_pasted_address_the_accounts_own_list_holds_is_treated_as_if_it_had_been_picked()
    {
        using var h = new LaunchHarness();
        var account = Connect(h);
        h.GitProviders.Holding(AccountToken, new RemoteRepository("acme/shop", Shop, "main", true));

        var id = await h.ReadyGitAsync(Bare(), url: "https://github.com/acme/shop");

        h.Git.Checkouts.Single().Token.Should().Be(AccountToken, "no token was typed, and the repository is plainly the account's");
        LaunchJson.ReadStored(h.Row(id).InspectionJson).GitProviderId.Should().Be(account.Id);
        await h.ConfirmAsync(id);
        RepositoryOf(h, id).GitProviderId.Should().Be(account.Id);
    }

    [Fact]
    public async Task A_pasted_address_the_account_does_not_hold_is_read_the_way_it_always_was()
    {
        using var h = new LaunchHarness();
        Connect(h);
        h.GitProviders.Holding(AccountToken, new RemoteRepository("acme/blog", "https://github.com/acme/blog.git", "main", false));

        var id = await h.ReadyGitAsync(Bare(), url: "https://github.com/someone/else.git");

        h.Git.Checkouts.Single().Token.Should().BeNull();
        await h.ConfirmAsync(id);
        RepositoryOf(h, id).Provider!.Type.Should().Be(GitProviderType.Custom);
    }

    [Fact]
    public async Task A_typed_token_is_the_persons_own_say_and_no_account_is_asked()
    {
        using var h = new LaunchHarness();
        Connect(h);
        h.GitProviders.Holding(AccountToken, new RemoteRepository("acme/shop", Shop, "main", true));

        var id = await h.ReadyGitAsync(Bare(), url: Shop, token: "ghp_typed");

        h.Git.Checkouts.Single().Token.Should().Be("ghp_typed");
        h.GitProviders.Listed.Should().BeEmpty();
        await h.ConfirmAsync(id);
        RepositoryOf(h, id).Provider!.Type.Should().Be(GitProviderType.Custom);
    }

    [Fact]
    public async Task Only_an_account_on_the_pasted_addresss_host_is_asked_about_it()
    {
        using var h = new LaunchHarness();
        Connect(h);

        await h.ReadyGitAsync(Bare(), url: "https://git.example.com/acme/shop.git");

        h.GitProviders.Listed.Should().BeEmpty("a github.com token is not shown a question about another host");
    }

    [Fact]
    public async Task An_account_that_cannot_be_asked_leaves_a_pasted_address_to_be_read_as_before()
    {
        using var h = new LaunchHarness();
        Connect(h);
        h.GitProviders.FailWith = new HttpRequestException("Connection refused");

        var id = await h.ReadyGitAsync(Bare(), url: Shop);

        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        h.Git.Checkouts.Single().Token.Should().BeNull();
    }

    [Fact]
    public async Task An_account_disconnected_before_the_confirm_is_said_on_the_screen_and_nothing_is_made()
    {
        using var h = new LaunchHarness();
        var account = Connect(h);
        var id = await PickedAsync(h, account);

        var db = h.Store();
        db.GitProviders.IgnoreQueryFilters().Single(p => p.Id == account.Id).EncryptedCredential = null;
        db.SaveChanges();

        var refusals = await h.Request().ValidateConfirmAsync(id, await h.DefaultsAsync(id), default);

        refusals.Should().Contain(r => r.Message.Contains("no longer connected") && r.MessageFa.Contains("دیگر وصل نیست"));
        h.Store().Apps.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_read_through_an_account_points_at_the_account_rather_than_asking_for_a_token()
    {
        using var h = new LaunchHarness();
        var account = Connect(h);
        h.Git.FailWith = $"authentication failed for https://{AccountToken}@github.com/acme/shop.git";

        var id = await PickedAsync(h, account);

        var row = h.Row(id);
        row.Status.Should().Be(LaunchStatus.InspectFailed);
        row.FailureMessage.Should().Contain("connected Git account").And.NotContain(AccountToken);
        row.FailureMessageFa.Should().Contain("اتصال Git").And.NotContain(AccountToken);
        h.Logs.Lines.Should().NotContain(l => l.Contains(AccountToken));
    }
}
