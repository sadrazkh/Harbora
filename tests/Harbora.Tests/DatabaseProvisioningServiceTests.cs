using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Creating and removing a managed database through <see cref="DatabaseProvisioningService"/> — the
/// code <c>DatabasesController.Create</c>/<c>Remove</c> used to hold, moved out in round six so the
/// in-panel agent runs the same checks. Every refusal the form gave is here, each in both languages;
/// every validate is shown to change nothing; and the audit entry creation never used to write is
/// shown to be written exactly once, and only for something that happened.
/// </summary>
public class DatabaseProvisioningServiceTests
{
    // ---- create: the happy path ----------------------------------------------------------------

    [Fact]
    public async Task Creating_a_database_saves_it_queues_its_provision_and_audits_it()
    {
        using var h = new DatabaseServiceHarness();

        var outcome = await h.Provisioning().CreateAsync(h.Request("Shop DB") with { IpAddress = "203.0.113.9" }, default);

        outcome.Ok.Should().BeTrue();
        var result = outcome.Value!;
        result.Slug.Should().Be("shop-db");
        result.ContainerName.Should().Be("harbora-svc-shop-db");
        result.Version.Should().Be("16", "an empty version takes the catalogue's first");
        result.EnvironmentId.Should().Be(h.Environment.Id, "no environment named means the default one");
        result.ServerId.Should().Be(h.Scheduler.PlacedOn);

        var row = await h.Db.ManagedServices.SingleAsync();
        row.Id.Should().Be(result.ServiceId);
        row.Status.Should().Be(ServiceStatus.Provisioning);
        row.VolumeName.Should().Be("harbora-svc-shop-db-data");
        row.DatabaseName.Should().Be("shop_db");
        row.InternalPort.Should().Be(5432);
        row.EncryptedPassword.Should().NotBeNullOrWhiteSpace();
        (await h.Db.ManagedServiceDatabases.SingleAsync()).IsDefault.Should().BeTrue(
            "the instance's own admin database is its first logical database");

        h.Engine.Provisioned.Should().Equal(result.ServiceId);
        h.Quota.Commits.Should().Be(1);

        var entry = h.Audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("database.created");
        entry.TargetType.Should().Be("service");
        entry.TargetId.Should().Be(result.ServiceId.ToString());
        entry.WorkspaceId.Should().Be(h.WorkspaceId);
        entry.IpAddress.Should().Be("203.0.113.9");
        entry.MetadataJson.Should().Contain("Shop DB").And.NotContain(row.EncryptedPassword);
    }

    [Fact]
    public async Task A_version_typed_in_another_case_is_stored_as_the_catalogue_spells_it()
    {
        using var h = new DatabaseServiceHarness();

        var outcome = await h.Provisioning().CreateAsync(h.Request() with { Version = "15" }, default);

        outcome.Value!.Version.Should().Be("15");
    }

    [Fact]
    public async Task The_chosen_tier_becomes_the_containers_ceiling()
    {
        using var h = new DatabaseServiceHarness();
        h.Db.InstanceSizes.Add(new InstanceSize
        {
            Key = "small", Name = "Small", CpuCores = 1, MemoryBytes = 1L << 30, DiskBytes = 10L << 30
        });
        await h.Db.SaveChangesAsync();

        await h.Provisioning().CreateAsync(h.Request() with { InstanceSizeKey = "small" }, default);

        var row = await h.Db.ManagedServices.SingleAsync();
        row.InstanceSizeKey.Should().Be("small");
        row.MemoryLimitBytes.Should().Be(1L << 30);
        row.DiskLimitBytes.Should().Be(10L << 30);
        row.CpuLimit.Should().Be(1);
    }

    [Fact]
    public async Task A_redis_instance_keeps_the_eviction_policy_it_was_created_with()
    {
        using var h = new DatabaseServiceHarness();

        await h.Provisioning().CreateAsync(h.Request("cache", ManagedServiceType.Redis) with
        {
            RedisEvictionPolicy = " allkeys-lru ", RedisMaxMemoryMb = 64
        }, default);

        var row = await h.Db.ManagedServices.SingleAsync();
        row.RedisEvictionPolicy.Should().Be("allkeys-lru");
        row.RedisMaxMemoryBytes.Should().Be(64L * 1024 * 1024);
        row.DatabaseName.Should().BeEmpty("Redis has no database name");
        h.Db.ManagedServiceDatabases.Should().BeEmpty("an engine with no database name has no logical database");
    }

