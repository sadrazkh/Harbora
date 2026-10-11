using System.Reflection;
using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Agent;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The rules every app tool shares, asked of each of them: its kind and capability (the capability read
/// off the matching controller action's own policy, not restated); a valid call validates with card text
/// in both languages and changes nothing; another workspace's app is "not found"; a Viewer, a scoped
/// member without a grant and a scoped member with a read-only grant are refused — at validation and,
/// for a step that got through anyway, at execution — and nothing changes.
/// </summary>
public class AppToolsCommonTests
{
    internal static readonly string[] AppToolNames =
    [
        "set_env_vars", "delete_env_var", "add_domain", "remove_domain", "add_volume", "remove_volume",
        "deploy_app", "restart_app", "stop_app", "rollback_app", "delete_app"
    ];

    public static TheoryData<string> AppTools()
    {
        var data = new TheoryData<string>();
        foreach (var name in AppToolNames) data.Add(name);
        return data;
    }

    /// <summary>A call each tool accepts against <see cref="AppToolsHarness.SeedApp"/>'s app.</summary>
    internal static object ArgsFor(string tool, string app) => tool switch
    {
        "set_env_vars" => new { app, variables = new[] { new { key = "LOG_LEVEL", value = "debug" } } },
        "delete_env_var" => new { app, key = "PORT" },
        "add_domain" => new { app, host = "shop.example.com" },
        "remove_domain" => new { app, host = $"{app}.example.org" },
        "add_volume" => new { app, mountPath = "/data" },
        "remove_volume" => new { app, mountPath = "/files" },
        _ => new { app }
    };

    [Theory]
    [InlineData("create_app", AgentToolKind.SafeWrite, Capabilities.AppsCreate, "Create")]
    [InlineData("deploy_template", AgentToolKind.SafeWrite, Capabilities.AppsCreate, null)]
    [InlineData("set_env_vars", AgentToolKind.SafeWrite, Capabilities.AppsEnv, "AddEnv")]
    [InlineData("delete_env_var", AgentToolKind.DangerousWrite, Capabilities.AppsEnv, "DeleteEnv")]
    [InlineData("add_domain", AgentToolKind.SafeWrite, Capabilities.AppsEnv, "AddDomain")]
    [InlineData("remove_domain", AgentToolKind.DangerousWrite, Capabilities.AppsEnv, "DeleteDomain")]
    [InlineData("add_volume", AgentToolKind.SafeWrite, Capabilities.AppsEnv, "AddVolume")]
    [InlineData("remove_volume", AgentToolKind.Destructive, Capabilities.AppsEnv, "RemoveVolume")]
    [InlineData("deploy_app", AgentToolKind.SafeWrite, Capabilities.AppsDeploy, "Deploy")]
    [InlineData("restart_app", AgentToolKind.SafeWrite, Capabilities.AppsOperate, "Restart")]
    [InlineData("stop_app", AgentToolKind.DangerousWrite, Capabilities.AppsOperate, "Stop")]
    [InlineData("rollback_app", AgentToolKind.DangerousWrite, Capabilities.AppsDeploy, "Rollback")]
    [InlineData("delete_app", AgentToolKind.Destructive, Capabilities.AppsDelete, "Delete")]
    public void Each_tool_has_its_kind_and_the_capability_its_panel_action_is_authorised_with(
        string name, AgentToolKind kind, string capability, string? action)
    {
        using var h = new AppToolsHarness();
        var tool = h.Tool(name);

        tool.Kind.Should().Be(kind);
        tool.Capability.Should().Be(capability);
        tool.Description.Should().NotBeNullOrWhiteSpace();
        System.Text.Json.JsonDocument.Parse(tool.ParametersSchema).RootElement.GetProperty("type").GetString().Should().Be("object");

        // Read off the controller rather than restated: the tool must ask exactly what a click asks.
        // (deploy_template's page decides in code — TemplatesController.CanDeployAsync: AppsCreate, and
        // DatabasesManage on top when the template brings databases, which the tool asks as well.)
        if (action is null) return;
        var policies = typeof(AppsController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == action)
            .SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>())
            .Select(a => a.Policy)
            .Distinct()
            .ToList();
        policies.Should().Equal([capability], $"AppsController.{action} is the panel action {name} stands in for");
    }

    [Theory]
    [MemberData(nameof(AppTools))]
    public async Task A_valid_call_validates_with_card_text_in_both_languages_and_changes_nothing(string name)
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.Tool(name), ArgsFor(name, "web"));

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Contain("web");
        verdict.TitleFa.Should().Contain("web").And.MatchRegex("[؀-ۿ]", "the Persian title is Persian");
        verdict.DetailEn.Should().NotBeNullOrWhiteSpace();
        // Not always Persian words: set_env_vars' detail for a plain variable is just "LOG_LEVEL = debug".
        verdict.DetailFa.Should().NotBeNullOrWhiteSpace();
        (verdict.WarningsEn ?? []).Count.Should().Be((verdict.WarningsFa ?? []).Count, "every warning is said in both languages");
        if (h.Tool(name).Kind == AgentToolKind.Destructive)
            verdict.ConfirmationPhrase.Should().Be("web", "a destructive step is confirmed by typing the app's slug");
        else
            verdict.ConfirmationPhrase.Should().BeNull();

        h.Snapshot().Should().Be(before, "validation changes nothing");
    }

    [Theory]
    [MemberData(nameof(AppTools))]
    public async Task Another_workspaces_app_is_not_found_and_nothing_changes(string name)
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        h.SeedForeignApp("theirs");
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.Tool(name), ArgsFor(name, "theirs"));
        var result = await h.ExecuteAsync(h.Tool(name), ArgsFor(name, "theirs"));

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Be("No app named 'theirs' that you can see.", "another tenant's app reads exactly like a missing one");
        result.Ok.Should().BeFalse();
        result.Error.Should().Be(verdict.Error);
        result.ErrorFa.Should().Be(verdict.ErrorFa);
        h.Snapshot().Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(AppTools))]
    public async Task A_viewer_is_refused_at_validation_and_at_execution(string name)
    {
        using var h = new AppToolsHarness(SystemRole.Viewer);
        h.SeedApp("web");
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.Tool(name), ArgsFor(name, "web"));
        var result = await h.ExecuteAsync(h.Tool(name), ArgsFor(name, "web"));

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("does not allow").And.Contain(h.Tool(name).Capability!);
        verdict.ErrorFa.Should().Contain("اجازه");
        result.Ok.Should().BeFalse("execution asks again rather than trusting the validation");
        result.Error.Should().Contain("does not allow");
        h.Snapshot().Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(AppTools))]
    public async Task A_scoped_member_without_a_grant_cannot_reach_the_app(string name)
    {
        using var h = new AppToolsHarness(SystemRole.Member, scoped: true);
        h.SeedApp("web");
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.Tool(name), ArgsFor(name, "web"));
        var result = await h.ExecuteAsync(h.Tool(name), ArgsFor(name, "web"));

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Be("No app named 'web' that you can see.");
        result.Ok.Should().BeFalse();
        h.Snapshot().Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(AppTools))]
    public async Task A_scoped_member_whose_grant_is_read_only_sees_the_app_but_may_not_touch_it(string name)
    {
        using var h = new AppToolsHarness(SystemRole.Member, scoped: true);
        h.SeedApp("web");
        h.Grant(SystemRole.Viewer);
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.Tool(name), ArgsFor(name, "web"));
        var result = await h.ExecuteAsync(h.Tool(name), ArgsFor(name, "web"));

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("does not allow this on app 'web'");
        result.Ok.Should().BeFalse();
        h.Snapshot().Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(AppTools))]
    public async Task A_scoped_member_granted_the_project_as_a_member_may_use_it(string name)
    {
        using var h = new AppToolsHarness(SystemRole.Member, scoped: true);
        h.SeedApp("web");
        h.Grant(SystemRole.Member);

        var verdict = await h.ValidateAsync(h.Tool(name), ArgsFor(name, "web"));

        verdict.Ok.Should().BeTrue(verdict.Error);
    }

    [Fact]
    public async Task An_operator_may_restart_and_stop_but_nothing_else()
    {
        using var h = new AppToolsHarness(SystemRole.Operator);
        h.SeedApp("web");

        foreach (var name in AppToolNames)
        {
            var verdict = await h.ValidateAsync(h.Tool(name), ArgsFor(name, "web"));
            verdict.Ok.Should().Be(name is "restart_app" or "stop_app", $"{name}: {verdict.Error}");
        }
    }

    [Theory]
    [InlineData("delete_env_var")]
    [InlineData("remove_domain")]
    [InlineData("remove_volume")]
    [InlineData("restart_app")]
    [InlineData("stop_app")]
    [InlineData("rollback_app")]
    [InlineData("delete_app")]
    public async Task A_tool_that_needs_the_app_as_it_is_now_does_not_accept_one_that_is_only_planned(string name)
    {
        // Removing, restarting, rolling back or deleting something that does not exist yet has nothing to
        // act on — and nothing to show on its card — so these tools accept only an app that exists.
        using var h = new AppToolsHarness();
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.App, "web", h.ProjectSlug, "production"));

        var verdict = await h.ValidateAsync(h.Tool(name), ArgsFor(name, "web"), scope);

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Be("No app named 'web' that you can see.");
    }

    [Fact]
    public async Task Execution_without_a_scope_does_not_accept_an_app_that_was_only_planned()
    {
        using var h = new AppToolsHarness();
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.App, "web", h.ProjectSlug, "production"));

        (await h.ValidateAsync(h.SetEnvVars(), ArgsFor("set_env_vars", "web"), scope)).Ok.Should().BeTrue();
        var result = await h.ExecuteAsync(h.SetEnvVars(), ArgsFor("set_env_vars", "web"));

        result.Ok.Should().BeFalse("by execution time the app has to exist");
        result.Error.Should().Be("No app named 'web' that you can see.");
    }

    [Fact]
    public async Task A_planned_app_in_an_environment_the_person_may_not_touch_is_refused()
    {
        using var h = new AppToolsHarness(SystemRole.Member, scoped: true);
        h.Grant(SystemRole.Viewer);
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.App, "web", h.ProjectSlug, "production"));

        var verdict = await h.ValidateAsync(h.AddDomain(), new { app = "web", host = "web.example.com" }, scope);

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("where app 'web' is to be created");
    }
}

/// <summary>The thirteen tools as the panel's own container builds them — every dependency resolvable,
/// every name once, beside the six read tools.</summary>
[Collection(HarboraHttpCollection.Name)]
public class AppToolRegistrationTests(HarboraHttpFixture fixture)
{
    [Fact]
    public void Every_app_tool_is_registered_once_and_resolves_with_the_panels_own_services()
    {
        using var scope = fixture.Panel.Services.CreateScope();

        var names = scope.ServiceProvider.GetServices<IAgentTool>().Select(t => t.Name).ToList();

        names.Should().OnlyHaveUniqueItems();
        names.Should().Contain([
            "create_app", "deploy_template", "set_env_vars", "delete_env_var", "add_domain", "remove_domain",
            "add_volume", "remove_volume", "deploy_app", "restart_app", "stop_app", "rollback_app", "delete_app",
            "list_resources", "get_app", "get_database", "get_catalog", "get_deployment", "get_usage"
        ]);
        scope.ServiceProvider.GetRequiredService<AgentToolRegistry>().All.Should().HaveCount(names.Count);
    }
}
