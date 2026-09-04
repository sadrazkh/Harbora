using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.ErrorTracking;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 1.8 (2026-09 market-gaps round two): injection proven at the seam the task asked for — the real
/// <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/> run over the fake Docker
/// engine, asserting on the <c>Env</c> dictionary the container actually received
/// (<see cref="FakeDockerEngine.RunRequests"/>), not on a helper's return value. Mirrors
/// <c>EmailProviderPipelineTests</c> (F6) exactly, one attachment kind later.
/// <see cref="ErrorTrackingProviderMergeTests"/> covers the precedence rules themselves at the same
/// seam.
/// </summary>
public class ErrorTrackingProviderPipelineTests
{
    private static ErrorTrackingProvider GivenProvider(
        PipelineHarness h, string name, string dsnPlaintext = "https://key@glitchtip.example/1") =>
        new()
        {
            WorkspaceId = h.Workspace.Id, Name = name,
            EncryptedDsn = h.Protector.Protect(dsnPlaintext)
        };

    private static AppErrorTrackingProvider Attach(PipelineHarness h, ErrorTrackingProvider provider, int order, bool unpublished = true)
    {
        h.Db.ErrorTrackingProviders.Add(provider);
        var join = new AppErrorTrackingProvider
        {
            AppId = h.App.Id, ErrorTrackingProviderId = provider.Id, AttachOrder = order, HasUnpublishedChanges = unpublished
        };
        h.Db.AppErrorTrackingProviders.Add(join);
        h.Db.SaveChanges();
        return join;
    }

    [Fact]
    public async Task An_attached_providers_dsn_reaches_the_actual_container_environment()
    {
        using var h = new PipelineHarness();
        var provider = GivenProvider(h, "GlitchTip");
        Attach(h, provider, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("SENTRY_DSN").WhoseValue.Should().Be("https://key@glitchtip.example/1");
    }

    [Fact]
    public async Task The_providers_dsn_reaches_the_container_decrypted_exactly_once()
    {
        using var h = new PipelineHarness();
        var provider = GivenProvider(h, "GlitchTip", dsnPlaintext: "https://correct-horse-battery-staple@glitchtip.example/9");
        Attach(h, provider, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["SENTRY_DSN"].Should().Be("https://correct-horse-battery-staple@glitchtip.example/9",
            "the container needs the plaintext, decrypted from the provider's ciphertext exactly once");
    }

    /// <summary>
    /// The explicit requirement this sub-project turns on, proven at the seam where env becomes
    /// container environment: a customer's own SENTRY_DSN — set as a plain app env var, pointing at
    /// Sentry SaaS or anywhere else — is never silently overridden by an attached, Harbora-tracked
    /// provider.
    /// </summary>
    [Fact]
    public async Task The_apps_own_sentry_dsn_reaches_the_container_over_an_attached_provider_defining_the_same_key()
    {
        using var h = new PipelineHarness();
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable
        {
            AppId = h.App.Id, Key = "SENTRY_DSN", Value = "https://own-key@sentry.io/9999"
        });
        h.Db.SaveChanges();
        var provider = GivenProvider(h, "GlitchTip");
        Attach(h, provider, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["SENTRY_DSN"].Should().Be("https://own-key@sentry.io/9999",
            "the app's own SENTRY_DSN must win over an attached provider in the actual run request");
    }

    [Fact]
    public async Task A_detached_apps_container_never_receives_sentry_dsn()
    {
        using var h = new PipelineHarness();
        var provider = GivenProvider(h, "GlitchTip");
        var join = Attach(h, provider, order: 1);
        h.Db.AppErrorTrackingProviders.Remove(join);
        h.Db.SaveChanges();

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().NotContainKey("SENTRY_DSN", "a detached app must not receive a provider's DSN at all");
    }

    [Fact]
    public async Task A_successful_deploy_clears_the_unpublished_flag_on_the_attached_provider()
    {
        using var h = new PipelineHarness();
        var provider = GivenProvider(h, "GlitchTip");
        var join = Attach(h, provider, order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var stored = await h.Db.AppErrorTrackingProviders.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeFalse(
            "this deployment's container was built from the provider's current DSN, so it is applied");
    }

    [Fact]
    public async Task A_failed_deployment_leaves_the_providers_unpublished_flag_set()
    {
        using var h = new PipelineHarness().WithDomain().WithHealthPath();
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;
        var provider = GivenProvider(h, "GlitchTip");
        var join = Attach(h, provider, order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        var stored = await h.Db.AppErrorTrackingProviders.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeTrue(
            "nothing actually shipped with this provider's DSN, so the stale flag must not be cleared");
    }
}
