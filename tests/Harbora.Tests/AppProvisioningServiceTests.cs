using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Apps;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="AppProvisioningService"/>: the app form's creation logic, moved out of
/// <c>AppsController.Create</c> so the AI agent's "create app" tool runs the same code. The HTTP tests
/// (<c>AppCreate*HttpTests</c>, <c>AppAddressHttpTests</c>) are the parity net for the form; these pin
/// the service's own contract — each refusal, validate changing nothing, the audit entry, and that
/// access is checked by the service itself rather than left to whoever calls it.
/// </summary>
public sealed class AppProvisioningServiceTests
{
    // ---- creating ----

    [Fact]
    public async Task Creating_an_image_app_saves_it_with_its_address_and_audits_it_without_deploying_when_not_asked()
    {
        using var h = new AppServiceHarness();

        var outcome = await h.Provisioning().CreateAsync(h.ImageApp(), default);

        outcome.Ok.Should().BeTrue(string.Join(" | ", outcome.Refusals.Select(r => r.Message)));
        var created = outcome.Value!;
        created.Slug.Should().Be("shop-api", "a blank slug is derived from the name");
        created.Host.Should().Be($"shop-api.{AppServiceHarness.RootDomain}");
        created.DeploymentId.Should().BeNull("DeployNow was false");
        h.Deployments.Queued.Should().BeEmpty();

        var app = await h.Db.Apps.Include(a => a.Domains).SingleAsync(a => a.Id == created.AppId);
        app.WorkspaceId.Should().Be(h.WorkspaceId);
        app.EnvironmentId.Should().Be(h.Environment!.Id);
        app.ServerId.Should().Be(h.Server.Id, "the scheduler's placement is where it lands");
        app.Domains.Should().ContainSingle().Which.IsPrimary.Should().BeTrue();

        var entry = h.Audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("app.created");
        entry.TargetId.Should().Be(app.Id.ToString());
        entry.WorkspaceId.Should().Be(h.WorkspaceId, "IAuditLogger never defaults the workspace; the service must pass it");
        entry.IpAddress.Should().Be("203.0.113.9");
        JsonDocument.Parse(entry.MetadataJson!).RootElement.GetProperty("slug").GetString().Should().Be("shop-api");
    }

    [Fact]
    public async Task Asking_to_deploy_queues_the_first_deployment_with_the_requests_trigger_and_user()
    {
        using var h = new AppServiceHarness();

        var outcome = await h.Provisioning().CreateAsync(
            h.ImageApp() with { DeployNow = true, Trigger = DeploymentTrigger.Assistant }, default);

        outcome.Value!.DeploymentId.Should().Be(h.Deployments.NextId);
        var queued = h.Deployments.Queued.Should().ContainSingle().Which;
        queued.AppId.Should().Be(outcome.Value.AppId);
        queued.Trigger.Should().Be(DeploymentTrigger.Assistant);
        queued.TriggeredByUserId.Should().Be(h.UserId);
    }

    [Fact]
    public async Task A_first_deploy_that_is_refused_leaves_the_app_created_and_says_why_in_both_languages()
    {
        using var h = new AppServiceHarness();
        h.Deployments.Refuse = new QuotaRefusedException(QuotaCheck.Deny("Two deployments are already running.", "دو استقرار در جریان است."));

        var outcome = await h.Provisioning().CreateAsync(h.ImageApp() with { DeployNow = true }, default);

        outcome.Ok.Should().BeTrue("the app and its first hour are committed before the deploy is queued");
        outcome.Value!.DeploymentId.Should().BeNull();
        outcome.Value.DeployRefusal.Should().Be(new ServiceRefusal("Two deployments are already running.", "دو استقرار در جریان است."));
        (await h.Db.Apps.CountAsync()).Should().Be(1);
        h.Audit.Entries.Select(e => e.Action).Should().Equal("app.created");
    }

