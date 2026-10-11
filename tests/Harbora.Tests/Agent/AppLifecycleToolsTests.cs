using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The tools that act on a running app — deploy, restart, stop, rollback, delete: what the card says,
/// what is queued (and that it is reported as queued, never as done), the refusals in both languages,
/// and a refusal at execution after the world changed under the card.
/// </summary>
public class AppLifecycleToolsTests
{
    // ---- deploy_app -----------------------------------------------------------------------------

    [Fact]
    public async Task Deploying_queues_as_the_assistant_and_links_the_deployment_without_waiting_for_it()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.DeployApp(), new { app = "web" });
        var result = await h.ExecuteAsync(h.DeployApp(), new { app = "web" });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Deploy web");
        verdict.TitleFa.Should().Be("استقرار web");
        verdict.DetailEn.Should().Be($"Pulls nginx:alpine again and releases web in {h.ProjectSlug}/production.");
        verdict.WarningsEn.Should().Contain("A deploy replaces the app's running containers with the new version.");

        result.Ok.Should().BeTrue(result.Error);
        var queued = h.Deployments.Queued.Should().ContainSingle().Subject;
        queued.Trigger.Should().Be(DeploymentTrigger.Assistant);
        queued.TriggeredByUserId.Should().Be(h.UserId);
        queued.GitRef.Should().Be("main");
        var third = await h.Db.Deployments.AsNoTracking().SingleAsync(d => d.AppId == app.Id && d.Number == 3);
        result.Links![0].Url.Should().Be($"/deployments/details/{third.Id}");
        result.SummaryEn.Should().Be("Deploy of 'web' requested: deployment #3 is queued.");
        result.SummaryFa.Should().Be("استقرار «web» درخواست شد: استقرار شمارهٔ 3 در صف است.");
        result.ResultJson.Should().Contain("\"joinedExisting\":false").And.Contain("\"status\":\"queued\"");
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.deploy" && e.TargetId == app.Id.ToString());
    }

    [Fact]
    public async Task In_a_protected_environment_the_card_warns_and_the_result_says_it_waits_for_approval()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        h.MakeProtected();

        var verdict = await h.ValidateAsync(h.DeployApp(), new { app = "web" });
        var result = await h.ExecuteAsync(h.DeployApp(), new { app = "web" });

        verdict.WarningsEn.Should().Contain(w => w.Contains("Environment 'production' is protected"));
        verdict.WarningsFa.Should().Contain(w => w.Contains("محافظت‌شده"));
        result.SummaryEn.Should().Contain("deployment #3 is waiting for approval");
        result.ResultJson.Should().Contain("pending_approval");
    }

    [Fact]
    public async Task A_deployment_already_in_flight_is_joined_and_the_result_says_nothing_new_was_queued()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        h.Db.Deployments.Add(new Deployment { AppId = app.Id, WorkspaceId = h.WorkspaceId, Number = 3, Status = DeploymentStatus.Building });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var verdict = await h.ValidateAsync(h.DeployApp(), new { app = "web" });
        var result = await h.ExecuteAsync(h.DeployApp(), new { app = "web" });

        verdict.WarningsEn.Should().Contain(w => w.Contains("Deployment #3 is still in progress"));
        result.Ok.Should().BeTrue(result.Error);
        result.SummaryEn.Should().Contain("already in progress, so nothing new was queued").And.Contain("deployment #3 is building");
        result.ResultJson.Should().Contain("\"joinedExisting\":true");
        (await h.Db.Deployments.CountAsync(d => d.AppId == app.Id)).Should().Be(3);
    }

    [Fact]
    public async Task A_git_ref_for_an_app_that_is_not_built_from_git_is_refused()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.DeployApp(), new { app = "web", gitRef = "release" });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("not built from a Git repository");
    }

    [Fact]
    public async Task A_workspace_over_its_app_quota_is_refused_as_the_panel_refuses_it()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        h.SeedApp("other");
        h.Plan.MaxApps = 1;
        h.Db.Update(h.Plan);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.DeployApp(), new { app = "web" });
        var result = await h.ExecuteAsync(h.DeployApp(), new { app = "web" });

        verdict.Error.Should().Be("App limit reached (1).");
        result.Ok.Should().BeFalse();
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_preflight_refusal_at_queue_time_comes_back_in_both_languages_and_nothing_is_audited()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        h.Deployments.Refuse = new CapacityRefusedException(PlacementResult.Fail("No room on the node.", "روی سرور جا نیست."));

        var result = await h.ExecuteAsync(h.DeployApp(), new { app = "web" });

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("No room on the node.");
        result.ErrorFa.Should().Be("روی سرور جا نیست.");
        h.Agent.Audit.Entries.Should().NotContain(e => e.Action == "app.deploy");
    }

    [Fact]
    public async Task A_deploy_of_an_app_an_earlier_step_creates_validates_against_the_planned_app()
    {
        using var h = new AppToolsHarness();
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.App, "web", h.ProjectSlug, "production"));

        var verdict = await h.ValidateAsync(h.DeployApp(), new { app = "web" }, scope);

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.DetailEn.Should().Contain("created by an earlier step of this plan");
    }

    // ---- restart_app / stop_app -----------------------------------------------------------------

    [Fact]
    public async Task Restarting_validates_with_its_card_and_restarts_through_the_operations_service()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.RestartApp(), new { app = "web" });
        var result = await h.ExecuteAsync(h.RestartApp(), new { app = "web" });

        verdict.TitleEn.Should().Be("Restart web");
        verdict.TitleFa.Should().Be("راه‌اندازی دوبارهٔ web");
        result.Ok.Should().BeTrue(result.Error);
        h.Operations.Calls.Should().Equal(("restart", app.Id));
        result.SummaryEn.Should().Be("Restarted 'web'.");
        result.SummaryFa.Should().Be("«web» دوباره راه‌اندازی شد.");
    }

    [Fact]
    public async Task An_app_never_deployed_has_nothing_to_restart()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web", deployed: false);

        var verdict = await h.ValidateAsync(h.RestartApp(), new { app = "web" });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("has not been deployed yet");
        h.Operations.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_billing_gate_refuses_a_restart_in_both_languages_at_validation_and_at_execution()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        h.GateOverride = new RefusingGate();

        var verdict = await h.ValidateAsync(h.RestartApp(), new { app = "web" });

        verdict.Error.Should().Be("The wallet is empty.");
        verdict.ErrorFa.Should().Be("کیف پول خالی است.");

        h.GateOverride = null;
        h.Operations.RestartThrows = new QuotaRefusedException(new QuotaCheck(false, "Top up first.", "اول کیف پول را شارژ کنید."));
        var result = await h.ExecuteAsync(h.RestartApp(), new { app = "web" });

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("Top up first.");
        result.ErrorFa.Should().Be("اول کیف پول را شارژ کنید.");
    }

    [Fact]
    public async Task Stopping_warns_that_the_app_stops_serving_and_stops_it()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.StopApp(), new { app = "web" });
        var result = await h.ExecuteAsync(h.StopApp(), new { app = "web" });

        verdict.TitleEn.Should().Be("Stop web");
        verdict.TitleFa.Should().Be("توقف web");
        verdict.WarningsEn.Should().Contain("web stops serving requests until it is started again.");
        result.Ok.Should().BeTrue(result.Error);
        h.Operations.Calls.Should().Equal(("stop", app.Id));
        result.SummaryEn.Should().StartWith("Stopped 'web'.");
    }

    [Fact]
    public async Task An_app_already_stopped_is_not_stopped_again()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        (await h.Db.Apps.SingleAsync(a => a.Id == app.Id)).Status = AppStatus.Stopped;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var verdict = await h.ValidateAsync(h.StopApp(), new { app = "web" });
        var result = await h.ExecuteAsync(h.StopApp(), new { app = "web" });

        verdict.Error.Should().Be("App 'web' is already stopped.");
        result.Ok.Should().BeFalse();
        h.Operations.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_app_never_deployed_has_nothing_running_to_stop()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web", deployed: false);

        var verdict = await h.ValidateAsync(h.StopApp(), new { app = "web" });

        verdict.Error.Should().Contain("has not been deployed yet");
        verdict.ErrorFa.Should().Contain("هنوز مستقر نشده است");
    }

    // ---- rollback_app ---------------------------------------------------------------------------

    [Fact]
    public async Task By_default_it_rolls_back_to_the_previous_successful_deployment_and_says_exactly_what()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        var first = await h.Db.Deployments.AsNoTracking().SingleAsync(d => d.AppId == app.Id && d.Number == 1);

        var verdict = await h.ValidateAsync(h.RollbackApp(), new { app = "web" });
        var result = await h.ExecuteAsync(h.RollbackApp(), new { app = "web" });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Roll back web to deployment #1");
        verdict.TitleFa.Should().Be("بازگردانی web به استقرار شمارهٔ 1");
        verdict.DetailEn.Should().Contain("the image of #1 (commit aaaaaaaa “First release”)").And.Contain("replacing #2, which is live now");
        verdict.WarningsEn.Should().Contain(w => w.StartsWith("Only the code goes back"));

        result.Ok.Should().BeTrue(result.Error);
        var queued = h.Deployments.Queued.Should().ContainSingle().Subject;
        queued.RollbackToDeploymentId.Should().Be(first.Id);
        queued.Trigger.Should().Be(DeploymentTrigger.Assistant);
        (await h.Db.Deployments.AsNoTracking().SingleAsync(d => d.AppId == app.Id && d.Number == 3)).RolledBackFromId.Should().Be(first.Id);
        result.SummaryEn.Should().Be("Rollback of 'web' to #1 requested: deployment #3 is queued.");
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.rollback" && e.MetadataJson!.Contains(first.Id.ToString()));
    }

    [Theory]
    [InlineData(9, "has no deployment #9")]
    [InlineData(2, "Deployment #2 is already the live version.")]
    public async Task A_deployment_number_that_cannot_be_rolled_back_to_is_refused(int number, string expected)
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.RollbackApp(), new { app = "web", number });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain(expected);
        verdict.ErrorFa.Should().MatchRegex("[؀-ۿ]");
    }

    [Fact]
    public async Task A_pruned_image_is_refused_with_the_planners_reason_carried_into_persian()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        h.Docker.ForgetImage("harbora/web:1");

        var verdict = await h.ValidateAsync(h.RollbackApp(), new { app = "web" });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("no longer on the server");
        verdict.ErrorFa.Should().StartWith("بازگردانی ممکن نیست: ").And.Contain("no longer on the server");
    }

    [Fact]
    public async Task An_app_with_no_earlier_successful_deployment_has_nothing_to_roll_back_to()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web", deployed: false);

        var verdict = await h.ValidateAsync(h.RollbackApp(), new { app = "web" });

        verdict.Error.Should().Contain("has no earlier successful deployment");
    }

    [Fact]
    public async Task A_rollback_is_refused_while_a_deployment_is_in_flight_rather_than_joined_to_it()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        var first = await h.Db.Deployments.AsNoTracking().SingleAsync(d => d.AppId == app.Id && d.Number == 1);
        // A rollback already running — the engine would join a second one onto it, whatever it restores.
        h.Db.Deployments.Add(new Deployment
        {
            AppId = app.Id, WorkspaceId = h.WorkspaceId, Number = 3, Status = DeploymentStatus.Deploying, RolledBackFromId = first.Id
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var verdict = await h.ValidateAsync(h.RollbackApp(), new { app = "web", number = 1 });
        var result = await h.ExecuteAsync(h.RollbackApp(), new { app = "web", number = 1 });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Be("Deployment #3 of 'web' is still deploying. Wait for it to finish (or cancel it), then roll back.");
        verdict.ErrorFa.Should().MatchRegex("[؀-ۿ]");
        result.Ok.Should().BeFalse();
        h.Deployments.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task An_image_pruned_after_the_card_fails_the_rollback_and_queues_nothing()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        (await h.ValidateAsync(h.RollbackApp(), new { app = "web" })).Ok.Should().BeTrue();
        h.Docker.ForgetImage("harbora/web:1");
        var before = h.Snapshot();

        var result = await h.ExecuteAsync(h.RollbackApp(), new { app = "web" });

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("no longer on the server");
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_rollback_the_engine_will_not_coalesce_is_refused_in_both_languages()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        h.Deployments.Refuse = new InvalidOperationException("Deployment #3 is still building; a rollback will not be merged into it.");

        var result = await h.ExecuteAsync(h.RollbackApp(), new { app = "web" });

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("Deployment #3 is still building; a rollback will not be merged into it.");
        result.ErrorFa.Should().StartWith("استقرار در صف قرار نگرفت: ");
        h.Agent.Audit.Entries.Should().NotContain(e => e.Action == "app.rollback");
    }

    // ---- delete_app -----------------------------------------------------------------------------

    private static async Task AttachDatabaseAsync(AppToolsHarness h, Guid appId, string name)
    {
        var service = new ManagedService
        {
            WorkspaceId = h.WorkspaceId, EnvironmentId = h.EnvironmentId, ServerId = h.Server.Id, Name = name,
            Type = ManagedServiceType.PostgreSql, Version = "16", ContainerName = "harbora-svc-" + name, VolumeName = "v-" + name,
            Username = "harbora", EncryptedPassword = "enc:pw", DatabaseName = "shop"
        };
        h.Db.ManagedServices.Add(service);
        h.Db.AppManagedServices.Add(new AppManagedService { AppId = appId, ManagedServiceId = service.Id });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task The_delete_card_lists_what_goes_and_what_stays_and_is_confirmed_with_the_slug()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        await AttachDatabaseAsync(h, app.Id, "shop-db");

        var verdict = await h.ValidateAsync(h.DeleteApp(), new { app = "web" });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.ConfirmationPhrase.Should().Be("web");
        verdict.TitleEn.Should().Be("Delete app web");
        verdict.TitleFa.Should().Be("حذف اپ web");
        verdict.DetailEn.Should().Contain("domains web.example.org").And.Contain("volumes /files")
            .And.Contain("The volumes' data stays on the server.")
            .And.Contain("Attached databases are not deleted, only detached: shop-db.");
        verdict.DetailFa.Should().Contain("shop-db").And.Contain("روی سرور می‌ماند");
        verdict.WarningsEn.Should().Contain("This cannot be undone.").And.Contain("Its addresses stop answering: web.example.org.");
    }

    [Fact]
    public async Task Deleting_removes_the_app_keeps_its_volume_data_audits_and_links_the_project()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        await AttachDatabaseAsync(h, app.Id, "shop-db");

        var result = await h.ExecuteAsync(h.DeleteApp(), new { app = "web" });

        result.Ok.Should().BeTrue(result.Error);
        (await h.Db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == app.Id)).Should().BeFalse();
        (await h.Db.ManagedServices.AnyAsync(s => s.Name == "shop-db")).Should().BeTrue("an attached database stays");
        h.Docker.CountOf("RemoveVolumeAsync").Should().Be(0);
        result.SummaryEn.Should().Contain("Deleted app 'web'").And.Contain("still on the server");
        result.SummaryFa.Should().Contain("حذف شد");
        result.Links.Should().ContainSingle().Which.Url.Should().Be($"/projects/{h.ProjectId}");
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.delete" && e.MetadataJson == "{\"removeVolumes\":false}");
    }

    [Fact]
    public async Task Deleting_with_its_volumes_deletes_their_data_too()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.DeleteApp(), new { app = "web", removeVolumes = true });
        var result = await h.ExecuteAsync(h.DeleteApp(), new { app = "web", removeVolumes = true });

        verdict.DetailEn.Should().Contain("The volumes' data is deleted from the server.");
        verdict.WarningsEn.Should().Contain("The data in its volumes is deleted for good.");
        result.Ok.Should().BeTrue(result.Error);
        h.Docker.OperationsOn("harbora-vol-web-files").Should().Contain("RemoveVolumeAsync");
        result.SummaryEn.Should().Contain("with its volume data");
    }

    [Fact]
    public async Task A_protected_volume_refuses_removeVolumes_at_the_card_and_at_execution_and_the_app_stays()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        (await h.ValidateAsync(h.DeleteApp(), new { app = "web", removeVolumes = true })).Ok.Should().BeTrue();

        (await h.Db.Volumes.SingleAsync(v => v.AppId == app.Id)).Protected = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var before = h.Snapshot();

        var verdict = await h.ValidateAsync(h.DeleteApp(), new { app = "web", removeVolumes = true });
        var result = await h.ExecuteAsync(h.DeleteApp(), new { app = "web", removeVolumes = true });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain("Protected");
        result.Ok.Should().BeFalse("the volume became Protected after the card");
        result.ErrorFa.Should().MatchRegex("[؀-ۿ]");
        (await h.Db.Apps.AnyAsync(a => a.Id == app.Id)).Should().BeTrue();
        h.Snapshot().Should().Be(before);
        h.Operations.Calls.Should().BeEmpty("the protection is asked again before the delete is");
    }
}