    [Fact]
    public async Task Creating_in_a_workspace_with_no_environment_makes_the_default_one_and_audits_the_project()
    {
        using var h = new DatabaseServiceHarness(withEnvironment: false);

        var outcome = await h.Provisioning().CreateAsync(h.Request(), default);

        outcome.Ok.Should().BeTrue();
        h.Db.Environments.Should().ContainSingle();
        h.Audit.Entries.Select(e => e.Action).Should().Equal("project.created", "database.created");
    }

    // ---- validate: no side effects --------------------------------------------------------------

    [Fact]
    public async Task Validating_an_acceptable_request_changes_nothing_and_takes_no_lock()
    {
        using var h = new DatabaseServiceHarness();
        var before = h.Counts();

        var refusals = await h.Provisioning().ValidateCreateAsync(h.Request(), default);

        refusals.Should().BeEmpty();
        h.Counts().Should().Be(before);
        h.Quota.LocksTaken.Should().Be(0);
    }

    [Fact]
    public async Task Validating_in_a_workspace_with_no_environment_does_not_create_one()
    {
        using var h = new DatabaseServiceHarness(withEnvironment: false);
        var before = h.Counts();

        var refusals = await h.Provisioning().ValidateCreateAsync(h.Request(), default);

        refusals.Should().BeEmpty("an unscoped admin may create where the default project will be");
        h.Counts().Should().Be(before, "only the real create may make the default project");
    }

    [Fact]
    public async Task Validating_a_refused_request_changes_nothing_either()
    {
        using var h = new DatabaseServiceHarness();
        h.SeedService("shop-db");
        var before = h.Counts();

        var refusals = await h.Provisioning().ValidateCreateAsync(h.Request(), default);

        refusals.Should().ContainSingle().Which.Field.Should().Be("Name");
        h.Counts().Should().Be(before);
    }

    // ---- create: each refusal -------------------------------------------------------------------

    public static TheoryData<string> RefusalCases =>
    [
        "empty-name", "unknown-type", "bad-version", "quota", "duplicate", "placement", "redis", "stranger"
    ];

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public async Task Each_refusal_stops_creation_with_nothing_saved_billed_queued_or_audited(string refusal)
    {
        using var h = new DatabaseServiceHarness();
        var request = h.Request();
        switch (refusal)
        {
            case "empty-name": request = request with { Name = "  " }; break;
            case "unknown-type": request = request with { Type = ManagedServiceType.MongoDb }; break;
            case "bad-version": request = request with { Version = "9.6" }; break;
            case "quota": h.Quota.ServiceCheck = QuotaCheck.Deny("No room.", "جا نیست."); break;
            case "duplicate": h.SeedService("shop-db"); break;
            case "placement": h.Scheduler.Refusal = PlacementResult.Fail("Full.", "پر است."); break;
            case "redis":
                request = request with { Type = ManagedServiceType.Redis, RedisMaxMemoryMb = 64 };
                break;
            case "stranger": request = request with { WorkspaceId = Guid.CreateVersion7() }; break;
        }
        var before = h.Counts();

        var outcome = await h.Provisioning().CreateAsync(request, default);

        outcome.Ok.Should().BeFalse();
        outcome.Value.Should().BeNull();
        outcome.Refusals.Should().NotBeEmpty();
        outcome.Refusals.Should().OnlyContain(r => r.Message.Length > 0 && r.MessageFa.Length > 0 && r.MessageFa != r.Message,
            "every refusal carries its own Persian, not the English twice");
        var after = h.Counts();
        after.Should().Be(before with { Locks = after.Locks });
        h.Quota.Commits.Should().Be(0);
    }

    [Fact]
    public async Task An_empty_name_is_refused_on_the_name_field()
    {
        using var h = new DatabaseServiceHarness();

        var refusals = await h.Provisioning().ValidateCreateAsync(h.Request() with { Name = "" }, default);

        refusals.Should().ContainSingle().Which.Should().Be(
            new ServiceRefusal("A name is required.", "نام لازم است.", "Name"));
    }

