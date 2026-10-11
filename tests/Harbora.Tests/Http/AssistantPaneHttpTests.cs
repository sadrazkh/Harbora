using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The assistant pane's place in the shell (round six, phase 3): the topbar toggle obeys the
/// <c>assistant</c> entitlement the way a sidebar <c>Feature:</c> item does — enabled is a working
/// button, locked is greyed with a lock and goes to the page that explains it, hidden is not drawn —
/// and the drawer mount exists only when the button works. The chat itself is a Vue island tested by
/// <c>node --test</c> (Scripts/tests/assistant-*.test.mjs); this is the Razor half.
///
/// <para>
/// Every test runs in a workspace of its own and says what that workspace is entitled to. The shared
/// fixture workspace is no place for this: other tests move it between plans (AppAddressHttpTests puts
/// it on a plan of its own, where the assistant is Locked by default — the context tests below were
/// first written against it and failed only in the full run), and a grant written on it would leak
/// into every test after this one.
/// Assertions read <c>data-</c> attributes, never a sentence: this harness renders Persian by default.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AssistantPaneHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record Given(Guid WorkspaceId, Guid EnvironmentId);

    /// <param name="state">The workspace-level decision, or null for none (the plan decides).</param>
    /// <param name="planState">When set, the workspace is put on a plan of its own that decides this.</param>
    private Given GivenWorkspaceWithAssistant(string slug, string email, FeatureState? state, FeatureState? planState = null)
    {
        var workspaceId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            Guid? planId = null;
            if (planState is { } p)
            {
                var plan = new Plan { Name = slug, NameFa = slug };
                db.Plans.Add(plan);
                db.FeatureGrants.Add(new FeatureGrant
                {
                    Scope = FeatureScope.Plan, TargetId = plan.Id,
                    FeatureKey = PlatformFeatures.Assistant, State = p
                });
                planId = plan.Id;
            }

            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = slug, Slug = slug, IsDefault = false, PlanId = planId });
            if (state is { } s)
            {
                db.FeatureGrants.Add(new FeatureGrant
                {
                    Scope = FeatureScope.Workspace, TargetId = workspaceId,
                    FeatureKey = PlatformFeatures.Assistant, State = s
                });
            }

            var projectId = Guid.CreateVersion7();
            db.Projects.Add(new Harbora.Domain.Projects.Project { Id = projectId, WorkspaceId = workspaceId, Name = slug, Slug = slug });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = workspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
        });
        Panel.GivenUser(workspaceId, email, SystemRole.Owner);
        return new Given(workspaceId, environmentId);
    }

    private static async Task<IDocument> PageAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} should render for its own owner");
        return await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStreamAsync());
    }

    [Fact]
    public async Task An_enabled_workspace_gets_a_working_toggle_and_the_drawer_it_opens()
    {
        GivenWorkspaceWithAssistant("assistant-enabled", "assistant-enabled@example.com", FeatureState.Enabled);
        var client = await Panel.SignedInAs("198.51.100.130", "assistant-enabled@example.com");

        var document = await PageAsync(client, "/apps");

        var toggle = document.QuerySelector("header [data-assistant-toggle]");
        toggle.Should().NotBeNull("an entitled workspace gets the button in the topbar");
        toggle!.TagName.Should().Be("BUTTON");
        toggle.GetAttribute("data-assistant-state").Should().Be("enabled");
        toggle.GetAttribute("aria-controls").Should().Be("assistant-panel");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.QuerySelector("[data-lucide='sparkles']").Should().NotBeNull();

        var drawer = document.GetElementById("assistant-panel");
        drawer.Should().NotBeNull("the button has to have something to open");
        drawer!.GetAttribute("data-csrf").Should().NotBeNullOrWhiteSpace("the island's POSTs carry it as X-CSRF-TOKEN");
        drawer.GetAttribute("data-lang").Should().Be("fa", "this harness renders Persian by default");
        drawer.GetAttribute("aria-hidden").Should().Be("true", "it starts closed; the script opens it");
        drawer.QuerySelector("[data-assistant-host]").Should().NotBeNull("the island mounts there");

        document.QuerySelector("[data-assistant-state='locked']").Should().BeNull();
    }

    [Fact]
    public async Task The_drawer_sits_outside_the_scrolling_content_so_it_does_not_scroll_away()
    {
        GivenWorkspaceWithAssistant("assistant-placement", "assistant-placement@example.com", FeatureState.Enabled);
        var client = await Panel.SignedInAs("198.51.100.131", "assistant-placement@example.com");

        var document = await PageAsync(client, "/apps");

        var drawer = document.GetElementById("assistant-panel")!;
        var content = document.GetElementById("content-scroll")!;
        content.Contains(drawer).Should().BeFalse();
        drawer.ParentElement.Should().BeSameAs(content.ParentElement,
            "the drawer and the content share the positioning box under the topbar");
        drawer.ClassList.Should().Contain("assistant-drawer");
    }

    [Fact]
    public async Task A_locked_workspace_sees_a_greyed_button_that_explains_itself_and_no_drawer()
    {
        GivenWorkspaceWithAssistant("assistant-locked", "assistant-locked@example.com", FeatureState.Locked);
        var client = await Panel.SignedInAs("198.51.100.132", "assistant-locked@example.com");

        var document = await PageAsync(client, "/apps");

        var locked = document.QuerySelector("header [data-assistant-state='locked']");
        locked.Should().NotBeNull("a customer who cannot see what they are not buying cannot ask for it");
        locked!.TagName.Should().Be("A");
        locked.GetAttribute("href").Should().Be("/features/assistant");
        locked.QuerySelector("[data-lucide='lock']").Should().NotBeNull();
        locked.HasAttribute("data-assistant-toggle").Should().BeFalse("a locked button must not open anything");

        document.GetElementById("assistant-panel").Should().BeNull("every request from a drawer here would be refused");
        document.QuerySelector("[data-assistant-toggle]").Should().BeNull();
    }

    [Fact]
    public async Task A_hidden_workspace_is_not_shown_the_assistant_at_all()
    {
        GivenWorkspaceWithAssistant("assistant-hidden", "assistant-hidden@example.com", FeatureState.Hidden);
        var client = await Panel.SignedInAs("198.51.100.133", "assistant-hidden@example.com");

        var document = await PageAsync(client, "/apps");

        document.QuerySelector("[data-assistant-state]").Should().BeNull();
        document.QuerySelector("[data-assistant-toggle]").Should().BeNull();
        document.GetElementById("assistant-panel").Should().BeNull();
    }

    [Fact]
    public async Task A_workspace_nobody_decided_for_follows_its_plan()
    {
        // No workspace-level grant, so the plan's decision is the answer — the same resolution the
        // sidebar's Feature: items get, because both read FeatureView.
        GivenWorkspaceWithAssistant("assistant-plan-locked", "assistant-plan-locked@example.com", state: null, planState: FeatureState.Locked);
        GivenWorkspaceWithAssistant("assistant-plan-enabled", "assistant-plan-enabled@example.com", state: null, planState: FeatureState.Enabled);

        var locked = await PageAsync(await Panel.SignedInAs("198.51.100.134", "assistant-plan-locked@example.com"), "/apps");
        locked.QuerySelector("header [data-assistant-state='locked']").Should().NotBeNull();
        locked.GetElementById("assistant-panel").Should().BeNull();

        var enabled = await PageAsync(await Panel.SignedInAs("198.51.100.139", "assistant-plan-enabled@example.com"), "/apps");
        enabled.QuerySelector("header [data-assistant-state='enabled']").Should().NotBeNull();
        enabled.GetElementById("assistant-panel").Should().NotBeNull();
    }

    [Fact]
    public async Task A_signed_out_page_draws_neither_the_toggle_nor_the_drawer()
    {
        // /error/{code} is anonymous and renders the whole shell, topbar included — the one place a
        // signed-out visitor sees the topbar. FeatureView answers "locked" for a request with no
        // workspace, so without the sign-in check this page would advertise a greyed assistant.
        var client = Panel.ClientFrom("198.51.100.140");

        var response = await client.GetAsync("/error/404");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStreamAsync());

        document.QuerySelector("header [data-command-open]").Should().NotBeNull(
            "the topbar did render, so what is missing below was left out on purpose");
        document.QuerySelector("[data-assistant-state]").Should().BeNull();
        document.QuerySelector("[data-assistant-toggle]").Should().BeNull();
        document.GetElementById("assistant-panel").Should().BeNull();
    }

    [Fact]
    public async Task The_drawers_token_is_accepted_as_the_X_CSRF_TOKEN_header()
    {
        // The island never posts a form: every POST sends data-csrf in the header Program.cs's
        // antiforgery reads. Proved against an existing [ValidateAntiForgeryToken] action, because the
        // assistant's own endpoints are being built in parallel and are not on this branch.
        GivenWorkspaceWithAssistant("assistant-csrf", "assistant-csrf@example.com", FeatureState.Enabled);
        var client = await Panel.SignedInAs("198.51.100.135", "assistant-csrf@example.com");
        var token = (await PageAsync(client, "/apps")).GetElementById("assistant-panel")!.GetAttribute("data-csrf")!;

        var withHeader = new HttpRequestMessage(HttpMethod.Post, "/account/panel-mode")
        {
            Content = new FormUrlEncodedContent([new("mode", "Advanced"), new("returnUrl", "/apps")])
        };
        withHeader.Headers.Add("X-CSRF-TOKEN", token);
        (await client.SendAsync(withHeader)).StatusCode.Should().Be(HttpStatusCode.Found);

        var without = new HttpRequestMessage(HttpMethod.Post, "/account/panel-mode")
        {
            Content = new FormUrlEncodedContent([new("mode", "Advanced"), new("returnUrl", "/apps")])
        };
        (await client.SendAsync(without)).StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "otherwise the first assertion proves nothing about the token");
    }

    [Fact]
    public async Task The_app_details_page_tells_the_assistant_which_app_this_is()
    {
        var given = GivenWorkspaceWithAssistant("assistant-context-app", "assistant-context-app@example.com", FeatureState.Enabled);
        var app = new App
        {
            WorkspaceId = given.WorkspaceId, EnvironmentId = given.EnvironmentId, ServerId = Guid.CreateVersion7(),
            Name = "assistant-context-app", Slug = "assistant-context-app",
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/assistant:1.0", Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        var client = await Panel.SignedInAs("198.51.100.136", "assistant-context-app@example.com");

        var context = ContextOf(await PageAsync(client, $"/apps/details/{app.Id}"));

        context.GetProperty("appId").GetGuid().Should().Be(app.Id);
        context.GetProperty("environmentId").GetGuid().Should().Be(given.EnvironmentId);
        context.GetProperty("serviceId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_database_details_page_tells_the_assistant_which_database_this_is()
    {
        var given = GivenWorkspaceWithAssistant("assistant-context-db", "assistant-context-db@example.com", FeatureState.Enabled);
        var service = new ManagedService
        {
            WorkspaceId = given.WorkspaceId, EnvironmentId = given.EnvironmentId, ServerId = Guid.CreateVersion7(),
            Name = "assistant-context-db", Type = ManagedServiceType.PostgreSql, Version = "16", Status = ServiceStatus.Running,
            ContainerName = "harbora-svc-assistant-context-db", InternalPort = 5432, Username = "harbora",
            DatabaseName = "assistant_context_db", VolumeName = "harbora-svc-assistant-context-db-data"
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        var client = await Panel.SignedInAs("198.51.100.137", "assistant-context-db@example.com");

        var context = ContextOf(await PageAsync(client, $"/databases/{service.Id}"));

        context.GetProperty("serviceId").GetGuid().Should().Be(service.Id);
        context.GetProperty("appId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_page_that_declares_no_context_sends_none()
    {
        GivenWorkspaceWithAssistant("assistant-context-none", "assistant-context-none@example.com", FeatureState.Enabled);
        var client = await Panel.SignedInAs("198.51.100.138", "assistant-context-none@example.com");

        var drawer = (await PageAsync(client, "/apps")).GetElementById("assistant-panel");

        drawer.Should().NotBeNull();
        drawer!.HasAttribute("data-assistant-context").Should().BeFalse(
            "a list page has no single resource for \"this\" to mean, and must not invent one");
    }

    private static JsonElement ContextOf(IDocument document)
    {
        var drawer = document.GetElementById("assistant-panel");
        drawer.Should().NotBeNull();
        var json = drawer!.GetAttribute("data-assistant-context");
        json.Should().NotBeNullOrWhiteSpace("this page declares its context");
        return JsonDocument.Parse(json!).RootElement.Clone();
    }
}
