using System.Text.Json;
using FluentAssertions;
using Harbora.Domain.Agent;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Agent.Tools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The project and database tools driven through the agent loop (<see cref="AgentConversationService"/>)
/// and the plan engine with a scripted model: a plan whose later steps name what earlier steps create, a
/// remembered tool running without a card, a typed delete, and a dangerous tool that is always carded. Also
/// the facts every tool shares: its kind, its capability, which roles are offered it, and its schema.
/// </summary>
public class ProjectDatabaseToolScenarioTests
{
    private static readonly string[] ToolNames =
    [
        "create_project", "add_environment", "create_database", "attach_database", "detach_database",
        "rotate_database_password", "stop_database", "start_database", "delete_database", "delete_project"
    ];

    private const string Request =
        "create project shop, a Postgres 16 database shop-db in its production environment, and attach it to app web";

    private static AgentApproveRequest Approve(IReadOnlyDictionary<int, string>? confirmations = null, params string[] remember) =>
        new(confirmations, remember);

    // ---- scenario 1 ----------------------------------------------------------------------------

    [Fact]
    public async Task The_briefs_request_plans_the_project_and_database_and_refuses_attaching_across_environments()
    {
        // "web exists": in the workspace's existing project. The new database lands in the new project's
        // production — another environment, so another private network — and the attach is refused while the
        // plan is being drawn, from the planned database's place alone, with the reason fed back to the model.
        using var rig = new DatabaseToolRig();
        var web = rig.SeedApp("web");
        var chat = rig.H.Chat;
        chat.ThenCalls(
                chat.Call("create_project", """{"name":"Shop","slug":"shop"}"""),
                chat.Call("create_database", """{"name":"shop-db","engine":"postgresql","version":"16","project":"shop"}"""),
                chat.Call("attach_database", """{"database":"shop-db","app":"web"}"""))
            .ThenText("Project shop and database shop-db are waiting for your approval; web is in another environment.");

        var (_, events) = await rig.H.SendAsync(Request);

        var refusal = chat.Requests[1].Messages.Last().Content;
        refusal.Should().StartWith($"Refused: App web is in {rig.ProjectSlug}/production, but database shop-db is in shop/production.");
        var plan = events.Single<AgentPlanView>("plan");
        plan.Steps.Select(s => (s.Tool, s.Kind)).Should().Equal(("create_project", "safe"), ("create_database", "safe"));
        plan.Steps[1].DetailEn.Should().Contain("shop/production").And.Contain("created by an earlier step");
        (await rig.Db.Projects.AnyAsync(p => p.Slug == "shop")).Should().BeFalse("nothing runs before approval");

        var outcome = await rig.H.Plans.ApproveAsync(rig.H.Actor, plan.Id, Approve(), CancellationToken.None);

        outcome.Ok.Should().BeTrue(outcome.Refusal?.MessageEn);
        outcome.Result!.Steps.Select(s => s.State).Should().Equal("done", "done");
        var service = await rig.Db.ManagedServices.Include(s => s.Environment).ThenInclude(e => e!.Project).SingleAsync();
        (service.Environment!.Project!.Slug, service.Environment.Slug, service.Version).Should().Be(("shop", "production", "16"));
        (await rig.Db.AppManagedServices.AnyAsync(x => x.AppId == web.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task One_plan_of_safe_steps_validated_against_what_earlier_steps_create_runs_in_order_with_links()
    {
        // The brief's request made coherent: the app the database is attached to sits in the new project's
        // production too, created by a stand-in for agent F's create_app (planned, then a real row).
        using var rig = new DatabaseToolRig();
        rig.H.Tools.Add(StandInCreateApp(rig));
        var chat = rig.H.Chat;
        chat.ThenCalls(
                chat.Call("create_project", """{"name":"Shop","slug":"shop"}"""),
                chat.Call("create_database", """{"name":"shop-db","engine":"postgresql","version":"16","project":"shop"}"""),
                chat.Call("create_app", """{"name":"web","project":"shop"}"""),
                chat.Call("attach_database", """{"database":"shop-db","app":"web"}"""))
            .ThenText("Four steps are waiting for your approval.");

        var (_, events) = await rig.H.SendAsync(Request);

        var plan = events.Single<AgentPlanView>("plan");
        plan.Steps.Select(s => s.Tool).Should().Equal("create_project", "create_database", "create_app", "attach_database");
        plan.Steps.Should().OnlyContain(s => s.Kind == "safe" && !s.AutoApproved && s.Rememberable);
        plan.Steps[3].DetailEn.Should().Contain("Both are created by earlier steps");
        rig.Counts().Projects.Should().Be(1, "only the harness's own project exists before approval");

        var outcome = await rig.H.Plans.ApproveAsync(rig.H.Actor, plan.Id, Approve(), CancellationToken.None);

        outcome.Ok.Should().BeTrue(outcome.Refusal?.MessageEn);
        outcome.Result!.Status.Should().Be("executed");
        outcome.Result.Steps.Select(s => s.State).Should().Equal("done", "done", "done", "done");
        var project = await rig.Db.Projects.SingleAsync(p => p.Slug == "shop");
        var service = await rig.Db.ManagedServices.SingleAsync(s => s.Name == "shop-db");
        var app = await rig.Db.Apps.SingleAsync(a => a.Slug == "web");
        outcome.Result.Steps[0].Links.Should().ContainSingle(l => l.Url == $"/projects/{project.Id}");
        outcome.Result.Steps[1].Links.Should().ContainSingle(l => l.Url == $"/databases/{service.Id}");
        outcome.Result.Steps[3].Links.Select(l => l.Url).Should().Equal($"/databases/{service.Id}", $"/apps/details/{app.Id}");
        (await rig.Db.AppManagedServices.SingleAsync()).Should().Match<AppManagedService>(x => x.AppId == app.Id && x.ManagedServiceId == service.Id);
        rig.Engine.Provisioned.Should().Equal(service.Id);

        // In order: each step's audit entry after the one before it, and the services' own entries between.
        rig.H.Audit.Entries.Select(e => e.Action).Should().Equal(
            "project.created", AgentPlanService.StepExecutedAction,
            "database.created", AgentPlanService.StepExecutedAction,
            AgentPlanService.StepExecutedAction,
            "database.attached", AgentPlanService.StepExecutedAction);
    }

    [Fact]
    public async Task When_the_database_step_fails_at_execution_the_steps_after_it_are_skipped_and_the_result_says_so()
    {
        using var rig = new DatabaseToolRig();
        var createApp = StandInCreateApp(rig);
        rig.H.Tools.Add(createApp);
        var chat = rig.H.Chat;
        chat.ThenCalls(
                chat.Call("create_project", """{"name":"Shop","slug":"shop"}"""),
                chat.Call("create_database", """{"name":"shop-db","engine":"postgresql","version":"16","project":"shop"}"""),
                chat.Call("create_app", """{"name":"web","project":"shop"}"""),
                chat.Call("attach_database", """{"database":"shop-db","app":"web"}"""))
            .ThenText("Four steps are waiting for your approval.");
        var (_, events) = await rig.H.SendAsync(Request);
        var plan = events.Single<AgentPlanView>("plan");

        // Billing switched on after the card was drawn: the first hour has no price, which only the creation
        // itself finds out — validation cannot ask the wallet.
        rig.Billing.Enabled = true;
        var outcome = await rig.H.Plans.ApproveAsync(rig.H.Actor, plan.Id, Approve(), CancellationToken.None);

        outcome.Ok.Should().BeTrue("the plan was approved; one of its steps failed");
        outcome.Result!.Status.Should().Be("failed");
        outcome.Result.Steps.Select(s => s.State).Should().Equal("done", "failed", "skipped", "skipped");
        outcome.Result.Steps[1].ErrorEn.Should().Contain("has no running hourly price");
        outcome.Result.Steps[1].ErrorFa.Should().Contain("قیمت ساعتی");
        outcome.Result.Steps[2].SummaryEn.Should().Be("Skipped: an earlier step failed.");
        (await rig.Db.Projects.AnyAsync(p => p.Slug == "shop")).Should().BeTrue("step 1 ran and stays done");
        (await rig.Db.ManagedServices.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
        createApp.Executions.Should().Be(0);
        (await rig.Db.AgentPlans.AsNoTracking().SingleAsync()).Status.Should().Be(AgentPlanStatus.Failed);

        // The conversation knows, for the next turn.
        var told = await rig.Db.AgentMessages.AsNoTracking().Where(m => m.Role == AgentMessageRole.Tool && m.PlanId == plan.Id)
            .OrderBy(m => m.Sequence).Select(m => m.Text).ToListAsync();
        told.Should().HaveCount(4);
        told[1].Should().Contain("\"state\":\"failed\"");
        told[3].Should().Contain("\"state\":\"skipped\"");
    }

    // ---- scenario 2 ----------------------------------------------------------------------------

    [Fact]
    public async Task A_remembered_create_database_runs_on_the_next_turn_without_a_card()
    {
        using var rig = new DatabaseToolRig();
        var chat = rig.H.Chat;
        chat.ThenCall("create_database", $$"""{"name":"shop-db","engine":"postgresql","project":"{{rig.ProjectSlug}}"}""")
            .ThenText("Approve it to create shop-db.");
        var (_, first) = await rig.H.SendAsync("create a postgres database shop-db");
        var conversation = first.Single<AgentStartedData>("started").ConversationId;
        var plan = first.Single<AgentPlanView>("plan");
        (await rig.H.Plans.ApproveAsync(rig.H.Actor, plan.Id, Approve(null, "create_database"), CancellationToken.None))
            .Ok.Should().BeTrue();

        chat.ThenCall("create_database", $$"""{"name":"cache","engine":"redis","project":"{{rig.ProjectSlug}}"}""")
            .ThenText("Creating cache.");
        var (_, second) = await rig.H.SendAsync("and a redis called cache", conversation);

        second.Names().Should().Equal("started", "message", "executed", "done");
        var executed = second.Single<AgentPlanResultView>("executed");
        executed.Status.Should().Be("executed");
        executed.Steps.Should().ContainSingle(s => s.State == "done"
            && s.SummaryEn == $"Created database cache (Redis 7) in {rig.ProjectSlug}/production; provisioning is queued.");
        (await rig.Db.ManagedServices.Select(s => s.Name).OrderBy(n => n).ToListAsync()).Should().Equal("cache", "shop-db");
    }

    // ---- scenario 3 ----------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_database_with_its_data_needs_its_exact_name_typed()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        rig.H.Chat.ThenCall("delete_database", """{"database":"shop-db","deleteData":true}""").ThenText("Type shop-db to confirm.");

        var (_, events) = await rig.H.SendAsync("delete shop-db and its data");

        var step = events.Single<AgentPlanView>("plan").Steps.Single();
        var planId = events.Single<AgentPlanView>("plan").Id;
        step.Kind.Should().Be("destructive");
        step.ConfirmationPhrase.Should().Be("shop-db");
        step.WarningsEn.Should().ContainSingle().Which.Should().Contain("deleted for good");
        step.WarningsFa.Should().ContainSingle().Which.Should().Contain("برای همیشه");

        var wrong = await rig.H.Plans.ApproveAsync(rig.H.Actor, planId,
            Approve(new Dictionary<int, string> { [0] = "Shop-DB" }), CancellationToken.None);
        wrong.Ok.Should().BeFalse();
        wrong.Refusal!.Code.Should().Be("confirmation_mismatch");
        (await rig.Db.ManagedServices.AnyAsync(s => s.Id == shop.Id)).Should().BeTrue();
        rig.Engine.Removed.Should().BeEmpty();

        var right = await rig.H.Plans.ApproveAsync(rig.H.Actor, planId,
            Approve(new Dictionary<int, string> { [0] = "shop-db" }), CancellationToken.None);
        right.Ok.Should().BeTrue(right.Refusal?.MessageEn);
        right.Result!.Steps.Single().State.Should().Be("done");
        rig.Engine.Removed.Should().Equal((shop.Id, true));
        (await rig.Db.ManagedServices.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_database_apps_are_attached_to_is_refused_while_the_plan_is_drawn()
    {
        using var rig = new DatabaseToolRig();
        rig.AttachRow(rig.SeedApp("web"), rig.SeedDatabase("shop-db"));
        rig.H.Chat.ThenCall("delete_database", """{"database":"shop-db","deleteData":true}""").ThenText("It is still attached to web.");

        var (_, events) = await rig.H.SendAsync("delete shop-db");

        rig.H.Chat.Requests[1].Messages.Last().Content.Should().StartWith(
            "Refused: This database is still attached to 1 app: web. Detach it from every app first, then delete it.");
        events.Names().Should().NotContain("plan");
        (await rig.Db.ManagedServices.CountAsync()).Should().Be(1);
    }

    // ---- scenario 4 ----------------------------------------------------------------------------

    [Fact]
    public async Task Stopping_a_database_is_carded_even_when_every_safe_tool_is_remembered()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        foreach (var tool in rig.All().Where(t => t.Kind == AgentToolKind.SafeWrite)) rig.H.Remember(tool.Name);
        rig.H.Remember("stop_database"); // a row nothing legitimate writes — the policy must still ignore it
        rig.H.Chat.ThenCall("stop_database", """{"database":"shop-db"}""").ThenText("Approve to stop shop-db.");

        var (_, events) = await rig.H.SendAsync("stop shop-db");

        events.Names().Should().Contain("plan").And.NotContain("executed");
        var step = events.Single<AgentPlanView>("plan").Steps.Single();
        step.Kind.Should().Be("dangerous");
        step.AutoApproved.Should().BeFalse();
        step.Rememberable.Should().BeFalse();
        rig.Engine.Stopped.Should().BeEmpty();
        (await rig.Db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == shop.Id)).Status.Should().Be(ServiceStatus.Running);
    }

    // ---- what every tool declares --------------------------------------------------------------

    [Theory]
    [InlineData("create_project", AgentToolKind.SafeWrite, Capabilities.AppsCreate)]
    [InlineData("add_environment", AgentToolKind.SafeWrite, Capabilities.AppsCreate)]
    [InlineData("create_database", AgentToolKind.SafeWrite, Capabilities.DatabasesManage)]
    [InlineData("attach_database", AgentToolKind.SafeWrite, Capabilities.DatabasesManage)]
    [InlineData("detach_database", AgentToolKind.DangerousWrite, Capabilities.DatabasesManage)]
    [InlineData("rotate_database_password", AgentToolKind.DangerousWrite, Capabilities.DatabasesManage)]
    [InlineData("stop_database", AgentToolKind.DangerousWrite, Capabilities.DatabasesManage)]
    [InlineData("start_database", AgentToolKind.SafeWrite, Capabilities.DatabasesManage)]
    [InlineData("delete_database", AgentToolKind.Destructive, Capabilities.DatabasesManage)]
    [InlineData("delete_project", AgentToolKind.Destructive, Capabilities.AppsDelete)]
    public void Each_tool_declares_its_kind_and_the_capability_of_the_matching_controller_action(
        string name, AgentToolKind kind, string capability)
    {
        using var rig = new DatabaseToolRig();

        var tool = rig.All().Single(t => t.Name == name);

        tool.Kind.Should().Be(kind);
        tool.Capability.Should().Be(capability);
        tool.Should().BeAssignableTo<AgentWriteTool>();
    }

    [Theory]
    [InlineData(SystemRole.Owner, 10)]
    [InlineData(SystemRole.Member, 10)]
    [InlineData(SystemRole.Operator, 0)]
    [InlineData(SystemRole.Viewer, 0)]
    public async Task The_registry_offers_these_tools_only_to_a_role_holding_their_capabilities(SystemRole role, int offered)
    {
        using var rig = new DatabaseToolRig(role);

        var allowed = await rig.H.Registry.AllowedForAsync(rig.H.Principal, CancellationToken.None);

        allowed.Count(t => ToolNames.Contains(t.Name)).Should().Be(offered);
    }

    [Fact]
    public void Every_schema_is_an_object_whose_required_arguments_are_described()
    {
        using var rig = new DatabaseToolRig();

        rig.All().Select(t => t.Name).Should().BeEquivalentTo(ToolNames);
        foreach (var tool in rig.All())
        {
            using var schema = JsonDocument.Parse(tool.ParametersSchema);
            var root = schema.RootElement;
            root.GetProperty("type").GetString().Should().Be("object", tool.Name);
            var properties = root.GetProperty("properties");
            foreach (var required in root.GetProperty("required").EnumerateArray())
                properties.TryGetProperty(required.GetString()!, out _).Should().BeTrue($"{tool.Name} requires {required}");
            foreach (var property in properties.EnumerateObject())
                property.Value.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace($"{tool.Name}.{property.Name}");
            tool.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// Stands in for agent F's <c>create_app</c> (not on this branch): plans an app in a project an earlier step
    /// creates, as F's tool does through <see cref="AgentValidation.Creates"/>, and on execution writes the row.
    /// </summary>
    private static FakeTool StandInCreateApp(DatabaseToolRig rig) => new("create_app", AgentToolKind.SafeWrite, Capabilities.AppsCreate)
    {
        OnValidate = (args, _, scope) =>
        {
            var project = AgentArgs.String(args, "project")!;
            return scope.Plans(AgentResourceKinds.Project, project) || rig.Db.Projects.Any(p => p.Slug == project)
                ? new AgentValidation(true, TitleEn: "Create app " + AgentArgs.String(args, "name"), TitleFa: "ساخت اپ",
                    Creates: new AgentPlannedResource(AgentResourceKinds.App, AgentArgs.String(args, "name")!, project, "production"))
                : AgentValidation.Refused("No such project.", "چنین پروژه‌ای نیست.");
        },
        OnExecute = (args, _) =>
        {
            var slug = AgentArgs.String(args, "project")!;
            var environment = rig.Db.Environments.Single(e => e.Project!.Slug == slug && e.IsDefault);
            rig.Db.Apps.Add(new App
            {
                WorkspaceId = rig.H.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
                Name = AgentArgs.String(args, "name")!, Slug = AgentArgs.String(args, "name")!,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "nginx:1.27", Status = AppStatus.Created
            });
            rig.Db.SaveChanges();
            return new AgentToolResult(true, """{"app":"web"}""", "Created app web.", "اپ web ساخته شد.");
        }
    };
}
