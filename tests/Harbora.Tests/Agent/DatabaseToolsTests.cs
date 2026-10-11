using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The database tools — create, attach, detach, rotate, stop, start, delete — called directly over the real
/// provisioning and attachment services. Each: the card, the refusals it maps, the planned-resource path where
/// it has one, who may, another workspace's database, validation changing nothing, and execution reporting
/// success only for work that happened (and nothing when the world changed after validation).
/// </summary>
public class DatabaseToolsTests
{
    private static string Create(string project, string extra = "") =>
        $$"""{"name":"shop-db","engine":"postgresql","version":"16","project":"{{project}}"{{extra}}}""";

    // ---- create_database -----------------------------------------------------------------------

    [Fact]
    public async Task Create_database_validates_into_a_card_and_writes_takes_and_queues_nothing()
    {
        using var rig = new DatabaseToolRig();
        var before = rig.Counts();

        var verdict = await rig.ValidateAsync(rig.CreateDatabase(), Create(rig.ProjectSlug));

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Create database shop-db");
        verdict.TitleFa.Should().Be("ساخت دیتابیس shop-db");
        verdict.DetailEn.Should().Be($"PostgreSQL 16 in {rig.ProjectSlug}/production, no size limit.");
        verdict.DetailFa.Should().Be($"پستگرس نسخهٔ 16 در {rig.ProjectSlug}/production، بدون سقف منابع.");
        verdict.WarningsEn.Should().ContainSingle().Which.Should().Contain("Provisioning is queued");
        verdict.WarningsFa.Should().ContainSingle();
        verdict.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.Database, "shop-db", rig.ProjectSlug, "production"));
        rig.Counts().Should().Be(before, "no row, no lock, no provision");
        rig.PendingChanges.Should().Be(0);
    }

    [Fact]
    public async Task Create_database_names_the_size_and_warns_about_the_first_hour_when_billing_is_on()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedSize("small");
        rig.Billing.Enabled = true;

        var verdict = await rig.ValidateAsync(rig.CreateDatabase(), Create(rig.ProjectSlug, ""","size":"Small" """));

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.DetailEn.Should().Contain("size small (1 CPU, 1024 MB memory)");
        verdict.WarningsEn.Should().Contain(w => w.Contains("first hour is charged"));
        verdict.WarningsFa.Should().Contain(w => w.Contains("ساعت اول"));
    }

    [Theory]
    [InlineData("""{"name":"shop-db","engine":"oracle","project":"__P__"}""", "Unknown engine 'oracle'. Offered: postgresql, redis, mongodb.")]
    [InlineData("""{"name":"shop-db","engine":"postgres","version":"9","project":"__P__"}""", "PostgreSQL is not offered in version '9'. Offered: 16, 15.")]
    [InlineData("""{"name":"shop-db","engine":"postgresql","size":"huge","project":"__P__"}""", "There is no instance size 'huge'.")]
    [InlineData("""{"name":"shop-db","engine":"postgresql","redisEvictionPolicy":"allkeys-lru","project":"__P__"}""", "apply only to Redis")]
    [InlineData("""{"name":"shop-db","engine":"postgresql"}""", "Say which project")]
    [InlineData("""{"name":"shop-db","engine":"postgresql","project":"__P__","environment":"staging"}""", "has no environment 'staging'")]
    [InlineData("""{"engine":"postgresql","project":"__P__"}""", "Say what the database is called")]
    public async Task Create_database_refuses_what_the_catalogue_and_the_workspace_do_not_have(string json, string expected)
    {
        using var rig = new DatabaseToolRig();

        var verdict = await rig.ValidateAsync(rig.CreateDatabase(), json.Replace("__P__", rig.ProjectSlug));

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain(expected);
        verdict.ErrorFa.Should().NotBeNullOrWhiteSpace().And.NotBe(verdict.Error);
    }

    [Fact]
    public async Task Create_database_passes_on_the_services_own_refusals()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("shop-db");

        var taken = await rig.ValidateAsync(rig.CreateDatabase(), Create(rig.ProjectSlug));
        rig.Quota.ServiceCheck = QuotaCheck.Deny("Service limit reached (2).", "سقف سرویس‌ها پر شده است (۲).");
        var quota = await rig.ValidateAsync(rig.CreateDatabase(),
            $$"""{"name":"other-db","engine":"redis","project":"{{rig.ProjectSlug}}"}""");

        taken.Should().BeEquivalentTo(new { Ok = false, Error = "A service with this name already exists.", ErrorFa = "سرویسی با این نام از قبل وجود دارد." });
        quota.Should().BeEquivalentTo(new { Ok = false, Error = "Service limit reached (2).", ErrorFa = "سقف سرویس‌ها پر شده است (۲)." });
    }

    [Fact]
    public async Task Create_database_accepts_a_project_or_an_environment_an_earlier_step_creates()
    {
        using var rig = new DatabaseToolRig();
        var scope = DatabaseToolRig.Planning(
            new AgentPlannedResource(AgentResourceKinds.Project, "shop", null, "production"),
            new AgentPlannedResource(AgentResourceKinds.Environment, "staging", rig.ProjectSlug));
        var before = rig.Counts();

        var inPlannedProject = await rig.ValidateAsync(rig.CreateDatabase(), Create("shop"), scope);
        var inPlannedEnvironment = await rig.ValidateAsync(rig.CreateDatabase(),
            $$"""{"name":"stage-db","engine":"postgresql","project":"{{rig.ProjectSlug}}","environment":"staging"}""", scope);
        var nowhere = await rig.ValidateAsync(rig.CreateDatabase(), Create("shop", ""","environment":"qa" """), scope);

        inPlannedProject.Ok.Should().BeTrue(inPlannedProject.Error);
        inPlannedProject.DetailEn.Should().Contain("PostgreSQL 16 in shop/production").And.Contain("created by an earlier step");
        inPlannedProject.DetailFa.Should().Contain("مراحل قبلی");
        inPlannedProject.Creates.Should().Be(new AgentPlannedResource(AgentResourceKinds.Database, "shop-db", "shop", "production"));
        inPlannedEnvironment.Ok.Should().BeTrue(inPlannedEnvironment.Error);
        inPlannedEnvironment.Creates!.Environment.Should().Be("staging");
        nowhere.Ok.Should().BeFalse();
        nowhere.Error.Should().Contain("has no environment 'qa'");
        rig.Counts().Should().Be(before);
    }

    [Fact]
    public async Task Create_database_refuses_a_name_an_earlier_step_already_creates()
    {
        using var rig = new DatabaseToolRig();
        var scope = DatabaseToolRig.Planning(new AgentPlannedResource(AgentResourceKinds.Database, "Shop DB", rig.ProjectSlug, "production"));

        var verdict = await rig.ValidateAsync(rig.CreateDatabase(), Create(rig.ProjectSlug), scope);

        verdict.Ok.Should().BeFalse("'Shop DB' and 'shop-db' would share one container name");
        verdict.Error.Should().Contain("already creates a database");
    }

    [Theory]
    [InlineData(SystemRole.Viewer, false, "You do not have permission to create a database in this environment.")]
    [InlineData(SystemRole.Member, true, "No project named")]
    public async Task A_viewer_or_a_scoped_member_without_a_grant_cannot_create_a_database(SystemRole role, bool scoped, string expected)
    {
        using var rig = new DatabaseToolRig(role, scoped);

        var verdict = await rig.ValidateAsync(rig.CreateDatabase(), Create(rig.ProjectSlug));
        var result = await rig.ExecuteAsync(rig.CreateDatabase(), Create(rig.ProjectSlug));

        verdict.Error.Should().Contain(expected);
        result.Ok.Should().BeFalse();
        (await rig.Db.ManagedServices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_database_executes_in_the_environment_asked_for_and_returns_no_secret()
    {
        using var rig = new DatabaseToolRig();

        var result = await rig.ExecuteAsync(rig.CreateDatabase(), Create(rig.ProjectSlug));

        result.Ok.Should().BeTrue(result.Error);
        var service = await rig.Db.ManagedServices.SingleAsync();
        service.EnvironmentId.Should().Be(rig.H.EnvironmentId);
        service.Status.Should().Be(ServiceStatus.Provisioning);
        rig.Engine.Provisioned.Should().Equal(service.Id);
        rig.H.Audit.Entries.Should().ContainSingle(e => e.Action == "database.created");
        result.Links.Should().ContainSingle(l => l.Url == $"/databases/{service.Id}" && l.Label == "shop-db");
        result.SummaryEn.Should().Be($"Created database shop-db (PostgreSQL 16) in {rig.ProjectSlug}/production; provisioning is queued.");
        result.SummaryFa.Should().Contain("در صف");

        var password = rig.H.Protector.Unprotect(service.EncryptedPassword);
        string[] everything = [result.ResultJson, result.SummaryEn!, result.SummaryFa!];
        everything.Should().NotContain(text => text.Contains(password) || text.Contains(service.EncryptedPassword));
        result.ResultJson.Should().Contain("provisioning queued").And.NotContain("running");
    }

    [Fact]
    public async Task A_name_taken_after_validation_fails_at_execute_and_creates_nothing()
    {
        using var rig = new DatabaseToolRig();
        (await rig.ValidateAsync(rig.CreateDatabase(), Create(rig.ProjectSlug))).Ok.Should().BeTrue();
        rig.SeedDatabase("shop-db");
        var before = rig.Counts();

        var result = await rig.ExecuteAsync(rig.CreateDatabase(), Create(rig.ProjectSlug));

        result.Ok.Should().BeFalse();
        result.ErrorFa.Should().Be("سرویسی با این نام از قبل وجود دارد.");
        rig.Counts().Should().Be(before with { Locks = before.Locks + 1 }, "only the creation lock was taken");
    }

    [Fact]
    public async Task A_create_the_wallet_cannot_pay_for_validates_and_then_fails_at_execute_leaving_nothing()
    {
        using var rig = new DatabaseToolRig();
        rig.Billing.Enabled = true; // no size: the first hour has no price, which billing refuses at creation

        var verdict = await rig.ValidateAsync(rig.CreateDatabase(), Create(rig.ProjectSlug));
        var result = await rig.ExecuteAsync(rig.CreateDatabase(), Create(rig.ProjectSlug));

        verdict.Ok.Should().BeTrue("validation cannot ask billing");
        result.Ok.Should().BeFalse();
        result.ErrorFa.Should().NotBeNullOrWhiteSpace().And.NotBe(result.Error);
        (await rig.Db.ManagedServices.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        rig.Engine.Provisioned.Should().BeEmpty();
    }

    [Fact]
    public async Task A_database_that_lands_in_another_environment_is_a_failure_with_its_link()
    {
        using var rig = new DatabaseToolRig();
        var elsewhere = Guid.CreateVersion7();
        var tool = rig.CreateDatabase(new LandsElsewhere(elsewhere));

        var result = await rig.ExecuteAsync(tool, Create(rig.ProjectSlug));

        result.Ok.Should().BeFalse("the service fell back to another environment than the one asked for");
        result.Error.Should().Contain($"instead of {rig.ProjectSlug}/production");
        result.ErrorFa.Should().Contain("به‌جای");
        result.Links.Should().ContainSingle(l => l.Kind == "database");
    }

    // ---- attach_database -----------------------------------------------------------------------

    [Fact]
    public async Task Attach_validates_into_a_card_with_the_alias_and_writes_no_attachment()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("shop-db");
        rig.SeedApp("web");
        var before = rig.Counts();

        var verdict = await rig.ValidateAsync(rig.Attach(), """{"database":"shop-db","app":"web"}""");

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Attach database shop-db to web");
        verdict.TitleFa.Should().Be("اتصال دیتابیس shop-db به اپ web");
        verdict.DetailEn.Should().Contain("prefixed SHOP_DB_").And.Contain("logical database shop_db");
        verdict.WarningsEn.Should().ContainSingle().Which.Should().Contain("next deploy");
        verdict.WarningsFa.Should().ContainSingle().Which.Should().Contain("استقرار بعدی");
        verdict.Creates.Should().BeNull();
        rig.Counts().Should().Be(before);
        rig.PendingChanges.Should().Be(0);
    }

    [Fact]
    public async Task Attach_passes_on_the_services_refusals_and_its_own()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        var web = rig.SeedApp("web");
        var (_, otherProduction) = rig.SeedProject("other");
        rig.SeedApp("faraway", otherProduction.Id);
        rig.SeedDatabase("shop-replica", primaryId: shop.Id);
        rig.AttachRow(web, shop);
        rig.SeedApp("api");

        (await rig.ValidateAsync(rig.Attach(), """{"database":"shop-db","app":"web"}""")).ErrorFa
            .Should().Be("این دیتابیس از قبل به این اپ متصل است.");
        (await rig.ValidateAsync(rig.Attach(), """{"database":"shop-db","app":"faraway"}""")).Error
            .Should().Contain("different environments");
        (await rig.ValidateAsync(rig.Attach(), """{"database":"shop-replica","app":"api"}""")).Error
            .Should().Contain("read-only replica");
        (await rig.ValidateAsync(rig.Attach(), """{"database":"shop-db","app":"api","logicalDatabase":"reports"}""")).Error
            .Should().Be("Database shop-db has no logical database 'reports'. It has: shop_db.");
        (await rig.ValidateAsync(rig.Attach(), """{"database":"nope","app":"api"}""")).Error
            .Should().Be("No database named 'nope' that you can see.");
        (await rig.ValidateAsync(rig.Attach(), """{"database":"shop-db","app":"nope"}""")).Error
            .Should().Be("No app named 'nope' that you can see.");
    }

    [Fact]
    public async Task Attach_accepts_a_database_an_earlier_step_creates_in_the_apps_environment_only()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedApp("web");
        var sameNetwork = DatabaseToolRig.Planning(new AgentPlannedResource(AgentResourceKinds.Database, "shop-db", rig.ProjectSlug, "production"));
        var otherNetwork = DatabaseToolRig.Planning(new AgentPlannedResource(AgentResourceKinds.Database, "shop-db", "shop", "production"));

        var accepted = await rig.ValidateAsync(rig.Attach(), """{"database":"shop-db","app":"web"}""", sameNetwork);
        var apart = await rig.ValidateAsync(rig.Attach(), """{"database":"shop-db","app":"web"}""", otherNetwork);

        accepted.Ok.Should().BeTrue(accepted.Error);
        accepted.DetailEn.Should().Contain("prefixed SHOP_DB_").And.Contain("created by an earlier step");
        apart.Ok.Should().BeFalse();
        apart.Error.Should().Be($"App web is in {rig.ProjectSlug}/production, but database shop-db is in shop/production. Each environment is its own private network, so the app could not reach it; attach a database in the app's own environment.");
        apart.ErrorFa.Should().Contain("شبکهٔ خصوصی");
    }

    [Fact]
    public async Task Attach_accepts_an_app_an_earlier_step_creates_and_still_refuses_a_replica()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        rig.SeedDatabase("shop-replica", primaryId: shop.Id);
        var scope = DatabaseToolRig.Planning(new AgentPlannedResource(AgentResourceKinds.App, "web", rig.ProjectSlug, "production"));

        var accepted = await rig.ValidateAsync(rig.Attach(), """{"database":"shop-db","app":"web"}""", scope);
        var replica = await rig.ValidateAsync(rig.Attach(), """{"database":"shop-replica","app":"web"}""", scope);
        var both = await rig.ValidateAsync(rig.Attach(), """{"database":"new-db","app":"web"}""",
            DatabaseToolRig.Planning(
                new AgentPlannedResource(AgentResourceKinds.Database, "new-db", rig.ProjectSlug, "production"),
                new AgentPlannedResource(AgentResourceKinds.App, "web", rig.ProjectSlug, "production")));

        accepted.Ok.Should().BeTrue(accepted.Error);
        accepted.DetailEn.Should().Contain("App 'web' is created by an earlier step");
        replica.Ok.Should().BeFalse();
        replica.Error.Should().Contain("read-only replica");
        both.Ok.Should().BeTrue(both.Error);
    }

    [Fact]
    public async Task A_viewer_cannot_attach_and_a_scoped_member_without_a_grant_sees_neither_side()
    {
        using var viewer = new DatabaseToolRig(SystemRole.Viewer);
        viewer.SeedDatabase("shop-db");
        viewer.SeedApp("web");
        using var scoped = new DatabaseToolRig(SystemRole.Member, scopedToProjects: true);
        scoped.SeedDatabase("shop-db");
        scoped.SeedApp("web");

        (await viewer.ValidateAsync(viewer.Attach(), """{"database":"shop-db","app":"web"}""")).Error
            .Should().Be(DatabaseRefusals.ServiceNotFound.Message);
        (await scoped.ValidateAsync(scoped.Attach(), """{"database":"shop-db","app":"web"}""")).Error
            .Should().Be("No database named 'shop-db' that you can see.");
        (await viewer.ExecuteAsync(viewer.Attach(), """{"database":"shop-db","app":"web"}""")).Ok.Should().BeFalse();
        (await viewer.Db.AppManagedServices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Attach_executes_writes_the_attachment_and_links_both_sides()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        var web = rig.SeedApp("web");

        var result = await rig.ExecuteAsync(rig.Attach(), """{"database":"shop-db","app":"web","alias":"MAIN"}""");

        result.Ok.Should().BeTrue(result.Error);
        var row = await rig.Db.AppManagedServices.SingleAsync();
        (row.AppId, row.ManagedServiceId, row.Alias, row.HasUnpublishedChanges).Should().Be((web.Id, shop.Id, "MAIN", true));
        result.SummaryEn.Should().Be("Attached shop-db to web as MAIN_*; it takes effect on web's next deploy.");
        result.SummaryFa.Should().Be("دیتابیس shop-db با پیشوند MAIN_* به اپ web وصل شد؛ با استقرار بعدی web اعمال می‌شود.");
        result.Links!.Select(l => l.Url).Should().Equal($"/databases/{shop.Id}", $"/apps/details/{web.Id}");
        rig.H.Audit.Entries.Should().ContainSingle(e => e.Action == "database.attached");
    }

    [Fact]
    public async Task An_attachment_made_after_validation_fails_at_execute_and_adds_nothing()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        var web = rig.SeedApp("web");
        (await rig.ValidateAsync(rig.Attach(), """{"database":"shop-db","app":"web"}""")).Ok.Should().BeTrue();
        rig.AttachRow(web, shop);
        var before = rig.Counts();

        var result = await rig.ExecuteAsync(rig.Attach(), """{"database":"shop-db","app":"web"}""");

        result.Ok.Should().BeFalse();
        rig.Counts().Should().Be(before);
    }

    // ---- detach_database -----------------------------------------------------------------------

    [Fact]
    public async Task Detach_is_dangerous_and_its_card_says_the_app_loses_its_settings_on_the_next_deploy()
    {
        using var rig = new DatabaseToolRig();
        rig.AttachRow(rig.SeedApp("web"), rig.SeedDatabase("shop-db"));
        var before = rig.Counts();

        var verdict = await rig.ValidateAsync(rig.Detach(), """{"database":"shop-db","app":"web"}""");

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Detach database shop-db from web");
        verdict.TitleFa.Should().Be("جدا کردن دیتابیس shop-db از اپ web");
        verdict.DetailEn.Should().Contain("(SHOP_DB_*)").And.Contain("No data is deleted");
        verdict.WarningsEn.Should().ContainSingle().Which.Should().Contain("next deploy");
        rig.Counts().Should().Be(before);
        rig.PendingChanges.Should().Be(0);
    }

    [Fact]
    public async Task Detach_refuses_what_is_not_attached_or_not_there_and_a_viewer()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("shop-db");
        rig.SeedApp("web");
        using var viewer = new DatabaseToolRig(SystemRole.Viewer);
        viewer.AttachRow(viewer.SeedApp("web"), viewer.SeedDatabase("shop-db"));

        (await rig.ValidateAsync(rig.Detach(), """{"database":"shop-db","app":"web"}""")).Should()
            .BeEquivalentTo(new { Ok = false, Error = "This app is not attached to this database.", ErrorFa = "این اپ به این دیتابیس وصل نیست." });
        (await rig.ValidateAsync(rig.Detach(), """{"database":"shop-db","app":"web","logicalDatabase":"other"}""")).Error
            .Should().Contain("no logical database 'other'");
        (await rig.ValidateAsync(rig.Detach(), """{"database":"gone","app":"web"}""")).Error
            .Should().Be("No database named 'gone' that you can see.");
        (await viewer.ValidateAsync(viewer.Detach(), """{"database":"shop-db","app":"web"}""")).Error
            .Should().Be(DatabaseRefusals.ServiceNotFound.Message);
    }

    [Fact]
    public async Task Detach_executes_and_a_detach_made_meanwhile_fails_without_changing_anything()
    {
        using var rig = new DatabaseToolRig();
        rig.AttachRow(rig.SeedApp("web"), rig.SeedDatabase("shop-db"));

        var result = await rig.ExecuteAsync(rig.Detach(), """{"database":"shop-db","app":"web"}""");
        var again = await rig.ExecuteAsync(rig.Detach(), """{"database":"shop-db","app":"web"}""");

        result.Ok.Should().BeTrue(result.Error);
        result.SummaryFa.Should().Contain("جدا شد");
        result.ResultJson.Should().Contain("\"alias\":\"SHOP_DB\"");
        (await rig.Db.AppManagedServices.CountAsync()).Should().Be(0);
        rig.H.Audit.Entries.Should().ContainSingle(e => e.Action == "database.detached");
        again.Ok.Should().BeFalse("it was no longer attached");
        again.Error.Should().Be("This app is not attached to this database.");
    }

    // ---- rotate_database_password --------------------------------------------------------------

    [Fact]
    public async Task Rotate_names_the_apps_that_must_be_redeployed_and_warns_when_the_container_is_recreated()
    {
        using var rig = new DatabaseToolRig();
        rig.AttachRow(rig.SeedApp("web"), rig.SeedDatabase("shop-db"));
        rig.SeedDatabase("cache", type: ManagedServiceType.Redis);
        var before = rig.Counts();

        var postgres = await rig.ValidateAsync(rig.Rotate(), """{"database":"shop-db"}""");
        var redis = await rig.ValidateAsync(rig.Rotate(), """{"database":"cache"}""");

        postgres.Ok.Should().BeTrue(postgres.Error);
        postgres.TitleEn.Should().Be("Rotate the password of database shop-db");
        postgres.TitleFa.Should().Be("تعویض رمز دیتابیس shop-db");
        postgres.DetailEn.Should().Contain("never shown");
        postgres.WarningsEn.Should().ContainSingle().Which.Should().StartWith("1 app: web");
        postgres.WarningsFa.Should().ContainSingle().Which.Should().StartWith("1 اپ: web");
        redis.WarningsEn.Should().HaveCount(2).And.Contain(w => w.Contains("recreated"));
        rig.Counts().Should().Be(before);
        rig.Engine.Rotated.Should().BeEmpty();
    }

    [Fact]
    public async Task Rotate_refuses_an_engine_it_cannot_rotate_a_replica_and_a_viewer()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("docs", type: ManagedServiceType.MongoDb);
        var primary = rig.SeedDatabase("shop-db");
        rig.SeedDatabase("shop-replica", primaryId: primary.Id);
        using var viewer = new DatabaseToolRig(SystemRole.Viewer);
        viewer.SeedDatabase("shop-db");

        var mongo = await rig.ValidateAsync(rig.Rotate(), """{"database":"docs"}""");
        var replica = await rig.ValidateAsync(rig.Rotate(), """{"database":"shop-replica"}""");
        var byViewer = await viewer.ValidateAsync(viewer.Rotate(), """{"database":"shop-db"}""");

        mongo.Error.Should().Be(CredentialRotationPlan.WhyUnsupported(ManagedServiceType.MongoDb));
        mongo.ErrorFa.Should().Contain("MongoDB").And.NotBe(mongo.Error);
        replica.Error.Should().Be("shop-replica is a read replica; rotate the password of shop-db instead — its replicas follow it.");
        byViewer.Error.Should().Be(DatabaseRefusals.ServiceNotFound.Message);
    }

    [Fact]
    public async Task Rotate_executes_changes_the_stored_password_and_never_returns_it()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        var web = rig.SeedApp("web");
        var legacy = rig.SeedApp("legacy");
        rig.AttachRow(web, shop);
        rig.Engine.Rewrites.Add(new RotatedApp(legacy.Id, legacy.Name));
        var oldCipher = shop.EncryptedPassword;

        var result = await rig.ExecuteAsync(rig.Rotate(), """{"database":"shop-db"}""");

        result.Ok.Should().BeTrue(result.Error);
        (await rig.Db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == shop.Id)).EncryptedPassword.Should().NotBe(oldCipher);
        result.ResultJson.Should().Contain("\"redeploy\":[\"legacy\",\"web\"]");
        result.SummaryEn.Should().Be("Rotated the password of shop-db. Redeploy legacy, web so they use it.");
        result.SummaryFa.Should().Be("رمز دیتابیس shop-db عوض شد. legacy، web را دوباره مستقر کنید تا از رمز تازه استفاده کنند.");
        result.Links!.Select(l => l.Kind).Should().Equal("database", "app", "app");
        string[] everything = [result.ResultJson, result.SummaryEn!, result.SummaryFa!];
        everything.Should().NotContain(text => text.Contains(ToolEngine.NewPassword) || text.Contains("OldPassw0rd"));
    }

    [Fact]
    public async Task A_rotation_the_database_refused_fails_with_the_reason_redacted_and_the_password_unchanged()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        var oldPassword = rig.H.Protector.Unprotect(shop.EncryptedPassword);
        rig.Engine.RotateRefusal = $"The database refused the new password, so nothing was changed. FATAL: password authentication failed: {oldPassword}";

        var result = await rig.ExecuteAsync(rig.Rotate(), """{"database":"shop-db"}""");

        result.Ok.Should().BeFalse();
        result.Error.Should().StartWith("The password was not changed. The database refused the new password");
        result.Error.Should().NotContain(oldPassword);
        result.ErrorFa.Should().StartWith("رمز عوض نشد.").And.NotContain(oldPassword);
        (await rig.Db.ManagedServices.AsNoTracking().SingleAsync()).EncryptedPassword.Should().Be(shop.EncryptedPassword);
    }

    [Fact]
    public async Task A_rotation_that_quietly_did_nothing_is_not_reported_as_one()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("shop-db");
        rig.Engine.RotateDoesNothing = true;

        var result = await rig.ExecuteAsync(rig.Rotate(), """{"database":"shop-db"}""");

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("The password of shop-db was not changed.");
    }

    // ---- stop_database / start_database --------------------------------------------------------

    [Fact]
    public async Task Stop_warns_who_loses_the_database_and_refuses_one_already_stopped()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        rig.AttachRow(rig.SeedApp("web"), shop);
        rig.SeedDatabase("shop-replica", primaryId: shop.Id);
        rig.SeedDatabase("idle", status: ServiceStatus.Stopped);
        var before = rig.Counts();

        var verdict = await rig.ValidateAsync(rig.Stop(), """{"database":"shop-db"}""");
        var idle = await rig.ValidateAsync(rig.Stop(), """{"database":"idle"}""");

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Stop database shop-db");
        verdict.TitleFa.Should().Be("توقف دیتابیس shop-db");
        verdict.WarningsEn.Should().Equal(
            "1 app: web — they lose their database while it is stopped.",
            "Its replicas stop receiving changes: shop-replica.");
        verdict.WarningsFa.Should().HaveCount(2);
        idle.Should().BeEquivalentTo(new { Ok = false, Error = "idle is already stopped.", ErrorFa = "idle از قبل متوقف است." });
        rig.Counts().Should().Be(before);
    }

    [Fact]
    public async Task Stop_executes_and_one_stopped_meanwhile_is_refused_at_execute()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");

        var result = await rig.ExecuteAsync(rig.Stop(), """{"database":"shop-db"}""");
        var again = await rig.ExecuteAsync(rig.Stop(), """{"database":"shop-db"}""");

        result.Ok.Should().BeTrue(result.Error);
        result.SummaryFa.Should().Be("دیتابیس shop-db متوقف شد.");
        (await rig.Db.ManagedServices.AsNoTracking().SingleAsync()).Status.Should().Be(ServiceStatus.Stopped);
        rig.Engine.Stopped.Should().Equal(shop.Id);
        again.Ok.Should().BeFalse();
        rig.Engine.Stopped.Should().ContainSingle("the second call stopped nothing");
    }

    [Fact]
    public async Task A_node_that_never_answers_is_a_failure_after_the_tools_own_deadline()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("shop-db");
        rig.SeedDatabase("idle", status: ServiceStatus.Stopped);
        rig.Engine.Hang = true;

        var stop = await rig.ExecuteAsync(rig.Stop(TimeSpan.FromMilliseconds(50)), """{"database":"shop-db"}""");
        var start = await rig.ExecuteAsync(rig.Start(TimeSpan.FromMilliseconds(50)), """{"database":"idle"}""");

        stop.Ok.Should().BeFalse();
        stop.ResultJson.Should().Contain("\"timedOut\":true");
        stop.ErrorFa.Should().Contain("ثانیه");
        start.Ok.Should().BeFalse();
        start.ResultJson.Should().Contain("\"timedOut\":true");
    }

    [Fact]
    public async Task Start_is_safe_refuses_one_running_and_maps_the_billing_gate_in_both_languages()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("idle", status: ServiceStatus.Stopped);
        rig.SeedDatabase("busy");
        rig.Billing.Enabled = true;

        var verdict = await rig.ValidateAsync(rig.Start(), """{"database":"idle"}""");
        var running = await rig.ValidateAsync(rig.Start(), """{"database":"busy"}""");
        rig.BillingGate.Check = QuotaCheck.Deny("The wallet is empty.", "کیف پول خالی است.");
        var unpaid = await rig.ValidateAsync(rig.Start(), """{"database":"idle"}""");

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleFa.Should().Be("راه‌اندازی دیتابیس idle");
        verdict.WarningsEn.Should().ContainSingle().Which.Should().Contain("charged by the hour");
        running.Error.Should().Be("busy is already running.");
        unpaid.Should().BeEquivalentTo(new { Ok = false, Error = "The wallet is empty.", ErrorFa = "کیف پول خالی است." });
    }

    [Fact]
    public async Task Start_executes_and_a_refusal_from_the_engine_changes_nothing()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("idle", status: ServiceStatus.Stopped);
        rig.SeedDatabase("other", status: ServiceStatus.Stopped);

        var started = await rig.ExecuteAsync(rig.Start(), """{"database":"idle"}""");
        rig.Engine.StartRefusal = QuotaCheck.Deny("Suspended by the provider.", "توسط مدیر پلتفرم معلق شده است.");
        var refused = await rig.ExecuteAsync(rig.Start(), """{"database":"other"}""");

        started.Ok.Should().BeTrue(started.Error);
        started.SummaryEn.Should().Be("Started idle.");
        started.SummaryFa.Should().Be("دیتابیس idle راه‌اندازی شد.");
        refused.Should().BeEquivalentTo(new { Ok = false, Error = "Suspended by the provider.", ErrorFa = "توسط مدیر پلتفرم معلق شده است." });
        (await rig.Db.ManagedServices.AsNoTracking().SingleAsync(s => s.Name == "other")).Status.Should().Be(ServiceStatus.Stopped);
    }

    // ---- delete_database -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_database_says_plainly_whether_the_data_goes_and_asks_for_the_exact_name()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("shop-db");
        var before = rig.Counts();

        var keep = await rig.ValidateAsync(rig.DeleteDatabase(), """{"database":"shop-db"}""");
        var wipe = await rig.ValidateAsync(rig.DeleteDatabase(), """{"database":"SHOP-DB","deleteData":true}""");

        keep.Ok.Should().BeTrue(keep.Error);
        keep.ConfirmationPhrase.Should().Be("shop-db");
        keep.TitleEn.Should().Be("Delete database shop-db, keeping its data");
        keep.DetailEn.Should().Contain("is kept on the server");
        keep.WarningsEn.Should().ContainSingle().Which.Should().Contain("harbora-svc-shop-db-data");
        wipe.ConfirmationPhrase.Should().Be("shop-db", "the phrase is the database's exact name, not the words the model used");
        wipe.TitleFa.Should().Be("حذف دیتابیس shop-db همراه با داده‌هایش");
        wipe.WarningsEn.Should().ContainSingle().Which.Should().Contain("deleted for good");
        wipe.WarningsFa.Should().ContainSingle().Which.Should().Contain("برای همیشه");
        rig.Counts().Should().Be(before);
    }

    [Fact]
    public async Task Delete_database_passes_on_the_services_structural_refusals_and_refuses_a_viewer()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        rig.AttachRow(rig.SeedApp("web"), shop);
        var primary = rig.SeedDatabase("main-db");
        rig.SeedDatabase("main-replica", primaryId: primary.Id);
        using var viewer = new DatabaseToolRig(SystemRole.Viewer);
        viewer.SeedDatabase("shop-db");

        var attached = await rig.ValidateAsync(rig.DeleteDatabase(), """{"database":"shop-db"}""");
        var replicated = await rig.ValidateAsync(rig.DeleteDatabase(), """{"database":"main-db"}""");

        attached.Error.Should().Be("This database is still attached to 1 app: web. Detach it from every app first, then delete it.");
        attached.ErrorFa.Should().Contain("متصل است");
        replicated.Error.Should().Contain("main-replica");
        (await viewer.ValidateAsync(viewer.DeleteDatabase(), """{"database":"shop-db"}""")).Error
            .Should().Be(DatabaseRefusals.ServiceNotFound.Message);
    }

    [Fact]
    public async Task Delete_database_executes_with_the_data_choice_it_was_given()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");

        var result = await rig.ExecuteAsync(rig.DeleteDatabase(), """{"database":"shop-db"}""");

        result.Ok.Should().BeTrue(result.Error);
        rig.Engine.Removed.Should().Equal((shop.Id, false));
        (await rig.Db.ManagedServices.CountAsync()).Should().Be(0);
        result.SummaryEn.Should().Be("Deleted shop-db; its data is kept on the server in volume harbora-svc-shop-db-data.");
        result.SummaryFa.Should().Be("دیتابیس shop-db حذف شد؛ داده‌هایش در والیوم harbora-svc-shop-db-data روی سرور مانده است.");
        result.ResultJson.Should().Contain("\"dataDeleted\":false");
        rig.H.Audit.Entries.Should().ContainSingle(e => e.Action == "database.removed");
    }

    [Fact]
    public async Task A_database_attached_after_validation_is_not_deleted()
    {
        using var rig = new DatabaseToolRig();
        var shop = rig.SeedDatabase("shop-db");
        (await rig.ValidateAsync(rig.DeleteDatabase(), """{"database":"shop-db","deleteData":true}""")).Ok.Should().BeTrue();
        rig.AttachRow(rig.SeedApp("web"), shop);

        var result = await rig.ExecuteAsync(rig.DeleteDatabase(), """{"database":"shop-db","deleteData":true}""");

        result.Ok.Should().BeFalse();
        rig.Engine.Removed.Should().BeEmpty();
        (await rig.Db.ManagedServices.CountAsync()).Should().Be(1);
    }

    // ---- every tool: validation changes nothing; who may not -----------------------------------

    [Theory]
    [InlineData("create_project", """{"name":"Shop","slug":"shop"}""")]
    [InlineData("add_environment", """{"project":"__P__","name":"staging"}""")]
    [InlineData("create_database", """{"name":"new-db","engine":"postgresql","project":"__P__"}""")]
    [InlineData("attach_database", """{"database":"shop-db","app":"web"}""")]
    [InlineData("detach_database", """{"database":"linked-db","app":"web"}""")]
    [InlineData("rotate_database_password", """{"database":"shop-db"}""")]
    [InlineData("stop_database", """{"database":"shop-db"}""")]
    [InlineData("start_database", """{"database":"idle"}""")]
    [InlineData("delete_database", """{"database":"idle","deleteData":true}""")]
    [InlineData("delete_project", """{"project":"store"}""")]
    public async Task Every_tools_validation_accepts_a_sound_step_and_leaves_nothing_behind(string tool, string json)
    {
        using var rig = new DatabaseToolRig();
        var web = rig.SeedApp("web");
        rig.SeedDatabase("shop-db");
        rig.SeedDatabase("idle", status: ServiceStatus.Stopped);
        rig.AttachRow(web, rig.SeedDatabase("linked-db"));
        var (_, storeProduction) = rig.SeedProject("store", "Store");
        rig.SeedApp("store-web", storeProduction.Id);
        rig.SeedDatabase("store-db", storeProduction.Id);
        var before = rig.Counts();

        var verdict = await rig.ValidateAsync(rig.All().Single(t => t.Name == tool), json.Replace("__P__", rig.ProjectSlug));

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().NotBeNullOrWhiteSpace();
        verdict.TitleFa.Should().NotBeNullOrWhiteSpace().And.NotBe(verdict.TitleEn);
        verdict.DetailFa.Should().NotBeNullOrWhiteSpace().And.NotBe(verdict.DetailEn);
        (verdict.WarningsFa?.Count ?? 0).Should().Be(verdict.WarningsEn?.Count ?? 0, "every warning in both languages");
        rig.Counts().Should().Be(before);
        rig.PendingChanges.Should().Be(0);
    }

    [Theory]
    [InlineData("detach_database", """{"database":"shop-db","app":"web"}""")]
    [InlineData("rotate_database_password", """{"database":"shop-db"}""")]
    [InlineData("stop_database", """{"database":"shop-db"}""")]
    [InlineData("start_database", """{"database":"idle"}""")]
    [InlineData("delete_database", """{"database":"idle"}""")]
    public async Task A_viewer_or_a_scoped_member_without_a_grant_cannot_touch_a_database(string tool, string json)
    {
        foreach (var (role, scoped) in new[] { (SystemRole.Viewer, false), (SystemRole.Member, true) })
        {
            using var rig = new DatabaseToolRig(role, scoped);
            var shop = rig.SeedDatabase("shop-db");
            rig.SeedDatabase("idle", status: ServiceStatus.Stopped);
            rig.AttachRow(rig.SeedApp("web"), shop);
            var before = rig.Counts();
            var named = json.Contains("idle") ? "idle" : "shop-db";

            var verdict = await rig.ValidateAsync(rig.All().Single(t => t.Name == tool), json);
            var result = await rig.ExecuteAsync(rig.All().Single(t => t.Name == tool), json);

            verdict.Ok.Should().BeFalse($"{role} scoped={scoped}");
            verdict.Error.Should().Be(scoped
                ? $"No database named '{named}' that you can see."
                : DatabaseRefusals.ServiceNotFound.Message, "seeing is not touching, and not seeing reads as not there");
            result.Ok.Should().BeFalse();
            rig.Counts().Should().Be(before);
        }
    }

    [Fact]
    public async Task Stop_and_start_leave_a_database_that_is_still_being_provisioned_alone()
    {
        using var rig = new DatabaseToolRig();
        rig.SeedDatabase("fresh", status: ServiceStatus.Provisioning);

        var stop = await rig.ValidateAsync(rig.Stop(), """{"database":"fresh"}""");
        var start = await rig.ValidateAsync(rig.Start(), """{"database":"fresh"}""");

        stop.Error.Should().Be("fresh is not running (it is provisioning), so there is nothing to stop.");
        start.Error.Should().Be("fresh is still being provisioned; it starts by itself when that finishes.");
        start.ErrorFa.Should().Contain("آماده‌سازی");
    }

    [Fact]
    public async Task A_database_started_or_removed_after_validation_is_refused_at_execute()
    {
        using var rig = new DatabaseToolRig();
        var idle = rig.SeedDatabase("idle", status: ServiceStatus.Stopped);
        var shop = rig.SeedDatabase("shop-db");
        (await rig.ValidateAsync(rig.Start(), """{"database":"idle"}""")).Ok.Should().BeTrue();
        (await rig.ValidateAsync(rig.Rotate(), """{"database":"shop-db"}""")).Ok.Should().BeTrue();

        idle.Status = ServiceStatus.Running;
        rig.Db.ManagedServices.Remove(shop);
        await rig.Db.SaveChangesAsync();
        var start = await rig.ExecuteAsync(rig.Start(), """{"database":"idle"}""");
        var rotate = await rig.ExecuteAsync(rig.Rotate(), """{"database":"shop-db"}""");

        start.Should().BeEquivalentTo(new { Ok = false, Error = "idle is already running." });
        rotate.Should().BeEquivalentTo(new { Ok = false, Error = "No database named 'shop-db' that you can see." });
        rig.Engine.Started.Should().BeEmpty();
        rig.Engine.Rotated.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rotation_that_fails_after_storing_the_new_password_says_the_password_changed()
    {
        using var rig = new DatabaseToolRig();
        var oldCipher = rig.SeedDatabase("cache", type: ManagedServiceType.Redis).EncryptedPassword;
        rig.Engine.RotateFailsAfterStoring = true;

        var result = await rig.ExecuteAsync(rig.Rotate(), """{"database":"cache"}""");

        result.Ok.Should().BeFalse();
        result.Error.Should().StartWith("The password of cache was changed, but the rest of the rotation failed: The container could not be recreated");
        result.ErrorFa.Should().StartWith("رمز cache عوض شد");
        result.ResultJson.Should().Contain("\"rotated\":true");
        (await rig.Db.ManagedServices.AsNoTracking().SingleAsync()).EncryptedPassword.Should().NotBe(oldCipher);
    }

    // ---- every tool: another workspace's database is not there ----------------------------------

    [Theory]
    [InlineData("attach_database", """{"database":"theirs","app":"web"}""")]
    [InlineData("detach_database", """{"database":"theirs","app":"web"}""")]
    [InlineData("rotate_database_password", """{"database":"theirs"}""")]
    [InlineData("stop_database", """{"database":"theirs"}""")]
    [InlineData("start_database", """{"database":"theirs"}""")]
    [InlineData("delete_database", """{"database":"theirs"}""")]
    public async Task Another_workspaces_database_is_not_found(string tool, string json)
    {
        using var rig = new DatabaseToolRig();
        rig.SeedApp("web");
        using var other = new DatabaseToolRig(rig.H.Another(sameWorkspace: false));
        other.SeedDatabase("theirs", status: ServiceStatus.Stopped);

        var verdict = await rig.ValidateAsync(rig.All().Single(t => t.Name == tool), json);

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Be("No database named 'theirs' that you can see.");
    }

    /// <summary>A provisioning service that reports the database landed somewhere it was not asked to.</summary>
    private sealed class LandsElsewhere(Guid environmentId) : IDatabaseProvisioningService
    {
        public Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(DatabaseCreateRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServiceRefusal>>([]);

        public Task<ServiceOutcome<DatabaseCreateResult>> CreateAsync(DatabaseCreateRequest request, CancellationToken ct) =>
            Task.FromResult(ServiceOutcome<DatabaseCreateResult>.Success(new DatabaseCreateResult(
                Guid.CreateVersion7(), request.Name, "shop-db", "harbora-svc-shop-db", request.Type, "16", environmentId, Guid.CreateVersion7())));

        public Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();

        public Task<ServiceOutcome<DatabaseRemoveResult>> RemoveAsync(Guid serviceId, bool deleteData, string? ipAddress, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
