using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Attaching a database to an app and detaching it through <see cref="DatabaseAttachmentService"/>,
/// moved out of <c>DatabasesController</c> in round six. The access checks moved with the logic, so
/// these are asked of the service directly — no controller in front of it to have asked first.
/// </summary>
public class DatabaseAttachmentServiceTests
{
    // ---- attach ---------------------------------------------------------------------------------

    [Fact]
    public async Task Attaching_writes_an_unpublished_join_row_to_the_default_logical_database_and_audits_it()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        var app = h.SeedApp("api");
        var defaultDatabaseId = await h.Db.ManagedServiceDatabases.Where(d => d.IsDefault).Select(d => d.Id).SingleAsync();

        var outcome = await h.Attachments().AttachAsync(svc.Id, app.Id, null, null, "203.0.113.20", default);

        outcome.Value.Should().Be(new DatabaseAttachResult(svc.Id, "orders", app.Id, "api", defaultDatabaseId, "ORDERS"));
        var join = await h.Db.AppManagedServices.SingleAsync();
        join.AppId.Should().Be(app.Id);
        join.ManagedServiceId.Should().Be(svc.Id);
        join.ManagedServiceDatabaseId.Should().Be(defaultDatabaseId);
        join.Alias.Should().Be("ORDERS");
        join.AttachOrder.Should().Be(1);
        join.HasUnpublishedChanges.Should().BeTrue("its variables reach the app only on the next deploy");

