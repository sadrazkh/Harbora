using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Registries;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 1.3 (2026-09 market-gaps round two): private-registry pull credentials, proven at the seam
/// <c>DeploymentPipeline</c> actually pulls through — <see cref="FakeDockerEngine"/>. A
/// <c>PrebuiltImage</c> app whose image lives on a registry with a stored credential must have that
/// credential handed to the pull; an app whose image is on some other registry must not, even when a
/// credential exists for a different host in the same workspace. The classifier's own three-plus-one
/// distinguishable failures (<see cref="RegistryPullDiagnosticsTests"/>) are proven end to end here
/// too, so a deployment's own stored <c>ErrorMessage</c> — the thing a customer actually reads —
/// carries the named failure, not a generic "pull failed".
/// </summary>
public class DeploymentPipelineRegistryCredentialTests
{
    [Fact]
    public async Task A_pull_for_a_matching_registry_carries_the_stored_credential()
    {
        using var h = new PipelineHarness();
        h.App.PrebuiltImage = "ghcr.io/acme/private-app:1.0";
        h.Db.SaveChanges();
        h.Db.RegistryCredentials.Add(new RegistryCredential
        {
            WorkspaceId = h.Workspace.Id, RegistryHost = "ghcr.io",
            Username = "acme-bot", EncryptedSecret = h.Protector.Protect("ghp_supersecret")
        });
        h.Db.SaveChanges();
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.PullCredentials.Should().ContainKey("ghcr.io/acme/private-app:1.0");
        var credential = h.Docker.PullCredentials["ghcr.io/acme/private-app:1.0"];
        credential.Should().NotBeNull();
        credential!.Registry.Should().Be("ghcr.io");
        credential.Username.Should().Be("acme-bot");
        credential.Secret.Should().Be("ghp_supersecret", "the pull must receive the decrypted secret, not the ciphertext");
    }

    [Fact]
    public async Task A_pull_for_a_registry_with_no_stored_credential_carries_none()
    {
        using var h = new PipelineHarness();
        h.App.PrebuiltImage = "quay.io/acme/public-app:1.0";
        h.Db.SaveChanges();
        // A credential exists in this same workspace, but for a different registry host — it must
        // never leak onto a pull for a host it was not stored for.
        h.Db.RegistryCredentials.Add(new RegistryCredential
        {
            WorkspaceId = h.Workspace.Id, RegistryHost = "ghcr.io",
            Username = "acme-bot", EncryptedSecret = h.Protector.Protect("ghp_supersecret")
        });
        h.Db.SaveChanges();
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.PullCredentials.Should().ContainKey("quay.io/acme/public-app:1.0");
        h.Docker.PullCredentials["quay.io/acme/public-app:1.0"].Should().BeNull(
            "a credential stored for ghcr.io must not be handed to a pull from quay.io");
    }

    [Fact]
    public async Task A_pull_with_no_credentials_configured_at_all_carries_none()
    {
        // The ordinary, pre-1.3 case: a public image, nothing configured for its registry.
        using var h = new PipelineHarness(); // defaults to PrebuiltImage "nginx:1.27" (docker.io)
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.PullCredentials["nginx:1.27"].Should().BeNull();
    }

    [Fact]
    public async Task A_registry_rejecting_configured_credentials_fails_the_deploy_by_that_exact_name()
    {
        using var h = new PipelineHarness();
        h.App.PrebuiltImage = "ghcr.io/acme/private-app:1.0";
        h.Db.SaveChanges();
        h.Db.RegistryCredentials.Add(new RegistryCredential
        {
            WorkspaceId = h.Workspace.Id, RegistryHost = "ghcr.io",
            Username = "acme-bot", EncryptedSecret = h.Protector.Protect("wrong-token")
        });
        h.Db.SaveChanges();
        h.Docker.PullRawFailures["ghcr.io/acme/private-app:1.0"] = "unauthorized: authentication required";
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("ghcr.io");
        result.ErrorMessage.Should().Contain("rejected the credentials",
            "credentials WERE supplied and the registry refused them — this must not read as 'no credentials configured'");
    }

    [Fact]
    public async Task A_registry_demanding_auth_with_nothing_configured_fails_the_deploy_naming_that_exact_gap()
    {
        using var h = new PipelineHarness();
        h.App.PrebuiltImage = "ghcr.io/acme/private-app:1.0";
        h.Db.SaveChanges();
        // Deliberately no RegistryCredential row at all for ghcr.io.
        h.Docker.PullRawFailures["ghcr.io/acme/private-app:1.0"] = "401 Unauthorized";
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("ghcr.io");
        result.ErrorMessage.Should().Contain("no credentials are configured",
            "nothing was configured for this registry — this must not read as rejected credentials");
    }

    [Fact]
    public async Task A_clean_not_found_answer_fails_the_deploy_naming_the_image_not_a_credential_problem()
    {
        using var h = new PipelineHarness();
        h.App.PrebuiltImage = "quay.io/acme/typo-d-app:1.0";
        h.Db.SaveChanges();
        h.Docker.PullRawFailures["quay.io/acme/typo-d-app:1.0"] =
            "manifest unknown: manifest tagged by \"1.0\" is not found";
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("quay.io");
        result.ErrorMessage.Should().Contain("does not exist");
    }

    [Fact]
    public async Task A_registry_answer_that_cannot_be_told_apart_says_so_instead_of_guessing()
    {
        using var h = new PipelineHarness();
        h.App.PrebuiltImage = "docker.io/acme/maybe-private:1.0";
        h.Db.SaveChanges();
        // Docker's own real daemon message for a private-repo pull with no/wrong credentials.
        h.Docker.PullRawFailures["docker.io/acme/maybe-private:1.0"] =
            "pull access denied for acme/maybe-private, repository does not exist or may require 'docker login'";
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("does not distinguish",
            "the registry's own answer does not say which it is — the failure must admit that rather than picking one");
    }
}
