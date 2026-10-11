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
/// Each entitlement test runs in a workspace of its own with an explicit workspace-level grant. The
/// shared fixture workspace sits on the seeded provider plan, which DbSeeder grants every feature,
/// so a grant written there would leak into every other test in the collection. Assertions read
/// <c>data-</c> attributes, never a sentence: this harness renders Persian by default.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AssistantPaneHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid GivenWorkspaceWithAssistant(string slug, string email, FeatureState? state)
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = slug, Slug = slug, IsDefault = false });
            if (state is { } s)
            {
                db.FeatureGrants.Add(new FeatureGrant
                {
                    Scope = FeatureScope.Workspace, TargetId = workspaceId,
                    FeatureKey = PlatformFeatures.Assistant, State = s
                });
            }
        });
        Panel.GivenUser(workspaceId, email, SystemRole.Owner);
        return workspaceId;
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
        // No workspace grant: the plan decides. This workspace has no plan of its own, so it is on the
        // platform default — the provider plan, which DbSeeder grants every feature.
        GivenWorkspaceWithAssistant("assistant-inherit", "assistant-inherit@example.com", state: null);
        var client = await Panel.SignedInAs("198.51.100.134", "assistant-inherit@example.com");

        var document = await PageAsync(client, "/apps");

        document.QuerySelector("header [data-assistant-state='enabled']").Should().NotBeNull();
        document.GetElementById("assistant-panel").Should().NotBeNull();
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
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId, ServerId = Guid.CreateVersion7(),
            Name = "assistant-context-app", Slug = "assistant-context-app",
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/assistant:1.0", Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        Panel.GivenUser(fixture.WorkspaceId, "assistant-context-app@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.136", "assistant-context-app@example.com");

        var context = ContextOf(await PageAsync(client, $"/apps/details/{app.Id}"));

        context.GetProperty("appId").GetGuid().Should().Be(app.Id);
        context.GetProperty("environmentId").GetGuid().Should().Be(fixture.DefaultEnvironmentId);
        context.GetProperty("serviceId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_database_details_page_tells_the_assistant_which_database_this_is()
    {
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId, ServerId = Guid.CreateVersion7(),
            Name = "assistant-context-db", Type = ManagedServiceType.PostgreSql, Version = "16", Status = ServiceStatus.Running,
            ContainerName = "harbora-svc-assistant-context-db", InternalPort = 5432, Username = "harbora",
            DatabaseName = "assistant_context_db", VolumeName = "harbora-svc-assistant-context-db-data"
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        Panel.GivenUser(fixture.WorkspaceId, "assistant-context-db@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.137", "assistant-context-db@example.com");

        var context = ContextOf(await PageAsync(client, $"/databases/{service.Id}"));

        context.GetProperty("serviceId").GetGuid().Should().Be(service.Id);
        context.GetProperty("appId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_page_that_declares_no_context_sends_none()
    {
        Panel.GivenUser(fixture.WorkspaceId, "assistant-context-none@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.138", "assistant-context-none@example.com");

        var drawer = (await PageAsync(client, "/apps")).GetElementById("assistant-panel");

        drawer.Should().NotBeNull("the shared workspace is on the provider plan, which has the assistant");
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