        var entry = h.Audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("database.attached");
        entry.TargetType.Should().Be("service");
        entry.TargetId.Should().Be($"{svc.Id}:{app.Id}");
        entry.IpAddress.Should().Be("203.0.113.20");
        entry.WorkspaceId.Should().Be(h.WorkspaceId);
    }

    [Fact]
    public async Task A_second_attachment_comes_after_the_first_and_a_taken_alias_gets_a_suffix()
    {
        using var h = new DatabaseServiceHarness();
        var first = h.SeedService("orders");
        var second = h.SeedService("orders-two");
        var app = h.SeedApp("api");
        await h.Attachments().AttachAsync(first.Id, app.Id, "MAIN", null, null, default);

        var outcome = await h.Attachments().AttachAsync(second.Id, app.Id, "main", null, null, default);

        outcome.Value!.Alias.Should().Be("MAIN_2");
        (await h.Db.AppManagedServices.SingleAsync(x => x.ManagedServiceId == second.Id)).AttachOrder.Should().Be(2);
    }

    [Fact]
    public async Task Validating_an_attach_changes_nothing()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        var app = h.SeedApp("api");
        var before = h.Counts();

        var refusals = await h.Attachments().ValidateAttachAsync(svc.Id, app.Id, null, null, default);

        refusals.Should().BeEmpty();
        h.Counts().Should().Be(before);
    }

    [Fact]
    public async Task A_viewer_cannot_attach_and_is_not_told_whether_the_database_exists()
    {
        using var h = new DatabaseServiceHarness(WorkspaceRole.Viewer);
        var svc = h.SeedService("orders");
        var app = h.SeedApp("api");

        var outcome = await h.Attachments().AttachAsync(svc.Id, app.Id, null, null, null, default);

        outcome.Refusals.Should().Equal(DatabaseRefusals.ServiceNotFound);
        h.Db.AppManagedServices.Should().BeEmpty();
        h.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_member_who_may_manage_the_database_but_not_reach_the_app_is_refused_on_the_app()
    {
        using var h = new DatabaseServiceHarness();
        var elsewhere = h.AddOtherProject();
        var svc = h.SeedService("orders", elsewhere.Id);
        var app = h.SeedApp("api");
        h.ScopeCallerTo(elsewhere.ProjectId);

        var refusals = await h.Attachments().ValidateAttachAsync(svc.Id, app.Id, null, null, default);

        refusals.Should().Equal(DatabaseRefusals.AppNotFound);
        DatabaseRefusals.AppNotFound.MessageFa.Should().NotBe(DatabaseRefusals.AppNotFound.Message);
    }

    [Fact]
    public async Task Another_workspaces_app_is_refused_as_not_found()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        var theirs = h.SeedApp("their-api", workspaceId: Guid.CreateVersion7());

        var refusals = await h.Attachments().ValidateAttachAsync(svc.Id, theirs.Id, null, null, default);

        refusals.Should().Equal(DatabaseRefusals.AppNotFound);
    }

    [Fact]
    public async Task A_read_replica_is_refused_with_the_primary_named_as_what_to_attach()
    {
        using var h = new DatabaseServiceHarness();
        var primary = h.SeedService("orders");
        var replica = h.SeedService("orders-replica", primaryId: primary.Id);
        var app = h.SeedApp("api");

        var refusal = (await h.Attachments().ValidateAttachAsync(replica.Id, app.Id, null, null, default)).Single();

        refusal.Field.Should().Be(DatabaseRefusalFields.Service);
        refusal.Message.Should().StartWith("'orders-replica' is a read-only replica");
        refusal.MessageFa.Should().StartWith("«orders-replica» یک رپلیکای فقط-خواندنی است");
    }

    [Fact]
    public async Task A_database_on_another_environment_is_refused_because_its_hostname_would_not_resolve()
    {
        using var h = new DatabaseServiceHarness();
        var elsewhere = h.AddOtherProject();
        var svc = h.SeedService("orders", elsewhere.Id);
        var app = h.SeedApp("api");
        var before = h.Counts();

        var outcome = await h.Attachments().AttachAsync(svc.Id, app.Id, null, null, null, default);

        var refusal = outcome.Refusals.Should().ContainSingle().Which;
        refusal.Field.Should().Be(DatabaseRefusalFields.Environment);
        refusal.Message.Should().Contain("different private networks");
        refusal.MessageFa.Should().Contain("شبکهٔ خصوصی");
        h.Counts().Should().Be(before);
    }

    [Fact]
    public async Task A_logical_database_from_another_instance_is_refused_on_the_database_field()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        h.SeedService("billing");
        var foreignDatabaseId = await h.Db.ManagedServiceDatabases
            .Where(d => d.Name == "billing").Select(d => d.Id).SingleAsync();
        var app = h.SeedApp("api");

        var refusal = (await h.Attachments().ValidateAttachAsync(svc.Id, app.Id, null, foreignDatabaseId, default)).Single();

        refusal.Field.Should().Be(DatabaseRefusalFields.Database);
        refusal.MessageFa.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_same_logical_database_attached_twice_is_refused()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        var app = h.SeedApp("api");
        await h.Attachments().AttachAsync(svc.Id, app.Id, null, null, null, default);

        var outcome = await h.Attachments().AttachAsync(svc.Id, app.Id, "AGAIN", null, null, default);

        outcome.Refusals.Should().ContainSingle().Which.Should().Be(new Harbora.Application.Abstractions.ServiceRefusal(
            "This database is already attached.", "این دیتابیس از قبل به این اپ متصل است."));
        h.Db.AppManagedServices.Should().ContainSingle();
        h.Audit.Entries.Should().ContainSingle("only the first attach happened");
    }

    [Fact]
    public async Task An_engine_with_no_logical_database_attaches_once_and_refuses_the_second_time()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("cache", withDefaultDatabase: false);
        var app = h.SeedApp("api");

        var first = await h.Attachments().AttachAsync(svc.Id, app.Id, null, null, null, default);
        var second = await h.Attachments().ValidateAttachAsync(svc.Id, app.Id, null, null, default);

        first.Value!.DatabaseId.Should().BeNull();
        second.Should().ContainSingle().Which.Message.Should().Be("This database is already attached.");
    }

    // ---- detach ---------------------------------------------------------------------------------

    [Fact]
    public async Task Detaching_removes_the_join_row_and_audits_it()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        var app = h.SeedApp("api");
        await h.Attachments().AttachAsync(svc.Id, app.Id, null, null, null, default);
        h.Audit.Entries.Clear();

        var outcome = await h.Attachments().DetachAsync(svc.Id, app.Id, null, "203.0.113.21", default);

        outcome.Ok.Should().BeTrue();
        outcome.Value!.Alias.Should().Be("ORDERS");
        h.Db.AppManagedServices.Should().BeEmpty();
        var entry = h.Audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("database.detached");
        entry.TargetId.Should().Be($"{svc.Id}:{app.Id}");
        entry.IpAddress.Should().Be("203.0.113.21");
        entry.WorkspaceId.Should().Be(h.WorkspaceId);
    }

    [Fact]
    public async Task Naming_the_logical_database_detaches_only_that_attachment()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        var reports = new ManagedServiceDatabase
        {
            WorkspaceId = h.WorkspaceId, ManagedServiceId = svc.Id, Name = "reports", Username = "reports",
            EncryptedPassword = "secret"
        };
        h.Db.ManagedServiceDatabases.Add(reports);
        await h.Db.SaveChangesAsync();
        var app = h.SeedApp("api");
        await h.Attachments().AttachAsync(svc.Id, app.Id, null, null, null, default);
        await h.Attachments().AttachAsync(svc.Id, app.Id, "REPORTS", reports.Id, null, default);

        await h.Attachments().DetachAsync(svc.Id, app.Id, reports.Id, null, default);

        (await h.Db.AppManagedServices.SingleAsync()).ManagedServiceDatabaseId.Should().NotBe(reports.Id);
    }

    [Fact]
    public async Task Detaching_what_is_not_attached_is_refused_and_validating_a_detach_changes_nothing()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        var app = h.SeedApp("api");
        var before = h.Counts();

        var validated = await h.Attachments().ValidateDetachAsync(svc.Id, app.Id, null, default);
        var outcome = await h.Attachments().DetachAsync(svc.Id, app.Id, null, null, default);

        validated.Should().ContainSingle().Which.Should().Be(new Harbora.Application.Abstractions.ServiceRefusal(
            "This app is not attached to this database.", "این اپ به این دیتابیس وصل نیست."));
        outcome.Refusals.Should().Equal(validated);
        h.Counts().Should().Be(before);
    }

    [Fact]
    public async Task A_viewer_cannot_detach()
    {
        using var h = new DatabaseServiceHarness();
        var svc = h.SeedService("orders");
        var app = h.SeedApp("api");
        await h.Attachments().AttachAsync(svc.Id, app.Id, null, null, null, default);
        var member = await h.Db.WorkspaceMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == h.UserId);
        member.Role = WorkspaceRole.Viewer;
        await h.Db.SaveChangesAsync();

        var outcome = await h.Attachments().DetachAsync(svc.Id, app.Id, null, null, default);

        outcome.Refusals.Should().Equal(DatabaseRefusals.ServiceNotFound);
        h.Db.AppManagedServices.Should().ContainSingle("the attachment is still there");
    }
}
