using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Infrastructure.Git;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests.Git;

/// <summary>
/// The push webhook of a connected account's repository, set up through the provider and read back
/// from it. The provider is a fake that keeps the hooks it was given, so "once only" is counted in
/// hooks on the repository, not in calls made.
/// </summary>
public sealed class GitHookServiceTests
{
    private const string Token = "gho_account";

    private static GitRepository Repository(AppServiceHarness h, GitProviderType type = GitProviderType.GitHub,
        string cloneUrl = "https://github.com/acme/shop.git", string? token = Token)
    {
        var row = new GitRepository
        {
            Provider = new GitProvider
            {
                WorkspaceId = h.WorkspaceId, Name = type.ToString(), Type = type,
                EncryptedCredential = token is null ? null : new PassthroughProtector().Protect(token)
            },
            FullName = "shop", CloneUrl = cloneUrl, DefaultBranch = "main", WebhookSecret = "the-secret"
        };
        h.Db.GitRepositories.Add(row);
        h.Db.SaveChanges();
        return row;
    }

    private static GitHookService Hooks(AppServiceHarness h, string? panel = AppServiceHarness.PanelDomain) =>
        h.GitProviders.HookService(h.Db, new PassthroughProtector(), h.Audit, h.Clock, panel);

    private static string Address(GitRepository row) => $"https://{AppServiceHarness.PanelDomain}/webhooks/git/{row.Id}";

    [Fact]
    public async Task A_repository_with_no_hook_gets_one_with_the_address_and_secret_harbora_already_has()
    {
        using var h = new AppServiceHarness();
        var row = Repository(h);

        var outcome = await Hooks(h).EnsureAsync(row.Id, default);

        outcome.State.Should().Be(GitHookState.Confirmed);
        outcome.Created.Should().BeTrue();
        outcome.MessageFa.Should().Contain("acme/shop");
        var create = h.GitProviders.HookCalls.Single(c => c.Call == "create");
        create.FullName.Should().Be("acme/shop", "the provider's API names it by its full path, read off the clone address");
        create.Token.Should().Be(Token);
        create.Spec.Should().Be(new HookSpec(Address(row), "the-secret"));
        h.Audit.Entries.Should().ContainSingle(e => e.Action == "git.webhook.created")
            .Which.MetadataJson.Should().NotContain("the-secret").And.NotContain(Token);
    }

    [Fact]
    public async Task Asked_again_it_finds_its_own_hook_and_sets_the_secret_rather_than_adding_another()
    {
        using var h = new AppServiceHarness();
        var row = Repository(h);
        var hooks = Hooks(h);

        await hooks.EnsureAsync(row.Id, default);
        var again = await hooks.EnsureAsync(row.Id, default);

        again.State.Should().Be(GitHookState.Confirmed);
        again.Created.Should().BeFalse();
        h.GitProviders.Hooks["acme/shop"].Should().ContainSingle();
        h.GitProviders.HookCalls.Count(c => c.Call == "create").Should().Be(1);
        h.GitProviders.HookCalls.Last().Call.Should().Be("update");
    }

    [Fact]
    public async Task A_hook_somebody_pasted_by_hand_is_adopted_and_given_the_current_secret()
    {
        using var h = new AppServiceHarness();
        var row = Repository(h);
        h.GitProviders.Hooks["acme/shop"] = [new RemoteHook("12", Address(row) + "/", false)];

        var outcome = await Hooks(h).EnsureAsync(row.Id, default);

        outcome.IsConfirmed.Should().BeTrue();
        h.GitProviders.HookCalls.Should().NotContain(c => c.Call == "create");
        h.GitProviders.Hooks["acme/shop"].Should().ContainSingle().Which.Active.Should().BeTrue();
    }

