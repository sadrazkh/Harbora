using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Git;

/// <summary>
/// Which repository row an app made by the ordinary form hangs off: the path the form, the launch and
/// the agent all share. A pasted address that a connected account already has a row for is that
/// account's; anything else gets the provider of its own it always did.
/// </summary>
public sealed class AppRepositorySourceTests
{
    private const string Shop = "https://github.com/acme/shop.git";

    private static (GitProvider Account, GitRepository Row) Imported(AppServiceHarness h, string cloneUrl = Shop)
    {
        var account = new GitProvider
        {
            WorkspaceId = h.WorkspaceId, Name = "GitHub", Type = GitProviderType.GitHub,
            EncryptedCredential = new PassthroughProtector().Protect("gho_account")
        };
        var row = new GitRepository
        {
            Provider = account, FullName = "acme/shop", CloneUrl = cloneUrl, DefaultBranch = "main", WebhookSecret = "imported-secret"
        };
        h.Db.GitRepositories.Add(row);
        h.Db.SaveChanges();
        return (account, row);
    }

    private static Harbora.Infrastructure.Apps.AppCreateRequest FromGit(AppServiceHarness h, string url, string? token = null) =>
        h.ImageApp() with { SourceType = AppSourceType.GitRepository, CloneUrl = url, GitToken = token, GitRef = "main", PrebuiltImage = null };

    private static async Task<App> CreatedAsync(AppServiceHarness h, Harbora.Infrastructure.Apps.AppCreateRequest request)
    {
        var outcome = await h.Provisioning().CreateAsync(request, default);
        outcome.Ok.Should().BeTrue(string.Join(" | ", outcome.Refusals.Select(r => r.Message)));
        return await h.Db.Apps.IgnoreQueryFilters().Include(a => a.GitRepository!).ThenInclude(r => r.Provider)
            .SingleAsync(a => a.Id == outcome.Value!.AppId);
    }

    [Fact]
    public async Task A_pasted_address_a_connected_account_already_has_shares_that_row()
    {
        using var h = new AppServiceHarness();
        var (account, row) = Imported(h);

        var app = await CreatedAsync(h, FromGit(h, "https://github.com/Acme/shop"));

        app.GitRepositoryId.Should().Be(row.Id);
        app.GitRepository!.Provider!.Id.Should().Be(account.Id);
        h.Db.GitProviders.IgnoreQueryFilters().Should().ContainSingle("no provider of the app's own was added");
    }

    [Fact]
    public async Task A_typed_token_still_gets_a_provider_of_the_apps_own()
    {
        using var h = new AppServiceHarness();
        Imported(h);

        var app = await CreatedAsync(h, FromGit(h, Shop, token: "ghp_typed"));

        app.GitRepository!.Provider!.Type.Should().Be(GitProviderType.Custom);
        new PassthroughProtector().Unprotect(app.GitRepository.Provider.EncryptedCredential!).Should().Be("ghp_typed");
    }

    [Fact]
    public async Task An_address_nobody_connected_has_is_made_exactly_as_before()
    {
        using var h = new AppServiceHarness();
        Imported(h);

        var app = await CreatedAsync(h, FromGit(h, "https://github.com/acme/blog.git"));

        app.GitRepository!.Provider!.Type.Should().Be(GitProviderType.Custom);
        app.GitRepository.FullName.Should().Be("blog", "a provider of the app's own keeps the bare name it always had");
        app.GitRepository.Provider.EncryptedCredential.Should().BeNull();
    }

    [Fact]
    public async Task Naming_an_account_adds_a_row_under_it_with_the_full_path()
    {
        using var h = new AppServiceHarness();
        var (account, _) = Imported(h, "https://github.com/acme/other.git");

        var app = await CreatedAsync(h, FromGit(h, Shop) with { GitProviderId = account.Id });

        app.GitRepository!.GitProviderId.Should().Be(account.Id);
        app.GitRepository.FullName.Should().Be("acme/shop");
        app.GitRepository.WebhookSecret.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Naming_an_account_that_cannot_be_used_is_refused_with_a_sentence_and_nothing_is_made()
    {
        using var h = new AppServiceHarness();
        var (account, _) = Imported(h);

        var elsewhere = await h.Provisioning().CreateAsync(FromGit(h, "https://gitlab.com/acme/shop.git") with { GitProviderId = account.Id }, default);
        var unknown = await h.Provisioning().ValidateCreateAsync(FromGit(h, Shop) with { GitProviderId = Guid.CreateVersion7() }, default);

        elsewhere.Ok.Should().BeFalse();
        elsewhere.Refusals.Should().Contain(r => r.Message.Contains("no longer connected") && r.MessageFa.Contains("اتصال Git"));
        unknown.Should().Contain(r => r.Message.Contains("no longer connected"));
        h.Db.Apps.IgnoreQueryFilters().Should().BeEmpty();
    }

    // ---- the push webhook ----

    [Fact]
    public async Task An_app_on_a_connected_accounts_repository_has_its_webhook_set_up_once_whatever_shares_it()
    {
        using var h = new AppServiceHarness();
        var (account, row) = Imported(h);

        var first = await h.Provisioning().CreateAsync(FromGit(h, Shop), default);
        var second = await h.Provisioning().CreateAsync(FromGit(h, Shop) with { Name = "Shop worker" }, default);

        first.Value!.Webhook!.State.Should().Be(Harbora.Infrastructure.Git.GitHookState.Confirmed);
        first.Value.Webhook.Created.Should().BeTrue();
        second.Value!.Webhook!.IsConfirmed.Should().BeTrue();
        second.Value.Webhook.Created.Should().BeFalse("the second app shares the row, so its address, so the hook");
        h.GitProviders.Hooks["acme/shop"].Should().ContainSingle()
            .Which.Url.Should().Be($"https://{AppServiceHarness.PanelDomain}/webhooks/git/{row.Id}");
        h.GitProviders.HookCalls.Should().OnlyContain(c => c.Token == "gho_account");
        account.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_app_with_a_provider_of_its_own_asks_no_provider_anything()
    {
        using var h = new AppServiceHarness();

        var created = await h.Provisioning().CreateAsync(FromGit(h, "https://github.com/acme/blog.git"), default);

        created.Value!.Webhook.Should().BeNull("the page keeps the paste, and says why");
        h.GitProviders.HookCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_that_refuses_the_hook_does_not_undo_the_app()
    {
        using var h = new AppServiceHarness();
        Imported(h);
        h.GitProviders.HookFailing["gho_account"] = new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden);

        var created = await h.Provisioning().CreateAsync(FromGit(h, Shop), default);

        created.Ok.Should().BeTrue();
        created.Value!.Webhook!.State.Should().Be(Harbora.Infrastructure.Git.GitHookState.Failed);
        h.Db.Apps.IgnoreQueryFilters().Should().ContainSingle();
    }
}
