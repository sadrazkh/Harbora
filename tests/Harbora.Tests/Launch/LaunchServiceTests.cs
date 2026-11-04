using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Domain.Settings;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Verification;
using Harbora.Shared.Inspection;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Harbora.Tests.Launch.LaunchFixtures;

namespace Harbora.Tests.Launch;

/// <summary>
/// A launch from the outside: the real create services over one store, walked through its states by the
/// same calls a page and the tick make. Every call is a new instance (see <see cref="LaunchHarness"/>),
/// so all of these also say that nothing it needs lives anywhere but the row.
/// </summary>
public sealed class LaunchServiceTests
{
    private const string Token = "ghp_super-secret-token";

    // ---- the whole way through -----------------------------------------------------------------

    [Fact]
    public async Task With_a_database_it_creates_waits_deploys_checks_and_ends_on_the_address()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());

        var view = await h.ConfirmAsync(id, c => c with
        {
            EnvVars = c.EnvVars.Select(v => v.Key == "STRIPE_SECRET_KEY" ? v with { Value = "sk_live_abc" } : v).ToList()
        });

        view.Status.Should().Be(LaunchStatus.WaitingForServices);
        view.AppName.Should().Be("shop");
        view.IsTerminal.Should().BeFalse();
        h.Provisioner.Provisioned.Should().ContainSingle();
        h.DeploymentsQueued.Should().Be(0);

        var app = h.Store().Apps.IgnoreQueryFilters().Include(a => a.GitRepository).Single();
        app.GitRepository!.CloneUrl.Should().Be(LaunchHarness.CloneUrl);
        app.GitRepository.DefaultBranch.Should().Be("trunk", "what the source was read from, not a form's main");
        app.GitRef.Should().Be("trunk");
        app.ContainerPort.Should().Be(3000);
        app.ReleaseCommand.Should().Be("npx prisma migrate deploy");
        app.InstanceSizeKey.Should().Be(LaunchHarness.SizeKey);
        app.SourceType.Should().Be(AppSourceType.GitRepository);

        var join = h.Store().AppManagedServices.IgnoreQueryFilters().Single();
        ManagedServiceAttachEnv.ReadAliases(join.EnvAliasesJson).Should().Equal(new AttachEnvAlias("DB_HOST", "PGHOST"));

        await h.TickAsync();
        h.Row(id).Status.Should().Be(LaunchStatus.WaitingForServices, "the database has not answered");
        h.DeploymentsQueued.Should().Be(0);

        h.ServicesAre(ServiceStatus.Running);
        (await h.TickAsync()).Should().Be(1);
        var row = h.Row(id);
        row.Status.Should().Be(LaunchStatus.Deploying);
        row.DeploymentId.Should().NotBeNull();
        var deployment = h.Store().Deployments.IgnoreQueryFilters().Single();
        deployment.GitRef.Should().Be("trunk");
        deployment.TriggeredByUserId.Should().Be(h.UserId);

        h.DeploymentIs(row.DeploymentId!.Value, DeploymentStatus.Succeeded);
        await h.TickAsync();
        h.Row(id).Status.Should().Be(LaunchStatus.Verifying);
        h.Row(id).VerifyStartedAt.Should().NotBeNull();

        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();
        h.Row(id).Status.Should().Be(LaunchStatus.Succeeded);

        var done = await h.Request().GetAsync(id, default);
        done!.Status.Should().Be(LaunchStatus.Succeeded);
        done.IsTerminal.Should().BeTrue();
        done.CanRetry.Should().BeFalse();
        done.PublicUrl.Should().Be($"https://shop.{LaunchHarness.RootDomain}");
        done.Checks.Should().ContainSingle().Which.Key.Should().Be("running");
        done.Steps.Select(s => s.Key).Should().Equal(
            LaunchStepKeys.Source, LaunchStepKeys.Services, LaunchStepKeys.App, LaunchStepKeys.Deploy, LaunchStepKeys.Verify);
        done.Steps.Should().OnlyContain(s => s.State == LaunchStepState.Done);
    }

    [Fact]
    public async Task With_no_database_it_goes_straight_to_deploying_and_says_the_step_was_skipped()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());

        var view = await h.ConfirmAsync(id);

        view.Status.Should().Be(LaunchStatus.Deploying);
        view.Steps.Single(s => s.Key == LaunchStepKeys.Services).State.Should().Be(LaunchStepState.Skipped);
        h.Provisioner.Provisioned.Should().BeEmpty();

        await h.TickAsync();
        var row = h.Row(id);
        row.DeploymentId.Should().NotBeNull();

        h.DeploymentIs(row.DeploymentId!.Value, DeploymentStatus.Succeeded);
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();
        h.Row(id).Status.Should().Be(LaunchStatus.Succeeded);
    }

    [Fact]
    public async Task A_check_that_failed_ends_as_running_with_problems_never_as_a_success()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        h.Verifier.Next = app => LaunchHarness.ScriptedVerifier.Report(app,
            new AppCheck("running", AppCheckStatus.Passed, "Running", "در حال اجرا"),
            new AppCheck("url", AppCheckStatus.Failed, "Address", "آدرس", "No answer", "پاسخی نیامد"));

        await h.TickAsync();
        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Succeeded);
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();

        var view = await h.Request().GetAsync(id, default);
        view!.Status.Should().Be(LaunchStatus.SucceededWithProblems);
        view.Steps.Single(s => s.Key == LaunchStepKeys.Verify).State.Should().Be(LaunchStepState.Failed);
        view.Steps.Single(s => s.Key == LaunchStepKeys.Deploy).State.Should().Be(LaunchStepState.Done);
        view.Checks.Should().Contain(c => c.Key == "url" && c.Status == AppCheckStatus.Failed);
    }

    [Fact]
    public async Task A_check_still_settling_is_asked_again_and_given_its_deadline_and_what_it_already_found()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        h.Verifier.Next = app => LaunchHarness.ScriptedVerifier.Report(app,
            new AppCheck("url", AppCheckStatus.Pending, "Address", "آدرس")) with { DeploymentId = h.Row(id).DeploymentId };

        await h.TickAsync();
        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Succeeded);
        await h.TickAsync();
        h.Verifier.Runs.Should().Be(0, "the row was written a moment ago: one launch asks no more than every ten seconds");

        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();
        h.Verifier.Runs.Should().Be(1);
        h.Row(id).Status.Should().Be(LaunchStatus.Verifying, "Pending is not settled");
        h.Verifier.Asked[0].Previous.Should().BeNull();
        h.Verifier.Asked[0].GiveUpAt.Should().Be(h.Row(id).VerifyStartedAt!.Value + TimeSpan.FromMinutes(5));

        h.Advance(TimeSpan.FromSeconds(30));
        h.Verifier.Next = app => LaunchHarness.ScriptedVerifier.Report(app,
            new AppCheck("running", AppCheckStatus.Passed, "Running", "در حال اجرا"),
            new AppCheck("url", AppCheckStatus.Passed, "Address", "آدرس"));
        await h.TickAsync();
        h.Verifier.Asked[1].Previous.Should().NotBeNull("a passed check is not repeated within one run");
        h.Row(id).Status.Should().Be(LaunchStatus.Succeeded);
    }

    [Fact]
    public async Task A_run_that_could_not_ask_whether_it_is_running_is_never_green_and_says_it_could_not_check()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        h.Verifier.Next = app => LaunchHarness.ScriptedVerifier.Report(app,
            new AppCheck("running", AppCheckStatus.Skipped, "Could not tell", "نشد فهمید"),
            new AppCheck("restarts", AppCheckStatus.Skipped, "Could not tell", "نشد فهمید"),
            new AppCheck("logs", AppCheckStatus.Skipped, "Could not read", "نشد خواند"));

        await h.TickAsync();
        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Succeeded);
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();

        var view = await h.Request().GetAsync(id, default);
        view!.Status.Should().Be(LaunchStatus.SucceededWithProblems);
        var verify = view.Steps.Single(s => s.Key == LaunchStepKeys.Verify);
        verify.State.Should().Be(LaunchStepState.Failed);
        verify.Detail.Should().Contain("could not check");
        verify.Detail.Should().NotContain("found a problem");
        verify.DetailFa.Should().Contain("نتوانست");
    }

    [Fact]
    public async Task A_log_complaining_beside_an_address_that_works_is_not_green()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        h.Verifier.Next = app => LaunchHarness.ScriptedVerifier.Report(app,
            new AppCheck("running", AppCheckStatus.Passed, "Running", "در حال اجرا"),
            new AppCheck("url", AppCheckStatus.Passed, "Address", "آدرس"),
            new AppCheck("logs", AppCheckStatus.Warning, "Log", "لاگ", "ECONNREFUSED 127.0.0.1:5432", "ECONNREFUSED 127.0.0.1:5432"));

        await h.TickAsync();
        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Succeeded);
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();

        h.Row(id).Status.Should().Be(LaunchStatus.SucceededWithProblems);
        (await h.Request().GetAsync(id, default))!.Steps.Single(s => s.Key == LaunchStepKeys.Verify).Detail
            .Should().Contain("found a problem");
    }

    [Fact]
    public async Task A_worker_is_judged_by_its_running_check_and_a_scheduled_job_is_not()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare() with { Kind = DetectedAppKind.Worker, StartCommand = "node bot.js" });
        await h.ConfirmAsync(id);
        h.Verifier.Next = app => LaunchHarness.ScriptedVerifier.Report(app, new AppCheck("running", AppCheckStatus.Skipped, "Not running", "روشن نیست"));

        await h.TickAsync();
        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Succeeded);
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();
        h.Row(id).Status.Should().Be(LaunchStatus.SucceededWithProblems, "a worker that could not be seen running");

        // The same report about an app whose kind says nothing was ever meant to keep running.
        using (var db = h.Store())
        {
            db.Apps.IgnoreQueryFilters().Single().Kind = ServiceKind.Cron;
            db.SaveChanges();
        }
        h.ChangeRow(id, l => { l.Status = LaunchStatus.Verifying; l.VerifyStartedAt = h.Clock.UtcNow; });
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();
        h.Row(id).Status.Should().Be(LaunchStatus.Succeeded);
    }

    [Fact]
    public async Task A_fault_in_the_panels_own_run_is_asked_again_and_only_the_time_limit_ends_it_at_verify()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        h.Verifier.Next = app => LaunchHarness.ScriptedVerifier.Report(app,
            new AppCheck("running", AppCheckStatus.Pending, "The checks could not run", "بررسی‌ها اجرا نشدند")) with { CompletedAt = null };

        await h.TickAsync();
        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Succeeded);
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();
        h.Row(id).Status.Should().Be(LaunchStatus.Verifying, "nothing was found out, so nothing is announced");

        var runs = h.Verifier.Runs;
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();
        h.Verifier.Runs.Should().BeGreaterThan(runs);
        h.Row(id).Status.Should().Be(LaunchStatus.Verifying);

        h.Advance(TimeSpan.FromMinutes(16));
        await h.TickAsync();
        var row = h.Row(id);
        row.Status.Should().Be(LaunchStatus.Failed);
        row.FailedStep.Should().Be(LaunchStepKeys.Verify);
    }

    [Fact]
    public async Task A_blip_in_the_panels_own_run_that_clears_ends_on_what_the_next_run_found()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        h.Verifier.Next = app => LaunchHarness.ScriptedVerifier.Report(app,
            new AppCheck("running", AppCheckStatus.Pending, "The checks could not run", "بررسی‌ها اجرا نشدند")) with { CompletedAt = null };

        await h.TickAsync();
        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Succeeded);
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();

        h.Verifier.Next = null;
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();

        h.Row(id).Status.Should().Be(LaunchStatus.Succeeded);
    }

    // ---- what must not happen before its time --------------------------------------------------

    [Fact]
    public async Task No_deployment_is_queued_while_any_database_is_still_provisioning()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(TwoDatabases());
        await h.ConfirmAsync(id);

        for (var i = 0; i < 3; i++)
        {
            h.Advance(TimeSpan.FromMinutes(1));
            await h.TickAsync();
        }

        h.DeploymentsQueued.Should().Be(0);
        h.Store().Deployments.IgnoreQueryFilters().Should().BeEmpty();

        using (var db = h.Store())
        {
            var first = db.ManagedServices.IgnoreQueryFilters().OrderBy(s => s.CreatedAt).First();
            first.Status = ServiceStatus.Running;
            db.SaveChanges();
        }

        await h.TickAsync();
        h.DeploymentsQueued.Should().Be(0, "one of two is not all of them");

        h.ServicesAre(ServiceStatus.Running);
        await h.TickAsync();
        h.DeploymentsQueued.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_provision_stops_the_launch_quoting_why_and_links_to_that_database()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        await h.ConfirmAsync(id);

        h.ServicesAre(ServiceStatus.Failed, "no space left on device");
        await h.TickAsync();

        h.DeploymentsQueued.Should().Be(0);
        var view = await h.Request().GetAsync(id, default);
        view!.Status.Should().Be(LaunchStatus.Failed);
        view.FailureMessage.Should().Contain("no space left on device");
        view.FailureMessageFa.Should().Contain("no space left on device");
        var step = view.Steps.Single(s => s.Key == LaunchStepKeys.Services);
        step.State.Should().Be(LaunchStepState.Failed);
        var service = h.Store().ManagedServices.IgnoreQueryFilters().Single();
        step.LinkPath.Should().Be($"/databases/{service.Id}");
        view.CanRetry.Should().BeTrue();
    }

    [Fact]
    public async Task Trying_a_failed_provision_again_asks_for_it_again_instead_of_failing_on_the_old_answer()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        await h.ConfirmAsync(id);
        h.ServicesAre(ServiceStatus.Failed, "no space left on device");
        await h.TickAsync();

        var retried = await h.Request().RetryAsync(id, null, default);

        retried.Ok.Should().BeTrue();
        retried.Value!.Status.Should().Be(LaunchStatus.WaitingForServices);
        h.Provisioner.Provisioned.Should().HaveCount(2, "the first attempt and this one");
        h.Store().ManagedServices.IgnoreQueryFilters().Should().ContainSingle()
            .Which.Status.Should().Be(ServiceStatus.Provisioning, "or the next tick reads the old Failed");
        h.Creates.DatabasesCreated.Should().Be(1);

        await h.TickAsync();
        h.Row(id).Status.Should().Be(LaunchStatus.WaitingForServices);
    }

    [Fact]
    public async Task A_failed_deployment_stops_the_launch_with_its_message_and_a_link_to_its_log()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        await h.TickAsync();
        var deploymentId = h.Row(id).DeploymentId!.Value;

        h.DeploymentIs(deploymentId, DeploymentStatus.Failed, "npm ci exited with 1");
        await h.TickAsync();

        var view = await h.Request().GetAsync(id, default);
        view!.Status.Should().Be(LaunchStatus.Failed);
        view.FailureMessage.Should().Contain("npm ci exited with 1");
        var step = view.Steps.Single(s => s.Key == LaunchStepKeys.Deploy);
        step.State.Should().Be(LaunchStepState.Failed);
        step.LinkPath.Should().Be($"/deployments/details/{deploymentId}");
        view.CanRetry.Should().BeTrue();

        h.Advance(TimeSpan.FromMinutes(1));
        var retried = await h.Request().RetryAsync(id, null, default);
        retried.Value!.Status.Should().Be(LaunchStatus.Deploying);
        retried.Value.FailureMessage.Should().BeNull();

        await h.TickAsync();
        h.DeploymentsQueued.Should().Be(2, "a failed deployment is not one to adopt");
        h.Row(id).DeploymentId.Should().NotBe(deploymentId);
        h.Creates.AppsCreated.Should().Be(1);
    }

    [Fact]
    public async Task An_upload_hands_its_archive_to_the_deployment_and_a_failure_cannot_be_retried()
    {
        using var h = new LaunchHarness();
        var (id, archive) = await h.ReadyUploadAsync(Bare());
        File.Exists(archive).Should().BeTrue("the deployment still needs it");
        (await h.Request().ProposeAsync(id, default))!.AppName.Should().Be("my-shop");

        await h.ConfirmAsync(id);
        h.Store().Apps.IgnoreQueryFilters().Single().SourceType.Should().Be(AppSourceType.Upload);
        await h.TickAsync();

        var row = h.Row(id);
        row.ArchivePath.Should().BeNull("it belongs to the deployment now");
        h.Store().Deployments.IgnoreQueryFilters().Single().SourceArchivePath.Should().Be(archive);

        h.DeploymentIs(row.DeploymentId!.Value, DeploymentStatus.Failed, "no Dockerfile");
        await h.TickAsync();

        var view = await h.Request().GetAsync(id, default);
        view!.CanRetry.Should().BeFalse();
        view.FailureMessage.Should().Contain("upload a new version");

        var retried = await h.Request().RetryAsync(id, null, default);
        retried.Ok.Should().BeFalse();
        h.DeploymentsQueued.Should().Be(1);
    }

    [Fact]
    public async Task A_refused_queue_fails_in_both_languages_and_keeps_the_archive_for_a_retry()
    {
        using var h = new LaunchHarness();
        var (id, archive) = await h.ReadyUploadAsync(Bare());
        await h.ConfirmAsync(id);

        h.RefuseNextDeployment = new QuotaRefusedException(QuotaCheck.Deny("Two deployments are running.", "دو استقرار در جریان است."));
        await h.TickAsync();

        var view = await h.Request().GetAsync(id, default);
        view!.Status.Should().Be(LaunchStatus.Failed);
        view.FailureMessage.Should().Contain("Two deployments are running.");
        view.FailureMessageFa.Should().Contain("دو استقرار در جریان است.");
        view.CanRetry.Should().BeTrue("the deployment never began, so the archive was not consumed");
        h.Row(id).ArchivePath.Should().Be(archive);

        h.Advance(TimeSpan.FromMinutes(1));
        (await h.Request().RetryAsync(id, null, default)).Ok.Should().BeTrue();
        await h.TickAsync();
        h.Store().Deployments.IgnoreQueryFilters().Single().SourceArchivePath.Should().Be(archive);
    }

    [Fact]
    public async Task Any_other_invalid_operation_from_the_queue_is_a_failure_a_person_can_read()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);

        h.RefuseNextDeployment = new InvalidOperationException("A rollback is running.");
        await h.TickAsync();

        var row = h.Row(id);
        row.Status.Should().Be(LaunchStatus.Failed);
        row.FailedStep.Should().Be(LaunchStepKeys.Deploy);
        row.FailureMessage.Should().Contain("A rollback is running.");
        row.FailureMessageFa.Should().Contain("A rollback is running.");
    }

    [Fact]
    public async Task A_deployment_waiting_for_approval_is_not_a_failure_and_the_step_says_so()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        await h.TickAsync();
        var deploymentId = h.Row(id).DeploymentId!.Value;

        h.DeploymentIs(deploymentId, DeploymentStatus.PendingApproval);
        h.Advance(TimeSpan.FromDays(2));
        await h.TickAsync();

        var view = await h.Request().GetAsync(id, default);
        view!.Status.Should().Be(LaunchStatus.Deploying);
        var step = view.Steps.Single(s => s.Key == LaunchStepKeys.Deploy);
        step.State.Should().Be(LaunchStepState.Running);
        step.Detail.Should().Contain("approve");
        step.DetailFa.Should().Contain("تأیید");
        step.LinkPath.Should().Be($"/deployments/details/{deploymentId}");
    }

    // ---- part-way, twice, and after a restart ---------------------------------------------------

    [Fact]
    public async Task A_refusal_part_way_names_what_exists_and_a_retry_creates_only_what_is_missing()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(TwoDatabases(), token: Token);
        h.Creates.RefuseSecondDatabase = new ServiceRefusal("The plan is full.", "پلن پر است.");

        var first = await h.ConfirmAsync(id);

        first.Status.Should().Be(LaunchStatus.Failed);
        first.FailureMessage.Should().Contain("The plan is full.").And.Contain("shop-postgresql");
        first.FailureMessageFa.Should().Contain("پلن پر است.").And.Contain("shop-postgresql");
        first.Steps.Single(s => s.Key == LaunchStepKeys.Services).State.Should().Be(LaunchStepState.Failed);
        first.CanRetry.Should().BeTrue();
        h.Store().ManagedServices.IgnoreQueryFilters().Should().ContainSingle("nothing is rolled back: a database that exists is named");
        h.Store().Apps.IgnoreQueryFilters().Should().BeEmpty();
        h.Row(id).EncryptedGitToken.Should().NotBeNull("no app has it yet, and a retry still needs it");

        var retried = await h.Request().RetryAsync(id, "203.0.113.9", default);

        retried.Value!.Status.Should().Be(LaunchStatus.WaitingForServices);
        h.Store().ManagedServices.IgnoreQueryFilters().Should().HaveCount(2);
        h.Store().Apps.IgnoreQueryFilters().Should().ContainSingle();
        h.Store().AppManagedServices.IgnoreQueryFilters().Should().HaveCount(2);
        h.Creates.DatabasesCreated.Should().Be(2);
        h.Creates.AppsCreated.Should().Be(1);
        h.Row(id).EncryptedGitToken.Should().BeNull();

        var repository = h.Store().GitRepositories.IgnoreQueryFilters().Include(r => r.Provider).Single();
        new PassthroughProtector().Unprotect(repository.Provider!.EncryptedCredential!).Should().Be(Token,
            "the token survived the failure and reached the app");
    }

    [Fact]
    public async Task A_refused_app_keeps_the_database_and_a_retry_does_not_make_a_second_one()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        h.Creates.RefuseApp = new ServiceRefusal("No address left.", "نشانی نمانده است.");

        var first = await h.ConfirmAsync(id);

        first.Status.Should().Be(LaunchStatus.Failed);
        h.Row(id).FailedStep.Should().Be(LaunchStepKeys.App);
        first.Steps.Single(s => s.Key == LaunchStepKeys.Services).State.Should().Be(LaunchStepState.Done);
        first.Steps.Single(s => s.Key == LaunchStepKeys.App).State.Should().Be(LaunchStepState.Failed);

        (await h.Request().RetryAsync(id, null, default)).Value!.Status.Should().Be(LaunchStatus.WaitingForServices);

        h.Creates.DatabasesCreated.Should().Be(1);
        h.Store().AppManagedServices.IgnoreQueryFilters().Should().ContainSingle();
    }

    [Fact]
    public async Task A_create_that_throws_is_a_failed_step_not_a_launch_stuck_creating()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        h.Creates.ThrowFromApp = new DbUpdateException("23505: duplicate key value violates unique constraint");

        var view = await h.ConfirmAsync(id);

        view.Status.Should().Be(LaunchStatus.Failed);
        view.FailureMessage.Should().Contain("duplicate key");
        view.CanRetry.Should().BeTrue();
    }

    [Fact]
    public async Task A_retry_cannot_recall_what_was_typed_so_it_says_so_and_names_what_is_not_set()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        h.Creates.RefuseApp = new ServiceRefusal("No address left.", "نشانی نمانده است.");

        var failed = await h.ConfirmAsync(id, c => c with
        {
            EnvVars = c.EnvVars.Select(v => v.Key == "STRIPE_SECRET_KEY" ? v with { Value = "sk_live_abc" } : v).ToList()
        });

        failed.FailureMessage.Should().Contain("STRIPE_SECRET_KEY was not kept").And.NotContain("LOG_LEVEL",
            "that one was left as the example file has it, so a retry reads it again");
        failed.FailureMessage.Should().NotContain("sk_live_abc");
        failed.FailureMessageFa.Should().Contain("STRIPE_SECRET_KEY").And.NotContain("sk_live_abc");
        h.Row(id).ChoicesJson.Should().NotContain("sk_live_abc");

        var retried = (await h.Request().RetryAsync(id, null, default)).Value!;

        var variables = h.Store().EnvironmentVariables.IgnoreQueryFilters().ToList();
        variables.Single(v => v.Key == "LOG_LEVEL").Value.Should().Be("info", "that one is in the example file");
        variables.Should().Contain(v => v.Key == "JWT_SECRET" && v.IsSecret, "that one is generated");
        variables.Should().NotContain(v => v.Key == "STRIPE_SECRET_KEY");
        retried.Steps.Single(s => s.Key == LaunchStepKeys.App).Detail.Should().Contain("STRIPE_SECRET_KEY");
        retried.Steps.Single(s => s.Key == LaunchStepKeys.App).DetailFa.Should().Contain("STRIPE_SECRET_KEY");
    }

    [Fact]
    public async Task Confirming_twice_creates_nothing_twice_and_the_second_lands_on_the_same_launch()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        var choices = await h.DefaultsAsync(id);

        var first = await h.Request().ConfirmAsync(id, choices, null, default);
        var second = await h.Request().ConfirmAsync(id, choices, null, default);

        first.Ok.Should().BeTrue();
        second.Ok.Should().BeTrue("a second submit lands on the progress page, not on an error");
        second.Value!.Id.Should().Be(id);
        h.Creates.DatabasesCreated.Should().Be(1);
        h.Creates.AppsCreated.Should().Be(1);
        h.Store().Apps.IgnoreQueryFilters().Should().ContainSingle();
        h.Audit.Entries.Count(e => e.Action == "launch.confirmed").Should().Be(1);
    }

    [Fact]
    public async Task A_restart_between_every_step_simply_carries_on()
    {
        using var h = new LaunchHarness();

        // Each line is a new context and a new service over the same store: a panel that came back.
        h.Reader.Inspection = NodeWithPostgres();
        var id = (await h.Request().StartAsync(h.GitRequest(), default)).Value;
        h.Row(id).Status.Should().Be(LaunchStatus.Inspecting);

        await h.Worker().InspectAsync(id, default);
        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);

        var choices = (await h.Request().ProposeAsync(id, default))!.Defaults;
        (await h.Request().ConfirmAsync(id, choices, null, default)).Ok.Should().BeTrue();
        h.Row(id).Status.Should().Be(LaunchStatus.WaitingForServices);

        await h.Worker().AdvanceAllAsync(default);
        h.Row(id).Status.Should().Be(LaunchStatus.WaitingForServices);

        h.ServicesAre(ServiceStatus.Running);
        await h.Worker().AdvanceAllAsync(default);
        h.Row(id).Status.Should().Be(LaunchStatus.Deploying);

        await h.Worker().AdvanceAllAsync(default);
        h.DeploymentsQueued.Should().Be(1, "a restart with a deployment already known does not queue another");

        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Succeeded);
        await h.Worker().AdvanceAllAsync(default);
        h.Row(id).Status.Should().Be(LaunchStatus.Verifying);

        h.Advance(TimeSpan.FromSeconds(30));
        await h.Worker().AdvanceAllAsync(default);
        h.Row(id).Status.Should().Be(LaunchStatus.Succeeded);
    }

    [Fact]
    public async Task A_panel_that_died_after_queueing_finds_the_deployment_instead_of_queueing_another()
    {
        using var h = new LaunchHarness();
        var (id, _) = await h.ReadyUploadAsync(Bare());
        await h.ConfirmAsync(id);
        await h.TickAsync();

        // The queue committed; the write of its id on the launch is what never happened.
        var deploymentId = h.Row(id).DeploymentId!.Value;
        h.ChangeRow(id, l =>
        {
            l.DeploymentId = null;
            l.StatusChangedAt = h.Clock.UtcNow - TimeSpan.FromMinutes(1);
        });

        await h.TickAsync();

        h.DeploymentsQueued.Should().Be(1);
        h.Row(id).DeploymentId.Should().Be(deploymentId);
    }

    [Fact]
    public async Task A_create_interrupted_by_a_restart_is_handed_back_and_a_retry_finishes_it()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(TwoDatabases());
        var choices = await h.DefaultsAsync(id);

        // Stopped dead after the first database, with no request left to write anything.
        h.Creates.ThrowFromApp = null;
        h.Creates.RefuseSecondDatabase = new ServiceRefusal("unused", "unused");
        await h.Request().ConfirmAsync(id, choices, null, default);
        h.ChangeRow(id, l =>
        {
            l.Status = LaunchStatus.Creating;
            l.FailedStep = null;
            l.FailureMessage = null;
            l.FailureMessageFa = null;
        });

        h.Advance(TimeSpan.FromMinutes(31));
        await h.TickAsync();

        var row = h.Row(id);
        row.Status.Should().Be(LaunchStatus.Failed);
        row.FailedStep.Should().Be(LaunchStepKeys.Services);
        row.FailureMessage.Should().Contain("Try again");

        h.Advance(TimeSpan.FromMinutes(1));
        (await h.Request().RetryAsync(id, null, default)).Value!.Status.Should().Be(LaunchStatus.WaitingForServices);
        h.Creates.DatabasesCreated.Should().Be(2);
        h.Creates.AppsCreated.Should().Be(1);
    }

    [Fact]
    public async Task One_launchs_exception_is_logged_and_does_not_stop_the_others()
    {
        using var h = new LaunchHarness();
        var bad = await h.ReadyGitAsync(Bare(), url: "https://git.example.com/acme/bad.git");
        var good = await h.ReadyGitAsync(Bare(), url: "https://git.example.com/acme/good.git");
        await h.ConfirmAsync(bad);
        await h.ConfirmAsync(good);
        await h.TickAsync();
        h.DeploymentIs(h.Row(bad).DeploymentId!.Value, DeploymentStatus.Succeeded);
        h.DeploymentIs(h.Row(good).DeploymentId!.Value, DeploymentStatus.Succeeded);
        await h.TickAsync();

        var badApp = h.Row(bad).AppId;
        h.Verifier.ThrowFor = app => app == badApp ? new InvalidOperationException("the daemon went away") : null;
        h.Advance(TimeSpan.FromSeconds(30));

        (await h.TickAsync()).Should().Be(1);

        h.Row(good).Status.Should().Be(LaunchStatus.Succeeded);
        h.Row(bad).Status.Should().Be(LaunchStatus.Verifying);
        h.Logs.Lines.Should().Contain(l => l.Contains("the daemon went away"));

        // And it does not stay there for ever.
        h.Advance(TimeSpan.FromMinutes(16));
        await h.TickAsync();
        h.Row(bad).Status.Should().Be(LaunchStatus.Failed);
        h.Row(bad).FailedStep.Should().Be(LaunchStepKeys.Verify);
    }

    [Fact]
    public async Task Retrying_the_checks_asks_them_again_with_a_fresh_deadline()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        await h.TickAsync();
        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Succeeded);
        await h.TickAsync();
        h.Verifier.ThrowFor = _ => new InvalidOperationException("the daemon went away");
        h.Advance(TimeSpan.FromMinutes(16));
        await h.TickAsync();
        var view = await h.Request().GetAsync(id, default);
        view!.CanRetry.Should().BeTrue();
        view.PublicUrl.Should().NotBeNull("it deployed: the address is real even though the checks did not finish");

        h.Verifier.ThrowFor = null;
        h.Advance(TimeSpan.FromMinutes(1));
        (await h.Request().RetryAsync(id, null, default)).Value!.Status.Should().Be(LaunchStatus.Verifying);
        h.Advance(TimeSpan.FromSeconds(30));
        await h.TickAsync();

        h.Row(id).Status.Should().Be(LaunchStatus.Succeeded);
        h.DeploymentsQueued.Should().Be(1, "the checks' retry does not deploy again");
    }

    // ---- tenancy -------------------------------------------------------------------------------

    [Fact]
    public async Task Another_workspaces_launch_reads_as_nothing_and_can_be_changed_by_nobody()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        var choices = await h.DefaultsAsync(id);

        (await h.OtherWorkspace().GetAsync(id, default)).Should().BeNull();
        (await h.OtherWorkspace().ProposeAsync(id, default)).Should().BeNull();
        (await h.OtherWorkspace().ConfirmAsync(id, choices, null, default)).Ok.Should().BeFalse();
        (await h.OtherWorkspace().RetryAsync(id, null, default)).Ok.Should().BeFalse();
        (await h.OtherWorkspace().AbandonAsync(id, default)).Should().BeFalse();
        (await h.OtherWorkspace().ValidateConfirmAsync(id, choices, default)).Should().NotBeEmpty();
        (await h.OtherWorkspace().QuoteAsync(id, choices, default)).Refusal.Should().NotBeNull();

        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        h.Store().Apps.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_request_naming_somebody_else_is_refused_before_anything_is_written()
    {
        using var h = new LaunchHarness();

        var outcome = await h.Request().StartAsync(h.GitRequest() with { WorkspaceId = h.OtherWorkspaceId }, default);

        outcome.Ok.Should().BeFalse();
        h.Store().AppLaunches.IgnoreQueryFilters().Should().BeEmpty();
        h.Jobs.Enqueued.Should().BeEmpty();
    }

    // ---- the token -----------------------------------------------------------------------------

    [Fact]
    public async Task A_url_carrying_credentials_never_shows_them_anywhere()
    {
        using var h = new LaunchHarness();
        var withCredentials = $"https://x-access-token:{Token}@git.example.com/acme/shop.git";

        var id = await h.ReadyGitAsync(NodeWithPostgres(), url: withCredentials);

        var row = h.Row(id);
        row.CloneUrl.Should().Be(LaunchHarness.CloneUrl);
        row.EncryptedGitToken.Should().NotBeNull();
        new PassthroughProtector().Unprotect(row.EncryptedGitToken!).Should().Be(Token, "moved, not lost");
        h.Git.Checkouts.Single().Url.Should().Be(LaunchHarness.CloneUrl);
        h.Git.Checkouts.Single().Token.Should().Be(Token);
        h.Audit.Entries.Single(e => e.Action == "launch.started").MetadataJson.Should().NotContain(Token);

        var before = await h.Request().GetAsync(id, default);
        JsonSerializer.Serialize(before).Should().NotContain(Token);
        before!.SourceLabel.Should().Be(LaunchHarness.CloneUrl);
        JsonSerializer.Serialize(await h.Request().ProposeAsync(id, default)).Should().NotContain(Token);

        await h.ConfirmAsync(id);

        var confirmed = h.Row(id);
        confirmed.EncryptedGitToken.Should().BeNull("it has been handed to the app's Git provider row");
        confirmed.ChoicesJson.Should().NotContain(Token);
        confirmed.CloneUrl.Should().NotContain(Token);
        JsonSerializer.Serialize(await h.Request().GetAsync(id, default)).Should().NotContain(Token);
        h.Audit.Entries.Should().OnlyContain(e => !(e.MetadataJson ?? "").Contains(Token) && !(e.TargetId ?? "").Contains(Token));
        h.Logs.Lines.Should().NotContain(l => l.Contains(Token));

        var repository = h.Store().GitRepositories.IgnoreQueryFilters().Include(r => r.Provider).Single();
        repository.CloneUrl.Should().NotContain(Token);
        new PassthroughProtector().Unprotect(repository.Provider!.EncryptedCredential!).Should().Be(Token);
    }

    [Fact]
    public async Task A_clone_failure_says_what_to_do_and_never_repeats_the_token_even_when_the_error_does()
    {
        using var h = new LaunchHarness();
        h.Git.FailWith = $"fatal: could not read from https://{Token}@git.example.com/acme/shop.git ({Uri.EscapeDataString(Token)})";

        // Arrives in the URL, and the failure quotes it back: the worst case for a message on a page.
        var id = (await h.Request().StartAsync(h.GitRequest($"https://oauth2:{Token}@git.example.com/acme/shop.git"), default)).Value;
        await h.Worker().InspectAsync(id, default);

        var view = await h.Request().GetAsync(id, default);
        view!.Status.Should().Be(LaunchStatus.InspectFailed);
        view.IsTerminal.Should().BeTrue();
        view.FailureMessage.Should().Contain("private").And.Contain("token").And.NotContain(Token);
        view.FailureMessageFa.Should().Contain("خصوصی").And.Contain("توکن").And.NotContain(Token);
        view.Steps.Single(s => s.Key == LaunchStepKeys.Source).State.Should().Be(LaunchStepState.Failed);
        view.Steps.Skip(1).Should().OnlyContain(s => s.State == LaunchStepState.Skipped);
        view.CanRetry.Should().BeFalse("the form comes back for another go, which is a new launch");
        h.Logs.Lines.Should().NotContain(l => l.Contains(Token));

        var row = h.Row(id);
        row.FailureMessage.Should().NotContain(Token);
        row.EncryptedGitToken.Should().BeNull("the launch has ended, and the token has no further use");
    }

    [Fact]
    public async Task A_message_too_long_for_its_column_is_clipped_rather_than_failing_the_write()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);
        await h.TickAsync();

        h.DeploymentIs(h.Row(id).DeploymentId!.Value, DeploymentStatus.Failed, new string('x', 5000));
        await h.TickAsync();

        h.Row(id).FailureMessage!.Length.Should().BeLessThanOrEqualTo(2000);
        h.Row(id).FailureMessageFa!.Length.Should().BeLessThanOrEqualTo(2000);
    }

    // ---- the variables -------------------------------------------------------------------------

    [Fact]
    public async Task No_variables_value_is_kept_in_the_choices()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());

        await h.ConfirmAsync(id, c => c with
        {
            EnvVars = c.EnvVars.Select(v => v.Key switch
            {
                "STRIPE_SECRET_KEY" => v with { Value = "sk_live_abc" },
                "LOG_LEVEL" => v with { Value = "debug-custom" },
                _ => v
            }).ToList()
        });

        var json = h.Row(id).ChoicesJson;
        json.Should().NotContain("sk_live_abc").And.NotContain("debug-custom");
        json.Should().Contain("STRIPE_SECRET_KEY").And.Contain("LOG_LEVEL");

        var variables = h.Store().EnvironmentVariables.IgnoreQueryFilters().ToList();
        var stripe = variables.Single(v => v.Key == "STRIPE_SECRET_KEY");
        stripe.IsSecret.Should().BeTrue();
        stripe.Value.Should().NotBe("sk_live_abc", "it is stored protected");
        new PassthroughProtector().Unprotect(stripe.Value).Should().Be("sk_live_abc");
        variables.Single(v => v.Key == "LOG_LEVEL").Value.Should().Be("debug-custom");

        var read = LaunchJson.ReadChoices(json);
        read!.EnvVars.Should().OnlyContain(v => v.Value == null);
    }

    [Fact]
    public async Task Generated_secrets_are_random_long_enough_and_stored_secret_and_a_blank_answer_is_not_written()
    {
        using var h = new LaunchHarness();
        var first = await h.ReadyGitAsync(NodeWithPostgres(), url: "https://git.example.com/acme/one.git");
        var second = await h.ReadyGitAsync(NodeWithPostgres(), url: "https://git.example.com/acme/two.git");
        await h.ConfirmAsync(first);
        await h.ConfirmAsync(second);

        var secrets = h.Store().EnvironmentVariables.IgnoreQueryFilters().Where(v => v.Key == "JWT_SECRET").ToList();
        secrets.Should().HaveCount(2).And.OnlyContain(v => v.IsSecret);
        var plain = secrets.Select(v => new PassthroughProtector().Unprotect(v.Value)).ToList();
        plain.Distinct().Should().HaveCount(2);
        plain.Should().OnlyContain(v => v.Length >= 32, "validators that demand 32 characters of a JWT secret must not fail at startup");

        h.Store().EnvironmentVariables.IgnoreQueryFilters().Should().NotContain(v => v.Key == "STRIPE_SECRET_KEY",
            "an empty variable and an unset one are different to an app that checks");

        var view = await h.Request().GetAsync(first, default);
        view!.Steps.Single(s => s.Key == LaunchStepKeys.App).Detail.Should().Contain("STRIPE_SECRET_KEY").And.NotContain("JWT_SECRET");
    }

    [Fact]
    public async Task A_generated_secret_never_takes_the_example_files_value()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare() with
        {
            EnvVars = [Var("SESSION_SECRET", EnvVarDisposition.GeneratedSecret, "changeme-from-the-example")]
        });

        await h.ConfirmAsync(id);

        var stored = h.Store().EnvironmentVariables.IgnoreQueryFilters().Single();
        new PassthroughProtector().Unprotect(stored.Value).Should().NotContain("changeme");
    }

    [Fact]
    public async Task App_key_beside_an_artisan_file_is_laravels_own_format()
    {
        using var h = new LaunchHarness();
        h.Git.Files.Add("artisan");
        var id = await h.ReadyGitAsync(Bare() with { Stack = DetectedStack.Php, EnvVars = [Var("APP_KEY", EnvVarDisposition.GeneratedSecret)] });

        await h.ConfirmAsync(id);

        var stored = h.Store().EnvironmentVariables.IgnoreQueryFilters().Single();
        stored.IsSecret.Should().BeTrue();
        var value = new PassthroughProtector().Unprotect(stored.Value);
        value.Should().StartWith("base64:");
        Convert.FromBase64String(value["base64:".Length..]).Should().HaveCount(32);
    }

    [Fact]
    public async Task App_key_with_no_artisan_file_is_an_ordinary_secret()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare() with { EnvVars = [Var("APP_KEY", EnvVarDisposition.GeneratedSecret)] });

        await h.ConfirmAsync(id);

        new PassthroughProtector().Unprotect(h.Store().EnvironmentVariables.IgnoreQueryFilters().Single().Value)
            .Should().NotStartWith("base64:");
    }

    // ---- one server ----------------------------------------------------------------------------

    [Fact]
    public async Task Everything_in_a_launch_lands_on_one_server()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(TwoDatabases());

        // Every time it is asked the scheduler says somewhere else, which is how an app ends up on a host
        // its database cannot be reached from.
        h.Scheduler.PlaceOn(h.ServerA.Id, h.ServerB.Id, h.ServerA.Id, h.ServerB.Id, h.ServerA.Id, h.ServerB.Id);

        var view = await h.ConfirmAsync(id);

        view.Status.Should().Be(LaunchStatus.WaitingForServices);
        var services = h.Store().ManagedServices.IgnoreQueryFilters().Select(s => s.ServerId).ToList();
        services.Should().HaveCount(2);
        var app = h.Store().Apps.IgnoreQueryFilters().Single();
        services.Should().AllBeEquivalentTo(app.ServerId);
    }

    [Fact]
    public async Task The_server_is_asked_for_once_for_all_of_it_together_and_inside_the_plans_pool()
    {
        using var h = new LaunchHarness();
        using (var db = h.Store())
        {
            db.Plans.IgnoreQueryFilters().Single().NodePool = "eu";
            db.Servers.IgnoreQueryFilters().Single(s => s.Id == h.ServerA.Id).Pool = "eu";
            db.SaveChanges();
        }
        var id = await h.ReadyGitAsync(TwoDatabases());

        await h.Request().QuoteAsync(id, await h.DefaultsAsync(id), default);

        h.Scheduler.LastAsked.Memory.Should().Be(3 * (1L << 30), "two databases and the app, all on one host");
        h.Scheduler.LastAsked.Cpu.Should().Be(3);
        h.Scheduler.LastAsked.Pool.Should().Be("eu", "a database placed by itself knows nothing of a plan's pool, and the app would follow it");
    }

    [Fact]
    public async Task A_chosen_server_is_passed_to_everything()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());

        await h.ConfirmAsync(id, c => c with { ServerId = h.ServerB.Id });

        h.Store().ManagedServices.IgnoreQueryFilters().Single().ServerId.Should().Be(h.ServerB.Id);
        h.Store().Apps.IgnoreQueryFilters().Single().ServerId.Should().Be(h.ServerB.Id);
    }

    [Fact]
    public async Task No_host_with_room_for_all_of_it_is_refused_before_anything_exists()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        var choices = await h.DefaultsAsync(id);
        h.Scheduler.RefusePlacement = PlacementResult.Fail("No node has room.", "هیچ نودی جا ندارد.");

        var outcome = await h.Request().ConfirmAsync(id, choices, null, default);

        outcome.Ok.Should().BeFalse();
        outcome.Refusals.Should().Contain(r => r.MessageFa == "هیچ نودی جا ندارد.");
        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        h.Store().ManagedServices.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_chosen_host_is_asked_about_all_of_it_together_because_the_form_always_chooses_one()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(TwoDatabases());
        var choices = await h.DefaultsAsync(id) with { ServerId = h.ServerB.Id };

        (await h.Request().ValidateConfirmAsync(id, choices, default)).Should().BeEmpty();

        h.Scheduler.CheckAsked.Should().Contain((h.ServerB.Id, 3 * (1L << 30), 3.0), "two databases and the app");
    }

    [Fact]
    public async Task A_chosen_host_that_fits_each_but_not_all_is_refused_before_anything_exists_and_charged()
    {
        using var h = new LaunchHarness();
        h.Billing.Enabled = true;
        using (var db = h.Store())
        {
            db.Wallets.Add(new Harbora.Domain.Billing.Wallet { WorkspaceId = h.WorkspaceId, BalanceMinor = 100_000 });
            db.SaveChanges();
        }
        var id = await h.ReadyGitAsync(TwoDatabases());
        var choices = await h.DefaultsAsync(id) with { ServerId = h.ServerB.Id };
        h.Scheduler.RoomOnAChosenHost = 2L << 30;

        (await h.Request().ValidateConfirmAsync(id, choices, default)).Should().ContainSingle(r => r.MessageFa == "آن سرور برای این جا ندارد.");

        var outcome = await h.Request().ConfirmAsync(id, choices, null, default);

        outcome.Ok.Should().BeFalse();
        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        h.Store().ManagedServices.IgnoreQueryFilters().Should().BeEmpty();
        h.Store().Apps.IgnoreQueryFilters().Should().BeEmpty();
        h.Store().BillingLedger.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_quote_on_a_host_without_room_is_still_about_that_host()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(TwoDatabases());
        var choices = await h.DefaultsAsync(id) with { ServerId = h.ServerB.Id };
        h.Scheduler.RoomOnAChosenHost = 1;

        await h.Request().QuoteAsync(id, choices, default);

        h.Scheduler.Placements.Should().Be(0, "somebody chose it, so nothing else is asked for instead");
    }

    [Fact]
    public async Task A_server_id_that_is_no_server_is_no_choice_at_all_the_way_the_create_services_read_it()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());

        await h.ConfirmAsync(id, c => c with { ServerId = Guid.CreateVersion7() });

        h.Store().Apps.IgnoreQueryFilters().Single().ServerId.Should().Be(h.ServerA.Id);
    }

    [Theory]
    [InlineData(1, 0, "service")]
    [InlineData(0, 2, "memory")]
    public async Task A_plan_with_room_for_each_but_not_all_is_refused_before_anything_exists(int maxServices, int maxMemoryGb, string what)
    {
        using var h = new LaunchHarness();
        using (var db = h.Store())
        {
            var plan = db.Plans.IgnoreQueryFilters().Single();
            plan.MaxServices = maxServices;
            plan.MaxMemoryBytes = maxMemoryGb * (1L << 30);
            db.SaveChanges();
        }
        var id = await h.ReadyGitAsync(TwoDatabases());
        var choices = await h.DefaultsAsync(id);

        var refusals = await h.Request().ValidateConfirmAsync(id, choices, default);

        refusals.Should().ContainSingle().Which.Message.Should().ContainEquivalentOf(what);
        refusals[0].MessageFa.Should().NotBe(refusals[0].Message);

        (await h.Request().ConfirmAsync(id, choices, null, default)).Ok.Should().BeFalse();
        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        h.Store().ManagedServices.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_that_is_already_full_says_so_once()
    {
        using var h = new LaunchHarness();
        using (var db = h.Store())
        {
            db.Plans.IgnoreQueryFilters().Single().MaxApps = 1;
            db.Apps.Add(new App
            {
                WorkspaceId = h.WorkspaceId, ServerId = h.ServerA.Id, EnvironmentId = h.Environment!.Id,
                Name = "already", Slug = "already", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "x:1"
            });
            db.SaveChanges();
        }
        var id = await h.ReadyGitAsync(Bare());

        (await h.Request().ValidateConfirmAsync(id, await h.DefaultsAsync(id), default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_plan_that_sells_past_its_caps_is_not_refused_by_the_batch_either()
    {
        using var h = new LaunchHarness();
        h.Billing.Enabled = true;
        using (var db = h.Store())
        {
            var plan = db.Plans.IgnoreQueryFilters().Single();
            plan.MaxServices = 1;
            plan.AllowsOverage = true;
            db.Wallets.Add(new Harbora.Domain.Billing.Wallet { WorkspaceId = h.WorkspaceId, BalanceMinor = 100_000 });
            db.SaveChanges();
        }
        var id = await h.ReadyGitAsync(TwoDatabases());

        (await h.Request().ValidateConfirmAsync(id, await h.DefaultsAsync(id), default)).Should().BeEmpty();
    }

    // ---- naming --------------------------------------------------------------------------------

    [Fact]
    public async Task A_database_name_taken_anywhere_on_the_platform_gets_a_suffix()
    {
        using var h = new LaunchHarness();
        using (var db = h.Store())
        {
            // The container name is unique across every workspace; the service's own check is not.
            db.ManagedServices.Add(new ManagedService
            {
                WorkspaceId = h.OtherWorkspaceId, EnvironmentId = Guid.CreateVersion7(), ServerId = h.ServerA.Id,
                Name = "shop-postgresql", Type = ManagedServiceType.PostgreSql, Version = "16",
                ContainerName = DatabaseProvisioningService.ContainerPrefix + "shop-postgresql",
                VolumeName = "v", Username = "u", DatabaseName = "d", EncryptedPassword = "p"
            });
            db.SaveChanges();
        }
        var id = await h.ReadyGitAsync(NodeWithPostgres());

        await h.ConfirmAsync(id);

        h.Store().ManagedServices.IgnoreQueryFilters().Should().Contain(s => s.WorkspaceId == h.WorkspaceId && s.Name == "shop-postgresql-2");
    }

    [Fact]
    public async Task A_name_the_person_chose_is_used_and_two_of_one_engine_do_not_collide()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());

        await h.ConfirmAsync(id, c => c with
        {
            Services =
            [
                new LaunchServiceChoice(ManagedServiceType.PostgreSql, "orders"),
                new LaunchServiceChoice(ManagedServiceType.PostgreSql),
                new LaunchServiceChoice(ManagedServiceType.PostgreSql)
            ]
        });

        h.Store().ManagedServices.IgnoreQueryFilters().Select(s => s.Name).Should()
            .BeEquivalentTo("orders", "shop-postgresql", "shop-postgresql-2");
    }

    // ---- what is asked of the app ---------------------------------------------------------------

    [Fact]
    public async Task A_worker_gets_its_start_command_and_no_address()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare() with { Kind = DetectedAppKind.Worker, Port = null, StartCommand = "python bot.py" });

        var view = await h.ConfirmAsync(id);

        var app = h.Store().Apps.IgnoreQueryFilters().Include(a => a.Domains).Single();
        app.Kind.Should().Be(ServiceKind.Worker);
        app.StartCommand.Should().Be("python bot.py", "the form's Command field is a scheduled job's, and is dropped for anything else");
        app.Domains.Should().BeEmpty();
        view.PublicUrl.Should().BeNull();
    }

    [Fact]
    public async Task A_database_ticked_by_hand_still_gets_the_names_the_app_reads_it_under()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare() with
        {
            Services = [Guess(DetectedServiceKind.Redis)],
            EnvVars = [Var("CACHE_URL", EnvVarDisposition.AliasOfAttach, null, DetectedServiceKind.Redis, EnvAliasRole.Url)]
        });

        var view = await h.ConfirmAsync(id, c => c with { Services = [new LaunchServiceChoice(ManagedServiceType.Redis)] });

        ManagedServiceAttachEnv.ReadAliases(h.Store().AppManagedServices.IgnoreQueryFilters().Single().EnvAliasesJson)
            .Should().Equal(new AttachEnvAlias("CACHE_URL", "REDIS_URL"));
        view.Steps.Single(s => s.Key == LaunchStepKeys.App).Detail.Should().BeNull("the attachment answers CACHE_URL, so nothing is missing");
    }

    [Fact]
    public async Task The_host_the_checks_approved_is_the_one_everything_lands_on()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        var choices = await h.DefaultsAsync(id);
        h.Scheduler.PlaceOn(h.ServerB.Id, h.ServerA.Id);

        (await h.Request().ConfirmAsync(id, choices, null, default)).Ok.Should().BeTrue();

        h.Scheduler.Placements.Should().Be(1, "asked twice, it could approve one host and land on another");
        h.Store().Apps.IgnoreQueryFilters().Single().ServerId.Should().Be(h.ServerB.Id);
        LaunchJson.ReadChoices(h.Row(id).ChoicesJson)!.ServerId.Should().Be(h.ServerB.Id, "so a retry stays on it");
    }

    [Fact]
    public async Task An_uploads_archive_swept_from_an_old_failure_says_what_is_left_to_do()
    {
        using var h = new LaunchHarness();
        var (id, archive) = await h.ReadyUploadAsync(Bare());
        await h.ConfirmAsync(id);
        h.RefuseNextDeployment = new InvalidOperationException("A rollback is running.");
        await h.TickAsync();
        (await h.Request().GetAsync(id, default))!.CanRetry.Should().BeTrue();

        h.Advance(TimeSpan.FromHours(25));
        await h.TickAsync();

        File.Exists(archive).Should().BeFalse();
        var view = await h.Request().GetAsync(id, default);
        view!.CanRetry.Should().BeFalse();
        view.FailureMessage.Should().Contain("upload a new version");
    }

    [Fact]
    public async Task A_project_with_only_a_compose_file_is_a_compose_app_from_git_and_refused_from_an_upload()
    {
        using var h = new LaunchHarness();
        var compose = Bare() with { Stack = DetectedStack.Compose };

        var fromGit = await h.ReadyGitAsync(compose);
        await h.ConfirmAsync(fromGit);
        h.Store().Apps.IgnoreQueryFilters().Single().SourceType.Should().Be(AppSourceType.DockerCompose);

        var (fromUpload, _) = await h.ReadyUploadAsync(compose);
        var refusals = await h.Request().ValidateConfirmAsync(fromUpload, await h.DefaultsAsync(fromUpload), default);
        refusals.Should().Contain(r => r.Message.Contains("Compose"));
    }

    // ---- starting ------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_records_the_launch_queues_its_read_and_audits_one_entry()
    {
        using var h = new LaunchHarness();

        var started = await h.Request().StartAsync(h.GitRequest(gitRef: "develop"), default);

        started.Ok.Should().BeTrue();
        var row = h.Row(started.Value);
        row.Status.Should().Be(LaunchStatus.Inspecting);
        row.WorkspaceId.Should().Be(h.WorkspaceId);
        row.UserId.Should().Be(h.UserId);
        row.GitRef.Should().Be("develop");
        h.Jobs.Enqueued.Should().Equal((JobKind.LaunchInspect, started.Value));
        h.Git.Checkouts.Should().BeEmpty("the source is not read on the request");

        var entry = h.Audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("launch.started");
        entry.WorkspaceId.Should().Be(h.WorkspaceId);
        entry.IpAddress.Should().Be("203.0.113.9");
    }

    [Fact]
    public async Task An_ssh_address_is_refused_on_the_request_and_nothing_is_written()
    {
        using var h = new LaunchHarness();

        var outcome = await h.Request().StartAsync(h.GitRequest("git@github.com:acme/shop.git"), default);

        outcome.Ok.Should().BeFalse();
        outcome.Refusals[0].Message.Should().Contain("https://github.com/acme/shop.git");
        h.Store().AppLaunches.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_suspended_workspace_cannot_start_one()
    {
        using var h = new LaunchHarness();
        using (var db = h.Store())
        {
            db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == h.WorkspaceId).IsSuspended = true;
            db.SaveChanges();
        }

        var outcome = await h.Request().StartAsync(h.GitRequest(), default);

        outcome.Ok.Should().BeFalse();
        outcome.Refusals[0].MessageFa.Should().Contain("معلق");
    }

    [Fact]
    public async Task Somebody_who_may_create_apps_nowhere_cannot_start_one()
    {
        using var h = new LaunchHarness(role: WorkspaceRole.Member, scoped: true);

        var outcome = await h.Request().StartAsync(h.GitRequest(), default);

        outcome.Ok.Should().BeFalse();
        h.Store().AppLaunches.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task An_archive_that_is_not_in_the_upload_directory_is_refused_because_a_launch_later_deletes_it()
    {
        using var h = new LaunchHarness();
        var outside = Path.Combine(h.WorkDir, "not-uploads.zip");
        File.WriteAllText(outside, "x");

        var outcome = await h.Request().StartAsync(new LaunchStartRequest
        {
            WorkspaceId = h.WorkspaceId, UserId = h.UserId, Source = LaunchSourceKind.Upload, ArchivePath = outside
        }, default);

        outcome.Ok.Should().BeFalse();
    }

    // ---- reading -------------------------------------------------------------------------------

    [Fact]
    public async Task Reading_stores_what_it_found_names_the_branch_and_leaves_no_folder_behind()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());

        h.Git.Checkouts.Single().Ref.Should().Be("", "blank means the repository's default branch");
        var row = h.Row(id);
        row.Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        row.ResolvedBranch.Should().Be("trunk");
        row.InspectionJson.Should().Contain("\"Postgres\"", "enums are stored as names");
        LaunchJson.ReadInspection(row.InspectionJson).Services.Should().HaveCount(2);

        h.Git.Checkouts.Single().Dir.Should().Be(Path.Combine(h.WorkDir, "_launch", id.ToString()));
        Directory.Exists(h.Git.Checkouts.Single().Dir).Should().BeFalse();
    }

    [Fact]
    public async Task Running_twice_is_safe_and_a_launch_past_reading_is_left_alone()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());

        await h.Worker().InspectAsync(id, default);
        h.Git.Checkouts.Should().ContainSingle();

        await h.ConfirmAsync(id);
        await h.Worker().InspectAsync(id, default);
        h.Row(id).Status.Should().Be(LaunchStatus.Deploying);
        await h.Worker().InspectAsync(Guid.CreateVersion7(), default);
    }

    [Fact]
    public async Task A_ref_that_is_not_there_is_said_out_loud_because_the_clone_read_the_default_instead()
    {
        using var h = new LaunchHarness();
        h.Reader.Inspection = Bare();

        var id = (await h.Request().StartAsync(h.GitRequest(gitRef: "develop"), default)).Value;
        await h.Worker().InspectAsync(id, default);

        var proposal = await h.Request().ProposeAsync(id, default);
        proposal!.Inspection.Notes.Should().ContainSingle(n => n.Code == "ref-not-found")
            .Which.Message.Should().Contain("develop").And.Contain("trunk");
    }

    [Fact]
    public async Task A_zip_is_really_unpacked_and_read_and_stays_for_the_deployment()
    {
        using var h = new LaunchHarness();
        var (id, archive) = await h.ReadyUploadAsync(NodeWithPostgres());

        h.Reader.ReadFrom.Should().ContainSingle().Which.Should().Be(Path.Combine(h.WorkDir, "_launch", id.ToString()));
        Directory.Exists(h.Reader.ReadFrom.Single()).Should().BeFalse();
        File.Exists(archive).Should().BeTrue();
        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
    }

    [Fact]
    public async Task An_upload_that_is_not_an_archive_fails_in_both_languages_and_is_not_kept()
    {
        using var h = new LaunchHarness();
        var archive = h.WriteArchive("junk.zip", junk: true);

        var id = (await h.Request().StartAsync(new LaunchStartRequest
        {
            WorkspaceId = h.WorkspaceId, UserId = h.UserId, Source = LaunchSourceKind.Upload, ArchivePath = archive
        }, default)).Value;
        await h.Worker().InspectAsync(id, default);

        var view = await h.Request().GetAsync(id, default);
        view!.Status.Should().Be(LaunchStatus.InspectFailed);
        view.FailureMessage.Should().Contain("not a valid");
        view.FailureMessageFa.Should().Contain("zip");
        File.Exists(archive).Should().BeFalse();
        h.Row(id).ArchivePath.Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_read_is_the_workers_to_settle_and_not_a_failure_of_the_source()
    {
        using var h = new LaunchHarness();
        h.Git.FailWith = null;
        var id = (await h.Request().StartAsync(h.GitRequest(), default)).Value;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await h.Worker().InspectAsync(id, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Row(id).Status.Should().Be(LaunchStatus.Inspecting);
    }

    // ---- the proposal --------------------------------------------------------------------------

    [Fact]
    public async Task The_proposal_turns_what_was_found_into_defaults_a_person_only_has_to_accept()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());

        var proposal = await h.Request().ProposeAsync(id, default);

        proposal.Should().NotBeNull();
        proposal!.LaunchId.Should().Be(id);
        proposal.AppName.Should().Be("shop");
        var defaults = proposal.Defaults;
        defaults.Name.Should().Be("shop");
        defaults.Kind.Should().Be(ServiceKind.Web);
        defaults.ContainerPort.Should().Be(3000);
        defaults.ReleaseCommand.Should().Be("npx prisma migrate deploy");
        defaults.EnvironmentId.Should().Be(h.Environment!.Id);
        defaults.InstanceSizeKey.Should().Be(LaunchHarness.SizeKey);

        var service = defaults.Services.Should().ContainSingle("a guess is not consent: the Redis stays unticked").Which;
        service.Type.Should().Be(ManagedServiceType.PostgreSql);
        service.Aliases.Should().Equal(new AttachEnvAlias("DB_HOST", "PGHOST"));

        proposal.EnvFields.Select(f => f.Key).Should().BeEquivalentTo(
            ["DB_HOST", "JWT_SECRET", "LOG_LEVEL", "STRIPE_SECRET_KEY"], "DATABASE_URL has nothing to ask");
        var fields = proposal.EnvFields.ToDictionary(f => f.Key);
        fields["LOG_LEVEL"].SuggestedValue.Should().Be("info");
        fields["LOG_LEVEL"].IsSecret.Should().BeFalse();
        fields["JWT_SECRET"].SuggestedValue.Should().BeNull();
        fields["JWT_SECRET"].IsSecret.Should().BeTrue();
        fields["STRIPE_SECRET_KEY"].IsSecret.Should().BeTrue();
        fields["STRIPE_SECRET_KEY"].SourceFile.Should().Be(ExampleFile);

        defaults.EnvVars.Select(v => v.Key).Should().NotContain("DB_HOST", "an alias of a ticked database has nothing to ask");

        proposal.ServiceOptions.Select(o => o.Type).Should().BeEquivalentTo(ServiceCatalog.All.Keys);
        proposal.ServiceOptions.Single(o => o.Type == ManagedServiceType.Redis).Detected!.Certain.Should().BeFalse();
        proposal.ServiceOptions.Single(o => o.Type == ManagedServiceType.MongoDb).Detected.Should().BeNull();
    }

    [Fact]
    public async Task A_proposal_exists_only_at_the_confirm_screen()
    {
        using var h = new LaunchHarness();
        h.Reader.Inspection = Bare();
        var id = (await h.Request().StartAsync(h.GitRequest(), default)).Value;
        (await h.Request().ProposeAsync(id, default)).Should().BeNull("it is still being read");

        await h.Worker().InspectAsync(id, default);
        (await h.Request().ProposeAsync(id, default)).Should().NotBeNull();

        await h.ConfirmAsync(id);
        (await h.Request().ProposeAsync(id, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_workspace_with_no_environment_gets_no_project_conjured_by_looking_at_a_screen()
    {
        using var h = new LaunchHarness(withEnvironment: false);
        var id = await h.ReadyGitAsync(Bare());

        var proposal = await h.Request().ProposeAsync(id, default);
        await h.Request().ValidateConfirmAsync(id, proposal!.Defaults, default);
        await h.Request().QuoteAsync(id, proposal.Defaults, default);

        proposal.Defaults.EnvironmentId.Should().BeNull();
        h.Store().Projects.IgnoreQueryFilters().Should().BeEmpty();
        h.Store().Environments.IgnoreQueryFilters().Should().BeEmpty();

        (await h.ConfirmAsync(id)).Status.Should().Be(LaunchStatus.Deploying);
        h.Store().Environments.IgnoreQueryFilters().Should().ContainSingle("the create makes the default one, as the form's does");
    }

    [Fact]
    public async Task A_platform_default_size_is_what_the_proposal_opens_on()
    {
        using var h = new LaunchHarness();
        using (var db = h.Store())
        {
            db.InstanceSizes.Add(new InstanceSize { Key = "large", Name = "Large", SortOrder = 9, RunningRatePerHourMinor = 900 });
            db.Settings.Add(new Setting { Key = SettingKeys.DefaultInstanceSize, Value = "large" });
            db.SaveChanges();
        }
        var id = await h.ReadyGitAsync(Bare());

        (await h.DefaultsAsync(id)).InstanceSizeKey.Should().Be("large");
    }

    // ---- validating ----------------------------------------------------------------------------

    [Fact]
    public async Task Validating_changes_nothing_at_all()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        var choices = await h.DefaultsAsync(id);
        var auditBefore = h.Audit.Entries.Count;

        (await h.Request().ValidateConfirmAsync(id, choices, default)).Should().BeEmpty();
        var quote = await h.Request().QuoteAsync(id, choices, default);

        quote.Refusal.Should().BeNull();
        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        h.Row(id).ChoicesJson.Should().BeNull();
        h.Store().Apps.IgnoreQueryFilters().Should().BeEmpty();
        h.Store().ManagedServices.IgnoreQueryFilters().Should().BeEmpty();
        h.Provisioner.Provisioned.Should().BeEmpty();
        h.Audit.Entries.Should().HaveCount(auditBefore);
    }

    [Fact]
    public async Task A_refusal_on_confirming_changes_nothing_and_the_screen_can_be_submitted_again()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        var choices = await h.DefaultsAsync(id);

        var refused = await h.Request().ConfirmAsync(id, choices with { Name = "" }, null, default);

        refused.Ok.Should().BeFalse();
        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        h.Store().ManagedServices.IgnoreQueryFilters().Should().BeEmpty();
        (await h.Request().ConfirmAsync(id, choices, null, default)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_typed_slug_that_is_taken_is_refused_by_the_create_services_own_rule()
    {
        using var h = new LaunchHarness();
        var first = await h.ReadyGitAsync(Bare(), url: "https://git.example.com/acme/one.git");
        await h.ConfirmAsync(first, c => c with { Slug = "shop" });
        var second = await h.ReadyGitAsync(Bare(), url: "https://git.example.com/acme/two.git");

        var refusals = await h.Request().ValidateConfirmAsync(second, (await h.DefaultsAsync(second)) with { Slug = "shop" }, default);

        refusals.Should().ContainSingle().Which.Message.Should().Contain("shop");
    }

    [Theory]
    [InlineData("HAS SPACE")]
    [InlineData("1STARTS_WITH_DIGIT")]
    [InlineData("HAS=EQUALS")]
    [InlineData("")]
    public async Task A_key_must_be_a_real_variable_name(string key)
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());

        var refusals = await h.Request().ValidateConfirmAsync(id,
            (await h.DefaultsAsync(id)) with { EnvVars = [new LaunchEnvChoice(key, "value")] }, default);

        refusals.Should().ContainSingle().Which.Field.Should().Be(nameof(LaunchChoices.EnvVars));
    }

    [Fact]
    public async Task A_blank_answer_to_a_question_is_allowed_because_some_are_optional()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());

        (await h.Request().ValidateConfirmAsync(id, await h.DefaultsAsync(id), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_launch_must_never_choose_no_limit()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());

        var refusals = await h.Request().ValidateConfirmAsync(id, (await h.DefaultsAsync(id)) with { InstanceSizeKey = null }, default);

        refusals.Should().Contain(r => r.Field == nameof(LaunchChoices.InstanceSizeKey));
    }

    [Fact]
    public async Task A_second_submit_validates_clean_so_it_lands_on_the_progress_page()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        var choices = await h.DefaultsAsync(id);
        await h.ConfirmAsync(id);

        (await h.Request().ValidateConfirmAsync(id, choices, default)).Should().BeEmpty();
    }

    // ---- giving up -----------------------------------------------------------------------------

    [Fact]
    public async Task Abandoning_deletes_the_archive_and_the_token_before_anything_exists()
    {
        using var h = new LaunchHarness();
        var (id, archive) = await h.ReadyUploadAsync(Bare());

        (await h.Request().AbandonAsync(id, default)).Should().BeTrue();

        File.Exists(archive).Should().BeFalse();
        var row = h.Row(id);
        row.Status.Should().Be(LaunchStatus.Abandoned);
        row.ArchivePath.Should().BeNull();
        (await h.Request().AbandonAsync(id, default)).Should().BeTrue("asked twice, it is still abandoned");
        (await h.Request().GetAsync(id, default))!.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task Abandoning_a_read_in_progress_asks_its_job_to_stop_and_clears_the_token()
    {
        using var h = new LaunchHarness();
        var id = (await h.Request().StartAsync(h.GitRequest(token: Token), default)).Value;

        (await h.Request().AbandonAsync(id, default)).Should().BeTrue();

        h.Jobs.CancelRequests.Should().Equal(id);
        h.Row(id).EncryptedGitToken.Should().BeNull();

        await h.Worker().InspectAsync(id, default);
        h.Git.Checkouts.Should().BeEmpty("a late job finds it is not reading any more");
    }

    [Fact]
    public async Task It_is_too_late_to_abandon_once_something_exists()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        await h.ConfirmAsync(id);

        (await h.Request().AbandonAsync(id, default)).Should().BeFalse();
        h.Row(id).Status.Should().Be(LaunchStatus.WaitingForServices);
    }

    [Fact]
    public async Task A_failure_before_anything_was_created_can_still_be_abandoned()
    {
        using var h = new LaunchHarness();
        var (id, archive) = await h.ReadyUploadAsync(Bare());
        h.Creates.RefuseApp = new ServiceRefusal("No address left.", "نشانی نمانده است.");
        await h.ConfirmAsync(id);

        (await h.Request().AbandonAsync(id, default)).Should().BeTrue();

        File.Exists(archive).Should().BeFalse();
    }

    // ---- the sweeps ----------------------------------------------------------------------------

    [Fact]
    public async Task A_confirm_screen_left_for_a_day_is_abandoned_with_its_archive_and_token_gone()
    {
        using var h = new LaunchHarness();
        var (upload, archive) = await h.ReadyUploadAsync(Bare());
        var git = await h.ReadyGitAsync(Bare(), token: Token);
        var fresh = upload;

        h.Advance(TimeSpan.FromHours(23));
        await h.TickAsync();
        h.Row(fresh).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        File.Exists(archive).Should().BeTrue();

        h.Advance(TimeSpan.FromHours(2));
        (await h.TickAsync()).Should().Be(2);

        h.Row(upload).Status.Should().Be(LaunchStatus.Abandoned);
        h.Row(upload).ArchivePath.Should().BeNull();
        File.Exists(archive).Should().BeFalse();
        h.Row(git).Status.Should().Be(LaunchStatus.Abandoned);
        h.Row(git).EncryptedGitToken.Should().BeNull();
    }

    [Fact]
    public async Task A_read_whose_job_was_lost_ends_with_nothing_kept()
    {
        using var h = new LaunchHarness();
        var archive = h.WriteArchive("lost.zip");
        var id = (await h.Request().StartAsync(new LaunchStartRequest
        {
            WorkspaceId = h.WorkspaceId, UserId = h.UserId, Source = LaunchSourceKind.Upload, ArchivePath = archive
        }, default)).Value;

        h.Advance(TimeSpan.FromMinutes(21));
        await h.TickAsync();

        var row = h.Row(id);
        row.Status.Should().Be(LaunchStatus.InspectFailed);
        row.FailureMessageFa.Should().NotBeNullOrWhiteSpace();
        File.Exists(archive).Should().BeFalse();
    }

    [Fact]
    public async Task An_old_failure_lets_go_of_its_token_and_says_it_has_closed()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(NodeWithPostgres(), token: Token);
        h.Creates.RefuseApp = new ServiceRefusal("No address left.", "نشانی نمانده است.");
        await h.ConfirmAsync(id);
        h.Row(id).EncryptedGitToken.Should().NotBeNull();

        h.Advance(TimeSpan.FromHours(25));
        await h.TickAsync();

        var row = h.Row(id);
        row.EncryptedGitToken.Should().BeNull();
        row.FailureMessage.Should().Contain("start a new one");
        (await h.Request().GetAsync(id, default))!.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task The_tick_has_no_session_and_still_sees_every_workspaces_launches()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());
        await h.ConfirmAsync(id);

        // h.Worker() runs under a scope that matches no tenant: a filtered read would find nothing,
        // move nothing and report a healthy pass.
        (await h.Worker().AdvanceAllAsync(default)).Should().Be(1);

        h.Row(id).DeploymentId.Should().NotBeNull();
    }
}