    [Fact]
    public async Task An_engine_this_installation_does_not_run_is_refused_on_the_type_field()
    {
        using var h = new DatabaseServiceHarness();

        var refusal = (await h.Provisioning().ValidateCreateAsync(
            h.Request() with { Type = ManagedServiceType.MongoDb }, default)).Single();

        refusal.Message.Should().Be("Unknown service type.");
        refusal.MessageFa.Should().Be("نوع سرویس ناشناخته است.");
        refusal.Field.Should().Be("Type");
    }

    [Fact]
    public async Task A_version_the_catalogue_does_not_offer_is_refused_on_the_version_field()
    {
        using var h = new DatabaseServiceHarness();

        var refusal = (await h.Provisioning().ValidateCreateAsync(h.Request() with { Version = "9.6" }, default)).Single();

        refusal.Message.Should().Be("The selected database version is not supported.");
        refusal.Field.Should().Be("Version");
    }

    [Fact]
    public async Task A_quota_refusal_carries_both_of_the_quota_services_sentences()
    {
        using var h = new DatabaseServiceHarness();
        h.Quota.ServiceCheck = QuotaCheck.Deny("Your plan allows 2 databases.", "پلن شما ۲ دیتابیس اجازه می‌دهد.");

        var refusal = (await h.Provisioning().ValidateCreateAsync(h.Request(), default)).Single();

        refusal.Should().Be(new ServiceRefusal("Your plan allows 2 databases.", "پلن شما ۲ دیتابیس اجازه می‌دهد."));
    }

    [Fact]
    public async Task The_first_group_of_refusals_is_reported_together_as_the_form_showed_it()
    {
        using var h = new DatabaseServiceHarness();
        h.Quota.ServiceCheck = QuotaCheck.Deny("No room.", "جا نیست.");

        var refusals = await h.Provisioning().ValidateCreateAsync(
            h.Request() with { Name = "", Type = ManagedServiceType.MongoDb }, default);

        refusals.Select(r => r.Field).Should().Equal("Name", "Type", null);
    }

    [Fact]
    public async Task A_member_scoped_to_another_project_may_not_create_here()
    {
        using var h = new DatabaseServiceHarness();
        var elsewhere = h.AddOtherProject();
        h.ScopeCallerTo(elsewhere.ProjectId);
        var before = h.Counts();

        var outcome = await h.Provisioning().CreateAsync(h.Request() with { EnvironmentId = h.Environment.Id }, default);

        var refusal = outcome.Refusals.Should().ContainSingle().Which;
        refusal.Field.Should().Be("EnvironmentId");
        refusal.Message.Should().Be("You do not have permission to create a database in this environment.");
        refusal.MessageFa.Should().Be("اجازهٔ ساختن دیتابیس در این محیط را ندارید.");
        var after = h.Counts();
        after.Should().Be(before with { Locks = after.Locks });
    }

    [Fact]
    public async Task The_same_scoped_member_may_create_in_the_project_they_were_granted()
    {
        using var h = new DatabaseServiceHarness();
        var elsewhere = h.AddOtherProject();
        h.ScopeCallerTo(elsewhere.ProjectId);

        var outcome = await h.Provisioning().CreateAsync(h.Request() with { EnvironmentId = elsewhere.Id }, default);

        outcome.Ok.Should().BeTrue();
        outcome.Value!.EnvironmentId.Should().Be(elsewhere.Id);
    }

    [Fact]
    public async Task A_viewer_may_not_create_a_database_at_all()
    {
        using var h = new DatabaseServiceHarness(WorkspaceRole.Viewer);

        var refusals = await h.Provisioning().ValidateCreateAsync(h.Request(), default);

        refusals.Should().ContainSingle().Which.Field.Should().Be("EnvironmentId");
    }

    [Fact]
    public async Task A_name_whose_container_already_exists_is_refused()
    {
        using var h = new DatabaseServiceHarness();
        h.SeedService("shop-db");

        var refusal = (await h.Provisioning().ValidateCreateAsync(h.Request("Shop DB"), default)).Single();

        refusal.Should().Be(new ServiceRefusal(
            "A service with this name already exists.", "سرویسی با این نام از قبل وجود دارد.", "Name"));
    }

