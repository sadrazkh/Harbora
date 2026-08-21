using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Email;
using Harbora.Domain.Functions;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F6 (2026-08-21 functions-and-services plan, HARBORA-0038 phase 1): injection proven at the seam
/// the plan asked for — the real <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/>
/// run over the fake Docker engine, asserting on the <c>Env</c> dictionary the container actually
/// received (<see cref="FakeDockerEngine.RunRequests"/>), not on a helper's return value. Mirrors
/// <c>StorageBucketPipelineTests</c> (F5) exactly, one attachment kind later.
/// <see cref="EmailProviderMergeTests"/> covers the precedence rules themselves at the same seam.
/// </summary>
public class EmailProviderPipelineTests
{
    private static EmailProvider GivenProvider(
        PipelineHarness h, string name, string host = "smtp.sendgrid.net", string passwordPlaintext = "s3cret") =>
        new()
        {
            WorkspaceId = h.Workspace.Id, Name = name, Host = host, Port = 587, Username = "apikey",
            EncryptedPassword = h.Protector.Protect(passwordPlaintext),
            FromAddress = "noreply@acme.example", FromName = "Acme", UseSsl = true
        };

    private static AppEmailProvider Attach(PipelineHarness h, EmailProvider provider, int order, bool unpublished = true)
    {
        h.Db.EmailProviders.Add(provider);
        var join = new AppEmailProvider
        {
            AppId = h.App.Id, EmailProviderId = provider.Id, AttachOrder = order, HasUnpublishedChanges = unpublished
        };
        h.Db.AppEmailProviders.Add(join);
        h.Db.SaveChanges();
        return join;
    }

    [Fact]
    public async Task An_attached_providers_six_variables_all_reach_the_actual_container_environment()
    {
        using var h = new PipelineHarness();
        var provider = GivenProvider(h, "SendGrid");
        Attach(h, provider, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("SMTP_HOST").WhoseValue.Should().Be("smtp.sendgrid.net");
        run.Env.Should().ContainKey("SMTP_PORT").WhoseValue.Should().Be("587");
        run.Env.Should().ContainKey("SMTP_USER").WhoseValue.Should().Be("apikey");
        run.Env.Should().ContainKey("SMTP_FROM").WhoseValue.Should().Be("Acme <noreply@acme.example>");
        run.Env.Should().ContainKey("SMTP_SECURE").WhoseValue.Should().Be("true");
    }

    [Fact]
    public async Task The_providers_password_reaches_the_container_decrypted_exactly_once()
    {
        using var h = new PipelineHarness();
        var provider = GivenProvider(h, "SendGrid", passwordPlaintext: "correct-horse-battery-staple");
        Attach(h, provider, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["SMTP_PASSWORD"].Should().Be("correct-horse-battery-staple",
            "the container needs the plaintext, decrypted from the provider's ciphertext exactly once");
    }

    [Fact]
    public async Task The_apps_own_variable_reaches_the_container_over_a_provider_defining_the_same_key()
    {
        using var h = new PipelineHarness();
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable { AppId = h.App.Id, Key = "SMTP_HOST", Value = "hand-picked.example" });
        h.Db.SaveChanges();
        var provider = GivenProvider(h, "SendGrid");
        Attach(h, provider, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["SMTP_HOST"].Should().Be("hand-picked.example", "the app's own variable must win over any provider in the actual run request");
    }

    [Fact]
    public async Task A_successful_deploy_clears_the_unpublished_flag_on_the_attached_provider()
    {
        using var h = new PipelineHarness();
        var provider = GivenProvider(h, "SendGrid");
        var join = Attach(h, provider, order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var stored = await h.Db.AppEmailProviders.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeFalse(
            "this deployment's container was built from the provider's current credentials, so it is applied");
    }

    [Fact]
    public async Task A_failed_deployment_leaves_the_providers_unpublished_flag_set()
    {
        using var h = new PipelineHarness().WithDomain().WithHealthPath();
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;
        var provider = GivenProvider(h, "SendGrid");
        var join = Attach(h, provider, order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        var stored = await h.Db.AppEmailProviders.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeTrue(
            "nothing actually shipped with this provider's credentials, so the stale flag must not be cleared");
    }

    /// <summary>
    /// The bucket-side precedent (F5's own acceptance criterion) applies unchanged: a function app is
    /// an ordinary <c>App</c> row (<c>AppSourceType.InlineCode</c>) that goes through the same
    /// <c>BuildEnv</c>, so nothing email-specific has to be written for a function host to receive
    /// SMTP_* — proven the same way <c>StorageBucketPipelineTests</c> proves it for buckets.
    /// </summary>
    [Fact]
    public async Task A_function_app_receives_an_attached_providers_env_the_same_way_any_other_app_does()
    {
        using var h = new PipelineHarness(sourceType: AppSourceType.InlineCode);
        h.App.FunctionRuntime = FunctionRuntime.CSharp;
        h.Db.SaveChanges();
        h.Db.FunctionDefinitions.Add(new FunctionDefinition
        {
            AppId = h.App.Id, WorkspaceId = h.Workspace.Id,
            Name = "Hello", Slug = "hello", Trigger = FunctionTrigger.Http,
            Code = "// v1", IsEnabled = true, HasUnpublishedChanges = false
        });
        h.Db.SaveChanges();
        var provider = GivenProvider(h, "fn-sendgrid");
        Attach(h, provider, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("SMTP_HOST",
            "a function app is an ordinary App row and goes through the same BuildEnv — provider env reaches it for free");
        run.Env.Should().ContainKey("SMTP_USER");
        run.Env.Should().ContainKey("SMTP_PASSWORD");
    }

    [Fact]
    public async Task Rolling_back_still_applies_the_providers_current_credentials_because_env_is_never_baked_into_the_image()
    {
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        var provider = GivenProvider(h, "SendGrid", passwordPlaintext: "v1-secret");
        var join = Attach(h, provider, order: 1, unpublished: false);

        // The credential rotates after v1 shipped — same as EmailProvidersController.Update rotating
        // a provider's password.
        var stored = await h.Db.EmailProviders.FirstAsync(p => p.Id == provider.Id);
        stored.EncryptedPassword = h.Protector.Protect("v2-secret");
        join.HasUnpublishedChanges = true;
        h.Db.SaveChanges();

        var rollback = h.QueueDeployment(number: 2, rollbackTo: h.App.ActiveDeploymentId);
        var result = await h.RunAsync(rollback);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["SMTP_PASSWORD"].Should().Be("v2-secret",
            "unlike function code, env is assembled fresh at run time regardless of which image is running");

        var storedJoin = await h.Db.AppEmailProviders.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        storedJoin.HasUnpublishedChanges.Should().BeFalse("the rollback's container really was built with v2's secret");
    }
}
