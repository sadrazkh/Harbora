using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Status;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>status-{workspaceSlug}.&lt;platform domain&gt;</c> end to end (P7, 2026-08-20 platform-options
/// plan) — a real request through the real pipeline, resolved by <c>Host</c> header alone, with no
/// cookie and no session behind it. This is the page where honesty matters most in the whole plan:
/// every assertion below either proves the opt-in gate or proves a specific fabricated-green trap does
/// not happen.
///
/// <para>
/// <b>Every test gets its own workspace</b> rather than reusing <c>fixture.WorkspaceId</c>:
/// <c>StatusPage.WorkspaceId</c> is unique, and <see cref="HarboraHttpFixture"/> is one shared panel
/// for the whole collection — seeding a second status page against the fixture's own workspace from a
/// second test would collide with the first. A fresh workspace per test is also, incidentally, exactly
/// the shape a tenancy test wants.
/// </para>
///
/// <para>
/// The panel renders Persian by default in this harness, so assertions read <c>data-*</c> attributes
/// (component state, uptime-has-data) rather than the rendered sentence — the same discipline every
/// other HTTP test file in this project follows.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class StatusPageHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>Seeds a fresh workspace (with the project/environment an App row needs) and returns
    /// the host its status page would answer on.</summary>
    private (Guid WorkspaceId, Guid EnvironmentId, string Host) GivenWorkspace(string slug)
    {
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = slug, Slug = slug });
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = workspaceId, Name = "App", Slug = "app"
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = workspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
        });
        return (workspaceId, environmentId, $"status-{slug}.example.test");
    }

    // ---- opt-in ------------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_with_no_status_page_row_at_all_answers_404()
    {
        var (_, _, host) = GivenWorkspace("no-page-row");
        var client = Panel.ClientFrom("203.0.113.1");

        var response = await client.GetWithHostAsync("/", host);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "opt-in only — no row means nothing is public, the same as a workspace that never asked");
    }

    [Fact]
    public async Task A_status_page_row_that_exists_but_is_not_enabled_answers_the_same_404()
    {
        var (workspaceId, _, host) = GivenWorkspace("disabled-page");
        Panel.Seed(db => db.StatusPages.Add(new StatusPage { WorkspaceId = workspaceId, IsEnabled = false }));
        var client = Panel.ClientFrom("203.0.113.2");

        var response = await client.GetWithHostAsync("/", host);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a page nobody switched on must not be distinguishable from one that does not exist");
    }

    [Fact]
    public async Task An_enabled_page_with_no_apps_chosen_renders_rather_than_404s()
    {
        var (workspaceId, _, host) = GivenWorkspace("empty-enabled-page");
        Panel.Seed(db => db.StatusPages.Add(new StatusPage { WorkspaceId = workspaceId, IsEnabled = true }));
        var client = Panel.ClientFrom("203.0.113.3");

        var response = await client.GetWithHostAsync("/", host);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "enabling the page is itself the customer's choice — an empty component list is not the same as opted out");
    }

    // ---- only the apps the customer chose, under the name the customer gave them ------------

    [Fact]
    public async Task Only_the_chosen_app_appears_and_only_under_its_given_display_name()
    {
        var (workspaceId, environmentId, host) = GivenWorkspace("selective-page");
        var shownAppId = Guid.CreateVersion7();
        var hiddenAppId = Guid.CreateVersion7();
        var pageId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = shownAppId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "public-api-internal-name", Slug = "public-api-internal-name", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/api:1.0",
                Status = AppStatus.Running
            });
            db.Apps.Add(new App
            {
                Id = hiddenAppId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "internal-admin-tool", Slug = "internal-admin-tool", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/admin:1.0",
                Status = AppStatus.Running
            });
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = workspaceId, StatusPageId = pageId, AppId = shownAppId,
                DisplayName = "Public API", SortOrder = 0
            });
        });
        var client = Panel.ClientFrom("203.0.113.4");

        var html = await (await client.GetWithHostAsync("/", host)).Content.ReadAsStringAsync();

        html.Should().Contain("Public API", "the customer-chosen name is what a visitor should see");
        html.Should().NotContain("internal-admin-tool", "an app never chosen for the page must not appear at all");
        html.Should().NotContain("public-api-internal-name",
            "the app's own slug/hostname must never leak even for the app that was chosen — only the given display name");
    }

    // ---- honesty: unknown health, never-deployed, no history --------------------------------

    [Fact]
    public async Task An_app_that_has_never_deployed_reads_as_unknown_with_no_uptime_history()
    {
        var (workspaceId, environmentId, host) = GivenWorkspace("never-deployed-page");
        var appId = Guid.CreateVersion7();
        var pageId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "brand-new", Slug = "brand-new", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/new:1.0",
                Status = AppStatus.Created
            });
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = workspaceId, StatusPageId = pageId, AppId = appId,
                DisplayName = "New service", SortOrder = 0
            });
        });
        var client = Panel.ClientFrom("203.0.113.5");

        var html = await (await client.GetWithHostAsync("/", host)).Content.ReadAsStringAsync();

        html.Should().Contain("data-status-component-state=\"unknown\"",
            "a never-deployed app must never render as operational");
        html.Should().Contain("data-status-component-uptime-has-data=\"false\"",
            "no deployment has ever run, so there is no 30-day window to have collected anything in");
    }

    [Fact]
    public async Task A_running_app_with_no_collected_metrics_still_shows_no_history_rather_than_a_fabricated_number()
    {
        // Running (not Created) but with zero MonitoringMetric/MetricRollup rows — the collector
        // simply has not ticked yet. LifecycleHistory's own "was anything ever collected" gate must
        // still say unknown, never a fabricated 100%.
        var (workspaceId, environmentId, host) = GivenWorkspace("fresh-running-page");
        var appId = Guid.CreateVersion7();
        var pageId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "freshly-running", Slug = "freshly-running", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/fresh:1.0",
                Status = AppStatus.Running
            });
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = workspaceId, StatusPageId = pageId, AppId = appId,
                DisplayName = "Fresh", SortOrder = 0
            });
        });
        var client = Panel.ClientFrom("203.0.113.6");

        var html = await (await client.GetWithHostAsync("/", host)).Content.ReadAsStringAsync();

        html.Should().Contain("data-status-component-state=\"operational\"");
        html.Should().Contain("data-status-component-uptime-has-data=\"false\"",
            "App.Status says Running, but nothing has ever been collected for its container — the two are different facts");
    }

    [Theory]
    [InlineData(AppStatus.Stopped, "stopped-page")]
    [InlineData(AppStatus.Crashed, "crashed-page")]
    [InlineData(AppStatus.Failed, "failed-page")]
    public async Task Stopped_crashed_and_failed_apps_read_as_degraded_not_operational(AppStatus status, string slug)
    {
        var (workspaceId, environmentId, host) = GivenWorkspace(slug);
        var appId = Guid.CreateVersion7();
        var pageId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "unwell", Slug = "unwell", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/unwell:1.0",
                Status = status
            });
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = workspaceId, StatusPageId = pageId, AppId = appId,
                DisplayName = "Unwell", SortOrder = 0
            });
        });
        var client = Panel.ClientFrom("203.0.113.7");

        var html = await (await client.GetWithHostAsync("/", host)).Content.ReadAsStringAsync();

        html.Should().Contain("data-status-component-state=\"degraded\"");
    }

    [Fact]
    public async Task A_maintaining_app_reads_as_maintenance_even_while_still_reported_running()
    {
        // App.MaintenanceMode redirects the app's own routes to a themed 503 (sub-project 5) but
        // leaves the container — and therefore AppStatus — running underneath. The public page must
        // follow the flag, not the status column, or a visitor sees "operational" for an app the
        // owner has deliberately taken out of service.
        var (workspaceId, environmentId, host) = GivenWorkspace("maintenance-page");
        var appId = Guid.CreateVersion7();
        var pageId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "under-maintenance", Slug = "under-maintenance", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/maint:1.0",
                Status = AppStatus.Running, MaintenanceMode = true, MaintenanceSince = DateTimeOffset.UtcNow
            });
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = workspaceId, StatusPageId = pageId, AppId = appId,
                DisplayName = "Under maintenance", SortOrder = 0
            });
        });
        var client = Panel.ClientFrom("203.0.113.13");

        var html = await (await client.GetWithHostAsync("/", host)).Content.ReadAsStringAsync();

        html.Should().Contain("data-status-component-state=\"maintenance\"");
    }

    // ---- 2.1 (2026-09 market-gaps round two): the probe result, not App.Status, decides the state --

    [Fact]
    public async Task A_failing_probe_reads_degraded_even_though_App_Status_says_running()
    {
        // The exact gap 2.1 closes: a container running happily while its app answers the wrong thing
        // to every request must not look healthy on the page a customer's own visitors read.
        var (workspaceId, environmentId, host) = GivenWorkspace("probe-down-page");
        var appId = Guid.CreateVersion7();
        var pageId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "quietly-broken", Slug = "quietly-broken", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/broken:1.0",
                Status = AppStatus.Running
            });
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = workspaceId, StatusPageId = pageId, AppId = appId,
                DisplayName = "Quietly broken", SortOrder = 0
            });
            db.UptimeChecks.Add(new Harbora.Domain.Monitoring.UptimeCheck
            {
                WorkspaceId = workspaceId, AppId = appId, IsEnabled = true,
                LastOutcome = Harbora.Domain.Monitoring.UptimeCheckOutcome.Down,
                LastCheckedAt = DateTimeOffset.UtcNow, LastHttpStatus = 503,
                LastDetail = "answered 503, expected 200."
            });
        });
        var client = Panel.ClientFrom("198.51.100.120");

        var html = await (await client.GetWithHostAsync("/", host)).Content.ReadAsStringAsync();

        html.Should().Contain("data-status-component-state=\"degraded\"",
            "App.Status says Running, but the outside-in probe says the app is not answering — the probe must win");
    }

    [Fact]
    public async Task A_probe_that_could_not_run_reads_unknown_never_operational_and_never_degraded()
    {
        var (workspaceId, environmentId, host) = GivenWorkspace("probe-could-not-run-page");
        var appId = Guid.CreateVersion7();
        var pageId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "unmeasured", Slug = "unmeasured", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/unmeasured:1.0",
                Status = AppStatus.Running
            });
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = workspaceId, StatusPageId = pageId, AppId = appId,
                DisplayName = "Unmeasured", SortOrder = 0
            });
            db.UptimeChecks.Add(new Harbora.Domain.Monitoring.UptimeCheck
            {
                WorkspaceId = workspaceId, AppId = appId, IsEnabled = true,
                LastOutcome = Harbora.Domain.Monitoring.UptimeCheckOutcome.CouldNotRun,
                LastCheckedAt = DateTimeOffset.UtcNow, LastDetail = "no public domain configured for this app."
            });
        });
        var client = Panel.ClientFrom("198.51.100.121");

        var html = await (await client.GetWithHostAsync("/", host)).Content.ReadAsStringAsync();

        html.Should().Contain("data-status-component-state=\"unknown\"",
            "a probe that never got to ask the question is neither a pass nor a confirmed failure");
    }

    [Fact]
    public async Task A_passing_probe_reads_operational_the_same_as_App_Status_running_alone()
    {
        var (workspaceId, environmentId, host) = GivenWorkspace("probe-up-page");
        var appId = Guid.CreateVersion7();
        var pageId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "healthy", Slug = "healthy", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/healthy:1.0",
                Status = AppStatus.Running
            });
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = workspaceId, StatusPageId = pageId, AppId = appId,
                DisplayName = "Healthy", SortOrder = 0
            });
            db.UptimeChecks.Add(new Harbora.Domain.Monitoring.UptimeCheck
            {
                WorkspaceId = workspaceId, AppId = appId, IsEnabled = true,
                LastOutcome = Harbora.Domain.Monitoring.UptimeCheckOutcome.Up,
                LastCheckedAt = DateTimeOffset.UtcNow, LastHttpStatus = 200, LastDetail = "answered 200."
            });
        });
        var client = Panel.ClientFrom("198.51.100.122");

        var html = await (await client.GetWithHostAsync("/", host)).Content.ReadAsStringAsync();

        html.Should().Contain("data-status-component-state=\"operational\"");
    }

    // ---- manual incident notes ----------------------------------------------------------------

    [Fact]
    public async Task An_open_incident_renders_and_a_resolved_one_moves_to_history()
    {
        var (workspaceId, _, host) = GivenWorkspace("incidents-page");
        var pageId = Guid.CreateVersion7();
        var openId = Guid.CreateVersion7();
        var resolvedId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusIncidents.Add(new StatusIncident
            {
                Id = openId, WorkspaceId = workspaceId, StatusPageId = pageId,
                TitleEn = "Investigating a slowdown", TitleFa = "بررسی کندی سرویس",
                BodyEn = "We are looking into it.", BodyFa = "در حال بررسی هستیم.",
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1)
            });
            db.StatusIncidents.Add(new StatusIncident
            {
                Id = resolvedId, WorkspaceId = workspaceId, StatusPageId = pageId,
                TitleEn = "Past outage", TitleFa = "قطعی پیشین",
                StartedAt = DateTimeOffset.UtcNow.AddDays(-2), ResolvedAt = DateTimeOffset.UtcNow.AddDays(-2).AddHours(1)
            });
        });
        var client = Panel.ClientFrom("203.0.113.8");

        var html = await (await client.GetWithHostAsync("/", host)).Content.ReadAsStringAsync();

        // Persian text renders as numeric HTML character references (System.Text.Encodings.Web's
        // default encoder), so — same discipline as every other HTTP test in this project — the open
        // vs. resolved distinction is asserted by data attribute, not by matching the sentence itself.
        html.Should().Contain($"data-status-incident-id=\"{openId}\" data-status-incident-open=\"true\"");
        html.Should().Contain($"data-status-incident-id=\"{resolvedId}\" data-status-incident-open=\"false\"");
    }

    // ---- tenancy, both directions --------------------------------------------------------------

    [Fact]
    public async Task Two_workspaces_own_status_pages_never_show_each_others_apps_or_incidents()
    {
        var (mineId, mineEnvId, mineHost) = GivenWorkspace("mine-status-co");
        var (theirsId, theirsEnvId, theirsHost) = GivenWorkspace("theirs-status-co");
        var mineAppId = Guid.CreateVersion7();
        var theirsAppId = Guid.CreateVersion7();
        var minePageId = Guid.CreateVersion7();
        var theirsPageId = Guid.CreateVersion7();
        var mineIncidentId = Guid.CreateVersion7();
        var theirsIncidentId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = mineAppId, WorkspaceId = mineId, EnvironmentId = mineEnvId,
                Name = "mine", Slug = "mine", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/mine:1.0",
                Status = AppStatus.Running
            });
            db.StatusPages.Add(new StatusPage { Id = minePageId, WorkspaceId = mineId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = mineId, StatusPageId = minePageId, AppId = mineAppId,
                DisplayName = "Mine Only", SortOrder = 0
            });
            db.StatusIncidents.Add(new StatusIncident
            {
                Id = mineIncidentId, WorkspaceId = mineId, StatusPageId = minePageId,
                TitleEn = "My incident", TitleFa = "رخداد من", StartedAt = DateTimeOffset.UtcNow
            });

            db.Apps.Add(new App
            {
                Id = theirsAppId, WorkspaceId = theirsId, EnvironmentId = theirsEnvId,
                Name = "theirs", Slug = "theirs", Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/theirs:1.0",
                Status = AppStatus.Running
            });
            db.StatusPages.Add(new StatusPage { Id = theirsPageId, WorkspaceId = theirsId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                WorkspaceId = theirsId, StatusPageId = theirsPageId, AppId = theirsAppId,
                DisplayName = "Theirs Only", SortOrder = 0
            });
            db.StatusIncidents.Add(new StatusIncident
            {
                Id = theirsIncidentId, WorkspaceId = theirsId, StatusPageId = theirsPageId,
                TitleEn = "Their incident", TitleFa = "رخداد آنها", StartedAt = DateTimeOffset.UtcNow
            });
        });

        var client = Panel.ClientFrom("203.0.113.9");

        // Direction one: my page shows only mine — incident identity asserted by id (data-attribute),
        // not by matching the rendered Persian sentence (HTML-entity-encoded by default).
        var mine = await (await client.GetWithHostAsync("/", mineHost)).Content.ReadAsStringAsync();
        mine.Should().Contain("Mine Only");
        mine.Should().Contain($"data-status-incident-id=\"{mineIncidentId}\"");
        mine.Should().NotContain("Theirs Only");
        mine.Should().NotContain($"data-status-incident-id=\"{theirsIncidentId}\"");

        // Direction two: their page shows only theirs, reached on its own host.
        var theirs = await (await client.GetWithHostAsync("/", theirsHost)).Content.ReadAsStringAsync();
        theirs.Should().Contain("Theirs Only");
        theirs.Should().Contain($"data-status-incident-id=\"{theirsIncidentId}\"");
        theirs.Should().NotContain("Mine Only");
        theirs.Should().NotContain($"data-status-incident-id=\"{mineIncidentId}\"");
    }

    [Fact]
    public async Task An_unregistered_slug_answers_404_rather_than_falling_back_to_any_workspace()
    {
        var client = Panel.ClientFrom("203.0.113.10");

        var response = await client.GetWithHostAsync("/", "status-no-such-workspace-exists.example.test");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_host_that_does_not_match_the_status_prefix_never_reaches_the_public_controller()
    {
        // No status- prefix at all: StatusPageHostMiddleware leaves the request untouched and it
        // falls through to whatever the ordinary pipeline does for that host — never the anonymous
        // status endpoint by accident.
        var client = Panel.ClientFrom("203.0.113.11");

        var response = await client.GetWithHostAsync("/__status-page", "harbora-http.example.test");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "reaching the internal path segment directly, on a host the middleware never matched, must resolve nothing");
    }

    // ---- no chrome, no cookie ------------------------------------------------------------------

    [Fact]
    public async Task The_page_carries_no_set_cookie_and_none_of_the_panels_own_chrome()
    {
        var (workspaceId, _, host) = GivenWorkspace("no-chrome-page");
        Panel.Seed(db => db.StatusPages.Add(new StatusPage { WorkspaceId = workspaceId, IsEnabled = true }));
        var client = Panel.ClientFrom("203.0.113.12");

        var response = await client.GetWithHostAsync("/", host);
        var html = await response.Content.ReadAsStringAsync();

        response.Headers.Contains("Set-Cookie").Should().BeFalse("the public page issues no cookie of its own");
        html.Should().NotContain("data-sidebar", "the panel's own chrome must not leak onto the public page");
        html.Should().NotContain("/account/logout");
    }
}
