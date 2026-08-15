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

    // ---- Task 3: a restore that admits what it cannot do -------------------------------------------
    //
    // "A backup naming an image nobody can pull restores into nothing" — sub-project F already named
    // this exact fact for deployments (DeploymentPlanning.RollbackEligibleDeploymentIds, borrowed
    // rather than re-invented): a Rollback link reads "instant" only for a deployment whose build
    // image the pruner has not yet reclaimed, "redeploy" otherwise. An application backup's own image
    // reference is the deployment that was active when the snapshot was taken — approximated here as
    // the newest succeeded deployment at or before the snapshot's StartedAt, since that is exactly
    // when ApplicationTargetStager read App.ActiveDeploymentId — checked against the SAME eligible
    // set the Deployments tab already computes from the app's current history. These routes live
    // behind Features:Backup, off by default, so every test below runs its own panel with the flag on.

    [Fact]
    public async Task A_restore_screen_marks_an_application_snapshot_instant_when_its_captured_deployment_still_has_a_retained_image()
    {
        await using var panel = new HarboraWebFactory(backupFeatureEnabled: true);
        var workspaceId = await SeedWorkspaceAndOwnerAsync(panel, "restore-instant@example.com");
        var (app, deployments) = SeedAppWithSevenSucceededDeployments(panel, workspaceId, "restore-instant-app");
        var repositoryId = SeedRepositoryInto(panel, workspaceId);

        // Taken shortly after #3 cut over — HarboraRuntimeOptions.ImageRetentionCount defaults to 5,
        // so the active deployment (#7) plus the newest five rollback targets protects #3 through #7.
        var capturedAt = deployments[2].CreatedAt.AddMinutes(30);
        var snapshotId = SeedApplicationSnapshot(panel, workspaceId, repositoryId, app.Id, capturedAt);

        var client = await panel.SignedInAs("203.0.113.240", "restore-instant@example.com");
        var html = await (await client.GetAsync($"/backup-center/snapshots/{snapshotId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-restore-image-state=\"instant\"",
            "deployment #3 is the fifth-newest and still inside the default retention window");
    }

    [Fact]
    public async Task A_restore_screen_marks_an_application_snapshot_needing_a_redeploy_when_its_captured_deployment_has_fallen_out_of_the_retention_window()
    {
        await using var panel = new HarboraWebFactory(backupFeatureEnabled: true);
        var workspaceId = await SeedWorkspaceAndOwnerAsync(panel, "restore-redeploy@example.com");
        var (app, deployments) = SeedAppWithSevenSucceededDeployments(panel, workspaceId, "restore-redeploy-app");
        var repositoryId = SeedRepositoryInto(panel, workspaceId);

        // Taken shortly after #1 — outside the newest five, the same boundary
        // DeploymentsRollbackDepthHttpTests proves for the Deployments tab's own marker.
        var capturedAt = deployments[0].CreatedAt.AddMinutes(30);
        var snapshotId = SeedApplicationSnapshot(panel, workspaceId, repositoryId, app.Id, capturedAt);

        var client = await panel.SignedInAs("203.0.113.241", "restore-redeploy@example.com");
        var html = await (await client.GetAsync($"/backup-center/snapshots/{snapshotId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-restore-image-state=\"redeploy\"",
            "deployment #1 fell out of the newest five rollback targets, the pruner's own rule");
    }

    [Fact]
    public async Task A_restore_screen_needs_a_redeploy_when_the_app_the_snapshot_named_no_longer_exists()
    {
        await using var panel = new HarboraWebFactory(backupFeatureEnabled: true);
        var workspaceId = await SeedWorkspaceAndOwnerAsync(panel, "restore-app-gone@example.com");
        var repositoryId = SeedRepositoryInto(panel, workspaceId);
        var vanishedAppId = Guid.CreateVersion7();
        var snapshotId = SeedApplicationSnapshot(
            panel, workspaceId, repositoryId, vanishedAppId, DateTimeOffset.UtcNow.AddDays(-1));

        var client = await panel.SignedInAs("203.0.113.242", "restore-app-gone@example.com");
        var html = await (await client.GetAsync($"/backup-center/snapshots/{snapshotId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-restore-image-state=\"redeploy\"",
            "the app itself is gone — there is nothing an instant rollback could mean here");
    }

    [Fact]
    public async Task A_restore_into_another_workspaces_backup_is_refused_the_same_way_a_missing_one_would_be()
    {
        // Task 1's settled rule: RestoreRequest and RestoreJob have no destination-workspace field,
        // and BackupSnapshot's own tenant filter already scopes RestoreService.QueueAsync to the
        // caller's workspace — so a snapshot from another workspace is indistinguishable from one
        // that was never there, the same "not theirs, or not there" answer CanTouchAppAsync gives.
        await using var panel = new HarboraWebFactory(backupFeatureEnabled: true);
        var theirWorkspaceId = await SeedWorkspaceAndOwnerAsync(panel, "restore-cross-theirs@example.com");
        var repositoryId = SeedRepositoryInto(panel, theirWorkspaceId);
        var (app, _) = SeedAppWithSevenSucceededDeployments(panel, theirWorkspaceId, "restore-cross-app");
        var snapshotId = SeedApplicationSnapshot(
            panel, theirWorkspaceId, repositoryId, app.Id, DateTimeOffset.UtcNow.AddDays(-1));

        var myWorkspaceId = await SeedWorkspaceAndOwnerAsync(panel, "restore-cross-mine@example.com");
        var client = await panel.SignedInAs("203.0.113.243", "restore-cross-mine@example.com");

        var token = await client.AntiforgeryTokenFrom("/backup-center");
        var response = await client.PostFormAsync("/backup-center/restore", token,
            ("snapshotId", snapshotId.ToString()), ("destination", "/var/lib/harbora/restore/x"),
            ("conflictStrategy", "Fail"));

        response.RedirectPath().Should().Be("/backup-center");
        panel.Read(db => db.RestoreJobs.IgnoreQueryFilters().Any(r => r.SnapshotId == snapshotId))
            .Should().BeFalse("nothing in this product can express a restore that crosses a workspace boundary");
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

    // ---- Task 3 fixtures — a panel of the caller's own, not the shared fixture.Panel -------------

    /// <summary>
    /// A freshly booted panel has no workspace and has never been set up — the same reason
    /// <c>DeploymentsRollbackDepthHttpTests</c> and <c>SetupGuardHttpTests</c> seed by hand instead
    /// of using <see cref="HarboraHttpFixture"/> whenever a test owns its own panel.
    /// </summary>
    private static async Task<Guid> SeedWorkspaceAndOwnerAsync(HarboraWebFactory panel, string email)
    {
        var workspaceId = Guid.CreateVersion7();
        panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
            {
                Id = workspaceId,
                Name = "Harbora",
                Slug = "harbora-restore-" + workspaceId.ToString("N")[..8],
                IsDefault = true,
                PlanId = planId == Guid.Empty ? null : planId
            });
            db.Settings.Add(new Harbora.Domain.Settings.Setting
            { Key = Harbora.Domain.Settings.SettingKeys.SetupCompleted, Value = "true" });
        });
        panel.GivenUser(workspaceId, email, SystemRole.Owner);
        await Task.CompletedTask;
        return workspaceId;
    }

    private static Guid SeedRepositoryInto(HarboraWebFactory panel, Guid workspaceId)
    {
        var id = Guid.CreateVersion7();
        panel.Seed(db => db.BackupRepositories.Add(new BackupRepository
        {
            Id = id,
            WorkspaceId = workspaceId,
            Name = "restore-repo-" + id.ToString("N")[..8],
            Type = BackupRepositoryType.Local,
            Engine = BackupEngineKind.Native,
            Status = BackupRepositoryStatus.Ready,
            IsEnabled = true
        }));
        return id;
    }

    /// <summary>
    /// Seven succeeded deployments, one hour apart starting 2026-01-01, so a snapshot's own
    /// <c>StartedAt</c> can be placed unambiguously between any two of them. The newest (#7) is the
    /// active deployment — the same fixture shape <c>DeploymentsRollbackDepthHttpTests</c> uses for
    /// the Deployments tab's own "instant" / "redeploy" marker, reused here rather than invented
    /// again for the restore screen's version of the same question.
    /// </summary>
    private static (App App, IReadOnlyList<Deployment> Deployments) SeedAppWithSevenSucceededDeployments(
        HarboraWebFactory panel, Guid workspaceId, string slug)
    {
        var app = new App
        {
            WorkspaceId = workspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };

        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var deployments = Enumerable.Range(1, 7).Select(n => new Deployment
        {
            AppId = app.Id,
            WorkspaceId = workspaceId,
            Number = n,
            Status = DeploymentStatus.Succeeded,
            Trigger = DeploymentTrigger.Manual,
            TriggeredByUserId = Guid.CreateVersion7(),
            ImageTag = $"harbora/{slug}:build-{n}",
            CreatedAt = baseTime.AddHours(n)
        }).ToList();

        app.ActiveDeploymentId = deployments[^1].Id;

        panel.Seed(db =>
        {
            db.Apps.Add(app);
            db.Deployments.AddRange(deployments);
        });

        return (app, deployments);
    }

    /// <summary>
    /// A completed application-target snapshot, as <c>ApplicationTargetStager</c> would have left it
    /// — <paramref name="capturedAt"/> stands in for the moment it read <c>App.ActiveDeploymentId</c>.
    /// </summary>
    private static Guid SeedApplicationSnapshot(
        HarboraWebFactory panel, Guid workspaceId, Guid repositoryId, Guid appId, DateTimeOffset capturedAt)
    {
        var snapshot = new BackupSnapshot
        {
            WorkspaceId = workspaceId,
            RepositoryId = repositoryId,
            TargetType = BackupTargetType.Application,
            TargetRef = appId.ToString(),
            Status = BackupSnapshotStatus.Completed,
            EngineSnapshotId = "restore-test-snapshot",
            StartedAt = capturedAt,
            CompletedAt = capturedAt.AddMinutes(1),
            CreatedAt = capturedAt
        };
        panel.Seed(db => db.BackupSnapshots.Add(snapshot));
        return snapshot.Id;
    }
}
