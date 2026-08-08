using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Inbound Git webhooks, delivered the way a provider delivers them.
///
/// <para>
/// <c>GitWebhookScopeTests</c> already covers what the processor decides. What only a request can
/// show is the wiring around it: that the controller reads the header names GitHub, Gitea and GitLab
/// actually send, that the raw body the HMAC was computed over is the raw body the panel hashes, and
/// that a delivery carrying no session at all can still find its repository — the panel's global
/// workspace filter resolves to "no tenant" for a request with no cookie, which has silently turned
/// a valid delivery into "Unknown repository" before.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class GitWebhookHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private const string Secret = "a-shared-secret-for-this-repository";
    private const string PushBody =
        """{"ref":"refs/heads/main","after":"a1b2c3d4e5f6","head_commit":{"id":"a1b2c3d4e5f6"}}""";

    private Guid GivenRepository(string label, out Guid appId, bool withAutoDeployingApp = false)
    {
        var provider = new GitProvider
        {
            WorkspaceId = fixture.WorkspaceId,
            Name = "GitHub " + label,
            Type = GitProviderType.GitHub,
            ApiBaseUrl = "https://api.github.com"
        };
        var repository = new GitRepository
        {
            GitProviderId = provider.Id,
            FullName = $"acme/{label}",
            CloneUrl = $"https://github.com/acme/{label}.git",
            DefaultBranch = "main",
            WebhookSecret = Secret
        };
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            Name = "hook-" + label,
            Slug = "hook-" + label,
            SourceType = AppSourceType.GitRepository,
            GitRepositoryId = repository.Id,
            GitRef = "main",
            AutoDeployOnPush = true
        };
        appId = app.Id;

        Panel.Seed(db =>
        {
            db.GitProviders.Add(provider);
            db.GitRepositories.Add(repository);
            if (withAutoDeployingApp) db.Apps.Add(app);
        });

        return repository.Id;
    }

    private static StringContent Push(string body = PushBody) =>
        new(body, Encoding.UTF8, "application/json");

    private static string Hmac(string body, string secret = Secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

    private HttpRequestMessage Delivery(Guid repositoryId, params (string Header, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/git/{repositoryId}")
        {
            Content = Push()
        };
        foreach (var (header, value) in headers) request.Headers.TryAddWithoutValidation(header, value);
        return request;
    }

    [Fact]
    public async Task A_github_delivery_signed_with_the_repositorys_secret_is_accepted_with_no_session()
    {
        var repositoryId = GivenRepository("github-ok", out var appId, withAutoDeployingApp: true);
        var client = Panel.ClientFrom("203.0.113.70");

        var response = await client.SendAsync(Delivery(repositoryId,
            ("X-Hub-Signature-256", "sha256=" + Hmac(PushBody)),
            ("X-GitHub-Event", "push")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("deploymentsQueued").GetInt32().Should().Be(1);
        Panel.Deployments.Queued.Should().Contain(r => r.AppId == appId && r.CommitSha == "a1b2c3d4e5f6");
    }

    [Fact]
    public async Task A_github_signature_over_a_different_body_is_refused()
    {
        var repositoryId = GivenRepository("github-tampered", out var appId, withAutoDeployingApp: true);
        var client = Panel.ClientFrom("203.0.113.71");

        // Signed correctly — for a payload that is not the one being sent. Only a real request can
        // fail this way: the bytes the controller hashes have to be the bytes that arrived.
        var response = await client.SendAsync(Delivery(repositoryId,
            ("X-Hub-Signature-256", "sha256=" + Hmac("""{"ref":"refs/heads/main"}"""))));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.JsonAsync()).GetProperty("message").GetString()
            .Should().Be("Signature verification failed.");
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == appId);
    }

    [Fact]
    public async Task A_gitea_delivery_is_accepted_on_its_own_header()
    {
        var repositoryId = GivenRepository("gitea-ok", out _);
        var client = Panel.ClientFrom("203.0.113.72");

        var response = await client.SendAsync(Delivery(repositoryId,
            ("X-Gitea-Signature", Hmac(PushBody)),
            ("X-Gitea-Event", "push")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_gitea_delivery_signed_with_the_wrong_secret_is_refused()
    {
        var repositoryId = GivenRepository("gitea-wrong", out _);
        var client = Panel.ClientFrom("203.0.113.73");

        var response = await client.SendAsync(Delivery(repositoryId,
            ("X-Gitea-Signature", Hmac(PushBody, "the-wrong-secret"))));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_gitlab_delivery_is_accepted_on_the_shared_token_header()
    {
        var repositoryId = GivenRepository("gitlab-ok", out _);
        var client = Panel.ClientFrom("203.0.113.74");

        var response = await client.SendAsync(Delivery(repositoryId,
            ("X-Gitlab-Token", Secret),
            ("X-Gitlab-Event", "Push Hook")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_gitlab_delivery_with_the_wrong_token_is_refused()
    {
        var repositoryId = GivenRepository("gitlab-wrong", out _);
        var client = Panel.ClientFrom("203.0.113.75");

        var response = await client.SendAsync(Delivery(repositoryId, ("X-Gitlab-Token", "not-the-secret")));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.JsonAsync()).GetProperty("message").GetString()
            .Should().Be("Signature verification failed.");
    }

    [Fact]
    public async Task A_delivery_carrying_no_proof_at_all_is_refused()
    {
        var repositoryId = GivenRepository("unsigned", out var appId, withAutoDeployingApp: true);
        var client = Panel.ClientFrom("203.0.113.76");

        var response = await client.SendAsync(Delivery(repositoryId));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == appId);
    }

    [Fact]
    public async Task A_delivery_for_a_repository_that_is_not_there_says_so_and_nothing_more()
    {
        var client = Panel.ClientFrom("203.0.113.77");

        var response = await client.SendAsync(Delivery(Guid.CreateVersion7(),
            ("X-Hub-Signature-256", "sha256=" + Hmac(PushBody))));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.JsonAsync()).GetProperty("message").GetString().Should().Be("Unknown repository.");
    }

    // The "a delivery reaches a mid-install panel" case deliberately does not live here. On this
    // shared panel setup is long finished, so SetupGuardMiddleware short-circuits on its cached flag
    // and the /webhooks exemption is never the reason anything passes through — the case would pass
    // with the exemption entry deleted. It belongs on a panel that has never been set up, and it is
    // in SetupGuardHttpTests.
}
