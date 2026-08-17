using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P4's surfacing half (2026-08-17 app-environment-management design): rotation used to end on a
/// one-line flash message telling the person to go and redeploy the affected apps by hand.
/// <c>DatabasesController.Rotate</c> now ends on <c>RotateConfirm</c>, which lists exactly what was
/// rewritten and can queue every one of those apps' redeploys with one press —
/// <c>DeploymentEngine.QueueDeploymentAsync</c> already coalesces per app, so the loop the queue
/// action runs is safe.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class RotationConfirmHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (ManagedService Service, App App) GivenAnAttachedDatabase(string suffix)
    {
        var protector = Panel.Resolve<ISecretProtector>();

        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "orders-" + suffix,
            Type = ManagedServiceType.PostgreSql,
            Version = "16",
            Status = ServiceStatus.Running,
            ContainerName = "harbora-svc-orders-" + suffix,
            InternalPort = 5432,
            Username = "harbora",
            DatabaseName = "orders",
            VolumeName = "harbora-svc-orders-" + suffix + "-data",
            EncryptedPassword = protector.Protect("original-password-12")
        };

        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "web-" + suffix,
            Slug = "web-" + suffix,
            Kind = ServiceKind.Web,
            ContainerPort = 8080,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "alpine:3.20"
        };

        // What the app already holds — the exact shape ManagedServiceEngine.RotatePasswordAsync
        // reads as "the old value to match against" before it will touch anything.
        var oldCreds = new ServiceCreds(
            service.ContainerName, ServiceCatalog.All[service.Type].Port,
            service.Username, "original-password-12", service.DatabaseName);
        var oldUrl = ServiceCatalog.All[service.Type].AttachEnv(oldCreds)["DATABASE_URL"];
        app.EnvironmentVariables.Add(new EnvironmentVariable
        {
            Key = "DATABASE_URL", Value = protector.Protect(oldUrl), IsSecret = true
        });

        Panel.Seed(db =>
        {
            db.ManagedServices.Add(service);
            db.Apps.Add(app);
        });

        return (service, app);
    }

    [Fact]
    public async Task Rotating_a_password_that_touched_an_app_redirects_to_the_confirmation_page()
    {
        var (service, _) = GivenAnAttachedDatabase("redirect");
        Panel.GivenUser(fixture.WorkspaceId, "rotate-redirect@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.170", "rotate-redirect@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{service.Id}");
        var response = await client.PostFormAsync($"/databases/{service.Id}/rotate", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be($"/databases/{service.Id}/rotate/confirm",
            "a rotation that touched an app ends on the confirmation page, not back on Details");
    }

    [Fact]
    public async Task The_confirmation_page_lists_the_app_the_rotation_rewrote()
    {
        var (service, app) = GivenAnAttachedDatabase("list");
        Panel.GivenUser(fixture.WorkspaceId, "rotate-list@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.171", "rotate-list@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{service.Id}");
        await client.PostFormAsync($"/databases/{service.Id}/rotate", token);
        var confirmResponse = await client.GetAsync($"/databases/{service.Id}/rotate/confirm");
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the confirmation page should render, not redirect away");
        var html = await confirmResponse.Content.ReadAsStringAsync();

        html.Should().Contain($"data-spec-rotate-app=\"{app.Id}\"",
            "the app whose environment was actually rewritten has to be named on the confirmation page");
    }

    [Fact]
    public async Task Confirming_queues_a_redeploy_for_the_listed_app()
    {
        var (service, app) = GivenAnAttachedDatabase("queue");
        Panel.GivenUser(fixture.WorkspaceId, "rotate-queue@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.172", "rotate-queue@example.com");

        var rotateToken = await client.AntiforgeryTokenFrom($"/databases/{service.Id}");
        await client.PostFormAsync($"/databases/{service.Id}/rotate", rotateToken);

        var confirmToken = await client.AntiforgeryTokenFrom($"/databases/{service.Id}/rotate/confirm");
        var response = await client.PostFormAsync(
            $"/databases/{service.Id}/rotate/confirm", confirmToken,
            ("appIds", app.Id.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be($"/databases/{service.Id}",
            "confirming lands back on the database's own page");
        Panel.Deployments.Queued.Should().ContainSingle(r => r.AppId == app.Id,
            "DeploymentEngine.QueueDeploymentAsync is what the confirmation page's redeploy button calls");
    }

    [Fact]
    public async Task A_rotation_that_touched_no_app_goes_straight_back_to_details()
    {
        var protector = Panel.Resolve<ISecretProtector>();
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "orders-untouched",
            Type = ManagedServiceType.PostgreSql,
            Version = "16",
            Status = ServiceStatus.Running,
            ContainerName = "harbora-svc-orders-untouched",
            InternalPort = 5432,
            Username = "harbora",
            DatabaseName = "orders",
            VolumeName = "harbora-svc-orders-untouched-data",
            EncryptedPassword = protector.Protect("original-password-12")
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        Panel.GivenUser(fixture.WorkspaceId, "rotate-untouched@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.173", "rotate-untouched@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{service.Id}");
        var response = await client.PostFormAsync($"/databases/{service.Id}/rotate", token);

        response.Headers.Location!.ToString().Should().Be($"/databases/{service.Id}",
            "no app held the password, so there is nothing to confirm");
    }
}
