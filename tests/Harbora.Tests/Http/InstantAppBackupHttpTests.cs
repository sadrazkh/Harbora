using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project E, Task 2: the "Back up now" control on an app's own Overview tab.
///
/// <para>
/// Reuses the backup module's existing creation path — <c>BackupSnapshotService.QueueAsync</c>, the
/// same call <c>BackupCenterController.RunBackup</c> makes for <see cref="BackupTargetType.Application"/>
/// — rather than a second way to make a backup. The panel renders Persian by default in this harness,
/// so every assertion reads a <c>data-</c> attribute or staged content, never a sentence.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class InstantAppBackupHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private App SeedApp(string slug, ServiceKind kind = ServiceKind.Web, Guid? workspaceId = null)
    {
        var app = new App
        {
            WorkspaceId = workspaceId ?? fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = kind,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app;
    }

    private void SeedVolume(Guid appId, string name, string mount) =>
        Panel.Seed(db => db.Volumes.Add(new Volume { AppId = appId, Name = name, MountPath = mount }));

    private void SeedEnvVar(Guid appId, string key, bool secret) =>
        Panel.Seed(db => db.EnvironmentVariables.Add(
            new EnvironmentVariable { AppId = appId, Key = key, Value = secret ? "sekret" : "plain", IsSecret = secret }));

    private void SeedRepository(Guid? workspaceId = null) => Panel.Seed(db => db.BackupRepositories.Add(
        new BackupRepository
        {
            WorkspaceId = workspaceId ?? fixture.WorkspaceId,
            Name = "instant-backup-repo-" + Guid.NewGuid().ToString("N")[..8],
            Type = BackupRepositoryType.Local,
            Engine = BackupEngineKind.Native,
            Status = BackupRepositoryStatus.Ready,
            IsEnabled = true
        }));

    // ---- the card states what it would capture ---------------------------------------------------

    [Fact]
    public async Task An_apps_overview_names_the_volume_the_instant_backup_would_capture()
    {
        var app = SeedApp("instant-shop");
        SeedVolume(app.Id, "instant-shop-data", "/data");
        SeedEnvVar(app.Id, "LOG_LEVEL", secret: false);
        SeedEnvVar(app.Id, "DATABASE_PASSWORD", secret: true);
        Panel.GivenUser(fixture.WorkspaceId, "instant-shop@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.230", "instant-shop@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-instant-backup-volume-count=\"1\"",
            "the app has exactly one volume, and the card is supposed to say so before anything is pressed");
        html.Should().Contain("data-instant-backup-volume=\"instant-shop-data\"",
            "the volume is named, not just counted");
        html.Should().Contain("data-instant-backup-env-count=\"2\"",
            "both variables count toward what would be captured, even though one is a secret");
    }

    [Fact]
    public async Task A_cron_app_with_no_volumes_is_told_what_a_backup_would_still_contain_rather_than_hiding_the_control()
    {
        // The same rule sub-project B3 settled for health and uptime: a Cron/ReleaseTask app has no
        // running container and may have no volumes, and the control must say what it would capture
        // rather than offering nothing or hiding.
        var app = SeedApp("instant-cron", ServiceKind.Cron);
        SeedRepository();
        Panel.GivenUser(fixture.WorkspaceId, "instant-cron@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.231", "instant-cron@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-instant-backup-volume-count=\"0\"",
            "a cron app with no volumes still has a definition worth backing up");
        html.Should().Contain("data-instant-backup-state=\"ready\"",
            "no volumes must not turn into a hidden or disabled control — only an honest one");
    }

    [Fact]
    public async Task With_no_backup_repository_configured_the_card_explains_rather_than_offering_a_dead_button()
    {
        var workspaceId = Guid.CreateVersion7();
        var app = await SeedFreshWorkspaceAppAsync(workspaceId, "instant-no-repo");
        Panel.GivenUser(workspaceId, "instant-no-repo@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.232", "instant-no-repo@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-instant-backup-state=\"no-repository\"",
            "there is nowhere to send the snapshot yet, and the card must say so rather than posting into a failure");
    }

    // ---- pressing the button reuses the backup module's own creation path ------------------------

    [Fact]
    public async Task Pressing_back_up_now_queues_an_application_snapshot_for_this_app()
    {
        // Its own workspace, not the fixture's shared one: several other tests in this file also
        // seed a BackupRepository into fixture.WorkspaceId, and this assertion needs to know exactly
        // which repository the snapshot landed in — a question the shared workspace cannot answer.
        var workspaceId = Guid.CreateVersion7();
        var app = await SeedFreshWorkspaceAppAsync(workspaceId, "instant-press");
        SeedVolume(app.Id, "instant-press-data", "/data");
        var repositoryId = SeedRepositoryReturningId(workspaceId);
        Panel.GivenUser(workspaceId, "instant-press@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.233", "instant-press@example.com");

        var response = await client.PostFormAsync($"/apps/{app.Id}/backup",
            await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}"));

        response.RedirectPath().Should().Be($"/Apps/Details/{app.Id}",
            "the outcome lands back on the tab that renders _Shell's TempData banner");

        var snapshot = Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters()
            .FirstOrDefault(s => s.TargetType == BackupTargetType.Application && s.TargetRef == app.Id.ToString()));
        snapshot.Should().NotBeNull("the module's own creation path was supposed to be reused, not a second one");
        snapshot!.RepositoryId.Should().Be(repositoryId);
        snapshot.WorkspaceId.Should().Be(workspaceId);
    }

    [Fact]
    public async Task Pressing_back_up_now_with_no_repository_writes_nothing_and_explains_why()
    {
        var workspaceId = Guid.CreateVersion7();
        var app = await SeedFreshWorkspaceAppAsync(workspaceId, "instant-press-no-repo");
        Panel.GivenUser(workspaceId, "instant-press-no-repo@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.234", "instant-press-no-repo@example.com");

        var before = Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters().Count());

        var response = await client.PostFormAsync($"/apps/{app.Id}/backup",
            await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}"));

        response.RedirectPath().Should().Be($"/Apps/Details/{app.Id}");
        Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters().Count()).Should().Be(before,
            "an archive of nothing presented as a success is exactly what this control must not produce");
    }

    [Fact]
    public async Task An_app_in_another_workspace_404s_rather_than_backing_up()
    {
        var mine = SeedApp("instant-cross-mine");
        SeedRepository();
        var otherWorkspaceId = Guid.CreateVersion7();
        var theirs = await SeedFreshWorkspaceAppAsync(otherWorkspaceId, "instant-cross-theirs");
        Panel.GivenUser(fixture.WorkspaceId, "instant-cross@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.235", "instant-cross@example.com");

        // A token from a page this caller CAN see — antiforgery is tied to the session, not the
        // route, the same way AppAddressHttpTests spends one page's token on a neighbouring action.
        var token = await client.AntiforgeryTokenFrom($"/apps/details/{mine.Id}");

        var response = await client.PostFormAsync($"/apps/{theirs.Id}/backup", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an app in a workspace the caller cannot see must read as absent, not as refused");
        Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters()
            .Any(s => s.TargetRef == theirs.Id.ToString())).Should().BeFalse();
    }

    [Fact]
    public async Task A_viewer_cannot_press_back_up_now()
    {
        var app = SeedApp("instant-viewer");
        SeedRepository();
        Panel.GivenUser(fixture.WorkspaceId, "instant-viewer-owner@example.com", SystemRole.Owner);
        Panel.GivenUser(fixture.WorkspaceId, "instant-viewer@example.com", SystemRole.Viewer);
        var owner = await Panel.SignedInAs("203.0.113.236", "instant-viewer-owner@example.com");
        var viewer = await Panel.SignedInAs("203.0.113.237", "instant-viewer@example.com");

        // The token itself is fine to fetch — Overview is visible to a viewer; only the POST is
        // gated, the same "read follows the list, write follows the capability" split CanSeeAppAsync
        // documents.
        var token = await viewer.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var before = Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters().Count());

        var response = await viewer.PostFormAsync($"/apps/{app.Id}/backup", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "Capabilities.BackupsRun is not a viewer's capability — the same policy backups.run already gates");
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters().Count()).Should().Be(before);

        // Not used for the assertion; keeps the "owner" client from an unused-variable warning while
        // documenting that an owner in the same workspace is who WOULD be allowed to press it.
        owner.Should().NotBeNull();
    }

    // ---- fixtures ----------------------------------------------------------------------------------

    /// <summary>A workspace with nothing pre-seeded, for the "no repository at all" scenarios.</summary>
    private async Task<App> SeedFreshWorkspaceAppAsync(Guid workspaceId, string slug)
    {
        Panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
            {
                Id = workspaceId,
                Name = slug,
                Slug = "ws-" + slug,
                IsDefault = false,
                PlanId = planId == Guid.Empty ? null : planId
            });
        });
        await Task.CompletedTask;
        return SeedApp(slug, workspaceId: workspaceId);
    }

    private Guid SeedRepositoryReturningId(Guid? workspaceId = null)
    {
        var id = Guid.CreateVersion7();
        Panel.Seed(db => db.BackupRepositories.Add(new BackupRepository
        {
            Id = id,
            WorkspaceId = workspaceId ?? fixture.WorkspaceId,
            Name = "instant-backup-repo-" + id.ToString("N")[..8],
            Type = BackupRepositoryType.Local,
            Engine = BackupEngineKind.Native,
            Status = BackupRepositoryStatus.Ready,
            IsEnabled = true
        }));
        return id;
    }
}
