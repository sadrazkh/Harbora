using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The one control this feature was missing before it could be called finished: a way for a customer
/// to actually set <c>App.DesiredReplicas</c>, rather than only ever reading it back. Written the same
/// way <c>Resize</c> already writes a resource change — to the row, applied on the next deployment —
/// so scaling never becomes a second, less-tested path around <c>DeploymentPipeline</c>.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppReplicasHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>EnvironmentId is required (P2, 2026-08-17 app-environment-management design); a
    /// project and environment of the app's own keeps every app this class seeds inside a scope the
    /// signed-in owner actually has capability grants over.</summary>
    private Guid SeedApp(string slug, ServiceKind kind = ServiceKind.Web, long memoryBytes = 0, double cpuCores = 0)
    {
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            EnvironmentId = environmentId,
            Name = slug,
            Slug = slug,
            Kind = kind,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running,
            MemoryLimitBytes = memoryBytes,
            CpuLimit = cpuCores
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "replicas-" + slug
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = fixture.WorkspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
            db.Apps.Add(app);
        });
        return app.Id;
    }

    [Fact]
    public async Task Setting_replicas_writes_the_row_and_says_it_applies_next_deploy()
    {
        var appId = SeedApp("replicas-set");
        Panel.GivenUser(fixture.WorkspaceId, "replicas-set@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.240", "replicas-set@example.com");

        var response = await client.PostFormAsync($"/apps/{appId}/replicas",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("replicas", "3"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().BeEquivalentTo($"/apps/details/{appId}");

        var stored = Panel.Read(db => db.Apps.First(a => a.Id == appId));
        stored.DesiredReplicas.Should().Be(3);
    }

    [Fact]
    public async Task A_replica_count_below_one_is_refused_and_changes_nothing()
    {
        var appId = SeedApp("replicas-zero");
        Panel.GivenUser(fixture.WorkspaceId, "replicas-zero@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.241", "replicas-zero@example.com");

        var response = await client.PostFormAsync($"/apps/{appId}/replicas",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("replicas", "0"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var stored = Panel.Read(db => db.Apps.First(a => a.Id == appId));
        stored.DesiredReplicas.Should().Be(1, "a refused change must leave the app's own default untouched");
    }

    [Fact]
    public async Task Scaling_past_the_plan_memory_cap_is_refused()
    {
        // 200 MB per replica, a 512 MB wall: two replicas fit (400 MB), a third does not (600 MB).
        const long mb = 1024L * 1024;
        var planId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Plans.Add(new Plan
            {
                Id = planId, Name = "capped", AllowsOverage = false, MaxMemoryBytes = 512 * mb, IsEnabled = true
            });
            var workspace = db.Workspaces.First(w => w.Id == fixture.WorkspaceId);
            workspace.PlanId = planId;
        });
        var appId = SeedApp("replicas-capped", memoryBytes: 200 * mb);
        Panel.GivenUser(fixture.WorkspaceId, "replicas-capped@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.242", "replicas-capped@example.com");

        var response = await client.PostFormAsync($"/apps/{appId}/replicas",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("replicas", "3"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.Apps.First(a => a.Id == appId));
        stored.DesiredReplicas.Should().Be(1, "the plan's memory cap must refuse the scale-up, not silently cap it");
    }

    [Fact]
    public async Task A_cron_apps_page_offers_no_replica_control()
    {
        var appId = SeedApp("replicas-cron", ServiceKind.Cron);
        Panel.GivenUser(fixture.WorkspaceId, "replicas-cron@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.243", "replicas-cron@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().NotContain("data-replicas-input",
            "a scheduled job never starts a long-running container, so a replica count describes nothing");
    }
}