    [Fact]
    public async Task A_placement_refusal_carries_the_schedulers_sentences()
    {
        using var h = new DatabaseServiceHarness();
        h.Scheduler.Refusal = PlacementResult.Fail("No server has 2 GB free.", "هیچ سروری ۲ گیگابایت آزاد ندارد.");

        var refusal = (await h.Provisioning().ValidateCreateAsync(h.Request(), default)).Single();

        refusal.Should().Be(new ServiceRefusal("No server has 2 GB free.", "هیچ سروری ۲ گیگابایت آزاد ندارد."));
    }

    [Fact]
    public async Task A_redis_memory_cap_without_an_eviction_policy_is_refused_in_both_languages()
    {
        using var h = new DatabaseServiceHarness();

        var refusal = (await h.Provisioning().ValidateCreateAsync(
            h.Request("cache", ManagedServiceType.Redis) with { RedisMaxMemoryMb = 64 }, default)).Single();

        refusal.Field.Should().Be("RedisEvictionPolicy");
        refusal.Message.Should().Be(RedisMemoryPolicy.WhyRefused(null, 64L * 1024 * 1024, 0, isFa: false));
        refusal.MessageFa.Should().Be(RedisMemoryPolicy.WhyRefused(null, 64L * 1024 * 1024, 0, isFa: true));
    }

    [Fact]
    public async Task A_request_for_another_workspace_is_refused_before_anything_is_asked()
    {
        using var h = new DatabaseServiceHarness();

        var refusals = await h.Provisioning().ValidateCreateAsync(
            h.Request() with { UserId = Guid.CreateVersion7() }, default);

        refusals.Should().ContainSingle().Which.Field.Should().BeNull();
        h.Quota.LocksTaken.Should().Be(0);
    }

    [Fact]
    public async Task A_wallet_that_cannot_pay_refuses_with_the_billing_sentences_and_leaves_nothing_behind()
    {
        using var h = new DatabaseServiceHarness();
        h.BillingEnabled = true;
        var workspace = await h.Db.Workspaces.IgnoreQueryFilters().SingleAsync(w => w.Id == h.WorkspaceId);
        workspace.IsSuspended = true;
        await h.Db.SaveChangesAsync();

        var outcome = await h.Provisioning().CreateAsync(h.Request(), default);

        outcome.Refusals.Should().ContainSingle().Which.Should().Be(new ServiceRefusal(
            "This workspace is suspended. No resource was created.",
            "این فضای کاری معلق است؛ هیچ منبعی ساخته نشد."));
        h.Db.ChangeTracker.Clear();
        (await h.Db.ManagedServices.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await h.Db.ManagedServiceDatabases.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        h.Engine.Provisioned.Should().BeEmpty();
        h.Audit.Entries.Should().BeEmpty();
        h.Quota.Commits.Should().Be(0);
    }

    // ---- remove ---------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_database_removes_it_and_audits_the_removal()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");

        var outcome = await h.Provisioning().RemoveAsync(svc.Id, deleteData: true, "203.0.113.10", default);

        outcome.Value.Should().Be(new DatabaseRemoveResult(svc.Id, "orders", "harbora-svc-orders-data", true));
        h.Engine.Removed.Should().Equal((svc.Id, true));
        (await h.Db.ManagedServices.AnyAsync(s => s.Id == svc.Id)).Should().BeFalse();

        var entry = h.Audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("database.removed");
        entry.TargetId.Should().Be(svc.Id.ToString());
        entry.WorkspaceId.Should().Be(h.WorkspaceId);
        entry.IpAddress.Should().Be("203.0.113.10");
        entry.MetadataJson.Should().Contain("orders").And.Contain("\"deleteData\":true");
    }

    [Fact]
    public async Task A_database_still_attached_to_an_app_is_refused_by_name_and_left_alone()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        var app = h.SeedApp("checkout");
        h.Db.AppManagedServices.Add(new AppManagedService { AppId = app.Id, ManagedServiceId = svc.Id, Alias = "ORDERS" });
        await h.Db.SaveChangesAsync();
        var before = h.Counts();