    [Fact]
    public async Task A_git_app_gets_its_custom_provider_and_repository_and_the_token_is_stored_encrypted_and_never_audited()
    {
        using var h = new AppServiceHarness();
        var request = h.ImageApp("Storefront") with
        {
            SourceType = AppSourceType.GitRepository,
            PrebuiltImage = null,
            CloneUrl = "https://git.example.com/acme/storefront.git",
            GitRef = "release",
            GitToken = "ghp_super-secret-token"
        };

        var outcome = await h.Provisioning().CreateAsync(request, default);

        outcome.Ok.Should().BeTrue(string.Join(" | ", outcome.Refusals.Select(r => r.Message)));
        var app = await h.Db.Apps.Include(a => a.GitRepository!).ThenInclude(r => r.Provider)
            .SingleAsync(a => a.Id == outcome.Value!.AppId);
        app.GitRepository!.FullName.Should().Be("storefront");
        app.GitRepository.DefaultBranch.Should().Be("release");
        app.GitRepository.Provider!.Name.Should().Be("Custom");
        app.GitRepository.Provider.EncryptedCredential.Should().NotBe("ghp_super-secret-token");
        new PassthroughProtector().Unprotect(app.GitRepository.Provider.EncryptedCredential!).Should().Be("ghp_super-secret-token");

        h.Audit.Entries.Should().OnlyContain(e => !(e.MetadataJson ?? "").Contains("ghp_super-secret-token"));
        request.ToString().Should().NotContain("ghp_super-secret-token", "a logged request must not be how the token leaks");
    }

    [Fact]
    public async Task A_template_gives_the_app_its_volumes_and_variables_with_the_secret_encrypted()
    {
        using var h = new AppServiceHarness();
        var template = new AppTemplate
        {
            Key = "notes", Name = "Notes", IsEnabled = true,
            ManifestJson = """{"image":"demo/notes:1","port":8080,"env":[{"key":"APP_KEY","secret":true},{"key":"APP_HOST"}],"volumes":[{"mount":"/data"}]}"""
        };
        h.Db.AppTemplates.Add(template);
        await h.Db.SaveChangesAsync();

        var outcome = await h.Provisioning().CreateAsync(h.ImageApp("Notes") with { TemplateId = template.Id }, default);

        outcome.Ok.Should().BeTrue(string.Join(" | ", outcome.Refusals.Select(r => r.Message)));
        var app = await h.Db.Apps.Include(a => a.Volumes).Include(a => a.EnvironmentVariables)
            .SingleAsync(a => a.Id == outcome.Value!.AppId);
        app.Volumes.Should().ContainSingle().Which.Name.Should().Be("harbora-vol-notes-data");
        var secret = app.EnvironmentVariables.Single(v => v.Key == "APP_KEY");
        secret.IsSecret.Should().BeTrue();
        secret.Value.Should().Contain("|nonce:", "a generated secret is stored through the protector, never raw");
        app.EnvironmentVariables.Single(v => v.Key == "APP_HOST").Value.Should().BeEmpty();
        outcome.Value!.TemplateAdvice.Should().Contain("APP_HOST");
    }

    [Fact]
    public async Task A_workspace_without_an_environment_gets_its_default_one_from_the_create_but_never_from_validate()
    {
        using var h = new AppServiceHarness(withEnvironment: false);
        var request = h.ImageApp();

        (await h.Provisioning().ValidateCreateAsync(request, default)).Should().BeEmpty();
        (await h.Db.Environments.IgnoreQueryFilters().CountAsync()).Should().Be(0,
            "validation must not create the default project the create would");

        var outcome = await h.Provisioning().CreateAsync(request, default);

        outcome.Ok.Should().BeTrue(string.Join(" | ", outcome.Refusals.Select(r => r.Message)));
        var environment = await h.Db.Environments.SingleAsync();
        (await h.Db.Apps.SingleAsync()).EnvironmentId.Should().Be(environment.Id);
    }

