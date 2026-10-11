using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// <c>create_project</c>, <c>add_environment</c> and <c>delete_project</c>, called directly over the real
/// <c>ProjectService</c> and <c>ProjectDeletionService</c>: the card a person reads, every refusal, the
/// planned-project path, who may, and — the defining rule — that validating changes nothing and that a step
/// reports success only for work that happened.
/// </summary>
public class ProjectToolsTests
{
    // ---- create_project ------------------------------------------------------------------------

    [Fact]
    public async Task Create_project_validates_into_a_card_names_what_it_creates_and_changes_nothing()
    {
        using var rig = new DatabaseToolRig();
        var before = rig.Counts();

        var verdict = await rig.ValidateAsync(rig.CreateProject(), """{"name":"Shop","slug":"shop"}""");

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Create project Shop");
        verdict.TitleFa.Should().Be("ساخت پروژهٔ Shop");
        verdict.DetailEn.Should().Contain("Slug 'shop'").And.Contain("'production'");
        verdict.DetailFa.Should().Contain("نامک «shop»");
        verdict.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.Project, "shop", null, "production"));
        rig.Counts().Should().Be(before);
        rig.PendingChanges.Should().Be(0);
    }

    [Fact]
    public async Task A_persian_name_needs_a_latin_slug_and_keeps_its_persian_display_name()
    {
        using var rig = new DatabaseToolRig();

        var noSlug = await rig.ValidateAsync(rig.CreateProject(), """{"name":"فروشگاه"}""");
        noSlug.Ok.Should().BeFalse();
        noSlug.Error.Should().Contain("slug in lowercase Latin letters");
        noSlug.ErrorFa.Should().Contain("حروف کوچک لاتین");

        var withSlug = await rig.ValidateAsync(rig.CreateProject(), """{"name":"فروشگاه","slug":"forushgah"}""");
        withSlug.Ok.Should().BeTrue();
        withSlug.TitleFa.Should().Be("ساخت پروژهٔ فروشگاه");
        withSlug.Creates!.Name.Should().Be("forushgah");
    }

    [Theory]
    [InlineData("""{"slug":"x"}""", "A project needs a name.", "پروژه به نام نیاز دارد.")]
    [InlineData("""{"name":"Again","slug":"__TAKEN__"}""", "already exists", "از قبل وجود دارد")]
    public async Task Create_project_refuses_a_missing_name_and_a_slug_already_taken(string json, string en, string fa)
    {
        using var rig = new DatabaseToolRig();

        var verdict = await rig.ValidateAsync(rig.CreateProject(), json.Replace("__TAKEN__", rig.ProjectSlug));

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain(en);
        verdict.ErrorFa.Should().Contain(fa);
    }

    [Fact]
    public async Task Create_project_maps_a_quota_refusal_in_both_languages()
    {
        using var rig = new DatabaseToolRig();
        rig.Quota.GovernedCheck = QuotaCheck.Deny("Project limit reached (3).", "سقف پروژه‌ها پر شده است (۳).");

        var verdict = await rig.ValidateAsync(rig.CreateProject(), """{"name":"Shop","slug":"shop"}""");

        verdict.Should().BeEquivalentTo(new { Ok = false, Error = "Project limit reached (3).", ErrorFa = "سقف پروژه‌ها پر شده است (۳)." });
    }

    [Fact]
    public async Task Create_project_refuses_a_second_step_creating_the_same_project()
    {
        using var rig = new DatabaseToolRig();
        var scope = DatabaseToolRig.Planning(new AgentPlannedResource(AgentResourceKinds.Project, "shop"));

        var verdict = await rig.ValidateAsync(rig.CreateProject(), """{"name":"Shop","slug":"shop"}""", scope);

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("already creates project 'shop'");
    }

    [Theory]
    [InlineData(SystemRole.Viewer, false)]
    [InlineData(SystemRole.Member, true)]
    public async Task A_viewer_or_a_scoped_member_may_not_create_a_project(SystemRole role, bool scoped)
    {
        using var rig = new DatabaseToolRig(role, scoped);

        var verdict = await rig.ValidateAsync(rig.CreateProject(), """{"name":"Shop","slug":"shop"}""");
        var result = await rig.ExecuteAsync(rig.CreateProject(), """{"name":"Shop","slug":"shop"}""");

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Be("You are not allowed to create projects in this workspace.");
        verdict.ErrorFa.Should().Be("اجازهٔ ساختن پروژه در این فضای کاری را ندارید.");
        result.Ok.Should().BeFalse();
        (await rig.Db.Projects.AnyAsync(p => p.Slug == "shop")).Should().BeFalse();
    }

    [Fact]
    public async Task Create_project_executes_through_the_project_service_and_links_the_new_project()
    {
        using var rig = new DatabaseToolRig();

        var result = await rig.ExecuteAsync(rig.CreateProject(), """{"name":"Shop","slug":"shop"}""");

        result.Ok.Should().BeTrue(result.Error);
        var project = await rig.Db.Projects.Include(p => p.Environments).SingleAsync(p => p.Slug == "shop");
        project.Environments.Should().ContainSingle(e => e.Slug == "production" && e.IsDefault);
        result.SummaryEn.Should().Be("Created project Shop (shop) with environment production.");
        result.SummaryFa.Should().Be("پروژهٔ Shop (shop) با محیط production ساخته شد.");
        result.Links.Should().ContainSingle(l => l.Url == $"/projects/{project.Id}" && l.Kind == "project");
        result.ResultJson.Should().Contain("\"project\":\"shop\"");
        rig.H.Audit.Entries.Should().ContainSingle(e => e.Action == "project.created" && e.TargetId == project.Id.ToString());
        rig.PendingChanges.Should().Be(0, "the tool saves its own work before it returns");
    }

    [Fact]
    public async Task A_slug_taken_after_validation_fails_at_execute_and_creates_nothing()
    {
        using var rig = new DatabaseToolRig();
        (await rig.ValidateAsync(rig.CreateProject(), """{"name":"Shop","slug":"shop"}""")).Ok.Should().BeTrue();
        rig.SeedProject("shop", "Someone else's shop");
        var before = rig.Counts();

        var result = await rig.ExecuteAsync(rig.CreateProject(), """{"name":"Shop","slug":"shop"}""");

        result.Ok.Should().BeFalse();
        result.ErrorFa.Should().Contain("از قبل وجود دارد");
        rig.Counts().Should().Be(before);
    }

    [Fact]
    public async Task A_slug_taken_while_the_service_runs_is_a_failure_that_names_the_slug_it_got()
    {
        using var rig = new DatabaseToolRig();
        // The tool's own check passes (call 1); before the service's check (call 2) somebody takes the slug,
        // so the service suffixes it — the project exists, but not where the later steps will look.
        rig.Quota.OnGovernedCheck = call => { if (call == 2) rig.SeedProject("shop", "Racer"); };

        var result = await rig.ExecuteAsync(rig.CreateProject(), """{"name":"Shop","slug":"shop"}""");

        result.Ok.Should().BeFalse("a project under another slug would send the later steps into the wrong one");
        result.Error.Should().Contain("created as 'shop-2'");
        result.ErrorFa.Should().Contain("shop-2");
        result.Links.Should().ContainSingle(l => l.Label == "shop-2");
    }

    // ---- add_environment -----------------------------------------------------------------------

    [Fact]
    public async Task Add_environment_validates_into_a_card_and_changes_nothing()
    {
        using var rig = new DatabaseToolRig();
        var before = rig.Counts();

        var verdict = await rig.ValidateAsync(rig.AddEnvironment(), $$"""{"project":"{{rig.ProjectSlug}}","name":"Staging"}""");

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be($"Add environment staging to {rig.ProjectSlug}");
        verdict.TitleFa.Should().Be($"افزودن محیط staging به پروژهٔ {rig.ProjectSlug}");
        verdict.DetailEn.Should().Contain("private network").And.Contain("does not become the project's default");
        verdict.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.Environment, "staging", rig.ProjectSlug));
        rig.Counts().Should().Be(before);
        rig.PendingChanges.Should().Be(0);
    }

    [Fact]
    public async Task Add_environment_accepts_a_project_an_earlier_step_creates()
    {
        using var rig = new DatabaseToolRig();
        var scope = DatabaseToolRig.Planning(new AgentPlannedResource(AgentResourceKinds.Project, "shop", null, "production"));

        var verdict = await rig.ValidateAsync(rig.AddEnvironment(), """{"project":"shop","name":"staging"}""", scope);
        var duplicate = await rig.ValidateAsync(rig.AddEnvironment(), """{"project":"shop","name":"production"}""", scope);

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.DetailEn.Should().Contain("created by an earlier step");
        verdict.DetailFa.Should().Contain("مراحل قبلی");
        verdict.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.Environment, "staging", "shop"));
        duplicate.Ok.Should().BeFalse("the planned project already comes with production");
    }

    [Theory]
    [InlineData("""{"project":"__P__"}""", "Say what the environment is called")]
    [InlineData("""{"project":"__P__","name":"استیجینگ"}""", "name in Latin letters")]
    [InlineData("""{"project":"nowhere","name":"staging"}""", "No project named 'nowhere'")]
    [InlineData("""{"project":"__P__","name":"Production"}""", "already has an environment 'production'")]
    public async Task Add_environment_refuses_what_it_cannot_add(string json, string expected)
    {
        using var rig = new DatabaseToolRig();

        var verdict = await rig.ValidateAsync(rig.AddEnvironment(), json.Replace("__P__", rig.ProjectSlug));

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain(expected);
        verdict.ErrorFa.Should().NotBeNullOrWhiteSpace().And.NotBe(verdict.Error);
    }

    [Fact]
    public async Task Add_environment_maps_a_quota_refusal()
    {
        using var rig = new DatabaseToolRig();
        rig.Quota.GovernedCheck = QuotaCheck.Deny("Environment limit reached (2).", "سقف محیط‌ها پر شده است.");

        var verdict = await rig.ValidateAsync(rig.AddEnvironment(), $$"""{"project":"{{rig.ProjectSlug}}","name":"staging"}""");

        verdict.Should().BeEquivalentTo(new { Ok = false, Error = "Environment limit reached (2).", ErrorFa = "سقف محیط‌ها پر شده است." });
    }

    [Fact]
    public async Task A_viewer_is_refused_and_a_scoped_member_without_a_grant_does_not_see_the_project()
    {
        using var viewer = new DatabaseToolRig(SystemRole.Viewer);
        using var scoped = new DatabaseToolRig(SystemRole.Member, scopedToProjects: true);

        var byViewer = await viewer.ValidateAsync(viewer.AddEnvironment(), $$"""{"project":"{{viewer.ProjectSlug}}","name":"staging"}""");
        var byScoped = await scoped.ValidateAsync(scoped.AddEnvironment(), $$"""{"project":"{{scoped.ProjectSlug}}","name":"staging"}""");

        byViewer.Error.Should().Be($"You are not allowed to add environments to project '{viewer.ProjectSlug}'.");
        byScoped.Error.Should().Be($"No project named '{scoped.ProjectSlug}' that you can see.");
    }

    [Fact]
    public async Task Another_workspaces_project_is_not_found()
    {
        using var rig = new DatabaseToolRig();
        // Its own slug: the harness's project slugs come from a v7 id's timestamp prefix, so two harnesses made
        // in the same few hours share one.
        using var other = new DatabaseToolRig(rig.H.Another(sameWorkspace: false));
        other.SeedProject("theirs", "Theirs");

        var adding = await rig.ValidateAsync(rig.AddEnvironment(), """{"project":"theirs","name":"staging"}""");
        var deleting = await rig.ValidateAsync(rig.DeleteProject(), """{"project":"theirs"}""");
        var creating = await rig.ValidateAsync(rig.CreateDatabase(), """{"name":"x-db","engine":"redis","project":"theirs"}""");

        adding.Error.Should().Be("No project named 'theirs' that you can see.");
        deleting.Error.Should().Be("No project named 'theirs' that you can see.");
        creating.Error.Should().Be("No project named 'theirs' that you can see.");
    }

    [Fact]
    public async Task Add_environment_executes_and_links_the_new_environment()
    {
        using var rig = new DatabaseToolRig();

        var result = await rig.ExecuteAsync(rig.AddEnvironment(), $$"""{"project":"{{rig.ProjectSlug}}","name":"staging"}""");

        result.Ok.Should().BeTrue(result.Error);
        var environment = await rig.Db.Environments.SingleAsync(e => e.ProjectId == rig.H.ProjectId && e.Slug == "staging");
        environment.IsDefault.Should().BeFalse();
        result.Links.Should().ContainSingle(l => l.Url == $"/projects/{rig.H.ProjectId}?environmentId={environment.Id}");
        result.SummaryFa.Should().Be($"محیط staging به پروژهٔ {rig.ProjectSlug} اضافه شد.");
        rig.H.Audit.Entries.Should().ContainSingle(e => e.Action == "environment.created");
    }

    [Fact]
    public async Task An_environment_added_after_validation_fails_at_execute_and_nothing_is_added()
    {
        using var rig = new DatabaseToolRig();
        var json = $$"""{"project":"{{rig.ProjectSlug}}","name":"staging"}""";
        (await rig.ValidateAsync(rig.AddEnvironment(), json)).Ok.Should().BeTrue();
        rig.SeedEnvironment(rig.H.ProjectId, "staging");
        var before = rig.Counts();

        var result = await rig.ExecuteAsync(rig.AddEnvironment(), json);

        result.Ok.Should().BeFalse();
        rig.Counts().Should().Be(before);
    }

    // ---- delete_project ------------------------------------------------------------------------

    [Fact]
    public async Task Delete_project_names_everything_that_goes_and_asks_for_the_projects_name()
    {
        using var rig = new DatabaseToolRig();
        var (project, production) = rig.SeedProject("store", "Store");
        var staging = rig.SeedEnvironment(project.Id, "staging");
        var web = rig.SeedApp("store-web", production.Id);
        rig.SeedApp("store-worker", staging.Id);
        rig.SeedDatabase("store-db", production.Id);
        rig.Db.Volumes.Add(new Volume { AppId = web.Id, Name = "store-web-data", MountPath = "/data" });
        rig.Db.Domains.Add(new DomainName { AppId = web.Id, Host = "store.example.com" });
        await rig.Db.SaveChangesAsync();
        var before = rig.Counts();

        var verdict = await rig.ValidateAsync(rig.DeleteProject(), """{"project":"store"}""");

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.ConfirmationPhrase.Should().Be("Store", "ProjectRemovalPlan.IsConfirmed compares the project's name");
        verdict.TitleEn.Should().Be("Delete project Store");
        verdict.TitleFa.Should().Be("حذف پروژهٔ Store");
        verdict.DetailEn.Should().Contain("store-web [Production, 1 volume]").And.Contain("store-worker [staging]")
            .And.Contain("Databases (1): store-db [Production]");
        verdict.DetailFa.Should().Contain("store-web").And.Contain("store-db");
        verdict.WarningsEn.Should().Contain(w => w.Contains("2 app(s) and 1 database(s)") && w.Contains("cannot be undone"));
        verdict.WarningsEn.Should().Contain(w => w.Contains("store.example.com"));
        verdict.WarningsFa.Should().HaveCount(2);
        rig.Counts().Should().Be(before);
        rig.PendingChanges.Should().Be(0);
    }

    [Fact]
    public async Task An_empty_project_says_only_its_environments_go()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedProject("empty", "Empty one");

        var verdict = await rig.ValidateAsync(rig.DeleteProject(), """{"project":"empty"}""");

        verdict.Ok.Should().BeTrue();
        verdict.ConfirmationPhrase.Should().Be("Empty one");
        verdict.DetailEn.Should().Contain("holds no apps or databases");
    }

    [Fact]
    public async Task A_project_holding_a_protected_volume_is_refused_rather_than_half_deleted()
    {
        using var rig = new DatabaseToolRig();
        var (_, production) = rig.SeedProject("store", "Store");
        var web = rig.SeedApp("store-web", production.Id);
        rig.Db.Volumes.Add(new Volume { AppId = web.Id, Name = "store-web-data", MountPath = "/data", Protected = true });
        await rig.Db.SaveChangesAsync();

        var verdict = await rig.ValidateAsync(rig.DeleteProject(), """{"project":"store"}""");

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("store-web").And.Contain("protected volume");
        verdict.ErrorFa.Should().Contain("محافظت‌شده");
    }

    [Fact]
    public async Task A_viewer_may_not_delete_a_project_and_a_scoped_member_does_not_see_it()
    {
        using var viewer = new DatabaseToolRig(SystemRole.Viewer);
        using var scoped = new DatabaseToolRig(SystemRole.Member, scopedToProjects: true);

        (await viewer.ValidateAsync(viewer.DeleteProject(), $$"""{"project":"{{viewer.ProjectSlug}}"}""")).Error
            .Should().Be($"You are not allowed to delete project '{viewer.ProjectSlug}'.");
        (await scoped.ValidateAsync(scoped.DeleteProject(), $$"""{"project":"{{scoped.ProjectSlug}}"}""")).Error
            .Should().Be($"No project named '{scoped.ProjectSlug}' that you can see.");
        (await viewer.Db.Projects.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Delete_project_removes_everything_and_writes_the_panels_audit_entry()
    {
        using var rig = new DatabaseToolRig();
        var (project, production) = rig.SeedProject("store", "Store");
        rig.SeedApp("store-web", production.Id);
        rig.SeedDatabase("store-db", production.Id);

        var result = await rig.ExecuteAsync(rig.DeleteProject(), """{"project":"store"}""");

        result.Ok.Should().BeTrue(result.Error);
        (await rig.Db.Projects.AnyAsync(p => p.Id == project.Id)).Should().BeFalse();
        (await rig.Db.Apps.AnyAsync(a => a.Slug == "store-web")).Should().BeFalse();
        rig.Engine.Removed.Should().ContainSingle(r => r.DeleteData);
        result.SummaryEn.Should().Be("Deleted project Store with 1 app(s) and 1 database(s).");
        result.SummaryFa.Should().Be("پروژهٔ Store همراه با 1 اپ و 1 دیتابیس حذف شد.");
        rig.H.Audit.Entries.Should().ContainSingle(e => e.Action == "project.deleted" && e.TargetId == project.Id.ToString());
    }

    [Fact]
    public async Task A_volume_protected_after_validation_stops_the_delete_before_anything_goes()
    {
        using var rig = new DatabaseToolRig();
        var (project, production) = rig.SeedProject("store", "Store");
        var web = rig.SeedApp("store-web", production.Id);
        rig.SeedDatabase("store-db", production.Id);
        (await rig.ValidateAsync(rig.DeleteProject(), """{"project":"store"}""")).Ok.Should().BeTrue();
        rig.Db.Volumes.Add(new Volume { AppId = web.Id, Name = "store-web-data", MountPath = "/data", Protected = true });
        await rig.Db.SaveChangesAsync();
        var before = rig.Counts();

        var result = await rig.ExecuteAsync(rig.DeleteProject(), """{"project":"store"}""");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("protected volume");
        rig.Counts().Should().Be(before);
        rig.AppOps.Deleted.Should().BeEmpty();
        (await rig.Db.Projects.AnyAsync(p => p.Id == project.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_project_that_is_only_partly_deleted_is_a_failure_naming_what_is_left()
    {
        using var rig = new DatabaseToolRig();
        var (project, production) = rig.SeedProject("store", "Store");
        var stubborn = rig.SeedApp("store-web", production.Id);
        rig.SeedDatabase("store-db", production.Id);
        rig.AppOps.Refuses.Add(stubborn.Id);

        var result = await rig.ExecuteAsync(rig.DeleteProject(), """{"project":"store"}""");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("could not be fully deleted").And.Contain("store-web");
        result.ErrorFa.Should().Contain("store-web");
        (await rig.Db.Projects.AnyAsync(p => p.Id == project.Id)).Should().BeTrue();
        rig.H.Audit.Entries.Should().NotContain(e => e.Action == "project.deleted");
    }
}