    [Fact]
    public async Task A_repository_given_by_its_address_alone_is_never_asked_about_and_says_why()
    {
        using var h = new AppServiceHarness();
        var row = Repository(h, GitProviderType.Custom);

        var ensured = await Hooks(h).EnsureAsync(row.Id, default);
        var status = await Hooks(h).StatusAsync(row.Id, default);

        ensured.State.Should().Be(GitHookState.NotPossible);
        status.State.Should().Be(GitHookState.NotPossible);
        status.Message.Should().Contain("connected Git account");
        status.MessageFa.Should().NotBeNullOrWhiteSpace();
        h.GitProviders.HookCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_public_address_for_the_panel_there_is_nowhere_to_point_a_hook()
    {
        using var h = new AppServiceHarness();
        var row = Repository(h);

        var outcome = await Hooks(h, panel: null).EnsureAsync(row.Id, default);

        outcome.State.Should().Be(GitHookState.NotPossible);
        outcome.Message.Should().Contain("PANEL_DOMAIN");
        h.GitProviders.HookCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_that_will_not_let_the_account_manage_hooks_is_a_sentence_and_nothing_is_claimed()
    {
        using var h = new AppServiceHarness();
        var row = Repository(h);
        h.GitProviders.HookFailing[Token] = new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);

        var ensured = await Hooks(h).EnsureAsync(row.Id, default);

        ensured.State.Should().Be(GitHookState.Failed);
        ensured.Message.Should().Contain("admin").And.Contain("acme/shop");
        ensured.MessageFa.Should().Contain("ادمین");
        h.Audit.Entries.Should().NotContain(e => e.Action.StartsWith("git.webhook"));
    }

    [Fact]
    public async Task An_expired_token_says_to_connect_the_account_again()
    {
        using var h = new AppServiceHarness();
        var row = Repository(h);
        h.GitProviders.HookFailing[Token] = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        var status = await Hooks(h).StatusAsync(row.Id, default);

        status.State.Should().Be(GitHookState.Failed);
        status.MessageFa.Should().Contain("اتصال Git");
    }

    [Fact]
    public async Task The_page_is_told_a_hook_is_there_only_when_the_provider_lists_it_turned_on()
    {
        using var h = new AppServiceHarness();
        var row = Repository(h);

        (await Hooks(h).StatusAsync(row.Id, default)).State.Should().Be(GitHookState.Missing, "nothing is there yet");

        h.GitProviders.Hooks["acme/shop"] = [new RemoteHook("1", Address(row), Active: false)];
        var off = await Hooks(h).StatusAsync(row.Id, default);
        off.State.Should().Be(GitHookState.Missing);
        off.Message.Should().Contain("turned off");

        h.GitProviders.Hooks["acme/shop"] = [new RemoteHook("1", Address(row), Active: true)];
        (await Hooks(h).StatusAsync(row.Id, default)).State.Should().Be(GitHookState.Confirmed);
    }

    [Fact]
    public async Task One_answer_is_held_for_a_few_minutes_and_a_set_up_replaces_it_at_once()
    {
        using var h = new AppServiceHarness();
        var row = Repository(h);
        var hooks = Hooks(h);

        (await hooks.StatusAsync(row.Id, default)).State.Should().Be(GitHookState.Missing);
        await hooks.StatusAsync(row.Id, default);
        h.GitProviders.HookCalls.Count(c => c.Call == "list").Should().Be(1, "the second page draw is answered from what was just heard");

        await hooks.EnsureAsync(row.Id, default);
        (await hooks.StatusAsync(row.Id, default)).State.Should().Be(GitHookState.Confirmed);

        h.GitProviders.Hooks["acme/shop"].Clear();
        h.Clock.UtcNow += GitHookStatusCache.ConfirmedFor + TimeSpan.FromSeconds(1);
        (await hooks.StatusAsync(row.Id, default)).State.Should().Be(GitHookState.Missing,
            "a hook deleted on the provider's side is noticed once the held answer runs out");
    }

    [Fact]
    public async Task GitLab_and_Gitea_are_asked_by_the_path_their_own_api_uses()
    {
        using var h = new AppServiceHarness();
        var gitlab = Repository(h, GitProviderType.GitLab, "https://gitlab.com/group/sub/shop.git", "glpat");
        var gitea = Repository(h, GitProviderType.Gitea, "https://code.example.com/me/app.git", "gt");
        h.Db.GitProviders.Find(gitea.GitProviderId)!.ApiBaseUrl = "https://code.example.com";
        h.Db.SaveChanges();

        (await Hooks(h).EnsureAsync(gitlab.Id, default)).IsConfirmed.Should().BeTrue();
        (await Hooks(h).EnsureAsync(gitea.Id, default)).IsConfirmed.Should().BeTrue();

        h.GitProviders.HookCalls.Where(c => c.Call == "create").Select(c => c.FullName).Should().Equal("group/sub/shop", "me/app");
    }
}
