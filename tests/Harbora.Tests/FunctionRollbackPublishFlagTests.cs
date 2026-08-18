using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Defect 3 of the 2026-08-18 functions design: a rollback never rebuilds (ADR-006) — it re-releases
/// a prior image — so <c>DeploymentPipeline</c>'s <c>_codeReadAt</c> is never set for that deployment
/// and the old <c>MarkFunctionsPublishedAsync</c> was a no-op on one. The rows' <c>HasUnpublishedChanges</c>
/// flags were then left exactly as they stood before the rollback — which reads as "live" on the
/// Details/Index chips whenever they already happened to be clean, over a container now running a
/// different image than the one those rows would build. This is this codebase's defining defect
/// pattern named elsewhere in the design doc: a panel reporting success for work it never did.
///
/// <para>
/// These run the real <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/> over a fake
/// Docker engine (<see cref="PipelineHarness"/>) — no Docker needed — so the assertion is on what the
/// pipeline actually wrote to <see cref="FunctionDefinition.HasUnpublishedChanges"/> after a real
/// rollback deployment ran, not on the flag being set by the test itself.
/// </para>
/// </summary>
public class FunctionRollbackPublishFlagTests
{
    private static FunctionDefinition GivenInlineCodeApp(PipelineHarness h, string code = "// v1")
    {
        h.App.FunctionRuntime = FunctionRuntime.CSharp;
        h.Db.SaveChanges();

        var fn = new FunctionDefinition
        {
            AppId = h.App.Id, WorkspaceId = h.Workspace.Id,
            Name = "Hello", Slug = "hello", Trigger = FunctionTrigger.Http,
            Code = code, IsEnabled = true, HasUnpublishedChanges = false
        };
        h.Db.FunctionDefinitions.Add(fn);
        h.Db.SaveChanges();
        return fn;
    }

    [Fact]
    public async Task A_normal_publish_still_clears_the_unpublished_flag()
    {
        using var h = new PipelineHarness(sourceType: AppSourceType.InlineCode);
        var fn = GivenInlineCodeApp(h);
        fn.Code = "// v2";
        fn.HasUnpublishedChanges = true;
        fn.UpdatedAt = h.Clock.UtcNow;
        h.Db.SaveChanges();

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var stored = await h.Db.FunctionDefinitions.AsNoTracking().FirstAsync(f => f.Id == fn.Id);
        stored.HasUnpublishedChanges.Should().BeFalse(
            "a deployment that actually built these rows is what 'live' is supposed to mean");
    }

    [Fact]
    public async Task Rolling_back_stops_the_chip_from_calling_stale_code_live()
    {
        using var h = new PipelineHarness(sourceType: AppSourceType.InlineCode);
        var fn = GivenInlineCodeApp(h);

        // Publish v1 for real, so there is a genuine prior artifact to roll back to.
        var v1 = h.QueueDeployment(number: 1);
        var v1Result = await h.RunAsync(v1);
        v1Result.Status.Should().Be(DeploymentStatus.Succeeded);
        (await h.Db.FunctionDefinitions.AsNoTracking().FirstAsync(f => f.Id == fn.Id))
            .HasUnpublishedChanges.Should().BeFalse("the v1 publish just built exactly these rows");

        // Edit and publish v2 — a second real deployment, so app.ActiveDeploymentId moves on and the
        // rows are clean again, now matching v2 instead of v1.
        fn.Code = "// v2";
        fn.HasUnpublishedChanges = true;
        fn.UpdatedAt = h.Clock.UtcNow;
        h.Db.SaveChanges();
        var v2 = h.QueueDeployment(number: 2);
        var v2Result = await h.RunAsync(v2);
        v2Result.Status.Should().Be(DeploymentStatus.Succeeded);
        (await h.Db.FunctionDefinitions.AsNoTracking().FirstAsync(f => f.Id == fn.Id))
            .HasUnpublishedChanges.Should().BeFalse("the v2 publish just built exactly these rows too");

        // Roll back to v1. Nobody touched the rows — they still read v2 — but the container the
        // rollback releases runs v1's image.
        var rollback = h.QueueDeployment(number: 3, rollbackTo: v1.Id);
        var rollbackResult = await h.RunAsync(rollback);

        rollbackResult.Status.Should().Be(DeploymentStatus.Succeeded);
        var appAfter = await h.Db.Apps.AsNoTracking().FirstAsync(a => a.Id == h.App.Id);
        appAfter.ActiveDeploymentId.Should().Be(rollback.Id);

        var stored = await h.Db.FunctionDefinitions.AsNoTracking().FirstAsync(f => f.Id == fn.Id);
        stored.Code.Should().Be("// v2", "a rollback rolls back the image, never the rows (§3c)");
        stored.HasUnpublishedChanges.Should().BeTrue(
            "the rows say v2 and the container that is now live was never built from them — the " +
            "chip must not call that 'live' just because nobody edited anything since");
    }
}
