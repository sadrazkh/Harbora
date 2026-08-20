using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Domain.Storage;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F5 (2026-08-21 functions-and-services plan): the plan itself, following the 2026-08-20
/// platform-options plan's own lesson, warns that a merge proven right in isolation is worse than no
/// merge if the pipeline wires it up wrong — so these run the real
/// <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/> over the fake Docker engine and
/// assert on the <c>Env</c> dictionary the container actually received
/// (<see cref="FakeDockerEngine.RunRequests"/>), not on a helper's return value. Mirrors
/// <c>ConfigGroupPipelineTests</c> exactly. <see cref="StorageBucketMergeTests"/> covers the
/// precedence rules themselves at the same seam.
/// </summary>
public class StorageBucketPipelineTests
{
    private static StorageBucket GivenBucket(
        PipelineHarness h, string name, string accessKey = "AKIATEST", string secretPlaintext = "s3cret") =>
        new()
        {
            WorkspaceId = h.Workspace.Id, Name = name, AccessKey = accessKey,
            // Ciphertext from the harness's own protector, so the value below proves BuildEnv really
            // does decrypt through to the plaintext for the container. NOTE: this harness's fake
            // protector (Fakes/PipelineFakes.PassthroughProtector) tolerates being called twice — its
            // Unprotect only strips a "|nonce:" marker if present and returns anything else unchanged
            // — so a double-decrypt regression here would NOT fail this specific assertion.
            // StorageBucketSecretDecryptionTests proves that hazard against the real AesGcmSecretProtector
            // instead, where a second Unprotect call throws rather than being harmless.
            EncryptedSecretKey = h.Protector.Protect(secretPlaintext), Status = BucketStatus.Ready
        };

    private static AppStorageBucket Attach(PipelineHarness h, StorageBucket bucket, int order, bool unpublished = true)
    {
        h.Db.StorageBuckets.Add(bucket);
        var join = new AppStorageBucket
        {
            AppId = h.App.Id, StorageBucketId = bucket.Id, AttachOrder = order, HasUnpublishedChanges = unpublished
        };
        h.Db.AppStorageBuckets.Add(join);
        h.Db.SaveChanges();
        return join;
    }

    [Fact]
    public async Task An_attached_buckets_four_variables_all_reach_the_actual_container_environment()
    {
        using var h = new PipelineHarness();
        var bucket = GivenBucket(h, "uploads");
        Attach(h, bucket, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("S3_ENDPOINT").WhoseValue.Should().Be(h.StorageOptions.PublicEndpoint,
            "a customer's app must be told the public endpoint, not the panel's own private one");
        run.Env.Should().ContainKey("S3_ACCESS_KEY").WhoseValue.Should().Be("AKIATEST");
        run.Env.Should().ContainKey("S3_BUCKET").WhoseValue.Should().Be("uploads");
    }

    [Fact]
    public async Task The_buckets_secret_key_reaches_the_container_decrypted_exactly_once()
    {
        using var h = new PipelineHarness();
        var bucket = GivenBucket(h, "uploads", secretPlaintext: "correct-horse-battery-staple");
        Attach(h, bucket, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["S3_SECRET_KEY"].Should().Be("correct-horse-battery-staple",
            "the container needs the plaintext, decrypted from the bucket's ciphertext exactly once");
    }

    [Fact]
    public async Task The_apps_own_variable_reaches_the_container_over_a_bucket_defining_the_same_key()
    {
        using var h = new PipelineHarness();
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable { AppId = h.App.Id, Key = "S3_BUCKET", Value = "hand-picked" });
        h.Db.SaveChanges();
        var bucket = GivenBucket(h, "uploads");
        Attach(h, bucket, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["S3_BUCKET"].Should().Be("hand-picked", "the app's own variable must win over any bucket in the actual run request");
    }

    [Fact]
    public async Task A_successful_deploy_clears_the_unpublished_flag_on_the_attached_bucket()
    {
        using var h = new PipelineHarness();
        var bucket = GivenBucket(h, "uploads");
        var join = Attach(h, bucket, order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var stored = await h.Db.AppStorageBuckets.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeFalse(
            "this deployment's container was built from the bucket's current credentials, so it is applied");
    }

    [Fact]
    public async Task A_failed_deployment_leaves_the_buckets_unpublished_flag_set()
    {
        using var h = new PipelineHarness().WithDomain().WithHealthPath();
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;
        var bucket = GivenBucket(h, "uploads");
        var join = Attach(h, bucket, order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        var stored = await h.Db.AppStorageBuckets.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeTrue(
            "nothing actually shipped with this bucket's credentials, so the stale flag must not be cleared");
    }

    /// <summary>
    /// F5's own acceptance criterion: "functions get it free via app env — one test proves the
    /// generated host env includes it." A function app is an ordinary <c>App</c> row
    /// (<c>AppSourceType.InlineCode</c>) that goes through this exact same <c>BuildEnv</c>, so nothing
    /// bucket-specific has to be written for a function host to receive S3_* — proven here the same
    /// way <c>FunctionRollbackPublishFlagTests</c> proves other pipeline behaviour for an inline-code
    /// app, by running the real pipeline over one and reading what the fake engine actually received.
    /// </summary>
    [Fact]
    public async Task A_function_app_receives_an_attached_buckets_env_the_same_way_any_other_app_does()
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
        var bucket = GivenBucket(h, "fn-uploads");
        Attach(h, bucket, order: 1);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("S3_BUCKET").WhoseValue.Should().Be("fn-uploads",
            "a function app is an ordinary App row and goes through the same BuildEnv — bucket env reaches it for free");
        run.Env.Should().ContainKey("S3_ACCESS_KEY");
        run.Env.Should().ContainKey("S3_SECRET_KEY");
        run.Env.Should().ContainKey("S3_ENDPOINT");
    }

    [Fact]
    public async Task Rolling_back_still_applies_the_buckets_current_credentials_because_env_is_never_baked_into_the_image()
    {
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        var bucket = GivenBucket(h, "uploads", secretPlaintext: "v1-secret");
        var join = Attach(h, bucket, order: 1, unpublished: false);

        // The credential rotates after v1 shipped — same as StorageController rotating a bucket's key.
        var stored = await h.Db.StorageBuckets.FirstAsync(b => b.Id == bucket.Id);
        stored.EncryptedSecretKey = h.Protector.Protect("v2-secret");
        join.HasUnpublishedChanges = true;
        h.Db.SaveChanges();

        var rollback = h.QueueDeployment(number: 2, rollbackTo: h.App.ActiveDeploymentId);
        var result = await h.RunAsync(rollback);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["S3_SECRET_KEY"].Should().Be("v2-secret",
            "unlike function code, env is assembled fresh at run time regardless of which image is running");

        var storedJoin = await h.Db.AppStorageBuckets.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        storedJoin.HasUnpublishedChanges.Should().BeFalse("the rollback's container really was built with v2's secret");
    }
}
