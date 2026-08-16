using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Git;
using Harbora.Infrastructure.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A webhook arrives with no session.
///
/// Its authenticity comes from an HMAC over the body, not from a signed-in user, so it must be able
/// to find its own repository while the tenant filter says the caller can see nothing. Getting this
/// wrong is silent in the worst way: the provider answers 401, retries a few times, and stops. The
/// panel shows a repository, a webhook URL and a secret, all correct, and no push ever deploys.
/// </summary>
public class GitWebhookScopeTests
{
    /// <summary>An HTTP request with no workspace claim — exactly what a provider's POST looks like.</summary>
    private sealed class AnonymousRequestScope : IWorkspaceScope
    {
        public bool IsUnscoped => false;
        public Guid WorkspaceId => Guid.Empty;
    }

    private sealed class NoDeployments : IDeploymentEngine
    {
        public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct) =>
            Task.FromResult(Guid.CreateVersion7());

        public Task CancelAsync(Guid deploymentId, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_webhook_finds_its_repository_with_nobody_signed_in()
    {
        var workspace = Guid.CreateVersion7();
        var name = "webhook-scope-" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(name).Options;

        var repositoryId = Guid.CreateVersion7();
        const string secret = "s3cr3t-webhook-key";

        // Seeded as the owning tenant, the way the panel writes it.
        using (var seed = new HarboraDbContext(options))
        {
            var provider = new GitProvider { WorkspaceId = workspace, Name = "GitHub" };
            seed.Add(provider);
            seed.Add(new GitRepository
            {
                Id = repositoryId,
                Provider = provider,
                FullName = "acme/shop",
                CloneUrl = "https://example.invalid/acme/shop.git",
                DefaultBranch = "main",
                WebhookSecret = secret
            });
            await seed.SaveChangesAsync();
        }

        // Read back the way the webhook endpoint reads it: a request, with no workspace.
        using var db = new HarboraDbContext(options, new AnonymousRequestScope());

        const string body = """{"ref":"refs/heads/main","after":"abc123"}""";
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        var processor = new GitWebhookProcessor(
            db, new NoDeployments(), previews: null!,
            Harbora.Infrastructure.Functions.NullFunctionEventBus.Instance,
            NullLogger<GitWebhookProcessor>.Instance);

        var result = await processor.ProcessAsync(repositoryId,
            new WebhookRequest(body, GitHubSignature256: "sha256=" + signature, null, null, "push", null),
            CancellationToken.None);

        // The failure this guards against reports "Unknown repository" — the repository is right
        // there, and the tenant filter hid it from the one caller that cannot be a tenant.
        result.Message.Should().NotContain("Unknown repository");
        result.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task An_app_in_a_workspace_still_deploys_on_a_push()
    {
        // The other half: finding the repository is useless if the apps hanging off it are hidden by
        // the same filter, which would turn a 401 into a silent "queued 0 deployments".
        var workspace = Guid.CreateVersion7();
        var name = "webhook-apps-" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(name).Options;

        var repositoryId = Guid.CreateVersion7();
        const string secret = "another-webhook-key";

        using (var seed = new HarboraDbContext(options))
        {
            var provider = new GitProvider { WorkspaceId = workspace, Name = "GitHub" };
            seed.Add(provider);
            seed.Add(new GitRepository
            {
                Id = repositoryId, Provider = provider, FullName = "acme/shop",
                CloneUrl = "https://example.invalid/acme/shop.git", DefaultBranch = "main", WebhookSecret = secret
            });
            seed.Add(new App
            {
                WorkspaceId = workspace, Name = "Shop", Slug = "shop",
                GitRepositoryId = repositoryId, GitRef = "main", AutoDeployOnPush = true
            });
            await seed.SaveChangesAsync();
        }

        using var db = new HarboraDbContext(options, new AnonymousRequestScope());

        const string body = """{"ref":"refs/heads/main","after":"abc123"}""";
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        var processor = new GitWebhookProcessor(
            db, new NoDeployments(), previews: null!,
            Harbora.Infrastructure.Functions.NullFunctionEventBus.Instance,
            NullLogger<GitWebhookProcessor>.Instance);

        var result = await processor.ProcessAsync(repositoryId,
            new WebhookRequest(body, "sha256=" + signature, null, null, "push", null),
            CancellationToken.None);

        result.DeploymentsQueued.Should().Be(1);
    }
}