    // ---- validate changes nothing ----

    [Fact]
    public async Task Validating_a_create_that_would_succeed_writes_tracks_queues_and_audits_nothing()
    {
        using var h = new AppServiceHarness();
        var template = new AppTemplate
        {
            Key = "notes", Name = "Notes", IsEnabled = true,
            ManifestJson = """{"image":"demo/notes:1","env":[{"key":"APP_KEY","secret":true}],"volumes":[{"mount":"/data"}]}"""
        };
        h.Db.AppTemplates.Add(template);
        await h.Db.SaveChangesAsync();
        var request = h.ImageApp() with
        {
            SourceType = AppSourceType.GitRepository, CloneUrl = "https://git.example.com/acme/api.git",
            GitToken = "token", TemplateId = template.Id, DeployNow = true
        };
        var before = h.Snapshot();

        var refusals = await h.Provisioning().ValidateCreateAsync(request, default);

        refusals.Should().BeEmpty();
        h.Snapshot().Should().Be(before);
        h.Audit.Entries.Should().BeEmpty();
        h.Deployments.Queued.Should().BeEmpty();

        // And the same request really does create — validate said yes to something create can do.
        (await h.Provisioning().CreateAsync(request, default)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Validating_a_refused_create_changes_nothing_and_gives_the_reasons_the_create_gives()
    {
        using var h = new AppServiceHarness();
        var request = h.ImageApp() with { PrebuiltImage = null, RootDirectory = "../outside" };
        var before = h.Snapshot();

        var validated = await h.Provisioning().ValidateCreateAsync(request, default);

        h.Snapshot().Should().Be(before);
        var created = await h.Provisioning().CreateAsync(request, default);
        created.Ok.Should().BeFalse();
        created.Refusals.Should().Equal(validated);
        h.Snapshot().Should().Be(before, "a refused create changes nothing either");
        h.Audit.Entries.Should().BeEmpty();
    }

    // ---- refusals ----

    public static TheoryData<string, Func<AppCreateRequest, AppCreateRequest>, string> FormRefusals => new()
    {
        { "a blank name", r => r with { Name = "  " }, nameof(AppCreateRequest.Name) },
        { "a Git source with no clone URL", r => r with { SourceType = AppSourceType.GitRepository, PrebuiltImage = null }, nameof(AppCreateRequest.CloneUrl) },
        { "an image source with no image", r => r with { PrebuiltImage = " " }, nameof(AppCreateRequest.PrebuiltImage) },
        { "a root directory outside the repository", r => r with { RootDirectory = "../other" }, nameof(AppCreateRequest.RootDirectory) },
        { "a cron job with an unreadable schedule", r => r with { Kind = ServiceKind.Cron, CronExpression = "every day", Command = "run" }, nameof(AppCreateRequest.CronExpression) },
        { "a cron job with nothing to run", r => r with { Kind = ServiceKind.Cron, CronExpression = "0 3 * * *" }, nameof(AppCreateRequest.Command) },
        { "a template that does not exist", r => r with { TemplateId = Guid.CreateVersion7() }, nameof(AppCreateRequest.TemplateId) },
        { "a typed domain that is the panel's own", r => r with { Domain = AppServiceHarness.PanelDomain }, nameof(AppCreateRequest.Domain) }
    };

    [Theory]
    [MemberData(nameof(FormRefusals))]
    public async Task Each_rule_of_the_form_is_refused_against_its_field_in_both_languages_by_validate_and_create(
        string because, Func<AppCreateRequest, AppCreateRequest> change, string field)
    {
        using var h = new AppServiceHarness();
        var request = change(h.ImageApp());

        var validated = await h.Provisioning().ValidateCreateAsync(request, default);
        var created = await h.Provisioning().CreateAsync(request, default);

        var refusal = validated.Should().ContainSingle(because).Which;
        refusal.Field.Should().Be(field);
        refusal.Message.Should().NotBeNullOrWhiteSpace();
        refusal.MessageFa.Should().NotBeNullOrWhiteSpace().And.NotBe(refusal.Message,
            "every refusal carries Persian of its own, including the ones the form only ever had in English");
        created.Refusals.Should().Equal(validated);
        (await h.Db.Apps.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_typed_slug_another_workspace_holds_is_refused_and_named_not_silently_renamed()
    {
        using var h = new AppServiceHarness();
        h.Db.Apps.Add(new App
        {
            WorkspaceId = Guid.CreateVersion7(), Name = "theirs", Slug = "api", SourceType = AppSourceType.PrebuiltImage
        });
        await h.Db.SaveChangesAsync();

        var refusals = await h.Provisioning().ValidateCreateAsync(h.ImageApp(slug: "API"), default);

        var refusal = refusals.Should().ContainSingle().Which;
        refusal.Field.Should().Be(nameof(AppCreateRequest.Slug));
        refusal.Message.Should().Contain("'api'").And.Contain("every workspace");
        refusal.MessageFa.Should().Contain("«api»").And.Contain("Workspace");

        // Left blank instead, the same name is derived and suffixed the way it always was.
        var outcome = await h.Provisioning().CreateAsync(h.ImageApp("api"), default);
        outcome.Value!.Slug.Should().Be("api-2");
    }

    [Fact]
    public async Task A_typed_domain_already_in_use_is_refused_rather_than_mangled()
    {
        using var h = new AppServiceHarness();
        h.Db.Apps.Add(new App
        {
            WorkspaceId = Guid.CreateVersion7(), Name = "theirs", Slug = "theirs", SourceType = AppSourceType.PrebuiltImage,
            Domains = { new DomainName { Host = "shop.mycompany.com", IsPrimary = true } }
        });
        await h.Db.SaveChangesAsync();

        var refusals = await h.Provisioning().ValidateCreateAsync(h.ImageApp() with { Domain = "shop.mycompany.com" }, default);

        refusals.Should().Equal(new ServiceRefusal(
            "'shop.mycompany.com' is already in use.", "«shop.mycompany.com» پیش‌تر استفاده شده است.", nameof(AppCreateRequest.Domain)));
    }

    [Fact]
    public async Task The_plans_app_ceiling_is_refused_and_an_untranslated_reason_reads_as_its_english_in_persian()
    {
        using var h = new AppServiceHarness();
        h.Plan.MaxApps = 1;
        h.Db.Apps.Add(new App
        {
            WorkspaceId = h.WorkspaceId, EnvironmentId = h.Environment!.Id, Name = "first", Slug = "first-app",
            SourceType = AppSourceType.PrebuiltImage
        });
        await h.Db.SaveChangesAsync();

        var created = await h.Provisioning().CreateAsync(h.ImageApp(), default);

        created.Refusals.Should().Equal(new ServiceRefusal("App limit reached (1).", "App limit reached (1)."));
        (await h.Db.Apps.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_plans_domain_ceiling_counts_the_address_the_app_would_get()
    {
        using var h = new AppServiceHarness();
        h.Plan.MaxDomains = 1;
        h.Db.Apps.Add(new App
        {
            WorkspaceId = h.WorkspaceId, EnvironmentId = h.Environment!.Id, Name = "first", Slug = "first-app",
            SourceType = AppSourceType.PrebuiltImage, Domains = { new DomainName { Host = "first.example.org", IsPrimary = true } }
        });
        await h.Db.SaveChangesAsync();

        var validated = await h.Provisioning().ValidateCreateAsync(h.ImageApp(), default);
        var created = await h.Provisioning().CreateAsync(h.ImageApp(), default);

        validated.Should().ContainSingle().Which.MessageFa.Should().Contain("دامنه");
        created.Refusals.Should().Equal(validated);
        (await h.Db.Apps.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task No_node_with_room_is_refused_with_the_schedulers_own_reason()
    {
        using var h = new AppServiceHarness();
        h.Scheduler.Result = PlacementResult.Fail("No node has 4 GB free.", "هیچ سروری ۴ گیگابایت جای خالی ندارد.");

        var refusals = await h.Provisioning().ValidateCreateAsync(h.ImageApp(), default);

        refusals.Should().Equal(new ServiceRefusal("No node has 4 GB free.", "هیچ سروری ۴ گیگابایت جای خالی ندارد."));
    }

    [Fact]
    public async Task A_template_another_workspace_keeps_private_is_refused_as_unavailable()
    {
        using var h = new AppServiceHarness();
        var theirs = new AppTemplate
        {
            Key = "private", Name = "Theirs", IsEnabled = true, WorkspaceId = Guid.CreateVersion7(),
            Status = TemplateStatus.Private, ManifestJson = """{"image":"evil/app:1"}"""
        };
        h.Db.AppTemplates.Add(theirs);
        await h.Db.SaveChangesAsync();

        var created = await h.Provisioning().CreateAsync(h.ImageApp() with { TemplateId = theirs.Id }, default);

        created.Refusals.Should().ContainSingle().Which.Field.Should().Be(nameof(AppCreateRequest.TemplateId));
        (await h.Db.Apps.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_refused_payment_leaves_nothing_behind_and_does_not_detach_what_the_caller_is_tracking()
    {
        using var h = new AppServiceHarness();
        h.Billing.Enabled = true;   // a non-default workspace with no priced size: the money gate refuses
        var callersOwn = new Harbora.Domain.Settings.Setting { Key = "callers.own", Value = "pending" };
        h.Db.Settings.Add(callersOwn);
        var request = h.ImageApp() with { SourceType = AppSourceType.GitRepository, CloneUrl = "https://git.example.com/a/b.git" };

        var created = await h.Provisioning().CreateAsync(request, default);

        var refusal = created.Refusals.Should().ContainSingle().Which;
        refusal.Message.Should().Contain("no resource was created");
        refusal.MessageFa.Should().Contain("هیچ منبعی ساخته نشد");
        h.Db.Entry(callersOwn).State.Should().Be(EntityState.Added,
            "only the create's own rows are forgotten — the agent may be tracking work of its own in this request");
        h.Db.ChangeTracker.Entries().Should().ContainSingle(e => e.State == EntityState.Added,
            "the app, its repository and its provider are no longer tracked for a later save to persist");
        (await h.Db.Apps.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await h.Db.GitProviders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        h.Audit.Entries.Should().BeEmpty();
    }

    // ---- access ----

    [Fact]
    public async Task A_member_scoped_to_projects_without_a_grant_here_is_refused_by_the_service_itself()
    {
        using var h = new AppServiceHarness(WorkspaceRole.Member, scoped: true);

        var validated = await h.Provisioning().ValidateCreateAsync(h.ImageApp(), default);
        var created = await h.Provisioning().CreateAsync(h.ImageApp(), default);

        validated.Should().Equal(new ServiceRefusal(
            "You do not have permission to create an app in this environment.",
            "اجازهٔ ساختن اپ در این محیط را ندارید.",
            nameof(AppCreateRequest.EnvironmentId)));
        created.Refusals.Should().Equal(validated);
        (await h.Db.Apps.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_same_member_with_a_grant_on_the_project_may_create_there()
    {
        using var h = new AppServiceHarness(WorkspaceRole.Member, scoped: true);
        h.Grant(SystemRole.Member);

        (await h.Provisioning().CreateAsync(h.ImageApp(), default)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_viewer_cannot_create_an_app_at_all()
    {
        using var h = new AppServiceHarness(WorkspaceRole.Viewer);

        var created = await h.Provisioning().CreateAsync(h.ImageApp(), default);

        created.Refusals.Should().ContainSingle().Which.Field.Should().Be(nameof(AppCreateRequest.EnvironmentId));
    }
}
