using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// D2 (2026-08-25 shared-databases plan), over the real pipeline: backing up one logical database on
/// demand, importing a dump into it with the named-apps confirmation
/// <c>DatabasesController.Import</c> never had, and restoring an existing backup into the same,
/// a different, or a brand-new logical database. Mirrors <see cref="DatabaseExportImportHttpTests"/>
/// and <see cref="LogicalDatabasesHttpTests"/>: those already prove the whole-instance self-serve
/// path and logical-database creation/attach/delete end to end; this is the same shape one level
/// down, at one logical database rather than a whole instance.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class LogicalDatabaseBackupHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static readonly Regex ErrorBanner = new(
        """<div class="mb-4 rounded-lg bg-danger-soft[^>]*>(?<text>.*?)</div>""", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string ErrorBannerText(string html)
    {
        var match = ErrorBanner.Match(html);
        match.Success.Should().BeTrue("a refused request must render the TempData[\"Error\"] banner");
        return match.Groups["text"].Value;
    }

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }

    private ManagedService SeedDatabase(string name, ManagedServiceType type = ManagedServiceType.PostgreSql)
    {
        var protector = Panel.Resolve<ISecretProtector>();
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(), Name = name, Type = type,
            Version = "16", Status = ServiceStatus.Running, ContainerName = "harbora-svc-" + name,
            InternalPort = 5432, Username = "harbora",
            DatabaseName = name.Replace('-', '_'),
            VolumeName = "harbora-svc-" + name + "-data",
            EncryptedPassword = protector.Protect("logicaldb-backup-http-01")
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        return service;
    }

    private ManagedServiceDatabase SeedLogicalDatabase(ManagedService svc, string name, bool isDefault = false)
    {
        var protector = Panel.Resolve<ISecretProtector>();
        var logical = new ManagedServiceDatabase
        {
            WorkspaceId = svc.WorkspaceId, ManagedServiceId = svc.Id, Name = name,
            Username = $"{name}_user", EncryptedPassword = protector.Protect("logicaldb-backup-http-01"),
            IsDefault = isDefault
        };
        Panel.Seed(db => db.ManagedServiceDatabases.Add(logical));
        return logical;
    }

    private App SeedApp(string slug)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, ServerId = Guid.CreateVersion7(),
            EnvironmentId = fixture.DefaultEnvironmentId,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0", Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app;
    }

    private void Attach(ManagedServiceDatabase logical, App app) =>
        Panel.Seed(db => db.AppManagedServices.Add(new AppManagedService
        {
            AppId = app.Id, ManagedServiceId = logical.ManagedServiceId, ManagedServiceDatabaseId = logical.Id,
            Alias = "DB", AttachOrder = 1
        }));

    private BackupDestination SeedDestination()
    {
        var destination = new BackupDestination
        {
            WorkspaceId = fixture.WorkspaceId, Name = "local-" + Guid.NewGuid().ToString("N")[..6],
            Type = BackupDestinationType.Local, IsDefault = true
        };
        Panel.Seed(db => db.BackupDestinations.Add(destination));
        return destination;
    }

    private Backup SeedCompletedBackup(ManagedService svc, Guid databaseId, string content = "-- dump\n")
    {
        // Its own destination, never borrowed from whatever another test in this shared-fixture
        // collection happened to seed first — the collection's workspace and FakeDockerEngine persist
        // across every test class here, so nothing about "some destination already exists" is safe.
        var destination = SeedDestination();
        var path = Path.Combine(Path.GetTempPath(), "harbora-logicaldb-http-" + Guid.NewGuid().ToString("N") + ".sql.gz");
        File.WriteAllBytes(path, Gzip(content));

        var backup = new Backup
        {
            WorkspaceId = svc.WorkspaceId, DestinationId = destination.Id,
            Type = BackupType.Database, Status = BackupStatus.Completed,
            TargetRef = svc.Id.ToString(), ManagedServiceDatabaseId = databaseId, ArtifactPath = path,
            SizeBytes = new FileInfo(path).Length, FinishedAt = DateTimeOffset.UtcNow
        };
        Panel.Seed(db => db.Backups.Add(backup));
        return backup;
    }

    // ---- on-demand backup of one logical database -----------------------------------------------

    [Fact]
    public async Task Backing_up_one_logical_database_queues_a_backup_scoped_to_it()
    {
        var svc = SeedDatabase("backup-now-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        SeedDestination();
        Panel.GivenUser(fixture.WorkspaceId, "backupnow@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.170", "backupnow@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");

        var response = await client.PostFormAsync($"/databases/{svc.Id}/logical-databases/{billing.Id}/backup", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var queued = Panel.Read(db => db.Backups.Single(b => b.TargetRef == svc.Id.ToString()));
        queued.ManagedServiceDatabaseId.Should().Be(billing.Id);
        queued.ExpiresAt.Should().BeNull("an ordinary on-demand backup is retained, not time-boxed like a self-serve export");
    }

    [Fact]
    public async Task Scheduling_a_recurring_backup_of_one_logical_database_persists_its_own_id()
    {
        var svc = SeedDatabase("schedule-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        SeedDestination();
        Panel.GivenUser(fixture.WorkspaceId, "schedulelogicaldb@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.169", "schedulelogicaldb@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");

        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{billing.Id}/schedule", token,
            ("intervalHours", "24"), ("retentionCount", "7"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var schedule = Panel.Read(db => db.BackupSchedules.Single(s => s.TargetRef == svc.Id.ToString()));
        schedule.ManagedServiceDatabaseId.Should().Be(billing.Id,
            "the schedule must be scoped to this one logical database, not the whole instance");
        schedule.IntervalHours.Should().Be(24);
    }

    [Fact]
    public async Task Self_serve_export_of_one_logical_database_carries_its_id_and_an_expiry()
    {
        var svc = SeedDatabase("export-scoped-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        SeedDestination();
        Panel.GivenUser(fixture.WorkspaceId, "exportscoped@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.171", "exportscoped@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");

        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/export", token, ("databaseId", billing.Id.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var queued = Panel.Read(db => db.Backups.Single(b => b.TargetRef == svc.Id.ToString()));
        queued.ManagedServiceDatabaseId.Should().Be(billing.Id);
        queued.ExpiresAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Exporting_a_database_id_that_belongs_to_a_different_instance_404s()
    {
        var svc = SeedDatabase("export-mismatch-svc");
        var otherSvc = SeedDatabase("export-mismatch-other");
        var foreignDb = SeedLogicalDatabase(otherSvc, "foreign_db");
        SeedDestination();
        Panel.GivenUser(fixture.WorkspaceId, "exportmismatch@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.172", "exportmismatch@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");

        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/export", token, ("databaseId", foreignDb.Id.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- import confirm page names attached apps ---------------------------------------------------

    [Fact]
    public async Task The_import_confirm_page_names_the_apps_attached_to_this_logical_database()
    {
        var svc = SeedDatabase("import-confirm-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        var app = SeedApp("billing-consumer");
        Attach(billing, app);
        Panel.GivenUser(fixture.WorkspaceId, "importconfirm@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.173", "importconfirm@example.com");

        var html = await (await client.GetAsync($"/databases/{svc.Id}/logical-databases/{billing.Id}/import"))
            .Content.ReadAsStringAsync();

        html.Should().Contain("billing-consumer",
            "the person restoring may not know who is attached, so the confirm page must name them");
        html.Should().Contain("billing_db");
    }

    [Fact]
    public async Task Import_without_the_typed_name_refuses_and_reads_nothing_from_the_upload()
    {
        var svc = SeedDatabase("import-noconfirm-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        Panel.GivenUser(fixture.WorkspaceId, "importnoconfirm@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.174", "importnoconfirm@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}/logical-databases/{billing.Id}/import");

        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), HttpConversation.AntiforgeryField },
            { new StringContent("not-the-right-name"), "confirmName" },
            { new StringContent("dump contents"), "file", "dump.sql.gz" }
        };

        var response = await client.PostAsync($"/databases/{svc.Id}/logical-databases/{billing.Id}/import", content);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/databases/{svc.Id}/logical-databases/{billing.Id}/import",
            "an unconfirmed import returns to the SAME confirm page, not the database's overview");
        Panel.Read(db => db.Backups.Any(b => b.ManagedServiceDatabaseId == billing.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Import_with_the_exact_name_restores_into_that_logical_database_and_reaches_the_engine()
    {
        var svc = SeedDatabase("import-confirmed-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        Panel.GivenUser(fixture.WorkspaceId, "importconfirmed@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.175", "importconfirmed@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}/logical-databases/{billing.Id}/import");

        Panel.Docker.OneOffExitCode = 0;

        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), HttpConversation.AntiforgeryField },
            { new StringContent("billing_db"), "confirmName" },
            { new ByteArrayContent(Gzip("-- a real looking dump\n")), "file", "dump.sql.gz" }
        };

        var response = await client.PostAsync($"/databases/{svc.Id}/logical-databases/{billing.Id}/import", content);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/databases/{svc.Id}");
        Panel.Read(db => db.Backups.Any(b => b.ManagedServiceDatabaseId == billing.Id
            && b.Status == BackupStatus.Completed)).Should().BeTrue();
        Panel.Docker.OneOffCommands.Should().NotBeEmpty("the restore must actually reach the engine");
        Panel.Docker.OneOffCommands.Last().Should().Contain("billing_db");
    }

    // ---- restore an existing backup into same / a different / a brand-new database ------------------

    [Fact]
    public async Task The_restore_confirm_page_shows_the_backup_and_who_is_attached()
    {
        var svc = SeedDatabase("restore-confirm-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        var app = SeedApp("restore-confirm-consumer");
        Attach(billing, app);
        var backup = SeedCompletedBackup(svc, billing.Id);
        Panel.GivenUser(fixture.WorkspaceId, "restoreconfirm@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.176", "restoreconfirm@example.com");

        var html = await (await client.GetAsync(
            $"/databases/{svc.Id}/logical-databases/{billing.Id}/restore/{backup.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("restore-confirm-consumer");
        html.Should().Contain("billing_db");
    }

    [Fact]
    public async Task Restoring_over_the_same_database_without_the_typed_name_is_refused()
    {
        var svc = SeedDatabase("restore-same-noconfirm-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        var backup = SeedCompletedBackup(svc, billing.Id);
        Panel.GivenUser(fixture.WorkspaceId, "restoresamenoconfirm@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.177", "restoresamenoconfirm@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}/logical-databases/{billing.Id}/restore/{backup.Id}");

        // The count before, not "the list is empty" — FakeDockerEngine's OneOffCommands is shared
        // across every test in this collection's fixture, so a prior test's own calls are already in
        // it; this test only owns the delta its own request produces.
        var before = Panel.Docker.OneOffCommands.Count;

        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{billing.Id}/restore/{backup.Id}", token,
            ("mode", "same"), ("confirmName", "not-the-name"));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("billing_db");
        Panel.Docker.OneOffCommands.Should().HaveCount(before, "an unconfirmed restore must never reach the engine");
    }

    [Fact]
    public async Task Restoring_over_the_same_database_with_the_exact_name_reaches_the_engine()
    {
        var svc = SeedDatabase("restore-same-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        var backup = SeedCompletedBackup(svc, billing.Id);
        Panel.GivenUser(fixture.WorkspaceId, "restoresame@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.178", "restoresame@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}/logical-databases/{billing.Id}/restore/{backup.Id}");

        Panel.Docker.OneOffExitCode = 0;

        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{billing.Id}/restore/{backup.Id}", token,
            ("mode", "same"), ("confirmName", "billing_db"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/databases/{svc.Id}");
        Panel.Docker.OneOffCommands.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Restoring_into_a_brand_new_database_creates_it_and_reaches_the_engine()
    {
        var svc = SeedDatabase("restore-new-svc");
        var billing = SeedLogicalDatabase(svc, "billing_db");
        var backup = SeedCompletedBackup(svc, billing.Id);
        Panel.GivenUser(fixture.WorkspaceId, "restorenew@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.179", "restorenew@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}/logical-databases/{billing.Id}/restore/{backup.Id}");

        Panel.Docker.OneOffExitCode = 0;

        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{billing.Id}/restore/{backup.Id}", token,
            ("mode", "new"), ("newDatabaseName", "staging-clone"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/databases/{svc.Id}");
        var created = Panel.Read(db => db.ManagedServiceDatabases
            .SingleOrDefault(d => d.ManagedServiceId == svc.Id && d.Name == "staging_clone"));
        created.Should().NotBeNull("restoring with mode=new must create the logical database first");
        Panel.Docker.OneOffCommands.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Restoring_into_a_new_database_on_an_incompatible_engine_refuses_before_creating_it()
    {
        var pg = SeedDatabase("restore-new-pg-source", ManagedServiceType.PostgreSql);
        var pgDb = SeedLogicalDatabase(pg, "pg_db");
        var backup = SeedCompletedBackup(pg, pgDb.Id);

        var mysql = SeedDatabase("restore-new-mysql-target", ManagedServiceType.MySql);
        var mysqlDb = SeedLogicalDatabase(mysql, "mysql_db");

        Panel.GivenUser(fixture.WorkspaceId, "restorenewmismatch@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.181", "restorenewmismatch@example.com");
        // Browsing the MYSQL instance's own restore page, but naming the Postgres backup — reachable
        // by URL even though nothing in the ordinary flow points a person at this combination.
        var token = await client.AntiforgeryTokenFrom($"/databases/{mysql.Id}/logical-databases/{mysqlDb.Id}/restore/{backup.Id}");
        var before = Panel.Docker.OneOffCommands.Count;

        var response = await client.PostFormAsync(
            $"/databases/{mysql.Id}/logical-databases/{mysqlDb.Id}/restore/{backup.Id}", token,
            ("mode", "new"), ("newDatabaseName", "orphan-clone"));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("PostgreSql").And.Contain("MySql");
        Panel.Docker.OneOffCommands.Should().HaveCount(before, "refused before any docker call");
        Panel.Read(db => db.ManagedServiceDatabases.Any(d => d.ManagedServiceId == mysql.Id && d.Name == "orphan_clone"))
            .Should().BeFalse("the mismatch must be caught before the new database is even created — no orphan left behind");
    }

    [Fact]
    public async Task Restoring_a_postgres_backup_into_a_mysql_instance_refuses_by_name()
    {
        var pg = SeedDatabase("restore-engine-pg", ManagedServiceType.PostgreSql);
        var pgDb = SeedLogicalDatabase(pg, "pg_db");
        var backup = SeedCompletedBackup(pg, pgDb.Id);

        var mysql = SeedDatabase("restore-engine-mysql", ManagedServiceType.MySql);
        var mysqlDb = SeedLogicalDatabase(mysql, "mysql_db");

        Panel.GivenUser(fixture.WorkspaceId, "restoreengine@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.180", "restoreengine@example.com");
        var token = await client.AntiforgeryTokenFrom($"/databases/{pg.Id}/logical-databases/{pgDb.Id}/restore/{backup.Id}");
        var before = Panel.Docker.OneOffCommands.Count;

        var response = await client.PostFormAsync(
            $"/databases/{pg.Id}/logical-databases/{pgDb.Id}/restore/{backup.Id}", token,
            ("mode", "existing"), ("targetDatabaseId", mysqlDb.Id.ToString()), ("confirmName", "mysql_db"));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("PostgreSql").And.Contain("MySql");
        Panel.Docker.OneOffCommands.Should().HaveCount(before, "an incompatible-engine restore must never reach the engine");
    }
}
