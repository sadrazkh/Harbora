using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Domain.Identity;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Functions;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F1 reversal (2026-08-21 functions-and-services plan follow-up): the door a generated host's own
/// report of a public call arrives through. The owner's exact complaint was "I cannot tell whether it
/// ran at all" — the priority here is the same one <c>CustomEventIngestServiceTests</c> already proved
/// for the other anonymous door: tenancy holds in both directions, and the row this writes is honestly
/// marked as something the panel never watched happen.
/// </summary>
public class FunctionInvocationReportServiceTests
{
    private static readonly PassthroughProtector Protector = new();

    private sealed record World(Guid WorkspaceId, App App, FunctionDefinition Function, string PlaintextSecret);

    private static HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("fn-report-" + Guid.NewGuid()).Options);

    private static async Task<World> SeedAppAsync(
        HarboraDbContext db, string label, bool asFunctionApp = true, bool withFunction = true)
    {
        var workspace = Guid.CreateVersion7();
        var plan = new Plan { Name = "Starter-" + label, IsEnabled = true };
        db.Plans.Add(plan);
        db.Workspaces.Add(new Workspace { Id = workspace, Name = label, Slug = "ws-" + label, PlanId = plan.Id });

        var secret = "secret-" + label;
        var app = new App
        {
            WorkspaceId = workspace,
            Name = "fns-" + label,
            Slug = "fns-" + label,
            SourceType = asFunctionApp ? AppSourceType.InlineCode : AppSourceType.GitRepository,
            FunctionRuntime = FunctionRuntime.CSharp,
            FunctionInvokeSecret = Protector.Protect(secret),
            ActiveDeploymentId = Guid.CreateVersion7()
        };
        db.Apps.Add(app);

        var fn = new FunctionDefinition
        {
            AppId = app.Id, WorkspaceId = workspace, Name = "hello", Slug = "hello",
            Trigger = FunctionTrigger.Http, Code = "// code", IsEnabled = true, IsPublic = true
        };
        if (withFunction) db.FunctionDefinitions.Add(fn);

        await db.SaveChangesAsync();
        return new World(workspace, app, fn, secret);
    }

    private static FunctionInvocationReportService ServiceFor(HarboraDbContext db) =>
        new(db, Protector, NullLogger<FunctionInvocationReportService>.Instance);

    // ------------------------------------------------------------------------------- the happy path

    [Fact]
    public async Task A_correctly_signed_report_writes_an_invocation_row_marked_as_a_public_call()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme");

        var outcome = await ServiceFor(db).ReportAsync(
            world.App.Id, world.PlaintextSecret,
            new FunctionInvocationReportRequest("hello", 200, 42, null), default);

        outcome.Should().Be(FunctionInvocationReportOutcome.Accepted);
        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        invocation.FunctionId.Should().Be(world.Function.Id);
        invocation.AppId.Should().Be(world.App.Id);
        invocation.WorkspaceId.Should().Be(world.WorkspaceId);
        invocation.Trigger.Should().Be(FunctionTrigger.Http);
        invocation.Origin.Should().Be(FunctionInvocationOrigin.PublicCall,
            "nobody at the panel watched this call — only the host's own account of it");
        invocation.StatusCode.Should().Be(200);
        invocation.DurationMs.Should().Be(42);
        invocation.Succeeded.Should().BeTrue();
        invocation.CompletedAt.Should().NotBeNull("a reported call is already finished by the time it is reported");
    }

    [Fact]
    public async Task A_reported_failure_is_recorded_as_failed_with_its_error()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-fail");

        await ServiceFor(db).ReportAsync(
            world.App.Id, world.PlaintextSecret,
            new FunctionInvocationReportRequest("hello", 500, 12, "The function threw."), default);

        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        invocation.Succeeded.Should().BeFalse();
        invocation.StatusCode.Should().Be(500);
        invocation.Error.Should().Be("The function threw.");
    }

    [Fact]
    public async Task A_null_status_code_from_a_host_that_never_got_a_response_out_is_not_a_success()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-null-status");

        await ServiceFor(db).ReportAsync(
            world.App.Id, world.PlaintextSecret,
            new FunctionInvocationReportRequest("hello", null, 5000, "timed out"), default);

        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        invocation.Succeeded.Should().BeFalse();
        invocation.StatusCode.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------- tenancy: in

    [Fact]
    public async Task A_foreign_workspaces_secret_cannot_report_into_this_apps_id()
    {
        var db = NewDb();
        var acme = await SeedAppAsync(db, "acme-in");
        var globex = await SeedAppAsync(db, "globex-in");

        var outcome = await ServiceFor(db).ReportAsync(
            acme.App.Id, globex.PlaintextSecret,
            new FunctionInvocationReportRequest("hello", 200, 1, null), default);

        outcome.Should().Be(FunctionInvocationReportOutcome.Unauthorized);
        (await db.FunctionInvocations.IgnoreQueryFilters().CountAsync()).Should().Be(0,
            "a foreign secret proves nothing about acme's app, so nothing may be recorded against it");
    }

    [Fact]
    public async Task An_unknown_app_id_is_refused()
    {
        var db = NewDb();
        await SeedAppAsync(db, "acme-unknown");

        var outcome = await ServiceFor(db).ReportAsync(
            Guid.CreateVersion7(), "whatever", new FunctionInvocationReportRequest("hello", 200, 1, null), default);

        outcome.Should().Be(FunctionInvocationReportOutcome.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_secret_for_a_real_app_id_is_refused()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-wrong");

        var outcome = await ServiceFor(db).ReportAsync(
            world.App.Id, "not-the-secret", new FunctionInvocationReportRequest("hello", 200, 1, null), default);

        outcome.Should().Be(FunctionInvocationReportOutcome.Unauthorized);
    }

    [Fact]
    public async Task An_app_that_is_not_a_function_app_cannot_report()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "not-a-function-app", asFunctionApp: false);

        var outcome = await ServiceFor(db).ReportAsync(
            world.App.Id, world.PlaintextSecret, new FunctionInvocationReportRequest("hello", 200, 1, null), default);

        outcome.Should().Be(FunctionInvocationReportOutcome.Unauthorized);
    }

    // --------------------------------------------------------------------------------- tenancy: out

    [Fact]
    public async Task A_report_lands_only_in_its_own_workspace_never_the_other()
    {
        var db = NewDb();
        var acme = await SeedAppAsync(db, "acme-out");
        var globex = await SeedAppAsync(db, "globex-out");

        await ServiceFor(db).ReportAsync(
            acme.App.Id, acme.PlaintextSecret,
            new FunctionInvocationReportRequest("hello", 200, 1, null), default);

        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        invocation.WorkspaceId.Should().Be(acme.WorkspaceId);
        invocation.WorkspaceId.Should().NotBe(globex.WorkspaceId);
    }

    // ------------------------------------------------------------------------------- unknown slug

    [Fact]
    public async Task A_slug_that_matches_no_function_in_this_app_is_refused_without_writing_a_row()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-badslug");

        var outcome = await ServiceFor(db).ReportAsync(
            world.App.Id, world.PlaintextSecret,
            new FunctionInvocationReportRequest("does-not-exist", 200, 1, null), default);

        outcome.Should().Be(FunctionInvocationReportOutcome.UnknownFunction);
        (await db.FunctionInvocations.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_missing_slug_is_refused_before_any_authenticated_lookup_matters()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-noslug");

        var outcome = await ServiceFor(db).ReportAsync(
            world.App.Id, world.PlaintextSecret,
            new FunctionInvocationReportRequest(null, 200, 1, null), default);

        outcome.Should().Be(FunctionInvocationReportOutcome.UnknownFunction);
    }

    // ----------------------------------------------------------------------------- honest clamping

    [Fact]
    public async Task A_negative_duration_never_produces_a_row_that_started_after_it_completed()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-negative-duration");

        await ServiceFor(db).ReportAsync(
            world.App.Id, world.PlaintextSecret,
            new FunctionInvocationReportRequest("hello", 200, -50, null), default);

        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        invocation.DurationMs.Should().Be(0);
        invocation.StartedAt.Should().BeOnOrBefore(invocation.CompletedAt!.Value);
    }

    [Fact]
    public async Task An_overlong_error_is_truncated_the_same_way_a_panel_made_calls_error_already_is()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-long-error");
        var longError = new string('x', 2000);

        await ServiceFor(db).ReportAsync(
            world.App.Id, world.PlaintextSecret,
            new FunctionInvocationReportRequest("hello", 500, 1, longError), default);

        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        invocation.Error!.Length.Should().Be(900);
    }
}
