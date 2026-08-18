using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The editor's progressive-enhancement contract: the CodeMirror island (<c>CodeEditor.vue</c>,
/// registered in <c>main.ts</c> as <c>function-code-editor</c>) mounts over a real
/// <c>&lt;textarea name="Code"&gt;</c> rather than an empty div, so the page a browser with no
/// JavaScript receives is already a complete, correct form — the island is an enhancement of it,
/// never a replacement it depends on.
///
/// <para>
/// Every request in this class goes through <see cref="System.Net.Http.HttpClient"/> and is parsed
/// as static HTML — no script on the page ever runs — so a passing <c>SaveFunction</c> assertion
/// here is itself proof the no-JavaScript path posts correctly, not an assumption about it.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionCodeEditorIslandHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record World(Guid AppId, Guid FunctionId);

    private World GivenFunctionApp(string slug, FunctionRuntime runtime, string code)
    {
        var appId = Guid.CreateVersion7();
        var functionId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.FeatureGrants.Add(new FeatureGrant
            {
                Scope = FeatureScope.Workspace, TargetId = fixture.WorkspaceId,
                FeatureKey = PlatformFeatures.Functions, State = FeatureState.Enabled
            });

            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
                Name = slug, Slug = slug, SourceType = AppSourceType.InlineCode,
                FunctionRuntime = runtime,
                DockerfilePath = "Dockerfile.harbora"
            });

            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                Id = functionId, AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Name = "Hello", Slug = "hello", Trigger = FunctionTrigger.Http, Code = code
            });
        });

        return new World(appId, functionId);
    }

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    [Fact]
    public async Task The_mount_point_names_the_apps_own_runtime_by_its_C_sharp_enum_name()
    {
        var world = GivenFunctionApp("fn-editor-py", FunctionRuntime.Python, "print('hi')");
        Panel.GivenUser(fixture.WorkspaceId, "fn-editor-py@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.170", "fn-editor-py@example.com");

        var html = await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}"))
            .Content.ReadAsStringAsync();
        var document = await ParseAsync(html);

        // Not "2" (the int value): main.ts branches on the C# name, so a fourth runtime that
        // forgot to update the switch fails loudly there instead of silently loading C#'s grammar.
        var mount = document.QuerySelector("[data-island='function-code-editor']");
        mount.Should().NotBeNull("the island needs a mount point to attach to");
        mount!.GetAttribute("data-runtime").Should().Be("Python");
    }

    [Fact]
    public async Task The_panel_s_own_language_travels_to_the_island_separately_from_the_codes_language()
    {
        var world = GivenFunctionApp("fn-editor-lang", FunctionRuntime.JavaScript, "export default () => 1;");
        Panel.GivenUser(fixture.WorkspaceId, "fn-editor-lang@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.171", "fn-editor-lang@example.com");

        var html = await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}"))
            .Content.ReadAsStringAsync();
        var document = await ParseAsync(html);

        // The panel renders Persian by default in this test host — asserted on the data attribute
        // the island actually reads, never on a sentence.
        document.QuerySelector("[data-island='function-code-editor']")!
            .GetAttribute("data-lang").Should().Be("fa");
    }

    [Fact]
    public async Task The_fallback_textarea_still_carries_every_attribute_the_form_post_needs()
    {
        var world = GivenFunctionApp("fn-editor-fallback", FunctionRuntime.CSharp, "// hello");
        Panel.GivenUser(fixture.WorkspaceId, "fn-editor-fallback@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.172", "fn-editor-fallback@example.com");

        var html = await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}"))
            .Content.ReadAsStringAsync();
        var document = await ParseAsync(html);

        var mount = document.QuerySelector("[data-island='function-code-editor']");
        var textarea = mount!.QuerySelector("textarea");

        // Every one of these is load-bearing with the island absent: `name` is what SaveFunction's
        // model binder reads, `dir="ltr"` is the RTL-page requirement the island must also honour,
        // and the field is never `disabled` — a disabled control is dropped from a form post
        // entirely, which is the one attribute that would make the no-JS path silently post no code.
        textarea.Should().NotBeNull("the plain textarea must still be the thing that submits when no script runs");
        textarea!.GetAttribute("name").Should().Be("Code");
        textarea.GetAttribute("dir").Should().Be("ltr");
        textarea.HasAttribute("disabled").Should().BeFalse();
        textarea.TextContent.Should().Be("// hello", "a browser with no JavaScript must see and post the real code");
    }

    [Fact]
    public async Task Saving_through_a_plain_form_post_with_no_script_executed_still_updates_the_code()
    {
        var world = GivenFunctionApp("fn-editor-nojs-save", FunctionRuntime.CSharp, "// before");
        Panel.GivenUser(fixture.WorkspaceId, "fn-editor-nojs-save@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.173", "fn-editor-nojs-save@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        // HttpClient never runs the page's script — this is exactly what a browser with JavaScript
        // off or blocked would post from the fallback textarea.
        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/save", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "Hello"), ("Trigger", ((int)FunctionTrigger.Http).ToString()),
            ("Route", ""), ("Code", "// after"), ("IsEnabled", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        stored.Code.Should().Be("// after");
    }
}
