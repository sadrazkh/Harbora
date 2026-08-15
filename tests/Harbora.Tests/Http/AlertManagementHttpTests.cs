using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Alert rule management end to end: the real pipeline, a real cookie, real Razor.
///
/// <c>AlertManagementTests</c> proves the controller's own decisions in isolation; this proves the
/// route is wired to the policy it claims, the antiforgery token is honoured, and the monitoring page
/// actually renders what the controller decided — in Persian, since that is what the panel renders by
/// default and <c>data-</c> attributes are asserted for exactly that reason.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AlertManagementHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid GivenApp(string slug)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            Name = slug,
            Slug = slug,
            SourceType = AppSourceType.Upload
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app.Id;
    }

    [Fact]
    public async Task The_monitoring_page_shows_a_disabled_rule_and_a_threshold_rules_own_line_by_data_attribute()
    {
        var appId = GivenApp("alert-watched-app");
        var thresholdRuleId = Guid.CreateVersion7();
        var disabledRuleId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.Alerts.Add(new Alert
            {
                Id = thresholdRuleId, WorkspaceId = fixture.WorkspaceId, Name = "cpu-watch",
                Channel = AlertChannel.Webhook, EncryptedTarget = "{}",
                AppId = appId, Metric = AlertMetric.CpuPercent, ThresholdPercent = 85, SustainedMinutes = 5,
                IsEnabled = true
            });
            db.Alerts.Add(new Alert
            {
                Id = disabledRuleId, WorkspaceId = fixture.WorkspaceId, Name = "quiet-rule",
                Channel = AlertChannel.Webhook, EncryptedTarget = "{}", IsEnabled = false
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "alert-mgmt-view@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.230", "alert-mgmt-view@example.com");

        var html = await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync();

        html.Should().Contain($"data-alert-id=\"{thresholdRuleId}\"");
        html.Should().Contain("data-alert-enabled=\"true\"");
        html.Should().Contain("data-alert-enabled=\"false\"", "the disabled rule stays in the list rather than being hidden or deleted");
        html.Should().Contain("data-alert-metric=\"CpuPercent\"");
        html.Should().Contain("data-alert-threshold-percent=\"85\"");

        // Persian is the panel's default in tests — the disabled badge and the "watches" line both
        // render through the isFa ternary, so both are asserted in their encoded Persian form rather
        // than the English fallback text.
        html.Should().Contain("&#x63A;&#x6CC;&#x631;&#x641;&#x639;&#x627;&#x644;", "the Persian word for 'disabled' (غیرفعال)");
        html.Should().Contain("&#x631;&#x648;&#x6CC; &#xAB;alert-watched-app&#xBB;", "the Persian threshold summary names the watched app");
    }

    [Fact]
    public async Task Toggling_a_rule_through_the_real_route_flips_it_and_the_page_reflects_it_on_reload()
    {
        var ruleId = Guid.CreateVersion7();
        Panel.Seed(db => db.Alerts.Add(new Alert
        {
            Id = ruleId, WorkspaceId = fixture.WorkspaceId, Name = "toggle-me",
            Channel = AlertChannel.Webhook, EncryptedTarget = "{}", IsEnabled = true
        }));
        Panel.GivenUser(fixture.WorkspaceId, "alert-mgmt-toggle@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.231", "alert-mgmt-toggle@example.com");

        var token = await client.AntiforgeryTokenFrom("/monitoring");
        var response = await client.PostFormAsync($"/alerts/{ruleId}/toggle", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Alerts.Single(a => a.Id == ruleId).IsEnabled).Should().BeFalse();

        var html = await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync();
        html.Should().Contain("data-alert-enabled=\"false\"");
    }

    [Fact]
    public async Task Editing_through_the_real_route_changes_severity_and_keeps_the_stored_target()
    {
        var ruleId = Guid.CreateVersion7();
        var protector = Panel.Resolve<Harbora.Application.Abstractions.ISecretProtector>();
        var storedTarget = protector.Protect("""{"url":"https://kept.example/hook"}""");

        Panel.Seed(db => db.Alerts.Add(new Alert
        {
            Id = ruleId, WorkspaceId = fixture.WorkspaceId, Name = "edit-me",
            Channel = AlertChannel.Webhook, EncryptedTarget = storedTarget,
            MinSeverity = AlertSeverity.Warning, IsEnabled = true
        }));
        Panel.GivenUser(fixture.WorkspaceId, "alert-mgmt-edit@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.232", "alert-mgmt-edit@example.com");

        var token = await client.AntiforgeryTokenFrom("/monitoring");
        var response = await client.PostFormAsync($"/alerts/{ruleId}/edit", token,
            ("name", "edit-me"), ("channel", "Webhook"), ("minSeverity", "Critical"),
            ("onDeployFailed", "true"), ("onAppCrashed", "true"), ("onBackupFailed", "true"), ("onDiskWarning", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var saved = Panel.Read(db => db.Alerts.Single(a => a.Id == ruleId));
        saved.MinSeverity.Should().Be(AlertSeverity.Critical);
        saved.EncryptedTarget.Should().Be(storedTarget, "the target field was left off the request entirely, so it must survive untouched");
    }

    [Fact]
    public async Task Creating_a_rule_with_a_metric_but_no_threshold_value_is_refused_through_the_real_route()
    {
        Panel.GivenUser(fixture.WorkspaceId, "alert-mgmt-create-refuse@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.233", "alert-mgmt-create-refuse@example.com");
        var appId = GivenApp("alert-refused-app");

        var before = Panel.Read(db => db.Alerts.Count());
        var token = await client.AntiforgeryTokenFrom("/monitoring");
        var response = await client.PostFormAsync("/alerts", token,
            ("name", "half-baked"), ("channel", "Webhook"), ("minSeverity", "Warning"),
            ("webhookUrl", "https://hooks.example/half"),
            ("onDeployFailed", "true"), ("onAppCrashed", "true"), ("onBackupFailed", "true"), ("onDiskWarning", "true"),
            ("appId", appId.ToString()), ("metric", "CpuPercent"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Alerts.Count()).Should().Be(before, "the incomplete threshold must not create a row");

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        // The refusal names the missing field in Persian, since that is the panel's default culture —
        // this is "درصد آستانه" ("threshold value"), the encoded form BillingPageHttpTests-style tests
        // already assert Persian text in.
        html.Should().Contain("&#x62F;&#x631;&#x635;&#x62F; &#x622;&#x633;&#x62A;&#x627;&#x646;&#x647;",
            "the refusal must name the field that is actually missing, not just say 'invalid'");
    }
}