        var validated = await h.Provisioning().ValidateRemoveAsync(svc.Id, default);
        var outcome = await h.Provisioning().RemoveAsync(svc.Id, deleteData: false, null, default);

        var refusal = validated.Should().ContainSingle().Which;
        refusal.Message.Should().Be(
            "This database is still attached to 1 app: checkout. Detach it from every app first, then delete it.");
        refusal.MessageFa.Should().Be(
            "این دیتابیس هنوز به 1 اپ: checkout متصل است. برای حذف، ابتدا آن را از همه‌ی اپ‌ها جدا کنید.");
        outcome.Refusals.Should().Equal(validated);
        h.Counts().Should().Be(before, "neither the validate nor the refused removal may touch anything");
    }

    [Fact]
    public async Task A_primary_with_a_live_replica_is_refused_and_both_structural_refusals_are_named()
    {
        using var h = new DatabaseServiceHarness();
        var primary = h.SeedService("orders");
        h.SeedService("orders-replica", primaryId: primary.Id);
        var app = h.SeedApp("checkout");
        h.Db.AppManagedServices.Add(new AppManagedService { AppId = app.Id, ManagedServiceId = primary.Id, Alias = "ORDERS" });
        await h.Db.SaveChangesAsync();

        var refusals = await h.Provisioning().ValidateRemoveAsync(primary.Id, default);

        refusals.Should().HaveCount(2, "the form shows the first; a caller like the agent can say both");
        refusals[1].Message.Should().Be(
            "This database still has 1 app: orders-replica replicating from it. Delete the replica(s) first, then delete the primary.");
        refusals[1].MessageFa.Should().Contain("رپلیکای 1 اپ: orders-replica");
    }

    [Fact]
    public async Task A_viewer_cannot_remove_a_database_and_is_not_told_whether_it_exists()
    {
        using var h = new DatabaseServiceHarness(WorkspaceRole.Viewer);
        var svc = h.SeedService("orders");

        var refusals = await h.Provisioning().ValidateRemoveAsync(svc.Id, default);
        var outcome = await h.Provisioning().RemoveAsync(svc.Id, deleteData: true, null, default);
        var missing = await h.Provisioning().ValidateRemoveAsync(Guid.CreateVersion7(), default);

        refusals.Should().Equal(DatabaseRefusals.ServiceNotFound);
        outcome.Refusals.Should().Equal(DatabaseRefusals.ServiceNotFound);
        missing.Should().Equal(refusals, "not there and not yours read the same");
        h.Engine.Removed.Should().BeEmpty();
        h.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_removal_the_engine_did_not_carry_out_is_neither_audited_nor_reported_as_done()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        h.Engine.RemoveQuietlyDoesNothing = true;

        var outcome = await h.Provisioning().RemoveAsync(svc.Id, deleteData: false, null, default);

        outcome.Ok.Should().BeFalse();
        outcome.Refusals.Single().Message.Should().Contain("still there");
        h.Audit.Entries.Should().BeEmpty();
    }

    // ---- helpers shared with the controller -----------------------------------------------------

    [Fact]
    public void Named_lists_past_three_say_how_many_more_in_both_languages()
    {
        string[] names = ["api", "worker", "cron", "web", "mail"];

        DatabaseProvisioningService.NamedList(names, isFa: false).Should().Be("5 apps: api, worker, cron and 2 more");
        DatabaseProvisioningService.NamedList(names, isFa: true).Should().Be("5 اپ: api، worker، cron و 2 مورد دیگر");
    }

    [Theory]
    [InlineData("Shop DB", "shop-db")]
    [InlineData("  --Orders__2026--  ", "orders-2026")]
    public void Container_slugs_are_lowercase_and_dash_separated(string name, string expected) =>
        DatabaseProvisioningService.Slugify(name).Should().Be(expected);

    [Fact]
    public void A_name_with_nothing_slug_safe_in_it_still_gets_a_usable_slug() =>
        DatabaseProvisioningService.Slugify("دیتابیس").Should().MatchRegex("^svc-[0-9a-f]{6}$");
}
