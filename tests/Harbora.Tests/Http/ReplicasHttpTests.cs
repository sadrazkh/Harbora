using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Read replicas end to end (3.2, round-2 market-gaps plan) — the real routes, a real cookie, real
/// Razor. Mirrors <see cref="AppManagedServicesHttpTests"/> and <see cref="PitrHttpTests"/>: the
/// service-layer tests (<c>ReadReplicaPlanTests</c>, <c>ReplicationLagPresenterTests</c>,
/// <c>ReplicaAttachEnvTests</c>) already prove the orchestration; this proves the controller actually
/// wires the named refusals up.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ReplicasHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    // ConfirmRemove.cshtml renders TempData["Error"] itself, in a bg-danger-soft div — the same
    // regex AppManagedServicesHttpTests already reads for the sibling "attached apps" refusal.
    private static readonly Regex ErrorBanner = new(
        """<div class="mb-4 rounded-lg bg-danger-soft[^>]*>(?<text>.*?)</div>""", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string ErrorBannerText(string html)
    {
        var match = ErrorBanner.Match(html);
        match.Success.Should().BeTrue("a refused delete must render the TempData[\"Error\"] banner");
        return match.Groups["text"].Value;
    }

    private ManagedService SeedDatabase(string name, ManagedServiceType type = ManagedServiceType.PostgreSql, Guid? primaryId = null)
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
            EncryptedPassword = protector.Protect("replica-http-password-01"),
            PrimaryManagedServiceId = primaryId
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        return service;
    }

    [Fact]
    public async Task Deleting_a_primary_with_a_replica_is_refused_and_names_it()
    {
        var primary = SeedDatabase("replicas-primary");
        var replica = SeedDatabase("replicas-standby-one", primaryId: primary.Id);

        Panel.GivenUser(fixture.WorkspaceId, "replica-delete-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.30", "replica-delete-refused@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{primary.Id}");
        var response = await client.PostFormAsync($"/databases/{primary.Id}/remove", token, ("deleteData", "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain(replica.Name,
            "the refusal must name the replica blocking the delete, not merely count it");

        Panel.Read(db => db.ManagedServices.Any(s => s.Id == primary.Id)).Should().BeTrue(
            "the primary must still exist — the delete was refused, not silently applied anyway");
        Panel.Read(db => db.ManagedServices.Any(s => s.Id == replica.Id)).Should().BeTrue(
            "the replica must still exist too — nothing here should have cascaded");
    }

    [Fact]
    public async Task A_non_postgresql_engine_is_refused_by_name_when_creating_a_replica()
    {
        var svc = SeedDatabase("replicas-mysql-instance", ManagedServiceType.MySql);
        Panel.GivenUser(fixture.WorkspaceId, "replica-mysql@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.31", "replica-mysql@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/replicas/create", token, ("name", "standby-one"));

        response.RedirectPath().Should().NotBeNull();
        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        html.Should().Contain("data-spec-error");
        html.Should().Contain("MySql", "the refusal must name which engine, not just say no");

        Panel.Read(db => db.ManagedServices.Any(s => s.PrimaryManagedServiceId == svc.Id)).Should().BeFalse(
            "nothing was ever sent to the engine — a refused create must leave no row behind");
    }

    [Fact]
    public async Task A_replica_is_never_attached_to_an_app_directly()
    {
        var primary = SeedDatabase("replicas-attach-primary");
        var replica = SeedDatabase("replicas-attach-standby", primaryId: primary.Id);
        var app = new Harbora.Domain.Apps.App
        {
            WorkspaceId = fixture.WorkspaceId, ServerId = Guid.CreateVersion7(),
            EnvironmentId = fixture.DefaultEnvironmentId, Name = "reader-app", Slug = "reader-app",
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/reader:1.0", Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));

        Panel.GivenUser(fixture.WorkspaceId, "replica-attach-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.32", "replica-attach-refused@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{replica.Id}");
        var response = await client.PostFormAsync($"/databases/{replica.Id}/attach", token, ("appId", app.Id.ToString()));

        response.RedirectPath().Should().NotBeNull();
        Panel.Read(db => db.AppManagedServices.Any(a => a.AppId == app.Id && a.ManagedServiceId == replica.Id))
            .Should().BeFalse("a replica must never gain its own, independently-attached AppManagedService row");
    }
}
