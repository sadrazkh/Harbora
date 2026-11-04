using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Launch;
using Xunit;

namespace Harbora.Tests.Launch;

/// <summary>
/// What the tick decides, with nothing behind it: no database, no queue, no clock. The rule that matters
/// most is here first: no deployment may be asked for while a database has not answered.
/// </summary>
public sealed class LaunchAdvanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AppId = Guid.CreateVersion7();

    private static LaunchSnapshot Snapshot(
        LaunchStatus status,
        TimeSpan? inStatus = null,
        TimeSpan? sinceTouched = null,
        TimeSpan? sinceVerifyStarted = null,
        Guid? appId = null,
        bool noApp = false,
        int? expected = null,
        LaunchServiceState[]? services = null,
        Guid? deploymentId = null,
        LaunchDeploymentState? deployment = null,
        LaunchSourceKind source = LaunchSourceKind.Git,
        bool leftovers = false) => new(
        status, source, Now,
        Now - (inStatus ?? TimeSpan.Zero),
        Now - (sinceTouched ?? TimeSpan.FromMinutes(1)),
        sinceVerifyStarted is { } started ? Now - started : null,
        noApp ? null : appId ?? AppId,
        expected ?? services?.Length ?? 0,
        services ?? [],
        deploymentId,
        deployment,
        leftovers);

    private static LaunchServiceState Service(ServiceStatus status, string name = "db", string? error = null) =>
        new(Guid.CreateVersion7(), name, status, error);

    [Theory]
    [InlineData(ServiceStatus.Provisioning)]
    [InlineData(ServiceStatus.Stopped)]
    public void A_database_that_is_not_running_yet_holds_the_deployment_back(ServiceStatus status)
    {
        var move = LaunchAdvance.Decide(Snapshot(LaunchStatus.WaitingForServices,
            services: [Service(ServiceStatus.Running), Service(status)]));

        move.Should().Be(LaunchMove.Stay);
    }

    [Fact]
    public void Every_database_running_moves_on_to_deploying()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.WaitingForServices,
                services: [Service(ServiceStatus.Running), Service(ServiceStatus.Running)]))
            .Kind.Should().Be(LaunchMoveKind.StartDeploying);
    }

    [Fact]
    public void A_failed_database_fails_the_launch_quoting_its_reason()
    {
        var move = LaunchAdvance.Decide(Snapshot(LaunchStatus.WaitingForServices,
            services: [Service(ServiceStatus.Running), Service(ServiceStatus.Failed, "shop-db", "image pull failed")]));

        move.Kind.Should().Be(LaunchMoveKind.Fail);
        move.FailedStep.Should().Be(LaunchStepKeys.Services);
        move.Message.Should().Contain("shop-db").And.Contain("image pull failed");
        move.MessageFa.Should().Contain("shop-db").And.Contain("image pull failed");
    }

    [Fact]
    public void A_failed_database_outranks_one_still_provisioning()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.WaitingForServices,
                services: [Service(ServiceStatus.Provisioning), Service(ServiceStatus.Failed)]))
            .Kind.Should().Be(LaunchMoveKind.Fail);
    }

    [Fact]
    public void Past_twenty_minutes_it_gives_up_and_says_which_is_still_provisioning()
    {
        var justBefore = LaunchAdvance.Decide(Snapshot(LaunchStatus.WaitingForServices, inStatus: TimeSpan.FromMinutes(20),
            services: [Service(ServiceStatus.Provisioning, "slow-db")]));
        justBefore.Should().Be(LaunchMove.Stay);

        var after = LaunchAdvance.Decide(Snapshot(LaunchStatus.WaitingForServices, inStatus: TimeSpan.FromMinutes(21),
            services: [Service(ServiceStatus.Running, "quick-db"), Service(ServiceStatus.Provisioning, "slow-db")]));
        after.Kind.Should().Be(LaunchMoveKind.Fail);
        after.Message.Should().Contain("slow-db").And.NotContain("quick-db");
        after.MessageFa.Should().Contain("slow-db");
    }

    [Fact]
    public void A_database_deleted_while_waiting_fails_rather_than_passing_as_all_running()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.WaitingForServices, expected: 2, services: [Service(ServiceStatus.Running)]))
            .FailedStep.Should().Be(LaunchStepKeys.Services);
    }

    [Theory]
    [InlineData(LaunchStatus.WaitingForServices)]
    [InlineData(LaunchStatus.Deploying)]
    [InlineData(LaunchStatus.Verifying)]
    public void An_app_deleted_in_the_middle_ends_the_launch(LaunchStatus status)
    {
        var move = LaunchAdvance.Decide(Snapshot(status, noApp: true));

        move.Kind.Should().Be(LaunchMoveKind.Fail);
        move.FailedStep.Should().Be(LaunchStepKeys.App);
    }

    [Fact]
    public void Deploying_with_no_deployment_yet_queues_one()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Deploying)).Kind.Should().Be(LaunchMoveKind.QueueDeployment);
    }

    [Theory]
    [InlineData(DeploymentStatus.Queued)]
    [InlineData(DeploymentStatus.Building)]
    [InlineData(DeploymentStatus.Pushing)]
    [InlineData(DeploymentStatus.Deploying)]
    [InlineData(DeploymentStatus.HealthChecking)]
    [InlineData(DeploymentStatus.PendingApproval)]
    public void A_deployment_that_has_not_settled_is_waited_for_and_an_approval_is_not_a_failure(DeploymentStatus status)
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Deploying, inStatus: TimeSpan.FromDays(3),
                deploymentId: Guid.CreateVersion7(), deployment: new(status, null)))
            .Should().Be(LaunchMove.Stay);
    }

    [Fact]
    public void A_deployment_that_succeeded_moves_on_to_the_checks()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Deploying, deploymentId: Guid.CreateVersion7(),
                deployment: new(DeploymentStatus.Succeeded, null)))
            .Kind.Should().Be(LaunchMoveKind.StartVerifying);
    }

    [Theory]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Cancelled)]
    [InlineData(DeploymentStatus.RolledBack)]
    public void A_deployment_that_did_not_succeed_fails_the_launch_with_its_own_message(DeploymentStatus status)
    {
        var move = LaunchAdvance.Decide(Snapshot(LaunchStatus.Deploying, deploymentId: Guid.CreateVersion7(),
            deployment: new(status, "npm ci exited with 1")));

        move.Kind.Should().Be(LaunchMoveKind.Fail);
        move.FailedStep.Should().Be(LaunchStepKeys.Deploy);
        move.Message.Should().Contain("npm ci exited with 1");
        move.MessageFa.Should().Contain("npm ci exited with 1");
    }

    [Fact]
    public void An_uploads_failure_says_to_upload_a_new_version_because_the_archive_is_gone()
    {
        var move = LaunchAdvance.Decide(Snapshot(LaunchStatus.Deploying, source: LaunchSourceKind.Upload,
            deploymentId: Guid.CreateVersion7(), deployment: new(DeploymentStatus.Failed, null)));

        move.Message.Should().Contain("upload a new version");
        move.MessageFa.Should().Contain("آپلود");
    }

    [Fact]
    public void A_deployment_row_that_vanished_fails_rather_than_waiting_for_ever()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Deploying, deploymentId: Guid.CreateVersion7(), deployment: null))
            .FailedStep.Should().Be(LaunchStepKeys.Deploy);
    }

    [Fact]
    public void Checks_run_at_most_every_ten_seconds_for_one_launch()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Verifying, sinceTouched: TimeSpan.FromSeconds(9), sinceVerifyStarted: TimeSpan.FromMinutes(1)))
            .Should().Be(LaunchMove.Stay);
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Verifying, sinceTouched: TimeSpan.FromSeconds(10), sinceVerifyStarted: TimeSpan.FromMinutes(1)))
            .Kind.Should().Be(LaunchMoveKind.RunChecks);
    }

    [Fact]
    public void Checks_that_can_never_finish_end_as_the_step_that_broke_not_as_a_launch_checking_for_ever()
    {
        var move = LaunchAdvance.Decide(Snapshot(LaunchStatus.Verifying, sinceVerifyStarted: TimeSpan.FromMinutes(16)));

        move.Kind.Should().Be(LaunchMoveKind.Fail);
        move.FailedStep.Should().Be(LaunchStepKeys.Verify);
    }

    [Fact]
    public void A_screen_left_for_a_day_is_swept_and_one_left_for_less_is_not()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.AwaitingConfirmation, inStatus: TimeSpan.FromHours(23))).Should().Be(LaunchMove.Stay);
        LaunchAdvance.Decide(Snapshot(LaunchStatus.AwaitingConfirmation, inStatus: TimeSpan.FromHours(25))).Kind.Should().Be(LaunchMoveKind.Abandon);
    }

    [Fact]
    public void A_read_whose_job_was_lost_is_written_off_after_twenty_minutes()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Inspecting, inStatus: TimeSpan.FromMinutes(19))).Should().Be(LaunchMove.Stay);

        var move = LaunchAdvance.Decide(Snapshot(LaunchStatus.Inspecting, inStatus: TimeSpan.FromMinutes(21)));
        move.Kind.Should().Be(LaunchMoveKind.FailInspection);
        move.MessageFa.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_create_that_never_came_back_is_handed_to_a_person_at_the_step_it_stopped()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Creating, inStatus: TimeSpan.FromMinutes(5), expected: 1)).Should().Be(LaunchMove.Stay);

        LaunchAdvance.Decide(Snapshot(LaunchStatus.Creating, inStatus: TimeSpan.FromMinutes(31), expected: 1))
            .FailedStep.Should().Be(LaunchStepKeys.Services);
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Creating, inStatus: TimeSpan.FromMinutes(31), expected: 1, services: [Service(ServiceStatus.Provisioning)]))
            .FailedStep.Should().Be(LaunchStepKeys.App);
    }

    [Fact]
    public void An_old_failure_lets_go_of_its_token_and_archive()
    {
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Failed, inStatus: TimeSpan.FromHours(23), leftovers: true)).Should().Be(LaunchMove.Stay);
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Failed, inStatus: TimeSpan.FromHours(25), leftovers: true)).Kind.Should().Be(LaunchMoveKind.DropLeftovers);
        LaunchAdvance.Decide(Snapshot(LaunchStatus.Failed, inStatus: TimeSpan.FromHours(25), leftovers: false)).Should().Be(LaunchMove.Stay);
    }

    [Theory]
    [InlineData(LaunchStatus.InspectFailed)]
    [InlineData(LaunchStatus.Succeeded)]
    [InlineData(LaunchStatus.SucceededWithProblems)]
    [InlineData(LaunchStatus.Abandoned)]
    public void A_finished_launch_is_left_alone(LaunchStatus status)
    {
        LaunchAdvance.Decide(Snapshot(status, inStatus: TimeSpan.FromDays(30))).Should().Be(LaunchMove.Stay);
        LaunchAdvance.IsTerminal(status).Should().BeTrue();
    }

    [Fact]
    public void A_retry_is_offered_only_where_running_the_step_again_could_help()
    {
        AppLaunch Failed(string step, LaunchSourceKind source = LaunchSourceKind.Git, string? archive = null, Guid? app = null, TimeSpan? age = null) => new()
        {
            Status = LaunchStatus.Failed, FailedStep = step, SourceKind = source, ArchivePath = archive, AppId = app,
            StatusChangedAt = Now - (age ?? TimeSpan.Zero)
        };

        LaunchAdvance.CanRetry(Failed(LaunchStepKeys.Deploy, app: AppId), Now).Should().BeTrue("a Git deployment can be queued again");
        LaunchAdvance.CanRetry(Failed(LaunchStepKeys.Deploy, LaunchSourceKind.Upload, app: AppId), Now)
            .Should().BeFalse("the pipeline consumed the archive");
        LaunchAdvance.CanRetry(Failed(LaunchStepKeys.Deploy, LaunchSourceKind.Upload, "/x.zip", AppId), Now)
            .Should().BeTrue("the deployment was refused before it began, so the archive is still here");
        LaunchAdvance.CanRetry(Failed(LaunchStepKeys.Verify, LaunchSourceKind.Upload, app: AppId), Now)
            .Should().BeTrue("the app is deployed, and asking the checks again needs no archive");
        LaunchAdvance.CanRetry(Failed(LaunchStepKeys.Services, age: TimeSpan.FromHours(25)), Now)
            .Should().BeFalse("with no app yet, the token has been swept and the app would be made without it");
        LaunchAdvance.CanRetry(Failed(LaunchStepKeys.Services, app: AppId, age: TimeSpan.FromHours(25)), Now)
            .Should().BeTrue("the app already has its token");
        LaunchAdvance.CanRetry(new AppLaunch { Status = LaunchStatus.Succeeded }, Now).Should().BeFalse();
    }
}
