using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P4's surfacing half (2026-08-17 app-environment-management design): <c>DatabasesController.
/// Reprovision</c> was already a working retry — it recreates the container from the current
/// definition, which is exactly what a database stuck on <see cref="ServiceStatus.Failed"/> needs —
/// but its button read "Rebuild container" unconditionally and never said anything about why the
/// database was down. This re-presents the same action as the retry it already is, rather than
/// adding a second one beside it.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ServiceRetryHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private ManagedService GivenService(string name, ServiceStatus status, string? errorMessage = null)
    {
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId,
            EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(),
            Name = name,
            Type = ManagedServiceType.PostgreSql,
            Version = "16",
            Status = status,
            ErrorMessage = errorMessage,
            ContainerName = "harbora-svc-" + name,
            InternalPort = 5432,
            Username = "harbora",
            DatabaseName = name,
            VolumeName = "harbora-svc-" + name + "-data"
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        return service;
    }

    [Fact]
    public async Task A_failed_databases_page_presents_the_rebuild_button_as_a_retry_and_says_why()
    {
        var svc = GivenService("orders-retry", ServiceStatus.Failed, "image pull timed out after 60s");
        Panel.GivenUser(fixture.WorkspaceId, "svc-retry-page@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.150", "svc-retry-page@example.com");

        var html = await (await client.GetAsync($"/databases/{svc.Id}")).Content.ReadAsStringAsync();

        // Assert on the data attribute, not the localized word — the panel renders Persian by
        // default, and "Retry"/"تلاش دوباره" is not what this test is proving.
        html.Should().Contain("data-spec-reprovision=\"retry\"",
            "a Failed database's own rebuild button must present itself as the retry it is");
        html.Should().Contain("data-spec-service-error=\"image pull timed out after 60s\"",
            "the reason the provision failed has to be readable on the database's own page, not only in a log");
    }

    [Fact]
    public async Task A_running_databases_page_presents_the_same_button_as_a_rebuild_with_no_error_banner()
    {
        var svc = GivenService("orders-healthy", ServiceStatus.Running);
        Panel.GivenUser(fixture.WorkspaceId, "svc-rebuild-page@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.151", "svc-rebuild-page@example.com");

        var html = await (await client.GetAsync($"/databases/{svc.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-reprovision=\"rebuild\"",
            "a running database's rebuild button is not a retry — nothing failed");
        html.Should().NotContain("data-spec-service-error",
            "a database that came up has no failure reason to show");
    }

    [Fact]
    public async Task Retrying_a_failed_database_reports_a_message_and_the_page_still_reads_retry_afterward()
    {
        var svc = GivenService("orders-msg", ServiceStatus.Failed, "simulated failure");
        Panel.GivenUser(fixture.WorkspaceId, "svc-retry-msg@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.152", "svc-retry-msg@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/reprovision", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var html = await (await client.GetAsync(response.Headers.Location!.ToString())).Content.ReadAsStringAsync();
        // data-spec-message carries the raw text regardless of which language rendered it — the
        // point here is that queuing a retry reports something, not which words it used.
        html.Should().Contain("data-spec-message=", "queuing a retry has to say so, the same as any other action");
        // The row itself has not moved past Failed yet (nothing processes the queued job in this
        // harness), so the button still reads as a retry on the very next render.
        html.Should().Contain("data-spec-reprovision=\"retry\"");
    }
}
