using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project D1: "back up now" on one row of the app's Volumes tab, and what the tab says about
/// when each volume was last backed up.
///
/// <para>
/// Reuses the exact creation path sub-project E wired for the whole application —
/// <c>BackupSnapshotService.QueueAsync</c>, the same call <c>AppsController.BackupNow</c> and
/// <c>BackupCenterController.RunBackup</c> already make — called here with
/// <see cref="BackupTargetType.DockerVolume"/> and one volume's own Docker name instead of
/// <see cref="BackupTargetType.Application"/> and the app's id. The panel renders Persian by default
/// in this harness, so every assertion below reads a <c>data-</c> attribute, a route fragment, a CSS
/// class or staged content — never a sentence.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class VolumeBackupHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private App SeedApp(string slug, Guid? workspaceId = null)
    {
        var app = new App
        {
            WorkspaceId = workspaceId ?? fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app;
    }

    private Volume SeedVolume(Guid appId, string name, string mount)
    {
        var volume = new Volume { AppId = appId, Name = name, MountPath = mount };
        Panel.Seed(db => db.Volumes.Add(volume));
        return volume;
    }

    private Guid SeedRepository(Guid workspaceId)
    {
        var id = Guid.CreateVersion7();
        Panel.Seed(db => db.BackupRepositories.Add(new BackupRepository
        {
            Id = id,
            WorkspaceId = workspaceId,
            Name = "volume-backup-repo-" + id.ToString("N")[..8],
            Type = BackupRepositoryType.Local,
            Engine = BackupEngineKind.Native,
            Status = BackupRepositoryStatus.Ready,
            IsEnabled = true
        }));
        return id;
    }

    /// <summary>
    /// A workspace with nothing pre-seeded, the same reason <c>InstantAppBackupHttpTests</c> gives
    /// several of its own scenarios one: a test that counts rows or asserts an exact absence cannot
    /// share the fixture's workspace with every other test in this collection.
    /// </summary>
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
        return SeedApp(slug, workspaceId);
    }

    // ---- pressing "back up now" reuses the module's own creation path, scoped to one volume ------

    [Fact]
    public async Task Backing_up_one_volume_names_only_that_volume_and_not_its_sibling()
    {
        var workspaceId = Guid.CreateVersion7();
        var app = await SeedFreshWorkspaceAppAsync(workspaceId, "volume-siblings");
        var data = SeedVolume(app.Id, "volume-siblings-data", "/data");
        var logs = SeedVolume(app.Id, "volume-siblings-logs", "/logs");
        SeedRepository(workspaceId);
        Panel.GivenUser(workspaceId, "volume-siblings@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.220", "volume-siblings@example.com");

        var response = await client.PostFormAsync($"/apps/{app.Id}/volumes/{data.Id}/backup",
            await client.AntiforgeryTokenFrom($"/apps/{app.Id}/volumes"));

        response.RedirectPath().Should().Be($"/apps/{app.Id}/volumes",
            "the outcome lands back on the tab that renders _Shell's TempData banner");

        var snapshots = Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters()
            .Where(s => s.WorkspaceId == workspaceId && s.TargetType == BackupTargetType.DockerVolume)
            .ToList());

        snapshots.Should().ContainSingle(s => s.TargetRef == data.Name,
            "the pressed volume's own Docker name is what the module was told to read");
        snapshots.Should().NotContain(s => s.TargetRef == logs.Name,
            "a volume's backup must not also name its sibling — pressing one row must not sweep in the other");
    }

    [Fact]
    public async Task Pressing_back_up_now_with_no_repository_writes_nothing_and_explains_on_the_volumes_tab()
    {
        var workspaceId = Guid.CreateVersion7();
        var app = await SeedFreshWorkspaceAppAsync(workspaceId, "volume-no-repo");
        var volume = SeedVolume(app.Id, "volume-no-repo-data", "/data");
        Panel.GivenUser(workspaceId, "volume-no-repo@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.221", "volume-no-repo@example.com");

        var before = Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters().Count());

        var response = await client.PostFormAsync($"/apps/{app.Id}/volumes/{volume.Id}/backup",
            await client.AntiforgeryTokenFrom($"/apps/{app.Id}/volumes"));

        response.RedirectPath().Should().Be($"/apps/{app.Id}/volumes");
        Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters().Count()).Should().Be(before,
            "an archive of nothing presented as a success is exactly what this control must not produce");

        // The banner's CSS class rather than its sentence — Persian is the default culture in this
        // harness, the same reasoning every other test in this file uses.
        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        html.Should().Contain("bg-danger-soft",
            "the refusal must actually render on the tab this redirects to, the seam that produced two Criticals in sub-project A");
    }

    // ---- the tab says when a volume was last backed up, and says so honestly when it never was ----

    [Fact]
    public async Task A_volume_with_no_backup_history_says_so_on_the_tab()
    {
        var workspaceId = Guid.CreateVersion7();
        var app = await SeedFreshWorkspaceAppAsync(workspaceId, "volume-never");
        SeedVolume(app.Id, "volume-never-data", "/data");
        Panel.GivenUser(workspaceId, "volume-never@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.222", "volume-never@example.com");

        var html = await (await client.GetAsync($"/apps/{app.Id}/volumes")).Content.ReadAsStringAsync();

        html.Should().Contain("data-volume-backup-state=\"never\"",
            "a volume that has never completed a backup must say so, not render a blank that reads as a bug");
        html.Should().Contain("data-volume-last-backup=\"\"",
            "there is no timestamp to show for a volume that has never been backed up");
    }

    [Fact]
    public async Task A_volume_that_has_completed_a_backup_says_when()
    {
        var workspaceId = Guid.CreateVersion7();
        var app = await SeedFreshWorkspaceAppAsync(workspaceId, "volume-done");
        var volume = SeedVolume(app.Id, "volume-done-data", "/data");
        var repositoryId = SeedRepository(workspaceId);
        var completedAt = DateTimeOffset.UtcNow.AddHours(-3);
        Panel.Seed(db => db.BackupSnapshots.Add(new BackupSnapshot
        {
            WorkspaceId = workspaceId,
            RepositoryId = repositoryId,
            TargetType = BackupTargetType.DockerVolume,
            TargetRef = volume.Name,
            Status = BackupSnapshotStatus.Completed,
            StartedAt = completedAt.AddMinutes(-1),
            CompletedAt = completedAt
        }));
        Panel.GivenUser(workspaceId, "volume-done@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.223", "volume-done@example.com");

        var html = await (await client.GetAsync($"/apps/{app.Id}/volumes")).Content.ReadAsStringAsync();

        html.Should().Contain("data-volume-backup-state=\"backed-up\"",
            "a volume with a completed backup must say it has one");
        html.Should().NotContain("data-volume-last-backup=\"\"",
            "a real timestamp must be carried, not the empty marker this tab uses for 'never'");
    }

    /// <summary>
    /// A snapshot still queued, preparing or running has not backed anything up yet — the row it is
    /// for must keep reading "never" until the run actually finishes, the same way B3 would not call
    /// a container healthy from a probe that has not returned.
    /// </summary>
    [Fact]
    public async Task A_backup_still_running_does_not_count_as_backed_up_yet()
    {
        var workspaceId = Guid.CreateVersion7();
        var app = await SeedFreshWorkspaceAppAsync(workspaceId, "volume-running");
        var volume = SeedVolume(app.Id, "volume-running-data", "/data");
        var repositoryId = SeedRepository(workspaceId);
        Panel.Seed(db => db.BackupSnapshots.Add(new BackupSnapshot
        {
            WorkspaceId = workspaceId,
            RepositoryId = repositoryId,
            TargetType = BackupTargetType.DockerVolume,
            TargetRef = volume.Name,
            Status = BackupSnapshotStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        }));
        Panel.GivenUser(workspaceId, "volume-running@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.227", "volume-running@example.com");

        var html = await (await client.GetAsync($"/apps/{app.Id}/volumes")).Content.ReadAsStringAsync();

        html.Should().Contain("data-volume-backup-state=\"never\"",
            "a run that has not finished has not backed anything up yet");
    }

    // ---- resolved through the app, never through a volume id or name taken bare from the route ----

    [Fact]
    public async Task Backing_up_a_volume_in_another_workspaces_app_is_refused()
    {
        var mine = SeedApp("volume-cross-mine");
        SeedRepository(fixture.WorkspaceId);
        var otherWorkspaceId = Guid.CreateVersion7();
        var theirs = await SeedFreshWorkspaceAppAsync(otherWorkspaceId, "volume-cross-theirs");
        var theirVolume = SeedVolume(theirs.Id, "volume-cross-theirs-data", "/data");
        Panel.GivenUser(fixture.WorkspaceId, "volume-cross@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.224", "volume-cross@example.com");

        // A token from a page this caller CAN see — antiforgery is tied to the session, not the
        // route, the same way InstantAppBackupHttpTests spends one app's token on a neighbouring one.
        var token = await client.AntiforgeryTokenFrom($"/apps/{mine.Id}/volumes");

        var response = await client.PostFormAsync($"/apps/{theirs.Id}/volumes/{theirVolume.Id}/backup", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an app in a workspace the caller cannot see must read as absent, not as refused differently");
        Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters()
            .Any(s => s.TargetRef == theirVolume.Name)).Should().BeFalse();
    }

    [Fact]
    public async Task A_viewer_cannot_press_back_up_now_on_a_volume()
    {
        var app = SeedApp("volume-viewer");
        var volume = SeedVolume(app.Id, "volume-viewer-data", "/data");
        SeedRepository(fixture.WorkspaceId);
        Panel.GivenUser(fixture.WorkspaceId, "volume-viewer-owner@example.com", SystemRole.Owner);
        Panel.GivenUser(fixture.WorkspaceId, "volume-viewer@example.com", SystemRole.Viewer);
        var owner = await Panel.SignedInAs("203.0.113.225", "volume-viewer-owner@example.com");
        var viewer = await Panel.SignedInAs("203.0.113.226", "volume-viewer@example.com");

        // The token itself is fine to fetch — the Volumes tab is visible to a viewer; only the POST
        // is gated, the same "read follows the list, write follows the capability" split
        // CanSeeAppAsync documents.
        var token = await viewer.AntiforgeryTokenFrom($"/apps/{app.Id}/volumes");
        var before = Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters().Count());

        var response = await viewer.PostFormAsync($"/apps/{app.Id}/volumes/{volume.Id}/backup", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "Capabilities.BackupsRun is not a viewer's capability — the same policy backups.run already gates");
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.BackupSnapshots.IgnoreQueryFilters().Count()).Should().Be(before);

        // Not used for the assertion; keeps the "owner" client from an unused-variable warning while
        // documenting that an owner in the same workspace is who WOULD be allowed to press it.
        owner.Should().NotBeNull();
    }
}
