using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Templates;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// <c>create_app</c> and <c>deploy_template</c>: the two tools that make an app where there was none —
/// through the app form's service and the template page's service, with the agent-only rules on top (no
/// credentials, an explicit slug, a resolved environment, every template variable accounted for).
/// </summary>
public class AppCreateToolsTests
{
    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement;

    private static Task<AgentValidation> ValidateRaw(AppToolsHarness h, IAgentTool tool, string json, AgentPlanScope? scope = null) =>
        tool.ValidateAsync(Raw(json), h.Context, scope ?? new AgentPlanScope(), CancellationToken.None);

    // ---- create_app ----------------------------------------------------------------------------

    [Fact]
    public async Task An_image_app_validates_with_its_card_and_says_what_it_will_create()
    {
        using var h = new AppToolsHarness();
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine", port = 80 });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Create app web");
        verdict.TitleFa.Should().Be("ساخت اپ web");
        verdict.DetailEn.Should().Contain("image nginx:alpine").And.Contain("port 80")
            .And.Contain($"in {h.ProjectSlug}/production").And.Contain("first deployment queued");
        verdict.DetailFa.Should().Contain("ایمیج nginx:alpine").And.Contain("پورت 80");
        verdict.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.App, "web", h.ProjectSlug, "production"));
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Executing_it_creates_the_app_and_queues_its_first_deployment_as_the_assistant()
    {
        using var h = new AppToolsHarness();

        var result = await h.ExecuteAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine", port = 8080 });

        result.Ok.Should().BeTrue(result.Error);
        var app = await h.Db.Apps.AsNoTracking().SingleAsync(a => a.Slug == "web");
        app.EnvironmentId.Should().Be(h.EnvironmentId);
        app.PrebuiltImage.Should().Be("nginx:alpine");
        app.ContainerPort.Should().Be(8080);

        var queued = h.Deployments.Queued.Should().ContainSingle().Subject;
        queued.Trigger.Should().Be(DeploymentTrigger.Assistant);
        queued.TriggeredByUserId.Should().Be(h.UserId);
        var deployment = await h.Db.Deployments.AsNoTracking().SingleAsync(d => d.AppId == app.Id);

        result.Links.Should().Contain(l => l.Url == $"/apps/details/{app.Id}" && l.Kind == "app");
        result.Links.Should().Contain(l => l.Url == $"/deployments/details/{deployment.Id}" && l.Label == "#1");
        result.SummaryEn.Should().Contain("Created app 'web'").And.Contain("deployment #1 is queued").And.NotContain("running");
        result.SummaryFa.Should().Contain("«web»").And.Contain("ساخته شد").And.Contain("در صف");
        using var json = JsonDocument.Parse(result.ResultJson);
        json.RootElement.GetProperty("app").GetString().Should().Be("web");
        json.RootElement.GetProperty("deployment").GetProperty("status").GetString().Should().Be("queued");
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.created");
        h.Db.ChangeTracker.Entries().Should().NotContain(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached,
            "the tool saved its work before returning");
    }

    [Fact]
    public async Task A_git_app_takes_its_branch_and_folder_and_stores_no_credential()
    {
        using var h = new AppToolsHarness();
        var args = new { name = "api", repository = "https://github.com/acme/api.git", gitRef = "develop", rootDirectory = "services/api" };

        var verdict = await h.ValidateAsync(h.CreateApp(), args);
        var result = await h.ExecuteAsync(h.CreateApp(), args);

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.DetailEn.Should().Contain("Git https://github.com/acme/api.git (ref develop, folder services/api)");
        result.Ok.Should().BeTrue(result.Error);
        var app = await h.Db.Apps.AsNoTracking().Include(a => a.GitRepository).ThenInclude(r => r!.Provider).SingleAsync(a => a.Slug == "api");
        app.SourceType.Should().Be(AppSourceType.GitRepository);
        app.GitRef.Should().Be("develop");
        app.BuildContextPath.Should().Be("services/api");
        app.GitRepository!.CloneUrl.Should().Be("https://github.com/acme/api.git");
        app.GitRepository.Provider!.EncryptedCredential.Should().BeNull("the assistant never takes a token");
    }

    [Theory]
    [InlineData("""{"name":"web","image":"nginx","gitToken":"ghp_secret"}""", "does not take Git tokens")]
    [InlineData("""{"name":"web","repository":"https://bob:ghp_secret@github.com/acme/api.git"}""", "user name or token")]
    [InlineData("""{"name":"web","repository":"https://github.com/acme/api.git?access_token=ghp_secret"}""", "user name or token")]
    [InlineData("""{"name":"web","repository":"https://gitlab.example.com/acme/api.git?private_token=ghp_secret"}""", "user name or token")]
    [InlineData("""{"name":"web","repository":"https://github.com/acme/api.git?ref=main&Token=ghp_secret"}""", "user name or token")]
    [InlineData("""{"name":"web","repository":"git@github.com:acme/api.git"}""", "public https clone URL")]
    [InlineData("""{"name":"web","image":"nginx","repository":"https://github.com/acme/api.git"}""", "not both")]
    [InlineData("""{"name":"web"}""", "Give an image")]
    [InlineData("""{"image":"nginx"}""", "Give the app a name")]
    [InlineData("""{"name":"فروشگاه","image":"nginx"}""", "gives no usable slug")]
    [InlineData("""{"name":"web","image":"nginx","port":70000}""", "between 1 and 65535")]
    [InlineData("""{"name":"web","image":"nginx","size":"huge"}""", "There is no size 'huge'")]
    [InlineData("""{"name":"web","image":"nginx","environment":"staging"}""", "Say which project environment 'staging'")]
    [InlineData("""{"name":"web","image":"nginx","project":"nope"}""", "No project named 'nope' that you can see.")]
    public async Task A_call_the_agent_must_not_make_is_refused_in_both_languages_and_changes_nothing(string json, string expected)
    {
        using var h = new AppToolsHarness();
        var before = h.Snapshot();

        var verdict = await ValidateRaw(h, h.CreateApp(), json);
        var result = await h.CreateApp().ExecuteAsync(Raw(json), h.Context, CancellationToken.None);

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain(expected);
        verdict.ErrorFa.Should().MatchRegex("[؀-ۿ]");
        verdict.Error.Should().NotContain("ghp_secret", "a credential is never repeated back");
        result.Ok.Should().BeFalse();
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_slug_taken_anywhere_on_the_platform_is_refused_in_the_services_own_words()
    {
        using var h = new AppToolsHarness();
        h.SeedForeignApp("web");

        var verdict = await h.ValidateAsync(h.CreateApp(), new { name = "Web", image = "nginx:alpine" });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("'web' is already taken on this platform");
        verdict.ErrorFa.Should().Contain("«web» قبلاً در پلتفرم استفاده شده است");
    }

    [Fact]
    public async Task With_two_projects_the_project_must_be_named_and_a_named_one_is_used()
    {
        using var h = new AppToolsHarness();
        var (blog, blogProduction) = h.SeedProject("blog");

        var unnamed = await h.ValidateAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine" });
        var named = await h.ExecuteAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine", project = "blog" });

        unnamed.Ok.Should().BeFalse();
        unnamed.Error.Should().Contain("Say which project").And.Contain("blog").And.Contain(h.ProjectSlug);
        named.Ok.Should().BeTrue(named.Error);
        (await h.Db.Apps.AsNoTracking().SingleAsync(a => a.Slug == "web")).EnvironmentId.Should().Be(blogProduction.Id);
        named.SummaryEn.Should().Contain("blog/production");
    }

    [Fact]
    public async Task Another_workspaces_project_is_not_found()
    {
        using var h = new AppToolsHarness();
        h.SeedForeignApp(); // brings project "theirs" in another workspace

        var verdict = await h.ValidateAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine", project = "theirs" });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Be("No project named 'theirs' that you can see.");
    }

    [Fact]
    public async Task Only_a_size_the_plan_offers_is_accepted()
    {
        using var h = new AppToolsHarness();
        h.Db.InstanceSizes.AddRange(
            new InstanceSize { Key = "small", Name = "Small", IsEnabled = true, CpuCores = 0.5, MemoryBytes = 512L << 20 },
            new InstanceSize { Key = "large", Name = "Large", IsEnabled = true, CpuCores = 4, MemoryBytes = 8L << 30 });
        h.Plan.AllowedSizeKeys = "small";
        h.Db.Update(h.Plan);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var small = await h.ValidateAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine", size = "SMALL" });
        var large = await h.ValidateAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine", size = "large" });

        small.Ok.Should().BeTrue(small.Error);
        small.DetailEn.Should().Contain("size small");
        large.Ok.Should().BeFalse();
        large.Error.Should().Contain("There is no size 'large'").And.Contain("Sizes: small");
    }

    [Fact]
    public async Task An_app_in_a_project_an_earlier_step_creates_is_validated_against_the_planned_project()
    {
        using var h = new AppToolsHarness();
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.Project, "blog"));
        var args = new { name = "api", image = "nginx:alpine", project = "blog" };

        var verdict = await h.ValidateAsync(h.CreateApp(), args, scope);
        var tooEarly = await h.ExecuteAsync(h.CreateApp(), args);

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.App, "api", "blog", "production"));
        verdict.WarningsEn.Should().Contain(w => w.Contains("blog/production is created by an earlier step"));
        verdict.DetailEn.Should().Contain("in blog/production");
        tooEarly.Ok.Should().BeFalse("at execution the project has to exist");
        tooEarly.Error.Should().Be("No project named 'blog' that you can see.");
    }

    [Fact]
    public async Task A_planned_project_offers_only_its_first_environment_unless_an_earlier_step_adds_another()
    {
        using var h = new AppToolsHarness();
        var scope = new AgentPlanScope();
        // create_project's convention: the project, with its default environment's slug.
        scope.Add(new AgentPlannedResource(AgentResourceKinds.Project, "blog", Environment: "production"));

        var production = await h.ValidateAsync(h.CreateApp(), new { name = "api", image = "nginx:alpine", project = "blog", environment = "production" }, scope);
        var staging = await h.ValidateAsync(h.CreateApp(), new { name = "api", image = "nginx:alpine", project = "blog", environment = "staging" }, scope);
        // add_environment's convention: the environment, with its project.
        scope.Add(new AgentPlannedResource(AgentResourceKinds.Environment, "staging", Project: "blog"));
        var stagingPlanned = await h.ValidateAsync(h.CreateApp(), new { name = "api", image = "nginx:alpine", project = "blog", environment = "staging" }, scope);

        production.Ok.Should().BeTrue(production.Error);
        staging.Ok.Should().BeFalse();
        staging.Error.Should().Contain("only its 'production' environment");
        stagingPlanned.Ok.Should().BeTrue(stagingPlanned.Error);
        stagingPlanned.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.App, "api", "blog", "staging"));
    }

    [Fact]
    public async Task An_environment_an_earlier_step_adds_to_an_existing_project_is_accepted_and_asked_about_that_project()
    {
        using var h = new AppToolsHarness();
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.Environment, "staging", Project: h.ProjectSlug));

        var verdict = await h.ValidateAsync(h.CreateApp(), new { name = "api", image = "nginx:alpine", project = h.ProjectSlug, environment = "staging" }, scope);

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.App, "api", h.ProjectSlug, "staging"));
    }

    [Fact]
    public async Task The_same_app_twice_in_one_plan_is_refused()
    {
        using var h = new AppToolsHarness();
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.App, "web", h.ProjectSlug, "production"));

        var verdict = await h.ValidateAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine" }, scope);

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("An earlier step of this plan already creates app 'web'");
    }

    [Fact]
    public async Task A_protected_environment_and_a_paying_workspace_are_both_warned_about()
    {
        using var h = new AppToolsHarness();
        h.MakeProtected();
        h.Billing.Enabled = true;

        var verdict = await h.ValidateAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine" });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.WarningsEn.Should().Contain(w => w.Contains("is protected")).And.Contain(w => w.Contains("first hour is charged"));
        verdict.WarningsFa.Should().HaveCount(verdict.WarningsEn!.Count);
    }

    [Fact]
    public async Task A_protected_environment_leaves_the_first_deployment_waiting_for_approval()
    {
        using var h = new AppToolsHarness();
        h.MakeProtected();

        var result = await h.ExecuteAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine" });

        result.Ok.Should().BeTrue(result.Error);
        result.SummaryEn.Should().Contain("waiting for approval");
        result.ResultJson.Should().Contain("pending_approval");
    }

    [Fact]
    public async Task A_create_refused_at_execution_returns_failed_and_creates_nothing()
    {
        using var h = new AppToolsHarness();
        (await h.ValidateAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine" })).Ok.Should().BeTrue();

        // Between the card and the approval, another workspace takes the name.
        h.SeedForeignApp("web");
        var before = h.Snapshot();
        var result = await h.ExecuteAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine" });

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("already taken on this platform");
        result.ErrorFa.Should().Contain("قبلاً در پلتفرم استفاده شده است");
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Deploy_now_false_creates_the_app_without_a_deployment_and_says_so()
    {
        using var h = new AppToolsHarness();

        var result = await h.ExecuteAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine", deployNow = false });

        result.Ok.Should().BeTrue(result.Error);
        h.Deployments.Queued.Should().BeEmpty();
        result.SummaryEn.Should().Contain("not deployed yet");
        result.SummaryFa.Should().Contain("هنوز مستقر نشده است");
    }

    [Fact]
    public async Task A_first_deploy_refused_after_the_create_reports_the_app_and_the_reason()
    {
        using var h = new AppToolsHarness();
        h.Deployments.Refuse = new QuotaRefusedException(new QuotaCheck(false, "Deployment limit reached.", "سقف استقرار پر شده است."));

        var result = await h.ExecuteAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine" });

        result.Ok.Should().BeTrue("the app exists either way");
        (await h.Db.Apps.AsNoTracking().CountAsync(a => a.Slug == "web")).Should().Be(1);
        result.SummaryEn.Should().Contain("did not start: Deployment limit reached.");
        result.SummaryFa.Should().Contain("سقف استقرار پر شده است.");
        result.ResultJson.Should().Contain("deployRefused");
    }

    [Theory]
    [InlineData(SystemRole.Viewer, false)]
    [InlineData(SystemRole.Member, true)]
    public async Task A_viewer_or_a_scoped_member_without_a_grant_cannot_create(SystemRole role, bool scoped)
    {
        using var h = new AppToolsHarness(role, scoped);
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine" });
        var result = await h.ExecuteAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine" });

        verdict.Ok.Should().BeFalse();
        result.Ok.Should().BeFalse();
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_scoped_member_granted_the_project_creates_in_it()
    {
        using var h = new AppToolsHarness(SystemRole.Member, scoped: true);
        h.Grant(SystemRole.Member);

        var result = await h.ExecuteAsync(h.CreateApp(), new { name = "web", image = "nginx:alpine", deployNow = false });

        result.Ok.Should().BeTrue(result.Error);
    }

    /// <summary>A provisioning service that stages an app's whole graph and then throws, as a save that
    /// lost a unique-slug race to another workspace does.</summary>
    private sealed class StagesThenThrows(AppToolsHarness h) : Harbora.Infrastructure.Apps.IAppProvisioningService
    {
        public Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(Harbora.Infrastructure.Apps.AppCreateRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServiceRefusal>>([]);

        public Task<ServiceOutcome<Harbora.Infrastructure.Apps.AppCreateResult>> CreateAsync(
            Harbora.Infrastructure.Apps.AppCreateRequest request, CancellationToken ct)
        {
            var app = new Harbora.Domain.Apps.App
            {
                WorkspaceId = h.WorkspaceId, EnvironmentId = h.EnvironmentId, ServerId = h.Server.Id, Name = "web", Slug = "web"
            };
            app.Domains.Add(new Harbora.Domain.Networking.DomainName { Host = "web.apps.example.com", IsPrimary = true });
            app.Volumes.Add(new Harbora.Domain.Apps.Volume { Name = "v", MountPath = "/data" });
            app.EnvironmentVariables.Add(new Harbora.Domain.Apps.EnvironmentVariable { Key = "A", Value = "1" });
            h.Db.Apps.Add(app);
            throw new DbUpdateException("duplicate key value violates unique constraint \"IX_Apps_Slug\"");
        }
    }

    [Fact]
    public async Task A_create_that_throws_after_staging_the_app_leaves_nothing_tracked_and_lets_the_failure_through()
    {
        using var h = new AppToolsHarness();
        var tool = new Harbora.Infrastructure.Agent.Tools.CreateAppTool(
            h.Db, h.Agent.Resolver, h.Agent.Access, new StagesThenThrows(h), Microsoft.Extensions.Options.Options.Create(h.Billing));
        var before = h.Snapshot();

        var act = () => h.ExecuteAsync(tool, new { name = "web", image = "nginx:alpine" });

        await act.Should().ThrowAsync<DbUpdateException>("it is not a refusal; the plan engine reports it as a failed step");
        h.Snapshot().Should().Be(before, "the staged app, its domain, volume and variable were all dropped — dependents first");
    }

    // ---- deploy_template ------------------------------------------------------------------------

    private const string GhostManifest = """
        {
          "image": "ghost:5", "port": 2368, "requires": ["mysql"],
          "env": [
            { "key": "url" },
            { "key": "database__client", "default": "mysql" },
            { "key": "database__connection__host", "default": "${{mysql.host}}" },
            { "key": "APP_KEY", "secret": true },
            { "key": "SMTP_PASSWORD", "secret": true, "required": true }
          ]
        }
        """;

    private static AppTemplate SeedTemplate(AppToolsHarness h, string key = "ghost", string manifest = GhostManifest, Guid? workspaceId = null,
        TemplateStatus status = TemplateStatus.Private)
    {
        var template = new AppTemplate
        {
            Key = key, Name = "Ghost", NameFa = "گوست", Category = "app", ManifestJson = manifest, IsBuiltIn = workspaceId is null,
            WorkspaceId = workspaceId, Status = status
        };
        h.Db.AppTemplates.Add(template);
        h.Db.SaveChanges();
        h.Db.ChangeTracker.Clear();
        return template;
    }

    /// <summary>Ghost with its two needed variables: a plain url, and the SMTP password as a lock-button handle.</summary>
    private static object GhostArgs(AppToolsHarness h) => new
    {
        template = "ghost",
        name = "blog",
        variables = new Dictionary<string, string>
        {
            ["url"] = "https://blog.example.com", ["SMTP_PASSWORD"] = h.Lock("SMTP_PASSWORD", "hunter2-secret")
        }
    };

    [Fact]
    public async Task A_plain_value_for_a_variable_the_template_marks_secret_is_refused_with_the_lock_button_guidance()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);

        var verdict = await h.ValidateAsync(h.DeployTemplate(), new
        {
            template = "ghost",
            variables = new Dictionary<string, string> { ["url"] = "https://blog.example.com", ["SMTP_PASSWORD"] = "hunter2-secret" }
        });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("SMTP_PASSWORD is a secret").And.Contain("lock button").And.NotContain("hunter2-secret");
        verdict.ErrorFa.Should().Contain("دکمهٔ قفل");
    }

    [Fact]
    public async Task A_missing_secret_variable_is_asked_for_through_the_lock_button()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);

        var verdict = await h.ValidateAsync(h.DeployTemplate(), new
        {
            template = "ghost", variables = new Dictionary<string, string> { ["url"] = "https://blog.example.com" }
        });

        verdict.Error.Should().Contain("needs values for: SMTP_PASSWORD").And.Contain("the secret ones (SMTP_PASSWORD) with the lock button");
    }

    [Fact]
    public async Task A_handle_for_a_variable_the_template_left_plain_is_stored_as_a_secret_before_anything_deploys()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);
        var url = h.Lock("url", "https://blog.example.com/private");

        var result = await h.ExecuteAsync(h.DeployTemplate(), new
        {
            template = "ghost", name = "blog",
            variables = new Dictionary<string, string> { ["url"] = url, ["SMTP_PASSWORD"] = h.Lock("SMTP_PASSWORD", "x") }
        });

        result.Ok.Should().BeTrue(result.Error);
        var row = await h.Db.EnvironmentVariables.AsNoTracking().SingleAsync(v => v.App!.Slug == "blog" && v.Key == "url");
        row.IsSecret.Should().BeTrue();
        row.Value.Should().Be("enc:https://blog.example.com/private");
        result.SummaryEn.Should().Contain("url stored as a secret");
        (result.ResultJson + result.SummaryEn + result.SummaryFa).Should().NotContain("blog.example.com/private");
    }

    [Fact]
    public async Task A_template_handle_that_expired_before_the_run_creates_nothing()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);
        var smtp = h.Lock("SMTP_PASSWORD", "hunter2-secret");
        var args = new
        {
            template = "ghost", name = "blog",
            variables = new Dictionary<string, string> { ["url"] = "https://blog.example.com", ["SMTP_PASSWORD"] = smtp }
        };
        (await h.ValidateAsync(h.DeployTemplate(), args)).Ok.Should().BeTrue();
        h.Vault.Forget(smtp);
        var before = h.Snapshot();

        var result = await h.ExecuteAsync(h.DeployTemplate(), args);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("The secret for SMTP_PASSWORD expired");
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_template_that_throws_after_committing_the_project_says_the_project_exists()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);
        h.ManagedServices.ProvisionThrows = new InvalidOperationException("The job queue is unavailable.");

        var result = await h.ExecuteAsync(h.DeployTemplate(), GhostArgs(h));

        result.Ok.Should().BeFalse();
        var project = await h.Db.Projects.AsNoTracking().SingleAsync(p => p.Slug == "ghost");
        result.Error.Should().StartWith("Project 'ghost' was created, but the template did not finish:")
            .And.Contain("error on the server").And.NotContain("job queue", "an unexpected failure is told in neutral words");
        result.ErrorFa.Should().Contain("«ghost» ساخته شد");
        result.Links.Should().ContainSingle().Which.Url.Should().Be($"/projects/{project.Id}");
    }

    [Fact]
    public async Task A_first_deploy_that_fails_unexpectedly_after_the_template_still_reports_the_project_as_created()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);
        h.Deployments.FailsFromElsewhere = () => throw new InvalidOperationException("A second operation was started on this context.");

        var result = await h.ExecuteAsync(h.DeployTemplate(), GhostArgs(h));

        result.Ok.Should().BeTrue("the project, its app and database exist");
        result.SummaryEn.Should().Contain("Its first deployment did not start: it could not be queued because of an error on the server")
            .And.NotContain("second operation");
    }

    [Fact]
    public async Task A_template_validates_as_a_new_project_with_its_database_and_hides_secret_values()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.DeployTemplate(), GhostArgs(h));

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Deploy template Ghost as blog");
        verdict.TitleFa.Should().Be("استقرار قالب گوست با نام blog");
        verdict.DetailEn.Should().Contain("New project 'Ghost' (ghost/production)").And.Contain("app blog from image ghost:5")
            .And.Contain("mysql").And.Contain("url = https://blog.example.com").And.Contain($"SMTP_PASSWORD (secret — {AgentSecretHandles.Masked}, entered with the lock button)");
        (verdict.DetailEn + verdict.DetailFa + verdict.TitleEn + verdict.TitleFa).Should().NotContain("hunter2-secret");
        verdict.WarningsEn.Should().Contain(w => w.Contains("always creates a new project"));
        verdict.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.App, "blog", "ghost", "production"));
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Executing_a_template_creates_the_project_app_and_database_and_queues_the_deploy_as_the_assistant()
    {
        using var h = new AppToolsHarness();
        var template = SeedTemplate(h);

        var result = await h.ExecuteAsync(h.DeployTemplate(), GhostArgs(h));

        result.Ok.Should().BeTrue(result.Error);
        var project = await h.Db.Projects.AsNoTracking().SingleAsync(p => p.Slug == "ghost");
        var app = await h.Db.Apps.AsNoTracking().Include(a => a.Environment).Include(a => a.EnvironmentVariables).SingleAsync(a => a.Slug == "blog");
        app.Environment!.ProjectId.Should().Be(project.Id);
        app.TemplateId.Should().Be(template.Id);
        app.EnvironmentVariables.Single(v => v.Key == "SMTP_PASSWORD").Value.Should().Be("enc:hunter2-secret");
        var database = await h.Db.ManagedServices.AsNoTracking().SingleAsync(s => s.EnvironmentId == app.EnvironmentId);
        h.ManagedServices.Provisioned.Should().Equal(database.Id);
        h.Deployments.Queued.Should().ContainSingle().Which.Trigger.Should().Be(DeploymentTrigger.Assistant);

        result.Links!.Select(l => l.Kind).Should().Contain(["project", "app", "database", "deployment"]);
        result.Links.Should().Contain(l => l.Url == $"/projects/{project.Id}");
        result.SummaryEn.Should().Contain("Created project 'ghost'").And.Contain("app 'blog'").And.Contain("deployment #1 is queued");
        result.SummaryFa.Should().Contain("«ghost»");
        (result.ResultJson + result.SummaryEn + result.SummaryFa).Should().NotContain("hunter2-secret");
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "template.deploy");
    }

    [Theory]
    [InlineData("missing", "needs values for: url, SMTP_PASSWORD")]
    [InlineData("unknown", "has no variable NOPE")]
    [InlineData("defaulted", "database__client is set by the template itself")]
    [InlineData("reference", "url refers to an unknown service value: redis.host")]
    [InlineData("repository", "does not build from a repository")]
    [InlineData("template", "There is no template 'nope'")]
    public async Task A_template_call_the_service_would_refuse_or_silently_ignore_is_refused_first(string what, string expected)
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);
        var variables = new Dictionary<string, string> { ["url"] = "https://blog.example.com", ["SMTP_PASSWORD"] = h.Lock("SMTP_PASSWORD", "x") };
        object args = what switch
        {
            "missing" => new { template = "ghost", variables = new Dictionary<string, string>() },
            "unknown" => new { template = "ghost", variables = new Dictionary<string, string>(variables) { ["NOPE"] = "1" } },
            "defaulted" => new { template = "ghost", variables = new Dictionary<string, string>(variables) { ["database__client"] = "pg" } },
            "reference" => new { template = "ghost", variables = new Dictionary<string, string>(variables) { ["url"] = "${{redis.host}}" } },
            "repository" => new { template = "ghost", repository = "https://github.com/acme/ghost.git", variables },
            _ => new { template = "nope" }
        };
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.DeployTemplate(), args);

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain(expected);
        verdict.ErrorFa.Should().MatchRegex("[؀-ۿ]");
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_taken_project_or_app_slug_is_refused_rather_than_quietly_suffixed()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);
        h.SeedProject("ghost");
        h.SeedForeignApp("blog");

        var projectTaken = await h.ValidateAsync(h.DeployTemplate(), GhostArgs(h));
        var appTaken = await h.ValidateAsync(h.DeployTemplate(), new
        {
            template = "ghost", name = "blog", project = "Ghost Blog",
            variables = new Dictionary<string, string> { ["url"] = "https://blog.example.com", ["SMTP_PASSWORD"] = h.Lock("SMTP_PASSWORD", "x") }
        });
        var free = await h.ValidateAsync(h.DeployTemplate(), new
        {
            template = "ghost", name = "journal", project = "Ghost Blog",
            variables = new Dictionary<string, string> { ["url"] = "https://blog.example.com", ["SMTP_PASSWORD"] = h.Lock("SMTP_PASSWORD", "x") }
        });

        projectTaken.Error.Should().Be("A project 'ghost' already exists. A template always creates a new project: pass another project name.");
        appTaken.Error.Should().Contain("'blog' is already taken on this platform");
        free.Ok.Should().BeTrue(free.Error);
        free.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.App, "journal", "ghost-blog", "production"));
    }

    [Fact]
    public async Task Another_workspaces_private_template_is_not_offered()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h, key: "secret-stack", workspaceId: Guid.NewGuid(), status: TemplateStatus.Private);

        var verdict = await h.ValidateAsync(h.DeployTemplate(), new { template = "secret-stack" });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("There is no template 'secret-stack'");
    }

    [Fact]
    public async Task A_git_template_needs_a_public_repository()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h, key: "starter", manifest: """{ "source": "git", "port": 3000 }""");

        var none = await h.ValidateAsync(h.DeployTemplate(), new { template = "starter" });
        var withToken = await h.ValidateAsync(h.DeployTemplate(), new { template = "starter", repository = "https://x:tok@github.com/a/b.git" });
        var fine = await h.ValidateAsync(h.DeployTemplate(), new { template = "starter", repository = "https://github.com/a/b.git" });

        none.Error.Should().Contain("builds from Git: give a public repository URL");
        withToken.Error.Should().Contain("user name or token");
        fine.Ok.Should().BeTrue(fine.Error);
        fine.DetailEn.Should().Contain("Git https://github.com/a/b.git");
    }

    [Theory]
    [InlineData(SystemRole.Viewer, false, false)]
    [InlineData(SystemRole.Member, true, false)]
    [InlineData(SystemRole.Member, true, true)]
    public async Task Only_someone_who_may_create_a_project_may_deploy_a_template(SystemRole role, bool scoped, bool granted)
    {
        using var h = new AppToolsHarness(role, scoped);
        if (granted) h.Grant(SystemRole.Member);
        SeedTemplate(h);

        var verdict = await h.ValidateAsync(h.DeployTemplate(), GhostArgs(h));

        verdict.Ok.Should().BeFalse("a template always makes a new project, which no project grant covers");
        verdict.Error.Should().Contain("does not allow");
    }

    [Fact]
    public async Task A_template_refused_after_the_service_staged_rows_returns_failed_and_leaves_nothing_behind()
    {
        using var h = new AppToolsHarness();
        SeedTemplate(h);
        (await h.ValidateAsync(h.DeployTemplate(), GhostArgs(h))).Ok.Should().BeTrue();

        // The plan's domain ceiling is checked by the service only after it has staged the project, its
        // environment and the database; the app's own address is one domain too many.
        h.SeedApp("web");
        h.Plan.MaxDomains = 1;
        h.Db.Update(h.Plan);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var before = h.Snapshot();

        var result = await h.ExecuteAsync(h.DeployTemplate(), GhostArgs(h));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("The plan allows 1 domain(s)");
        result.ErrorFa.Should().StartWith("قالب مستقر نشد: ");
        h.Snapshot().Should().Be(before, "what the service staged was dropped, not left for the agent's next save");
        await h.Db.SaveChangesAsync();
        h.Snapshot().Should().Be(before);
    }
}
