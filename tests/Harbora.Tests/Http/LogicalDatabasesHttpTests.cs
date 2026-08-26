using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Logical databases end to end (D1, 2026-08-25 shared-databases plan) — the real pipeline routes, a
/// real cookie, real Razor, against the panel's shared <c>FakeDockerEngine</c>, which answers every
/// one-off command with exit code 0 unless a test says otherwise. Mirrors
/// <see cref="AppManagedServicesHttpTests"/>: this proves the controller wiring (attach resolving a
/// logical database, the create/delete routes, the typed-name confirmation) reaches the pages a
/// person actually uses; <see cref="LogicalDatabaseServiceTests"/> already proves the engine
/// orchestration itself at the service layer.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class LogicalDatabasesHttpTests(HarboraHttpFixture fixture)
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
            EncryptedPassword = protector.Protect("logicaldb-http-password-01")
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        return service;
    }

    /// <summary>A service seeded the way <c>DatabasesController.Create</c> actually leaves one: with
    /// its own default logical database already materialised.</summary>
    private ManagedService SeedDatabaseWithDefault(string name)
    {
        var service = SeedDatabase(name);
        Panel.Seed(db => db.ManagedServiceDatabases.Add(ManagedServiceDatabase.DefaultFor(service)!));
        return service;
    }

    private Guid LogicalDatabaseId(Guid managedServiceId, string name) =>
        Panel.Read(db => db.ManagedServiceDatabases.Single(d => d.ManagedServiceId == managedServiceId && d.Name == name).Id);

    [Fact]
    public async Task Creating_a_logical_database_succeeds_and_is_readable_afterwards()
    {
        var svc = SeedDatabase("shared-instance");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-create@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.260", "logicaldb-create@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/logical-databases", token, ("name", "reports"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.ManagedServiceDatabases
            .Any(d => d.ManagedServiceId == svc.Id && d.Name == "reports" && !d.IsDefault)).Should().BeTrue();
    }

    [Fact]
    public async Task Creating_a_logical_database_on_an_engine_with_no_clean_story_is_refused_by_name()
    {
        var svc = SeedDatabase("cache-instance", ManagedServiceType.Redis);
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-refuse@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.261", "logicaldb-refuse@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/logical-databases", token, ("name", "sessions"));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        html.Should().Contain("Redis", "the refusal must name the engine that cannot do this, not just fail silently");
        Panel.Read(db => db.ManagedServiceDatabases.Any(d => d.ManagedServiceId == svc.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Attaching_without_naming_a_database_uses_the_instances_own_default()
    {
        var svc = SeedDatabaseWithDefault("with-default");
        var app = SeedApp("default-picker");
        var defaultId = LogicalDatabaseId(svc.Id, "with_default");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-default@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.262", "logicaldb-default@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", token, ("appId", app.Id.ToString()));

        Panel.Read(db => db.AppManagedServices
            .Single(x => x.AppId == app.Id && x.ManagedServiceId == svc.Id).ManagedServiceDatabaseId)
            .Should().Be(defaultId, "an attach form that names no database must still land on the instance's own default");
    }

    [Fact]
    public async Task An_app_can_attach_to_two_different_logical_databases_on_the_same_instance()
    {
        var svc = SeedDatabaseWithDefault("multi-logical");
        var app = SeedApp("two-logical-dbs");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-two@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.263", "logicaldb-two@example.com");

        var createToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/logical-databases", createToken, ("name", "reports"));
        var reportsId = LogicalDatabaseId(svc.Id, "reports");
        var defaultId = LogicalDatabaseId(svc.Id, "multi_logical");

        var attachToken1 = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var first = await client.PostFormAsync($"/databases/{svc.Id}/attach", attachToken1,
            ("appId", app.Id.ToString()), ("databaseId", defaultId.ToString()));
        first.StatusCode.Should().Be(HttpStatusCode.Found);

        var attachToken2 = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var second = await client.PostFormAsync($"/databases/{svc.Id}/attach", attachToken2,
            ("appId", app.Id.ToString()), ("databaseId", reportsId.ToString()), ("alias", "REPORTS"));
        second.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.AppManagedServices.Count(x => x.AppId == app.Id && x.ManagedServiceId == svc.Id))
            .Should().Be(2, "two different logical databases on one instance must both be attachable to the same app");
    }

    [Fact]
    public async Task Attaching_the_same_logical_database_twice_to_one_app_is_still_refused()
    {
        var svc = SeedDatabaseWithDefault("no-double-attach");
        var app = SeedApp("no-double-attach-app");
        var defaultId = LogicalDatabaseId(svc.Id, "no_double_attach");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-nodup@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.264", "logicaldb-nodup@example.com");

        var token1 = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", token1,
            ("appId", app.Id.ToString()), ("databaseId", defaultId.ToString()));

        var token2 = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var second = await client.PostFormAsync($"/databases/{svc.Id}/attach", token2,
            ("appId", app.Id.ToString()), ("databaseId", defaultId.ToString()));

        var html = await (await client.GetAsync(second.RedirectPath()!)).Content.ReadAsStringAsync();
        Panel.Read(db => db.AppManagedServices.Count(x => x.AppId == app.Id && x.ManagedServiceId == svc.Id))
            .Should().Be(1, "the second attach of the same logical database must not create a second row");
    }

    [Fact]
    public async Task Deleting_a_logical_database_without_the_correct_typed_name_is_refused()
    {
        var svc = SeedDatabaseWithDefault("typed-confirm");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-typed@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.265", "logicaldb-typed@example.com");

        var createToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/logical-databases", createToken, ("name", "scratch"));
        var scratchId = LogicalDatabaseId(svc.Id, "scratch");

        var removeToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{scratchId}/remove", removeToken, ("confirmName", "not-the-name"));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("scratch");
        Panel.Read(db => db.ManagedServiceDatabases.Any(d => d.Id == scratchId)).Should().BeTrue(
            "the database must still exist — the delete was refused, not silently applied anyway");
    }

    [Fact]
    public async Task Deleting_a_logical_database_still_attached_to_an_app_is_refused_and_names_the_app()
    {
        // A second (non-default) database — the default's own removal is already always refused
        // regardless of attachments, which is the fact The_instances_own_default_database... proves.
        var svc = SeedDatabaseWithDefault("attached-logical");
        var app = SeedApp("logical-consumer");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-attached@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.266", "logicaldb-attached@example.com");

        var createToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/logical-databases", createToken, ("name", "reports"));
        var reportsId = LogicalDatabaseId(svc.Id, "reports");

        var attachToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", attachToken,
            ("appId", app.Id.ToString()), ("databaseId", reportsId.ToString()));

        var removeToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{reportsId}/remove", removeToken, ("confirmName", "reports"));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("logical-consumer",
            "the refusal must name the app blocking the delete, not merely count it");
        Panel.Read(db => db.ManagedServiceDatabases.Any(d => d.Id == reportsId)).Should().BeTrue();
    }

    [Fact]
    public async Task An_unattached_non_default_database_is_deleted_with_the_correct_typed_name()
    {
        var svc = SeedDatabaseWithDefault("delete-me-host");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-delete@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.267", "logicaldb-delete@example.com");

        var createToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/logical-databases", createToken, ("name", "delete-me"));
        var deleteId = LogicalDatabaseId(svc.Id, "delete_me");

        var removeToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{deleteId}/remove", removeToken, ("confirmName", "delete_me"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.ManagedServiceDatabases.Any(d => d.Id == deleteId)).Should().BeFalse();
    }

    [Fact]
    public async Task The_instances_own_default_database_cannot_be_deleted_even_with_the_correct_typed_name()
    {
        var svc = SeedDatabaseWithDefault("protected-default");
        var defaultId = LogicalDatabaseId(svc.Id, "protected_default");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-protected@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.268", "logicaldb-protected@example.com");

        var removeToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{defaultId}/remove", removeToken, ("confirmName", "protected_default"));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("instance",
            "the refusal must explain this is the instance's own database, not just fail");
        Panel.Read(db => db.ManagedServiceDatabases.Any(d => d.Id == defaultId)).Should().BeTrue();
    }

    // -------------------------------------------------------------------------------------------
    // The management surface (D3, 2026-08-25 shared-databases plan)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_instances_own_page_lists_every_logical_database_with_who_is_attached()
    {
        var svc = SeedDatabaseWithDefault("surfaced-instance");
        var app = SeedApp("surfaced-consumer");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-surface@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.200", "logicaldb-surface@example.com");

        var createToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/logical-databases", createToken, ("name", "reports"));
        var reportsId = LogicalDatabaseId(svc.Id, "reports");

        var attachToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", attachToken,
            ("appId", app.Id.ToString()), ("databaseId", reportsId.ToString()));

        var html = await (await client.GetAsync($"/databases/{svc.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain($"data-logical-database=\"{reportsId}\"");
        html.Should().Contain("surfaced-consumer", "the row must name which app is attached");
        html.Should().Contain("data-logical-database-size-state=\"not-measured\"",
            "per-database size is not genuinely measurable yet, and must never render as a fabricated zero");
        html.Should().NotContain("data-logical-databases-unsupported",
            "PostgreSQL has a logical-database story, so the unsupported banner must not render for it");
    }

    [Fact]
    public async Task An_engine_with_no_logical_database_story_explains_why_instead_of_showing_an_empty_table()
    {
        var svc = SeedDatabase("legible-cache", ManagedServiceType.Redis);
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-legible@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.201", "logicaldb-legible@example.com");

        var html = await (await client.GetAsync($"/databases/{svc.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-logical-databases-unsupported=\"Redis\"",
            "an unsupported engine must say so by name, not render an empty list indistinguishable from one that simply has none yet");
        html.Should().NotContain("data-logical-databases-table");
    }

    [Fact]
    public async Task Renaming_a_non_default_logical_database_changes_the_name_shown_on_the_instances_page()
    {
        var svc = SeedDatabaseWithDefault("rename-target");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-rename@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.202", "logicaldb-rename@example.com");

        var createToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/logical-databases", createToken, ("name", "scratch"));
        var scratchId = LogicalDatabaseId(svc.Id, "scratch");

        var renameToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{scratchId}/rename", renameToken, ("name", "renamed-scratch"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.ManagedServiceDatabases.Single(d => d.Id == scratchId).Name).Should().Be("renamed_scratch");
    }

    [Fact]
    public async Task Renaming_the_instances_own_default_database_is_refused_by_name()
    {
        var svc = SeedDatabaseWithDefault("rename-default-refused");
        var defaultId = LogicalDatabaseId(svc.Id, "rename_default_refused");
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-rename-default@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.203", "logicaldb-rename-default@example.com");

        var renameToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{defaultId}/rename", renameToken, ("name", "renamed"));

        // RenameDatabase redirects to Details, not ConfirmRemoveDatabase — the error renders through
        // _Shell.cshtml's generic data-spec-error banner, not ConfirmRemoveDatabase's own inline one
        // ErrorBannerText matches, so this reads the raw text directly instead.
        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        html.Should().Contain("data-spec-error");
        html.Should().Contain("instance",
            "the refusal must explain this is the instance's own database, not just fail");
        Panel.Read(db => db.ManagedServiceDatabases.Single(d => d.Id == defaultId).Name).Should().Be("rename_default_refused");
    }

    [Fact]
    public async Task Renaming_on_an_engine_with_no_lossless_rename_is_refused_by_name_over_http()
    {
        // Built inline, not through SeedDatabase: that helper only gives a PostgreSql seed a real
        // DatabaseName (its MySql/MariaDb branch is empty, on purpose, for the engine-refusal tests
        // above) — a logical database needs one to create against regardless of engine.
        var protector = Panel.Resolve<ISecretProtector>();
        var svc = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(), Name = "mysql-rename-refused", Type = ManagedServiceType.MySql,
            Version = "11", Status = ServiceStatus.Running, ContainerName = "harbora-svc-mysql-rename-refused",
            InternalPort = 3306, Username = "harbora", DatabaseName = "mysql_rename_refused",
            VolumeName = "harbora-svc-mysql-rename-refused-data",
            EncryptedPassword = protector.Protect("logicaldb-http-password-02")
        };
        Panel.Seed(db =>
        {
            db.ManagedServices.Add(svc);
            db.ManagedServiceDatabases.Add(ManagedServiceDatabase.DefaultFor(svc)!);
        });
        Panel.GivenUser(fixture.WorkspaceId, "logicaldb-mysql-rename@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.204", "logicaldb-mysql-rename@example.com");

        var createToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/logical-databases", createToken, ("name", "orders"));
        var ordersId = LogicalDatabaseId(svc.Id, "orders");

        var renameToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync(
            $"/databases/{svc.Id}/logical-databases/{ordersId}/rename", renameToken, ("name", "invoices"));

        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        html.Should().Contain("data-spec-error");
        html.Should().Contain("MySql", "the refusal must name which engine, not just say no");
        Panel.Read(db => db.ManagedServiceDatabases.Single(d => d.Id == ordersId).Name).Should().Be("orders");
    }
}
