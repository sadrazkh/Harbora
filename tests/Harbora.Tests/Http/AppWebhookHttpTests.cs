using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The app page's auto-deploy block as requests: it says pushes deploy only when the provider lists
/// Harbora's hook, and otherwise keeps the paste with the reason above it. Here because this panel is the
/// one with a fake provider API in it; each test has a workspace of its own.
/// </summary>
public partial class LaunchHttpTests
{
    private const string HookToken = "gho_webhook-account";

    private async Task<(HttpClient Client, App App, GitRepository Repository)> AppOnRepositoryAsync(
        GitProviderType type, string slug, SystemRole role = SystemRole.Owner)
    {
        var workspaceId = _fixture.GivenEmptyWorkspace($"hook-{slug}");
        string encrypted;
        using (var scope = _fixture.Panel.Services.CreateScope())
            encrypted = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect(HookToken);

        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var repository = new GitRepository
        {
            Provider = new GitProvider
            {
                WorkspaceId = workspaceId, Name = type.ToString(), Type = type,
                EncryptedCredential = type == GitProviderType.Custom ? null : encrypted
            },
            FullName = "acme/shop", CloneUrl = "https://github.com/acme/shop.git", DefaultBranch = "main",
            WebhookSecret = $"secret-{slug}"
        };
        var app = new App
        {
            WorkspaceId = workspaceId, ServerId = Guid.CreateVersion7(), EnvironmentId = environmentId,
            Name = slug, Slug = $"hook-{slug}", Kind = ServiceKind.Web, SourceType = AppSourceType.GitRepository,
            GitRepository = repository, GitRef = "main", Status = AppStatus.Running
        };
        _fixture.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project { Id = projectId, WorkspaceId = workspaceId, Name = "Shop", Slug = $"hook-{slug}" });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = workspaceId, ProjectId = projectId, Name = "Production", Slug = "production", IsDefault = true
            });
            db.Apps.Add(app);
        });

        var client = await _fixture.SignedInAs(workspaceId, $"hook-{slug}-{Interlocked.Increment(ref _nextUser)}@example.com", role);
        return (client, app, repository);
    }

    private static string HookAddress(GitRepository repository) => $"https://panel.example.com/webhooks/git/{repository.Id}";

    [Fact]
    public async Task A_repository_given_by_its_address_keeps_the_paste_and_says_why()
    {
        var (client, app, repository) = await AppOnRepositoryAsync(GitProviderType.Custom, "custom");

        var html = await PageAsync(client, $"/apps/details/{app.Id}");

        html.Should().Contain("data-app-webhook=\"NotPossible\"");
        html.Should().Contain("استقرار خودکار (Git)");
        html.Should().Contain("نه از راه یک حساب Git وصل‌شده");
        html.Should().Contain($"/webhooks/git/{repository.Id}").And.Contain("در تنظیمات مخزن قرار دهید");
        html.Should().NotContain("وب‌هوک را بگذار", "there is no account to put it there with");
    }

    [Fact]
    public async Task A_hook_the_provider_lists_is_said_as_pushes_deploying_with_nothing_to_paste()
    {
        var (client, app, repository) = await AppOnRepositoryAsync(GitProviderType.GitHub, "confirmed");
        _fixture.GitProviders.Hooks["acme/shop"] = [new RemoteHook("1", HookAddress(repository), true)];

        var html = await PageAsync(client, $"/apps/details/{app.Id}");

        html.Should().Contain("data-app-webhook=\"Confirmed\"");
        html.Should().Contain("وب‌هوک Harbora را روی acme/shop دارد");
        html.Should().NotContain("در تنظیمات مخزن قرار دهید", "nothing is asked to be pasted");
        html.Should().NotContain($"secret-confirmed");
        _fixture.GitProviders.Hooks.Remove("acme/shop");
    }

    [Fact]
    public async Task With_no_hook_yet_the_button_puts_one_there_and_the_page_then_says_so()
    {
        var (client, app, repository) = await AppOnRepositoryAsync(GitProviderType.GitHub, "missing");
        _fixture.GitProviders.Hooks.Remove("acme/shop");

        var before = await PageAsync(client, $"/apps/details/{app.Id}");
        before.Should().Contain("data-app-webhook=\"Missing\"").And.Contain("وب‌هوک را بگذار");
        before.Should().Contain("در تنظیمات مخزن قرار دهید", "until it is there, pasting is still the way");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var response = await client.PostFormAsync($"/git/repos/{repository.Id}/webhook", token, ("back", $"/apps/details/{app.Id}"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/apps/details/{app.Id}");
        _fixture.GitProviders.Hooks["acme/shop"].Should().ContainSingle().Which.Url.Should().Be(HookAddress(repository));
        _fixture.GitProviders.HookCalls.Last(c => c.Call == "create").Spec!.Secret.Should().Be("secret-missing");

        var after = await PageAsync(client, $"/apps/details/{app.Id}");
        after.Should().Contain("data-app-webhook=\"Confirmed\"");
        _fixture.GitProviders.Hooks.Remove("acme/shop");
    }

    [Fact]
    public async Task Another_workspaces_repository_cannot_have_a_hook_set_up_from_here()
    {
        var (_, _, repository) = await AppOnRepositoryAsync(GitProviderType.GitHub, "theirs");
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostFormAsync($"/git/repos/{repository.Id}/webhook", token, ("back", "/"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _fixture.GitProviders.HookCalls.Should().NotContain(c => c.Call == "create" && c.Spec!.Url == HookAddress(repository));
    }

    [Fact]
    public async Task A_rotated_secret_reaches_the_hook_in_the_same_press_so_pushes_keep_being_accepted()
    {
        var (client, _, repository) = await AppOnRepositoryAsync(GitProviderType.GitHub, "rotated");
        _fixture.GitProviders.Hooks["acme/shop"] = [new RemoteHook("1", HookAddress(repository), true)];
        var token = await client.AntiforgeryTokenFrom("/git");

        (await client.PostFormAsync($"/git/repos/{repository.Id}/rotate-secret", token)).StatusCode.Should().Be(HttpStatusCode.Found);

        string stored;
        using (var scope = _fixture.Panel.Services.CreateScope())
            stored = scope.ServiceProvider.GetRequiredService<Harbora.Data.HarboraDbContext>().GitRepositories
                .IgnoreQueryFilters().Single(r => r.Id == repository.Id).WebhookSecret;
        stored.Should().NotBe("secret-rotated");
        var update = _fixture.GitProviders.HookCalls.Last(c => c.Call == "update");
        update.Spec!.Url.Should().Be(HookAddress(repository));
        update.Spec.Secret.Should().Be(stored);
        _fixture.GitProviders.Hooks.Remove("acme/shop");
    }
}
