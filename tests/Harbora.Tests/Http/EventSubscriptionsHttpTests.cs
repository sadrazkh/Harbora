using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>/notifications/webhooks</c> end to end (P6, 2026-08-20 platform-options plan) — the real
/// pipeline, a real cookie, real Razor. Mirrors <c>AlertManagementHttpTests</c>' own idiom
/// deliberately: this is the same kind of workspace-level integration settings page, reusing the same
/// capability policy.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class EventSubscriptionsHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>
    /// Gap 2 of the maintenance-mode follow-up: <c>EventKind.MaintenanceOn</c>/<c>MaintenanceOff</c>
    /// fire for real (<c>AppOperationsService.SetMaintenanceModeAsync</c>) but used to have no checkbox
    /// here — an event that fires and cannot be subscribed to is a half-connected feature. Asserted on
    /// the checkbox's own <c>data-event-checkbox</c> attribute, not the label sentence, because the
    /// panel renders Persian by default in tests.
    /// </summary>
    [Fact]
    public async Task The_add_subscription_form_offers_both_maintenance_events_as_checkboxes()
    {
        Panel.GivenUser(fixture.WorkspaceId, "events-maintenance-checkbox@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.249", "events-maintenance-checkbox@example.com");

        var html = await (await client.GetAsync("/notifications/webhooks")).Content.ReadAsStringAsync();

        html.Should().Contain("data-event-checkbox=\"MaintenanceOn\"");
        html.Should().Contain("data-event-checkbox=\"MaintenanceOff\"");
    }

    [Fact]
    public async Task Creating_a_subscription_to_maintenance_events_persists_both_bits()
    {
        Panel.GivenUser(fixture.WorkspaceId, "events-maintenance-create@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.250", "events-maintenance-create@example.com");

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        await client.PostFormAsync("/notifications/webhooks", token,
            ("name", "maintenance-watch"), ("channel", "Webhook"), ("webhookUrl", "https://hooks.example.com/m"),
            ("onMaintenanceOn", "true"), ("onMaintenanceOff", "true"));

        var stored = Panel.Read(db => db.EventSubscriptions.Single(s => s.Name == "maintenance-watch"));
        stored.Events.Should().Be(EventKind.MaintenanceOn | EventKind.MaintenanceOff);
    }

    /// <summary>
    /// F4 (2026-08-21 functions-and-services plan, "Function failures become visible")'s own
    /// acceptance criterion, checked the same way the maintenance-event gap above was: the WIP commit
    /// this session inherited added <c>EventKind.FunctionFailed</c> to <c>Publishable</c> and to
    /// <c>FunctionInvoker</c>'s own publish seam, but never to this page's own checkbox list — an event
    /// that fires and cannot be subscribed to, the same half-connected gap this file already guards.
    /// </summary>
    [Fact]
    public async Task The_add_subscription_form_offers_function_failed_as_a_checkbox()
    {
        Panel.GivenUser(fixture.WorkspaceId, "events-function-checkbox@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.251", "events-function-checkbox@example.com");

        var html = await (await client.GetAsync("/notifications/webhooks")).Content.ReadAsStringAsync();

        html.Should().Contain("data-event-checkbox=\"FunctionFailed\"");
    }

    [Fact]
    public async Task Creating_a_subscription_to_function_failed_persists_the_bit()
    {
        Panel.GivenUser(fixture.WorkspaceId, "events-function-create@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.252", "events-function-create@example.com");

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        await client.PostFormAsync("/notifications/webhooks", token,
            ("name", "function-watch"), ("channel", "Webhook"), ("webhookUrl", "https://hooks.example.com/f"),
            ("onFunctionFailed", "true"));

        var stored = Panel.Read(db => db.EventSubscriptions.Single(s => s.Name == "function-watch"));
        stored.Events.Should().Be(EventKind.FunctionFailed);
    }

    [Fact]
    public async Task The_page_lists_a_subscription_and_a_disabled_one_by_data_attribute()
    {
        var enabledId = Guid.CreateVersion7();
        var disabledId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.EventSubscriptions.Add(new EventSubscription
            {
                Id = enabledId, WorkspaceId = fixture.WorkspaceId, Name = "billing-hook",
                Channel = AlertChannel.Webhook, EncryptedTarget = "{}",
                Events = EventKind.DeploymentSucceeded, IsEnabled = true
            });
            db.EventSubscriptions.Add(new EventSubscription
            {
                Id = disabledId, WorkspaceId = fixture.WorkspaceId, Name = "quiet-hook",
                Channel = AlertChannel.Telegram, EncryptedTarget = "{}",
                Events = EventKind.BackupFailed, IsEnabled = false
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "events-view@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.240", "events-view@example.com");

        var html = await (await client.GetAsync("/notifications/webhooks")).Content.ReadAsStringAsync();

        html.Should().Contain($"data-subscription-id=\"{enabledId}\"");
        html.Should().Contain("data-subscription-enabled=\"true\"");
        html.Should().Contain($"data-subscription-id=\"{disabledId}\"");
        html.Should().Contain("data-subscription-enabled=\"false\"", "a disabled subscription stays in the list rather than being hidden");
        html.Should().Contain("data-subscription-channel=\"Webhook\"");
        html.Should().Contain("data-subscription-channel=\"Telegram\"");
    }

    [Fact]
    public async Task Creating_a_webhook_subscription_shows_the_signing_secret_once_and_never_again()
    {
        Panel.GivenUser(fixture.WorkspaceId, "events-create@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.241", "events-create@example.com");

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        var created = await client.PostFormAsync("/notifications/webhooks", token,
            ("name", "payments"), ("channel", "Webhook"), ("webhookUrl", "https://hooks.example.com/x"),
            ("onDeploymentFailed", "true"));

        created.StatusCode.Should().Be(HttpStatusCode.Found);
        created.RedirectPath().Should().Be("/notifications/webhooks");

        // TempData survives exactly the one redirect a browser follows next — the same client, one more request.
        var afterCreate = await client.GetAsync(created.RedirectPath());
        var html = await afterCreate.Content.ReadAsStringAsync();
        html.Should().Contain("data-new-secret=\"", "the secret is shown exactly once, right after creation");

        var reload = await client.GetAsync("/notifications/webhooks");
        var reloadHtml = await reload.Content.ReadAsStringAsync();
        reloadHtml.Should().NotContain("data-new-secret=\"", "a page reload must never show the secret again");

        var stored = Panel.Read(db => db.EventSubscriptions.Single(s => s.Name == "payments"));
        stored.EncryptedSigningSecret.Should().NotBeNullOrEmpty();
        stored.Channel.Should().Be(AlertChannel.Webhook);
        stored.Events.Should().Be(EventKind.DeploymentFailed);
    }

    [Fact]
    public async Task Creating_a_telegram_subscription_mints_no_signing_secret()
    {
        Panel.GivenUser(fixture.WorkspaceId, "events-telegram@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.242", "events-telegram@example.com");

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        await client.PostFormAsync("/notifications/webhooks", token,
            ("name", "ops-chat"), ("channel", "Telegram"), ("telegramToken", "tok"), ("telegramChatId", "1"),
            ("onAppCrashed", "true"));

        var stored = Panel.Read(db => db.EventSubscriptions.Single(s => s.Name == "ops-chat"));
        stored.Channel.Should().Be(AlertChannel.Telegram);
        stored.EncryptedSigningSecret.Should().BeEmpty("Telegram has nothing to sign — the target itself is the credential");
    }

    /// <summary>
    /// The owner's decision enforced server-side, not only by the form's own <select> options: even a
    /// crafted request naming Discord or Email is refused, because <c>AlertChannel</c> itself still
    /// carries both and nothing about the column stops one from being persisted.
    /// </summary>
    [Fact]
    public async Task A_channel_outside_webhook_or_telegram_is_refused_even_posted_directly()
    {
        Panel.GivenUser(fixture.WorkspaceId, "events-discord@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.243", "events-discord@example.com");

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        var response = await client.PostFormAsync("/notifications/webhooks", token,
            ("name", "discord-attempt"), ("channel", "Discord"), ("webhookUrl", "https://discord.example/x"),
            ("onAppCrashed", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        (Panel.Read(db => db.EventSubscriptions.Any(s => s.Name == "discord-attempt"))).Should().BeFalse(
            "HTTP webhooks and Telegram are the only channels the owner selected for v1");
    }

    [Fact]
    public async Task A_subscription_with_no_event_checked_is_refused()
    {
        Panel.GivenUser(fixture.WorkspaceId, "events-empty-mask@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.244", "events-empty-mask@example.com");

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        await client.PostFormAsync("/notifications/webhooks", token,
            ("name", "nothing-selected"), ("channel", "Webhook"), ("webhookUrl", "https://hooks.example.com/x"));

        (Panel.Read(db => db.EventSubscriptions.Any(s => s.Name == "nothing-selected"))).Should().BeFalse(
            "a subscription that hears nothing is not a subscription");
    }

    [Fact]
    public async Task Toggling_through_the_real_route_flips_it_and_the_page_reflects_it_on_reload()
    {
        var id = Guid.CreateVersion7();
        Panel.Seed(db => db.EventSubscriptions.Add(new EventSubscription
        {
            Id = id, WorkspaceId = fixture.WorkspaceId, Name = "toggle-me", Channel = AlertChannel.Webhook,
            EncryptedTarget = "{}", Events = EventKind.AppCrashed, IsEnabled = true
        }));
        Panel.GivenUser(fixture.WorkspaceId, "events-toggle@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.245", "events-toggle@example.com");

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        var response = await client.PostFormAsync($"/notifications/webhooks/{id}/toggle", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        (Panel.Read(db => db.EventSubscriptions.Single(s => s.Id == id).IsEnabled)).Should().BeFalse();

        var html = await (await client.GetAsync("/notifications/webhooks")).Content.ReadAsStringAsync();
        html.Should().Contain("data-subscription-enabled=\"false\"");
    }

    [Fact]
    public async Task Deleting_through_the_real_route_removes_it()
    {
        var id = Guid.CreateVersion7();
        Panel.Seed(db => db.EventSubscriptions.Add(new EventSubscription
        {
            Id = id, WorkspaceId = fixture.WorkspaceId, Name = "delete-me", Channel = AlertChannel.Webhook,
            EncryptedTarget = "{}", Events = EventKind.AppCrashed, IsEnabled = true
        }));
        Panel.GivenUser(fixture.WorkspaceId, "events-delete@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.246", "events-delete@example.com");

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        var response = await client.PostFormAsync($"/notifications/webhooks/{id}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        (Panel.Read(db => db.EventSubscriptions.Any(s => s.Id == id))).Should().BeFalse();
    }

    [Fact]
    public async Task A_workspace_member_without_alerts_manage_is_denied_creating_a_subscription()
    {
        Panel.GivenUser(fixture.WorkspaceId, "events-member@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.247", "events-member@example.com");

        // The page itself has no capability policy — read access follows the base authenticated
        // policy, the same as AlertsController's own Index-equivalent (the Monitoring page).
        var page = await client.GetAsync("/notifications/webhooks");
        page.StatusCode.Should().Be(HttpStatusCode.OK);

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        var response = await client.PostFormAsync("/notifications/webhooks", token,
            ("name", "not-allowed"), ("channel", "Webhook"), ("webhookUrl", "https://hooks.example.com/x"),
            ("onAppCrashed", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found, "alerts.manage is not a Member's");
        response.RedirectPath().Should().Be("/account/denied");
        (Panel.Read(db => db.EventSubscriptions.Any(s => s.Name == "not-allowed"))).Should().BeFalse();
    }

    [Fact]
    public async Task No_event_crosses_workspaces_through_the_page()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
            {
                Id = otherWorkspaceId, Name = "Other", Slug = "other-events-ws"
            });
            db.EventSubscriptions.Add(new EventSubscription
            {
                Id = theirs, WorkspaceId = otherWorkspaceId, Name = "not-yours", Channel = AlertChannel.Webhook,
                EncryptedTarget = "{}", Events = EventKind.AppCrashed, IsEnabled = true
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "events-tenancy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.248", "events-tenancy@example.com");

        var html = await (await client.GetAsync("/notifications/webhooks")).Content.ReadAsStringAsync();
        html.Should().NotContain("not-yours");

        var token = await client.AntiforgeryTokenFrom("/notifications/webhooks");
        var toggle = await client.PostFormAsync($"/notifications/webhooks/{theirs}/toggle", token);
        toggle.StatusCode.Should().Be(HttpStatusCode.NotFound, "an id from another workspace must not resolve, even for its owner's own account");

        (Panel.Read(db => db.EventSubscriptions.Single(s => s.Id == theirs).IsEnabled)).Should().BeTrue(
            "the other workspace's row must be untouched");
    }
}
