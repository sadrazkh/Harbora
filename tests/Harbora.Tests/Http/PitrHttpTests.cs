using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 3.1 (round-2 market-gaps plan) end to end: the real routes, a real cookie, real Razor — mirrors
/// <see cref="LogicalDatabasesHttpTests"/>'s own reasoning. The service-layer tests
/// (<c>PitrTests</c>, <c>PitrRestoreServiceTests</c>, <c>WalArchiveShipperTests</c>) already prove the
/// orchestration; this proves the controller and the Details page actually wire it up — in
/// particular that the panel never shows PITR as active before the instance has been rebuilt.
///
/// Both <c>Pitr</c> and <c>PitrRestore</c> redirect back to <c>Details</c>, which renders TempData
/// through <c>_Shell.cshtml</c>'s own generic <c>data-spec-error</c> banner rather than a page-local
/// one — the same shape <c>LogicalDatabasesHttpTests.Renaming_the_instances_own_default_database_is_refused_by_name</c>
/// already reads directly off the raw HTML rather than through a page-specific banner regex.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class PitrHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private ManagedService SeedDatabase(string name, ManagedServiceType type = ManagedServiceType.PostgreSql)
    {
        var protector = Panel.Resolve<ISecretProtector>();
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(), Name = name, Type = type,
            Version = "16", Status = ServiceStatus.Running, ContainerName = "harbora-svc-" + name,
            InternalPort = 5432, Username = "harbora",
            DatabaseName = type == ManagedServiceType.PostgreSql ? name.Replace('-', '_') : "",
            VolumeName = "harbora-svc-" + name + "-data",
            EncryptedPassword = protector.Protect("pitr-http-password-01")
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        return service;
    }

    [Fact]
    public async Task Enabling_pitr_never_shows_it_active_before_the_next_rebuild()
    {
        var svc = SeedDatabase("pitr-toggle-instance");
        Panel.GivenUser(fixture.WorkspaceId, "pitr-toggle@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.180", "pitr-toggle@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/pitr", token, ("enable", "true"));
        response.RedirectPath().Should().NotBeNull();

        var html = await (await client.GetAsync($"/databases/{svc.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("data-pitr-instance-state=\"pending-restart\"",
            "turned on but not yet rebuilt — the panel must never claim archiving is active before it has started");
        html.Should().NotContain("data-pitr-instance-state=\"healthy\"");

        Panel.Read(db => db.ManagedServices.Single(s => s.Id == svc.Id).PitrEnabled).Should().BeTrue();
        Panel.Read(db => db.ManagedServices.Single(s => s.Id == svc.Id).HasUnpublishedChanges).Should().BeTrue();
    }

    [Fact]
    public async Task A_non_postgresql_instance_is_refused_by_name_over_http()
    {
        var svc = SeedDatabase("pitr-mysql-instance", ManagedServiceType.MySql);
        Panel.GivenUser(fixture.WorkspaceId, "pitr-mysql@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.181", "pitr-mysql@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/pitr", token, ("enable", "true"));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        html.Should().Contain("data-spec-error");
        html.Should().Contain("MySql", "the refusal must name which engine, not just say no");
        Panel.Read(db => db.ManagedServices.Single(s => s.Id == svc.Id).PitrEnabled).Should().BeFalse();
    }

    [Fact]
    public async Task Overwriting_an_existing_logical_database_without_the_typed_name_is_refused_and_names_the_attached_apps()
    {
        var svc = SeedDatabase("pitr-overwrite-instance");
        var now = Panel.Resolve<ISystemClock>().UtcNow;

        var target = new ManagedServiceDatabase
        {
            WorkspaceId = fixture.WorkspaceId, ManagedServiceId = svc.Id, Name = "orders", Username = "orders_user"
        };
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, ServerId = Guid.CreateVersion7(),
            EnvironmentId = fixture.DefaultEnvironmentId, Name = "checkout-api", Slug = "checkout-api",
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/checkout:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.ManagedServiceDatabases.Add(target);
            db.Apps.Add(app);
        });
        Panel.Seed(db => db.AppManagedServices.Add(new AppManagedService
        {
            AppId = app.Id, ManagedServiceId = svc.Id, ManagedServiceDatabaseId = target.Id, Alias = "ORDERS"
        }));
        Panel.Seed(db =>
        {
            var row = db.ManagedServices.Single(s => s.Id == svc.Id);
            row.PitrEnabled = true;
            row.HasUnpublishedChanges = false;
            db.Backups.Add(new Backup
            {
                WorkspaceId = fixture.WorkspaceId, DestinationId = Guid.NewGuid(), Type = BackupType.PostgresBaseBackup,
                TargetRef = svc.Id.ToString(), Status = BackupStatus.Completed, FinishedAt = now.AddHours(-6)
            });
            db.WalArchivingStatuses.Add(new WalArchivingStatus
            {
                WorkspaceId = fixture.WorkspaceId, ManagedServiceId = svc.Id, LastSuccessAt = now.AddMinutes(-1)
            });
        });

        Panel.GivenUser(fixture.WorkspaceId, "pitr-overwrite@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.182", "pitr-overwrite@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/pitr/restore", token,
            ("targetUnixSeconds", now.AddMinutes(-30).ToUnixTimeSeconds().ToString()),
            ("overwriteDatabaseId", target.Id.ToString()));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        html.Should().Contain("data-spec-error");
        html.Should().Contain("orders").And.Contain("checkout-api",
            "the person restoring may not know what is attached, so the refusal itself must say");

        Panel.Read(db => db.ManagedServiceDatabases.Single(d => d.Id == target.Id).Name).Should().Be("orders",
            "a refused overwrite must change nothing");
    }
}
