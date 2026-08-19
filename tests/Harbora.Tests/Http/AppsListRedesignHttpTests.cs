using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Networking;
using Harbora.Domain.Servers;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The Applications list redesign (2026-08-19 apps-redesign, option 3b of the design handoff):
/// filter tabs that carry their own counts and disable themselves at zero, a HEALTH · 1H column that
/// replaces the old separate SOURCE/CPU/MEMORY/STATUS columns, and a DOMAIN column that stays grey
/// until something has actually deployed.
///
/// <para>
/// The panel renders Persian by default in this harness, so every assertion here reads a
/// <c>data-</c> attribute, a form's own action, or one of the technical readings (CPU/memory/dates)
/// that <c>Views/Apps/Index.cshtml</c> deliberately never translates — never a UI sentence, which
/// would only be true under English.
/// </para>
///
/// <para>Each test gets a workspace, project and environment of its own rather than the shared HTTP
/// fixture's: the filter tab counts this suite checks are exact, and a shared workspace accumulates
/// apps from every other test in the collection.</para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppsListRedesignHttpTests
{
    private sealed record Panel(HarboraWebFactory Factory, Guid WorkspaceId, Guid EnvironmentId, Guid ServerId);

    private static Panel GivenAFreshPanel(string suffix)
    {
        var factory = new HarboraWebFactory();
        var workspaceId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var serverId = Guid.CreateVersion7();

        factory.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId, Name = "Apps redesign", Slug = "apps-redesign-" + suffix,
                IsDefault = true, PlanId = planId == Guid.Empty ? null : planId
            });
            db.Settings.Add(new Setting { Key = SettingKeys.SetupCompleted, Value = "true" });

            var projectId = Guid.CreateVersion7();
            db.Projects.Add(new Harbora.Domain.Projects.Project
            { Id = projectId, WorkspaceId = workspaceId, Name = "Shop", Slug = "shop-" + suffix });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = workspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
            db.Servers.Add(new Server
            {
                Id = serverId, Name = "local", Hostname = "localhost", IsLocal = true,
                Status = ServerStatus.Online, TotalMemoryBytes = 8L << 30, CpuCores = 4
            });
        });

        factory.GivenUser(workspaceId, $"apps-redesign-{suffix}@example.com", SystemRole.Owner);
        return new Panel(factory, workspaceId, environmentId, serverId);
    }

    private static App NewApp(Panel panel, string slug, AppStatus status) => new()
    {
        WorkspaceId = panel.WorkspaceId, EnvironmentId = panel.EnvironmentId, ServerId = panel.ServerId,
        Name = slug, Slug = slug, Kind = ServiceKind.Web,
        SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/" + slug + ":1.0",
        Status = status
    };

    // ---- filter tabs -----------------------------------------------------------------------------

    [Fact]
    public async Task A_status_tab_with_nothing_in_it_is_disabled_while_a_populated_one_stays_clickable()
    {
        var panel = GivenAFreshPanel("tabs-1");
        await using var factory = panel.Factory;
        panel.Factory.Seed(db => db.Apps.Add(NewApp(panel, "tabs-only-running", AppStatus.Running)));
        var client = await panel.Factory.SignedInAs("203.0.113.10", $"apps-redesign-tabs-1@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain("data-status=\"Running\">",
            "a tab with at least one app must stay a plain, clickable button");
        html.Should().Contain("data-status=\"Failed\" disabled",
            "a tab whose count is zero must carry the disabled attribute, not just look dim");
        html.Should().Contain("data-status=\"Stopped\" disabled");
        html.Should().Contain("data-status=\"Deploying\" disabled");
    }

    // ---- HEALTH · 1H -------------------------------------------------------------------------------

    [Fact]
    public async Task An_app_that_has_never_deployed_reads_no_metrics_with_a_warn_dot_and_no_sparkline()
    {
        var panel = GivenAFreshPanel("health-1");
        await using var factory = panel.Factory;
        var app = NewApp(panel, "never-deployed-app", AppStatus.Created);
        panel.Factory.Seed(db => db.Apps.Add(app));
        var client = await panel.Factory.SignedInAs("203.0.113.11", "apps-redesign-health-1@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain("data-health=\"no-metrics\"",
            "an app with nothing measured must be marked this way regardless of which language rendered the sentence");
        html.Should().Contain("data-deployed=\"false\"");
        html.Should().Contain("bg-warn", "the spec pins the no-data dot to --warn specifically, not the app's own status colour");
        html.Should().NotContain("viewBox=\"0 0 100 20\"",
            "no series exists to draw a trend from — the sparkline must not appear at all");
    }

    [Fact]
    public async Task A_running_app_with_real_traffic_shows_its_cpu_and_memory_reading_and_a_sparkline()
    {
        var panel = GivenAFreshPanel("health-2");
        await using var factory = panel.Factory;
        var (app, containerName) = DeployedApp(panel, "busy-app", memoryLimitBytes: 1024L * 1024 * 1024);
        panel.Factory.Seed(db =>
        {
            db.Apps.Add(app.App);
            db.Deployments.Add(app.Deployment);
            db.MonitoringMetrics.Add(new MonitoringMetric
            {
                ServerId = panel.ServerId, Name = "cpu.percent", ResourceRef = containerName,
                Value = 0.6, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            db.MonitoringMetrics.Add(new MonitoringMetric
            {
                ServerId = panel.ServerId, Name = "mem.used", ResourceRef = containerName,
                Value = 141.0 * 1024 * 1024, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            // Two samples in different six-minute buckets so SparklinePath (needs >= 2 points) draws.
            db.MonitoringMetrics.Add(new MonitoringMetric
            {
                ServerId = panel.ServerId, Name = "cpu.percent", ResourceRef = containerName,
                Value = 0.4, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-40)
            });
        });
        var client = await panel.Factory.SignedInAs("203.0.113.12", "apps-redesign-health-2@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain("data-health=\"normal\"");
        // Razor's HTML encoder rewrites the literal middle dot as a numeric entity, so the string
        // that actually reaches the browser (and this assertion) carries "&#xB7;" rather than "·".
        html.Should().Contain("CPU 0.6% &#xB7; 141/1024MB",
            "the reading is a technical token — always this exact English notation, never translated");
        html.Should().Contain("viewBox=\"0 0 100 20\"", "a real reading with more than one bucketed sample must draw a trend");
    }

    [Fact]
    public async Task A_running_app_with_no_traffic_reads_idle_rather_than_a_zero_percent_reading()
    {
        var panel = GivenAFreshPanel("health-3");
        await using var factory = panel.Factory;
        var (app, containerName) = DeployedApp(panel, "idle-app", memoryLimitBytes: 512L * 1024 * 1024);
        panel.Factory.Seed(db =>
        {
            db.Apps.Add(app.App);
            db.Deployments.Add(app.Deployment);
            db.MonitoringMetrics.Add(new MonitoringMetric
            {
                ServerId = panel.ServerId, Name = "cpu.percent", ResourceRef = containerName,
                Value = 0, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
        });
        var client = await panel.Factory.SignedInAs("203.0.113.13", "apps-redesign-health-3@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain("data-health=\"idle\"");
        html.Should().Contain("idle &#xB7; 512MB limit");
        html.Should().NotContain("CPU 0%", "a rounded-to-zero reading must read as idle, not as a measurement of nothing");
    }

    // ---- DOMAIN --------------------------------------------------------------------------------

    [Fact]
    public async Task A_domain_on_an_app_that_has_never_deployed_is_plain_text_not_a_live_link()
    {
        var panel = GivenAFreshPanel("domain-1");
        await using var factory = panel.Factory;
        var app = NewApp(panel, "placeholder-app", AppStatus.Created);
        panel.Factory.Seed(db =>
        {
            db.Apps.Add(app);
            db.Domains.Add(new DomainName { AppId = app.Id, Host = "placeholder.example.test", IsPrimary = true });
        });
        var client = await panel.Factory.SignedInAs("203.0.113.14", "apps-redesign-domain-1@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain("placeholder.example.test");
        html.Should().NotContain("href=\"https://placeholder.example.test\"",
            "nothing is actually live there yet, so the domain must not read as a clickable link");
    }

    [Fact]
    public async Task A_domain_on_a_deployed_app_is_a_live_link()
    {
        var panel = GivenAFreshPanel("domain-2");
        await using var factory = panel.Factory;
        var (app, _) = DeployedApp(panel, "live-app");
        panel.Factory.Seed(db =>
        {
            db.Apps.Add(app.App);
            db.Deployments.Add(app.Deployment);
            db.Domains.Add(new DomainName { AppId = app.App.Id, Host = "live.example.test", IsPrimary = true });
        });
        var client = await panel.Factory.SignedInAs("203.0.113.15", "apps-redesign-domain-2@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain("href=\"https://live.example.test\"");
    }

    // ---- row actions -----------------------------------------------------------------------------

    [Fact]
    public async Task A_never_deployed_app_only_offers_a_deploy_button_no_logs_link()
    {
        var panel = GivenAFreshPanel("actions-1");
        await using var factory = panel.Factory;
        var app = NewApp(panel, "not-yet-deployed", AppStatus.Created);
        panel.Factory.Seed(db => db.Apps.Add(app));
        var client = await panel.Factory.SignedInAs("203.0.113.16", "apps-redesign-actions-1@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain($"action=\"/Apps/Deploy/{app.Id}\"",
            "the dark Deploy button posts to the same Deploy action every other deploy trigger uses");
        html.Should().NotContain($"/apps/{app.Id}/logs",
            "there is nothing to look at yet, so no Logs link should exist for this app");
    }

    [Fact]
    public async Task A_deployed_app_offers_both_logs_and_redeploy()
    {
        var panel = GivenAFreshPanel("actions-2");
        await using var factory = panel.Factory;
        var (app, _) = DeployedApp(panel, "already-deployed");
        panel.Factory.Seed(db =>
        {
            db.Apps.Add(app.App);
            db.Deployments.Add(app.Deployment);
        });
        var client = await panel.Factory.SignedInAs("203.0.113.17", "apps-redesign-actions-2@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain($"href=\"/apps/{app.App.Id}/logs\"");
        html.Should().Contain($"action=\"/Apps/Deploy/{app.App.Id}\"");
    }

    // ---- empty state and layout -------------------------------------------------------------------

    [Fact]
    public async Task An_empty_workspace_shows_the_shared_empty_state_not_a_zero_row_table()
    {
        var panel = GivenAFreshPanel("empty-1");
        await using var factory = panel.Factory;
        var client = await panel.Factory.SignedInAs("203.0.113.18", "apps-redesign-empty-1@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().NotContain("id=\"appRows\"", "with nothing to list, the grid itself must not render at all");
        html.Should().Contain("href=\"/apps/create\"");
    }

    [Fact]
    public async Task The_list_never_falls_back_to_a_horizontally_scrolling_table()
    {
        var panel = GivenAFreshPanel("mobile-1");
        await using var factory = panel.Factory;
        panel.Factory.Seed(db => db.Apps.Add(NewApp(panel, "mobile-check-app", AppStatus.Running)));
        var client = await panel.Factory.SignedInAs("203.0.113.19", "apps-redesign-mobile-1@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().NotContain("overflow-x-auto",
            "the redesign replaces the old horizontal-scroll wrapper with a grid that folds into a mobile card");
    }

    // ---- the project, when there is more than one ------------------------------------------------
    //
    // The redesign dropped the PROJECT column because the top switcher already names the project.
    // That premise holds for environment but not for project: AppsController.Index scopes its query
    // to the workspace, so a workspace with two projects lists both together. These two tests hold
    // the conditional from both sides — it must appear when it distinguishes rows, and stay out of
    // the way when it would only repeat itself.

    [Fact]
    public async Task The_project_appears_on_each_row_once_the_list_covers_more_than_one()
    {
        var panel = GivenAFreshPanel("proj-many");
        await using var factory = panel.Factory;

        panel.Factory.Seed(db =>
        {
            var otherProjectId = Guid.CreateVersion7();
            var otherEnvironmentId = Guid.CreateVersion7();
            db.Projects.Add(new Harbora.Domain.Projects.Project
            { Id = otherProjectId, WorkspaceId = panel.WorkspaceId, Name = "Billing", Slug = "billing-proj-many" });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = otherEnvironmentId, WorkspaceId = panel.WorkspaceId, ProjectId = otherProjectId,
                Name = "Production", Slug = "production"
            });

            db.Apps.Add(NewApp(panel, "shop-web", AppStatus.Running));
            var other = NewApp(panel, "billing-web", AppStatus.Running);
            other.EnvironmentId = otherEnvironmentId;
            db.Apps.Add(other);
        });

        var client = await panel.Factory.SignedInAs("203.0.113.60", "apps-redesign-proj-many@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain("data-app-project",
            "with two projects in one list, a row that does not name its project is ambiguous");
        html.Should().Contain(">Billing<");
        html.Should().Contain(">Shop<");
    }

    [Fact]
    public async Task The_project_stays_off_the_row_when_every_app_shares_one()
    {
        var panel = GivenAFreshPanel("proj-one");
        await using var factory = panel.Factory;
        panel.Factory.Seed(db =>
        {
            db.Apps.Add(NewApp(panel, "only-web", AppStatus.Running));
            db.Apps.Add(NewApp(panel, "only-api", AppStatus.Running));
        });
        var client = await panel.Factory.SignedInAs("203.0.113.61", "apps-redesign-proj-one@example.com");

        var html = await client.GetStringAsync("/apps");

        html.Should().NotContain("data-app-project",
            "repeating one project's name on every row costs space and tells the reader nothing");
        html.Should().Contain("data-app-subtitle",
            "the subtitle still renders — it is the project fragment inside it that is conditional");
    }

    // ---- fixtures -----------------------------------------------------------------------------

    private sealed record DeployedAppFixture(App App, Deployment Deployment);

    /// <summary>An app with a succeeded deployment and an active container, so a container name can
    /// be derived the same way <c>AppsController.Index</c> derives one for the metrics lookups.</summary>
    private static (DeployedAppFixture Fixture, string ContainerName) DeployedApp(
        Panel panel, string slug, long memoryLimitBytes = 0)
    {
        var deploymentId = Guid.CreateVersion7();
        var app = NewApp(panel, slug, AppStatus.Running);
        app.ActiveDeploymentId = deploymentId;
        app.MemoryLimitBytes = memoryLimitBytes;
        var deployment = new Deployment
        {
            Id = deploymentId, AppId = app.Id, WorkspaceId = panel.WorkspaceId,
            Number = 7, Status = DeploymentStatus.Succeeded, Trigger = DeploymentTrigger.Manual,
            TriggeredByUserId = Guid.CreateVersion7(),
            StartedAt = new DateTimeOffset(2026, 8, 18, 11, 18, 48, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 8, 18, 11, 20, 0, TimeSpan.Zero)
        };
        var containerName = DeploymentPlanning.ContainerName(app.WorkspaceId, slug, deployment.Number);
        return (new DeployedAppFixture(app, deployment), containerName);
    }
}
