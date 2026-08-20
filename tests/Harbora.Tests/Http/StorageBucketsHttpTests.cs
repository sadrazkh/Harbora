using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Buckets attaching to apps end to end (F5, 2026-08-21 functions-and-services plan) — the real
/// pipeline routes, a real cookie, real Razor. Mirrors <see cref="ConfigGroupsHttpTests"/>: provenance
/// is never hidden on the app's own env page, a secret entry stays masked, and deleting an attached
/// bucket refuses with the named list (the <c>ProjectsController.Delete</c> idiom
/// <c>ConfigGroupsController.Delete</c> already reused, now reused a second time). Precedence and the
/// actual container environment are proven at the assembly seam by
/// <c>StorageBucketMergeTests</c>/<c>StorageBucketPipelineTests</c>; this class proves the same facts
/// reach the pages a person actually reads.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class StorageBucketsHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static readonly Regex ErrorBanner = new(
        """<div class="alert-danger[^>]*>(?<text>.*?)</div>""", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string ErrorBannerText(string html)
    {
        var match = ErrorBanner.Match(html);
        match.Success.Should().BeTrue("a refused delete must render the TempData[\"Error\"] banner");
        return match.Groups["text"].Value;
    }

    /// <summary>Same shape <see cref="ConfigGroupsHttpTests"/> seeds an app with.</summary>
    private Guid SeedApp(string slug)
    {
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, ServerId = Guid.CreateVersion7(), EnvironmentId = environmentId,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0", Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "sb-" + slug
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = fixture.WorkspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
            db.Apps.Add(app);
        });
        return app.Id;
    }

    private Guid SeedBucket(string name)
    {
        var bucket = new StorageBucket
        {
            WorkspaceId = fixture.WorkspaceId, Name = name, AccessKey = "AKIA" + name,
            EncryptedSecretKey = "cipher:" + name, Status = BucketStatus.Ready
        };
        Panel.Seed(db => db.StorageBuckets.Add(bucket));
        return bucket.Id;
    }

    [Fact]
    public async Task Attaching_a_bucket_makes_its_keys_show_on_the_apps_env_page_with_provenance()
    {
        var appId = SeedApp("api");
        var bucketId = SeedBucket("uploads");
        Panel.GivenUser(fixture.WorkspaceId, "sb-attach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.240", "sb-attach@example.com");

        var token = await client.AntiforgeryTokenFrom("/storage");
        var attach = await client.PostFormAsync($"/storage/buckets/{bucketId}/attach", token,
            ("appId", appId.ToString()));
        attach.StatusCode.Should().Be(HttpStatusCode.Found);

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        html.Should().Contain("S3_ENDPOINT");
        html.Should().Contain("S3_ACCESS_KEY");
        html.Should().Contain("S3_BUCKET");
        html.Should().Contain("data-env-source=\"bucket\"", "a bucket-provided row must say it came from a bucket, not the app or a group");
        html.Should().Contain("uploads", "the row must name the specific bucket it came from");
        html.Should().Contain("data-attached-storage-bucket=\"uploads\"");
    }

    [Fact]
    public async Task The_buckets_secret_key_stays_masked_on_the_apps_env_page()
    {
        var appId = SeedApp("secretive");
        var bucketId = SeedBucket("with-secret");
        Panel.Seed(db => db.AppStorageBuckets.Add(new AppStorageBucket
        {
            AppId = appId, StorageBucketId = bucketId, AttachOrder = 1
        }));
        Panel.GivenUser(fixture.WorkspaceId, "sb-mask@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.241", "sb-mask@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("S3_SECRET_KEY");
        html.Should().NotContain("cipher:with-secret", "a bucket secret's ciphertext must never reach the page either");
        html.Should().Contain("&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;",
            "S3_SECRET_KEY masks with the same bullet every other secret env var uses");
    }

    [Fact]
    public async Task Detaching_a_bucket_removes_its_keys_from_the_apps_effective_env_page()
    {
        var appId = SeedApp("detach-me");
        var bucketId = SeedBucket("goes-away");
        Panel.Seed(db => db.AppStorageBuckets.Add(new AppStorageBucket
        {
            AppId = appId, StorageBucketId = bucketId, AttachOrder = 1
        }));
        Panel.GivenUser(fixture.WorkspaceId, "sb-detach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.242", "sb-detach@example.com");

        (await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync())
            .Should().Contain("S3_BUCKET");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        var detach = await client.PostFormAsync($"/storage/buckets/{bucketId}/detach", token,
            ("appId", appId.ToString()), ("returnUrl", $"/apps/details/{appId}"));
        detach.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.AppStorageBuckets.Any(x => x.AppId == appId && x.StorageBucketId == bucketId)).Should().BeFalse();
        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        html.Should().NotContain("S3_BUCKET");
    }

    [Fact]
    public async Task Deleting_a_bucket_still_attached_to_an_app_is_refused_and_names_the_app()
    {
        var appId = SeedApp("checkout");
        var bucketId = SeedBucket("attached-bucket");
        Panel.Seed(db => db.AppStorageBuckets.Add(new AppStorageBucket
        {
            AppId = appId, StorageBucketId = bucketId, AttachOrder = 1
        }));
        Panel.GivenUser(fixture.WorkspaceId, "sb-delete-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.243", "sb-delete-refused@example.com");

        var token = await client.AntiforgeryTokenFrom("/storage");
        var response = await client.PostFormAsync($"/storage/buckets/{bucketId}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/storage");

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("checkout",
            "the refusal must name the app blocking the delete, not merely count it");

        Panel.Read(db => db.StorageBuckets.Any(b => b.Id == bucketId)).Should().BeTrue(
            "the bucket must still exist — the delete was refused, not silently applied anyway");
    }

    /// <summary>
    /// This test fixture runs with no S3-compatible server configured (no Docker on the dev machine —
    /// see the standing note on this repo), so <c>ObjectStorageAdmin.DeleteAsync</c> always refuses
    /// with "Object storage is not configured." That is still the right seam to prove the
    /// attachment-refusal check against: an unattached bucket must reach that storage-server refusal
    /// rather than being blocked by the named-list check first, which only <see
    /// cref="Deleting_a_bucket_still_attached_to_an_app_is_refused_and_names_the_app"/>'s bucket should
    /// ever see.
    /// </summary>
    [Fact]
    public async Task An_unattached_bucket_reaches_the_storage_server_refusal_not_the_attachment_one()
    {
        var bucketId = SeedBucket("unattached-bucket");
        Panel.GivenUser(fixture.WorkspaceId, "sb-delete-clean@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.244", "sb-delete-clean@example.com");

        var token = await client.AntiforgeryTokenFrom("/storage");
        var response = await client.PostFormAsync($"/storage/buckets/{bucketId}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("Object storage is not configured",
            "with no app attached, the only reason left to refuse is the storage server itself, not the named-list check");
        Panel.Read(db => db.StorageBuckets.Any(b => b.Id == bucketId)).Should().BeTrue(
            "the row stays when the server refused, exactly as it does when the delete is blocked by an attachment");
    }

    [Fact]
    public async Task A_viewer_cannot_attach_a_bucket_to_an_app()
    {
        var appId = SeedApp("viewer-app");
        var bucketId = SeedBucket("viewer-bucket");
        Panel.GivenUser(fixture.WorkspaceId, "sb-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.245", "sb-viewer@example.com");

        var token = await client.AntiforgeryTokenFrom("/storage");
        var attachResponse = await client.PostFormAsync($"/storage/buckets/{bucketId}/attach", token,
            ("appId", appId.ToString()));
        attachResponse.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.AppStorageBuckets.Any(x => x.AppId == appId)).Should().BeFalse();
    }

    [Fact]
    public async Task No_buckets_or_attachments_cross_workspaces()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        var theirBucketId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = otherWorkspaceId, Name = "Other", Slug = "other-sb-ws" });
            db.StorageBuckets.Add(new StorageBucket
            {
                Id = theirBucketId, WorkspaceId = otherWorkspaceId, Name = "not-yours",
                AccessKey = "AKIAOTHER", EncryptedSecretKey = "cipher:other", Status = BucketStatus.Ready
            });
        });
        var appId = SeedApp("tenancy-app");
        Panel.GivenUser(fixture.WorkspaceId, "sb-tenancy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.246", "sb-tenancy@example.com");

        var html = await (await client.GetAsync("/storage")).Content.ReadAsStringAsync();
        html.Should().NotContain("not-yours");

        var token = await client.AntiforgeryTokenFrom("/storage");
        var attachAttempt = await client.PostFormAsync($"/storage/buckets/{theirBucketId}/attach", token,
            ("appId", appId.ToString()));
        attachAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another workspace's bucket id must not resolve, even for a signed-in owner of a different workspace");

        var deleteAttempt = await client.PostFormAsync($"/storage/buckets/{theirBucketId}/delete", token);
        deleteAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Panel.Read(db => db.StorageBuckets.Any(b => b.Id == theirBucketId)).Should().BeTrue("the other workspace's row must be untouched");
    }
}
