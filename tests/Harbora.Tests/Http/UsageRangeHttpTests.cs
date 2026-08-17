using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The usage-range design (docs/superpowers/specs/2026-08-15-usage-range-design.md): a range control
/// on both usage tabs, the choice living in <c>?minutes=</c>, the chart islands passed the chosen
/// window, and the <c>Metrics</c> endpoint honouring it without becoming a new way past the tenancy
/// check it already has.
///
/// <para>
/// Assertions read the control's own markup — a <c>data-</c> attribute and a query value — never a
/// sentence, because this panel renders Persian by default (do-not-change item, restated in the spec
/// itself).
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class UsageRangeHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    // ---- the control itself, on both tabs ------------------------------------------------------

    [Fact]
    public async Task The_apps_usage_tab_range_control_marks_the_requested_window_as_selected()
    {
        var appId = SeedApp("range-app-1");
        Panel.GivenUser(fixture.WorkspaceId, "range-app-owner-1@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.200", "range-app-owner-1@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/usage?minutes=1440")).Content.ReadAsStringAsync();

        html.Should().Contain("data-selected-minutes=\"1440\"",
            "the control's own wrapper must carry the resolved window, not a sentence that merely mentions the number");
        SelectedWindowFromControl(html).Should().Be("1440", "the 24h option is the one that must read as selected");
        CountOccurrences(html, "aria-current=\"true\"").Should().Be(1, "only one option may read as selected at a time");
    }

    [Fact]
    public async Task The_apps_usage_tab_defaults_its_range_control_to_one_hour_with_no_query_value()
    {
        var appId = SeedApp("range-app-2");
        Panel.GivenUser(fixture.WorkspaceId, "range-app-owner-2@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.201", "range-app-owner-2@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/usage")).Content.ReadAsStringAsync();

        html.Should().Contain("data-selected-minutes=\"60\"");
    }

    [Fact]
    public async Task An_unrecognised_minutes_value_on_the_apps_usage_tab_falls_back_to_one_hour_rather_than_selecting_nothing()
    {
        var appId = SeedApp("range-app-3");
        Panel.GivenUser(fixture.WorkspaceId, "range-app-owner-3@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.202", "range-app-owner-3@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/usage?minutes=999999")).Content.ReadAsStringAsync();

        html.Should().Contain("data-selected-minutes=\"60\"",
            "an unrecognised window must not leave the control with nothing highlighted");
    }

    [Fact]
    public async Task The_databases_usage_tab_range_control_marks_the_requested_window_as_selected()
    {
        var dbId = SeedManagedDatabase("range-db-1");
        Panel.GivenUser(fixture.WorkspaceId, "range-db-owner-1@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.203", "range-db-owner-1@example.com");

        var html = await (await client.GetAsync($"/databases/{dbId}/usage?minutes=10080")).Content.ReadAsStringAsync();

        html.Should().Contain("data-selected-minutes=\"10080\"");
        SelectedWindowFromControl(html).Should().Be("10080", "the 7d option is the one that must read as selected");
    }

    // ---- the choice reaching the chart islands --------------------------------------------------

    [Fact]
    public async Task The_chosen_window_is_passed_to_every_chart_island_on_the_apps_usage_tab()
    {
        var appId = SeedApp("range-app-4");
        Panel.GivenUser(fixture.WorkspaceId, "range-app-owner-4@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.204", "range-app-owner-4@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/usage?minutes=1440")).Content.ReadAsStringAsync();

        // Three chart islands on the apps Usage tab — mem.used, cpu.percent, net.rx — every one of
        // them, or a chart that quietly kept asking for the default hour would look identical to one
        // that redrew correctly.
        CountOccurrences(html, "data-minutes=\"1440\"").Should().Be(3,
            "every metrics-chart island on this tab must carry the page's chosen window");
    }

    [Fact]
    public async Task The_chosen_window_is_passed_to_every_chart_island_on_the_databases_usage_tab()
    {
        var dbId = SeedManagedDatabase("range-db-2");
        Panel.GivenUser(fixture.WorkspaceId, "range-db-owner-2@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.205", "range-db-owner-2@example.com");

        var html = await (await client.GetAsync($"/databases/{dbId}/usage?minutes=1440")).Content.ReadAsStringAsync();

        CountOccurrences(html, "data-minutes=\"1440\"").Should().Be(2,
            "both metrics-chart islands on this tab must carry the page's chosen window");
    }

    /// <summary>
    /// Which window's link carries <c>aria-current="true"</c> — tolerant of the whitespace Razor
    /// preserves between attributes written on their own lines in the partial, unlike a literal
    /// substring match on <c>href="…" aria-current="true"</c> with a single space assumed between
    /// them.
    /// </summary>
    private static string? SelectedWindowFromControl(string html)
    {
        var match = Regex.Match(html, "href=\"\\?minutes=(\\d+)\"\\s+aria-current=\"true\"",
            RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ---- the endpoint the islands actually call ---------------------------------------------------

    [Fact]
    public async Task Requesting_the_24_hour_window_returns_a_point_the_60_minute_window_does_not()
    {
        var (appId, containerName, serverId) = SeedDeployedApp("range-endpoint-1");
        var old = DateTimeOffset.UtcNow.AddHours(-20); // outside 1h, inside 24h
        var recent = DateTimeOffset.UtcNow.AddMinutes(-5); // inside both
        Panel.Seed(db =>
        {
            db.MonitoringMetrics.Add(new MonitoringMetric
            {
                ServerId = serverId, Name = "cpu.percent", ResourceRef = containerName, Value = 42, Timestamp = old
            });
            db.MonitoringMetrics.Add(new MonitoringMetric
            {
                ServerId = serverId, Name = "cpu.percent", ResourceRef = containerName, Value = 7, Timestamp = recent
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "range-endpoint-owner-1@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.206", "range-endpoint-owner-1@example.com");

        var hourValues = await ValuesAsync(client, appId, minutes: 60);
        var dayValues = await ValuesAsync(client, appId, minutes: 1440);

        hourValues.Should().NotContain(42, "the 20-hour-old sample is outside a 60-minute window");
        dayValues.Should().Contain(42, "a request for 24 hours must not return only the 60-minute slice");
    }

    [Fact]
    public async Task A_window_with_no_stored_points_returns_an_empty_series_not_a_series_of_zeroes()
    {
        var (appId, _, _) = SeedDeployedApp("range-endpoint-2");
        // No MonitoringMetric rows at all for this container.
        Panel.GivenUser(fixture.WorkspaceId, "range-endpoint-owner-2@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.207", "range-endpoint-owner-2@example.com");

        var response = await client.GetAsync($"/monitoring/metrics?name=cpu.percent&appId={appId}&minutes=60");
        var body = await response.JsonAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(0,
            "an unmeasured window must come back empty, not as points the chart could mistake for real zeroes");
    }

    [Fact]
    public async Task A_window_with_stored_zero_values_returns_them_as_real_points_not_as_emptiness()
    {
        var (appId, containerName, serverId) = SeedDeployedApp("range-endpoint-3");
        Panel.Seed(db =>
        {
            db.MonitoringMetrics.Add(new MonitoringMetric
            {
                ServerId = serverId, Name = "cpu.percent", ResourceRef = containerName,
                Value = 0, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10)
            });
            db.MonitoringMetrics.Add(new MonitoringMetric
            {
                ServerId = serverId, Name = "cpu.percent", ResourceRef = containerName,
                Value = 0, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "range-endpoint-owner-3@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.208", "range-endpoint-owner-3@example.com");

        var response = await client.GetAsync($"/monitoring/metrics?name=cpu.percent&appId={appId}&minutes=60");
        var body = await response.JsonAsync();

        body.GetArrayLength().Should().Be(2,
            "an app that genuinely used nothing must still be told apart from a window nobody measured");
    }

    // ---- tenancy is unchanged by the new parameter ------------------------------------------------

    [Fact]
    public async Task A_wider_window_does_not_let_the_caller_see_another_workspaces_app_metrics()
    {
        var foreignWorkspace = Guid.CreateVersion7();
        var (foreignAppId, containerName, serverId) = SeedDeployedApp("range-foreign-app", foreignWorkspace);
        Panel.Seed(db => db.MonitoringMetrics.Add(new MonitoringMetric
        {
            ServerId = serverId, Name = "cpu.percent", ResourceRef = containerName,
            Value = 99, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
        }));
        Panel.GivenUser(fixture.WorkspaceId, "range-tenancy-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.209", "range-tenancy-owner@example.com");

        // The widest of the three offered windows — the one most likely to tempt a "just clamp it"
        // shortcut that skips the visibility check entirely.
        var response = await client.GetAsync(
            $"/monitoring/metrics?name=cpu.percent&appId={foreignAppId}&minutes=10080");

        // MonitoringController.Metrics returns Forbid() for an app the caller cannot see
        // (MonitoringController.cs:169); the cookie scheme turns that into a redirect to its
        // AccessDeniedPath, the same way CapabilityPolicyHttpTests pins it for every other route.
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }

    // ---- fixtures -----------------------------------------------------------------------------

    private async Task<List<int>> ValuesAsync(HttpClient client, Guid appId, int minutes)
    {
        var response = await client.GetAsync($"/monitoring/metrics?name=cpu.percent&appId={appId}&minutes={minutes}");
        var body = await response.JsonAsync();
        return body.EnumerateArray().Select(p => p.GetProperty("v").GetInt32()).ToList();
    }

    private Guid SeedApp(string slug)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/" + slug + ":1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app.Id;
    }

    /// <summary>
    /// An app with an active deployment, so <c>MonitoringController.ContainerForAppAsync</c> can
    /// resolve a container name — unlike the Usage view's own "moment" query, the Metrics endpoint has
    /// no fallback to the latest succeeded deployment when <c>ActiveDeploymentId</c> is unset.
    /// </summary>
    private (Guid AppId, string ContainerName, Guid ServerId) SeedDeployedApp(string slug, Guid? workspaceId = null)
    {
        var serverId = Guid.CreateVersion7();
        var deploymentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = workspaceId ?? fixture.WorkspaceId,
            EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = serverId,
            Name = slug,
            Slug = slug,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/" + slug + ":1.0",
            Status = AppStatus.Running,
            ActiveDeploymentId = deploymentId
        };
        var deployment = new Deployment
        {
            Id = deploymentId,
            AppId = app.Id,
            WorkspaceId = workspaceId ?? fixture.WorkspaceId,
            Number = 1,
            Status = DeploymentStatus.Succeeded,
            Trigger = DeploymentTrigger.Manual,
            TriggeredByUserId = Guid.CreateVersion7()
        };
        Panel.Seed(db =>
        {
            db.Apps.Add(app);
            db.Deployments.Add(deployment);
        });
        return (app.Id, DeploymentPlanning.ContainerName(app.WorkspaceId, slug, 1), serverId);
    }

    private Guid SeedManagedDatabase(string name)
    {
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId,
            EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(),
            Name = name,
            Type = ManagedServiceType.PostgreSql,
            Version = "16",
            Status = ServiceStatus.Running,
            ContainerName = "harbora-svc-" + name,
            InternalPort = 5432,
            Username = "harbora",
            DatabaseName = "db_" + name.Replace('-', '_'),
            VolumeName = "harbora-svc-" + name + "-data",
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        return service.Id;
    }
}
