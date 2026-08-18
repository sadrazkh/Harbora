using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Infrastructure.Functions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Revision history — §9's "not by the name you would have called it" search came back clean: this
/// feature did not exist anywhere for Functions under any name (the closest siblings were
/// <c>Deployment</c>'s own immutable-row-per-release history, mirrored here, and the Backup module's
/// keep-N-prune-the-rest retention, which this reimplements at save time rather than on a sweeper's
/// schedule because a function's code changes by a human pressing Save, not by traffic arriving).
///
/// <c>FunctionAppService.MaxRevisions</c> (20) is asserted by reference, never by the literal 20, so
/// this suite cannot be quietly wrong the moment somebody changes the constant for a reason of their
/// own.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionRevisionHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record World(Guid AppId, Guid FunctionId);

    private World GivenFunctionApp(string slug, string code)
    {
        var appId = Guid.CreateVersion7();
        var functionId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.FeatureGrants.Add(new FeatureGrant
            {
                Scope = FeatureScope.Workspace, TargetId = fixture.WorkspaceId,
                FeatureKey = PlatformFeatures.Functions, State = FeatureState.Enabled
            });

            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
                Name = slug, Slug = slug, SourceType = AppSourceType.InlineCode,
                FunctionRuntime = FunctionRuntime.CSharp,
                DockerfilePath = "Dockerfile.harbora"
            });

            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                Id = functionId, AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Name = "Hello", Slug = "hello", Trigger = FunctionTrigger.Http, Code = code
            });
        });

        return new World(appId, functionId);
    }

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string token, World world, string code) =>
        client.PostFormAsync(
            $"/functions/{world.AppId}/save", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "Hello"), ("Trigger", ((int)FunctionTrigger.Http).ToString()),
            ("Route", ""), ("Code", code), ("IsEnabled", "true"));

    [Fact]
    public async Task Saving_writes_a_new_immutable_revision_carrying_the_code_just_saved()
    {
        var world = GivenFunctionApp("fn-rev-write", "// v0");
        Panel.GivenUser(fixture.WorkspaceId, "fn-rev-write@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.190", "fn-rev-write@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await SaveAsync(client, token, world, "// v1");
        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var revisions = Panel.Read(db => db.FunctionCodeRevisions.AsNoTracking()
            .Where(r => r.FunctionId == world.FunctionId).ToList());

        revisions.Should().ContainSingle(r => r.Code == "// v1",
            "the code just saved must be captured as a revision, not only written onto the row");
    }

    [Fact]
    public async Task A_brand_new_functions_first_save_is_itself_a_revision()
    {
        var appId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.FeatureGrants.Add(new FeatureGrant
            {
                Scope = FeatureScope.Workspace, TargetId = fixture.WorkspaceId,
                FeatureKey = PlatformFeatures.Functions, State = FeatureState.Enabled
            });
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
                Name = "fn-rev-new", Slug = "fn-rev-new", SourceType = AppSourceType.InlineCode,
                FunctionRuntime = FunctionRuntime.CSharp, DockerfilePath = "Dockerfile.harbora"
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "fn-rev-new@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.191", "fn-rev-new@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{appId}/new");

        var response = await client.PostFormAsync(
            $"/functions/{appId}/save", token,
            ("Name", "First"), ("Trigger", ((int)FunctionTrigger.Http).ToString()),
            ("Route", ""), ("Code", "// the very first save"), ("IsEnabled", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var revision = Panel.Read(db => db.FunctionCodeRevisions.AsNoTracking()
            .Single(r => r.Code == "// the very first save"));
        revision.Should().NotBeNull("a function's own first version is worth restoring too, not only its edits");
    }

    [Fact]
    public async Task Saving_more_than_MaxRevisions_times_keeps_only_the_newest_ones()
    {
        var world = GivenFunctionApp("fn-rev-prune", "// v0");
        Panel.GivenUser(fixture.WorkspaceId, "fn-rev-prune@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.192", "fn-rev-prune@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var total = FunctionAppService.MaxRevisions + 5;
        for (var i = 1; i <= total; i++)
            (await SaveAsync(client, token, world, $"// v{i}")).StatusCode.Should().Be(HttpStatusCode.Found);

        var revisions = Panel.Read(db => db.FunctionCodeRevisions.AsNoTracking()
            .Where(r => r.FunctionId == world.FunctionId)
            .OrderByDescending(r => r.CreatedAt)
            .ToList());

        revisions.Should().HaveCount(FunctionAppService.MaxRevisions,
            "the table must stop growing at MaxRevisions per function, however many times somebody saves");
        revisions.First().Code.Should().Be($"// v{total}", "the newest save must never be the one pruned");
        revisions.Should().NotContain(r => r.Code == "// v1",
            "the oldest revisions are the ones that make room, not the newest");
    }

    [Fact]
    public async Task Restoring_an_old_revision_writes_the_code_back_and_records_its_own_new_revision()
    {
        var world = GivenFunctionApp("fn-rev-restore", "// original");
        Panel.GivenUser(fixture.WorkspaceId, "fn-rev-restore@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.193", "fn-rev-restore@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        await SaveAsync(client, token, world, "// original"); // the revision we will restore to
        await SaveAsync(client, token, world, "// a mistake");

        var target = Panel.Read(db => db.FunctionCodeRevisions.AsNoTracking()
            .First(r => r.FunctionId == world.FunctionId && r.Code == "// original"));

        var restoreToken = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");
        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/{world.FunctionId}/revisions/{target.Id}/restore", restoreToken);

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var fn = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        fn.Code.Should().Be("// original", "restoring must bring the old code back onto the live row");
        fn.HasUnpublishedChanges.Should().BeTrue("a restore is a code change like any other save");

        var revisionCount = Panel.Read(db => db.FunctionCodeRevisions.AsNoTracking()
            .Count(r => r.FunctionId == world.FunctionId && r.Code == "// original"));
        revisionCount.Should().Be(2,
            "the restore writes its own new revision rather than reusing or deleting the one it copied from");
    }

    [Fact]
    public async Task The_editor_lists_revisions_newest_first_and_marks_the_current_one()
    {
        var world = GivenFunctionApp("fn-rev-list", "// v0");
        Panel.GivenUser(fixture.WorkspaceId, "fn-rev-list@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.194", "fn-rev-list@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        // GivenFunctionApp seeds the row directly, bypassing SaveFunction — so, correctly, it seeds
        // no revision either; only a real save does that. Two real saves are what puts two rows in
        // the history.
        await SaveAsync(client, token, world, "// v1");
        await SaveAsync(client, token, world, "// v2");

        var html = await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}"))
            .Content.ReadAsStringAsync();
        var document = await ParseAsync(html);

        var rows = document.QuerySelectorAll("[data-revision-id]");
        rows.Length.Should().BeGreaterThanOrEqualTo(2, "the original save and the edit should both be listed");
        rows[0].QuerySelector("[data-revision-current]").Should().NotBeNull(
            "the newest revision is the one on screen right now, so restoring it would be a no-op");
    }

    [Fact]
    public async Task Restoring_a_revision_that_belongs_to_a_different_function_is_refused()
    {
        var world = GivenFunctionApp("fn-rev-mismatch", "// mine");
        var other = GivenFunctionApp("fn-rev-mismatch-other", "// not mine");
        Panel.GivenUser(fixture.WorkspaceId, "fn-rev-mismatch@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.195", "fn-rev-mismatch@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");
        await SaveAsync(client, token, world, "// mine, edited");
        // A real save for `other` too — GivenFunctionApp seeds the row only, not a revision.
        await SaveAsync(client, token, other, "// not mine, edited");

        var otherRevision = Panel.Read(db => db.FunctionCodeRevisions.AsNoTracking()
            .First(r => r.FunctionId == other.FunctionId));

        var restoreToken = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");
        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/{world.FunctionId}/revisions/{otherRevision.Id}/restore", restoreToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var fn = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        fn.Code.Should().Be("// mine, edited", "a mismatched revision id must change nothing");
    }
}
