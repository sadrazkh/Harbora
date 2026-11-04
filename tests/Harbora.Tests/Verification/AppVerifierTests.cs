using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Verification;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Harbora.Tests.Verification.VerifierHarness;

namespace Harbora.Tests.Verification;

/// <summary>
/// What the checks say, branch by branch. The rule they are all held to: "we could not check" and "it
/// works" are different sentences, so anything that could not be asked comes back Skipped with the
/// reason and is never a pass.
/// </summary>
public class AppVerifierTests
{
    private static readonly Regex PersianText = new(@"[؀-ۿ]");

    private static readonly DateTimeOffset InFiveMinutes = Now.AddMinutes(5);

    /// <summary>The English the real probe writes when a database's name does not resolve.</summary>
    private static string NameDoesNotResolve =>
        ConnectionProbe.Explain(ManagedServiceType.PostgreSql, "could not translate host name");

    private static string NothingListening =>
        ConnectionProbe.Explain(ManagedServiceType.PostgreSql, "Connection refused");

    private static async Task<AppVerificationReport> RunAsync(
        VerifierHarness h, App app, DateTimeOffset? giveUpAt = null, AppVerificationReport? previous = null,
        AppVerifier? verifier = null)
    {
        var report = await (verifier ?? h.Verifier()).RunAsync(app.Id, previous, giveUpAt ?? InFiveMinutes, default);

        // True of every check in every branch, so stated once here rather than in each test.
        foreach (var check in report.Checks)
        {
            PersianText.IsMatch(check.TitleFa).Should().BeTrue($"{check.Key}: \"{check.TitleFa}\" is read by a Persian speaker first");
            (check.Detail is null).Should().Be(check.DetailFa is null, $"{check.Key} says as much in both languages");
            (check.Fix is null).Should().Be(check.FixFa is null, $"{check.Key} says as much in both languages");
            if (check.Status == AppCheckStatus.Passed) check.Fix.Should().BeNull($"{check.Key} passed, so there is nothing to fix");
        }

        return report;
    }

    private static AppCheck Check(AppVerificationReport report, string key) => report.Checks.Single(c => c.Key == key);

    private static string ServiceKey(Harbora.Domain.Services.ManagedService svc) => $"service:{svc.Id}";

    // ---- running ----

    [Fact]
    public async Task A_running_container_passes()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();

        var running = Check(await RunAsync(h, app), "running");

        running.Status.Should().Be(AppCheckStatus.Passed);
    }

    [Theory]
    [InlineData(ServiceKind.Cron)]
    [InlineData(ServiceKind.ReleaseTask)]
    public async Task A_kind_that_runs_and_exits_is_skipped_with_the_reason_and_never_a_pass(ServiceKind kind)
    {
        var h = new VerifierHarness();
        var app = h.GivenApp(kind: kind);

        var report = await RunAsync(h, app);

        foreach (var key in new[] { "running", "restarts", "logs" })
        {
            Check(report, key).Status.Should().Be(AppCheckStatus.Skipped);
            Check(report, key).Detail.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task No_active_deployment_fails_and_points_at_the_deployments_tab()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();

        var report = await RunAsync(h, app);

        var running = Check(report, "running");
        running.Status.Should().Be(AppCheckStatus.Failed);
        running.LinkPath.Should().Be($"/apps/{app.Id}/deployments");

        // Nothing to look at is not the same as nothing wrong.
        Check(report, "restarts").Status.Should().Be(AppCheckStatus.Skipped);
        Check(report, "logs").Status.Should().Be(AppCheckStatus.Skipped);
    }

    [Fact]
    public async Task A_container_that_has_stopped_fails()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.GivenContainer(ContainerOf(app), app, state: "exited");

        var running = Check(await RunAsync(h, app), "running");

        running.Status.Should().Be(AppCheckStatus.Failed);
        running.LinkPath.Should().Be($"/apps/{app.Id}/logs");
    }

    [Fact]
    public async Task A_container_that_is_gone_fails()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);

        Check(await RunAsync(h, app), "running").Status.Should().Be(AppCheckStatus.Failed);
    }

    [Fact]
    public async Task Another_deployments_container_running_is_not_this_one_running()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app, number: 8);
        h.GivenContainer(ContainerOf(app, 7), app);

        Check(await RunAsync(h, app), "running").Status.Should().Be(AppCheckStatus.Failed);
    }

    [Fact]
    public async Task Two_of_three_replicas_says_two_of_three_rather_than_showing_green()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp(configure: a => a.DesiredReplicas = 3);
        h.GivenActiveDeployment(app);
        var names = DeploymentPlanning.ReplicaContainerNames(app.WorkspaceId, app.Slug, 7, 3);
        h.GivenContainer(names[0], app);
        h.GivenContainer(names[1], app);
        h.GivenContainer(names[2], app, state: "exited");

        var running = Check(await RunAsync(h, app), "running");

        running.Status.Should().Be(AppCheckStatus.Warning);
        running.Title.Should().Contain("2 of its 3");
    }

    [Fact]
    public async Task A_compose_stack_is_found_by_its_service_labels()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp(configure: a => a.SourceType = AppSourceType.DockerCompose);
        h.GivenActiveDeployment(app);
        h.GivenContainer(DeploymentPlanning.ComposeContainerName(app.WorkspaceId, app.Slug, "web", 7), app, composeService: "web");
        h.GivenContainer(DeploymentPlanning.ComposeContainerName(app.WorkspaceId, app.Slug, "worker", 7), app, composeService: "worker");

        // Last deployment's stack, still there during a cutover: not what is being served.
        h.GivenContainer(DeploymentPlanning.ComposeContainerName(app.WorkspaceId, app.Slug, "web", 6), app, state: "exited", composeService: "web");

        var report = await RunAsync(h, app);

        Check(report, "running").Status.Should().Be(AppCheckStatus.Passed);
        Check(report, "restarts").Status.Should().Be(AppCheckStatus.Passed);
    }

    [Fact]
    public async Task An_engine_that_refuses_is_skipped_saying_this_server_cannot_answer_yet()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.Engines.On(h.ServerId, EngineWith.Overrides(h.Docker,
            ("ListContainersAsync", _ => throw new NotSupportedException("a v1 node has no such verb"))));

        var running = Check(await RunAsync(h, app), "running");

        running.Status.Should().Be(AppCheckStatus.Skipped);
        running.Detail.Should().Contain("cannot answer this yet");
    }

    // ---- restarts ----

    [Fact]
    public async Task Up_for_an_hour_with_no_restarts_passes()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();

        Check(await RunAsync(h, app), "restarts").Status.Should().Be(AppCheckStatus.Passed);
    }

    [Fact]
    public async Task The_first_thirty_seconds_are_not_yet_evidence()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.GivenContainer(ContainerOf(app), app, startedAt: Now.AddSeconds(-29));

        var report = await RunAsync(h, app);

        Check(report, "restarts").Status.Should().Be(AppCheckStatus.Pending);
        report.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task At_thirty_seconds_it_is_evidence()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.GivenContainer(ContainerOf(app), app, startedAt: Now.AddSeconds(-30));

        Check(await RunAsync(h, app), "restarts").Status.Should().Be(AppCheckStatus.Passed);
    }

    [Fact]
    public async Task Past_the_give_up_time_a_fresh_start_is_read_for_what_it_is_rather_than_waited_on()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.GivenContainer(ContainerOf(app), app, startedAt: Now.AddSeconds(-5));

        var restarts = Check(await RunAsync(h, app, giveUpAt: Now), "restarts");

        restarts.Status.Should().Be(AppCheckStatus.Passed);
        restarts.Detail.Should().Contain("less than half a minute", "it must not claim more than it saw");
    }

    [Fact]
    public async Task A_restart_count_fails_with_the_tail_of_its_log_and_points_at_the_logs()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.GivenContainer(ContainerOf(app), app, restartCount: 4);
        h.Docker.ContainerLogs = "booting\nTypeError: cannot read 'x' of undefined\n";

        var restarts = Check(await RunAsync(h, app), "restarts");

        restarts.Status.Should().Be(AppCheckStatus.Failed);
        restarts.Detail.Should().Contain("4 time(s)").And.Contain("TypeError: cannot read");
        restarts.DetailFa.Should().Contain("TypeError: cannot read");
        restarts.LinkPath.Should().Be($"/apps/{app.Id}/logs");
    }

    [Fact]
    public async Task A_crash_loop_is_failed_at_once_even_though_a_restart_makes_it_look_freshly_started()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.GivenContainer(ContainerOf(app), app, restartCount: 9, startedAt: Now.AddSeconds(-2));

        Check(await RunAsync(h, app), "restarts").Status.Should().Be(AppCheckStatus.Failed);
    }

    [Fact]
    public async Task A_log_that_cannot_be_read_costs_the_tail_and_never_the_failure()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.GivenContainer(ContainerOf(app), app, restartCount: 2);
        h.Engines.On(h.ServerId, EngineWith.Overrides(h.Docker,
            ("GetLogsAsync", _ => throw new InvalidOperationException("the daemon went away"))));

        var report = await RunAsync(h, app);

        Check(report, "restarts").Status.Should().Be(AppCheckStatus.Failed);
        Check(report, "logs").Status.Should().Be(AppCheckStatus.Skipped);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_null_figure_is_skipped_and_never_read_as_zero(bool restartsKnown, bool startKnown)
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        var name = ContainerOf(app);
        h.Docker.SeedContainer(name, app.Slug);
        h.Engines.On(h.ServerId, EngineWith.Overrides(h.Docker,
            ("GetLifecycleAsync", _ => Task.FromResult<ContainerLifecycle?>(new ContainerLifecycle(
                restartsKnown ? 0 : null, startKnown ? Now.AddHours(-1) : null)))));

        Check(await RunAsync(h, app), "restarts").Status.Should().Be(AppCheckStatus.Skipped);
    }

    [Fact]
    public async Task No_lifecycle_answer_at_all_is_skipped()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.Docker.SeedContainer(ContainerOf(app), app.Slug);

        Check(await RunAsync(h, app), "restarts").Status.Should().Be(AppCheckStatus.Skipped);
    }

    // ---- service: its status, before anything costs a container ----

    [Theory]
    [InlineData(ServiceStatus.Stopped)]
    [InlineData(ServiceStatus.Failed)]
    public async Task A_database_that_is_not_running_fails_with_a_link_and_why_and_starts_no_container(ServiceStatus status)
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app, status: status, configure: s => s.ErrorMessage = "disk full");

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Failed);
        check.Detail.Should().Be("disk full");
        check.LinkPath.Should().Be($"/databases/{svc.Id}");
        h.Services.Tested.Should().BeEmpty();
    }

    [Fact]
    public async Task A_database_still_being_set_up_waits_and_then_fails_saying_what_it_waited_for()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app, status: ServiceStatus.Provisioning);

        var waiting = await RunAsync(h, app);
        Check(waiting, ServiceKey(svc)).Status.Should().Be(AppCheckStatus.Pending);
        waiting.IsComplete.Should().BeFalse();

        h.Clock.UtcNow = InFiveMinutes;
        var given = Check(await RunAsync(h, app, previous: waiting), ServiceKey(svc));

        given.Status.Should().Be(AppCheckStatus.Failed);
        given.Detail.Should().Contain("still being set up");
        h.Services.Tested.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ManagedServiceType.MongoDb)]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public async Task An_engine_with_no_probe_is_skipped_with_the_reason_translated_and_is_not_a_pass(ManagedServiceType type)
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app, type: type);

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Skipped);
        check.Detail.Should().Be(ConnectionProbe.WhyUnsupported(type));
        PersianText.IsMatch(check.DetailFa!).Should().BeTrue();
        h.Services.Tested.Should().BeEmpty();
    }

    // ---- service: the false passes this exists to catch ----

    [Fact]
    public async Task A_database_on_another_server_fails_because_the_test_would_pass_where_the_app_is_not()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app, configure: s => s.ServerId = Guid.CreateVersion7());

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Failed);
        h.Services.Tested.Should().BeEmpty("the probe would run on the database's server and answer happily");
    }

    [Fact]
    public async Task A_database_in_another_environment_fails_and_is_not_joined_to_this_one()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app, configure: s => s.EnvironmentId = Guid.CreateVersion7());

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Failed);
        h.Services.Tested.Should().BeEmpty("the probe runs on the database's own network, not this app's");
        h.Docker.ConnectedNetworks(svc.ContainerName).Should().BeEmpty(
            "bridging two environments is exactly the isolation an environment exists for");
    }

    // ---- service: asking it ----

    [Fact]
    public async Task A_database_that_answers_passes_and_says_exactly_what_that_proves()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app);

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Passed);
        check.Title.Should().Contain("answers on this app's network");
        check.Detail.Should().Contain("not that the app itself has connected");
        h.Services.Tested.Should().Equal(svc.Id);
        h.Docker.ConnectedNetworks(svc.ContainerName).Should().BeEmpty("nothing was wrong, so nothing was touched");
    }

    [Fact]
    public async Task The_self_heal_the_second_test_passes_only_after_the_right_container_joined_the_right_network()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app);
        var network = EnvironmentNetwork.For("shop", "production", h.EnvironmentId);

        // Answers only once the membership really exists, so a verifier that merely retried would fail.
        h.Services.Test = (_, _) => Task.FromResult(
            h.Docker.ConnectedNetworks(svc.ContainerName).Contains(network) ? null : NameDoesNotResolve);

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Passed);
        check.Detail.Should().Contain("was not on this app's private network");
        check.DetailFa.Should().Contain("هاربورا آن را به این شبکه وصل کرد");
        h.Services.Tested.Should().HaveCount(2);
        h.Docker.ConnectedNetworks(svc.ContainerName).Should().Equal(network);
        h.Docker.Calls.Select(c => c.Operation).Should().NotContain(
            ["RestartContainerAsync", "StopContainerAsync", "RemoveContainerAsync"], "additive, never a restart");
    }

    [Fact]
    public async Task Still_failing_after_connecting_fails_with_the_probes_explanation_in_both_languages()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app);
        h.Services.Test = (_, _) => Task.FromResult<string?>(NothingListening);

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Failed);
        check.Detail.Should().Be(NothingListening);
        check.DetailFa.Should().Be(ConnectionProbe.InPersian(NothingListening));
        PersianText.IsMatch(check.DetailFa!).Should().BeTrue();
        check.LinkPath.Should().Be($"/databases/{svc.Id}");
        h.Services.Tested.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_connect_that_throws_is_not_the_answer_the_second_test_still_is()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app);
        h.Services.Test = (_, _) => Task.FromResult<string?>(NothingListening);
        h.Engines.On(h.ServerId, EngineWith.Overrides(h.Docker,
            ("ConnectNetworkAsync", _ => throw new InvalidOperationException("agent is unreachable"))));

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Failed);
        check.Detail.Should().Be(NothingListening);
        h.Services.Tested.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_v1_node_that_cannot_run_a_one_off_is_skipped_and_the_rest_still_run()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app);
        var svc = h.GivenService(app);
        h.Services.Test = (_, _) => throw new NotSupportedException("A v1 node has no verb for this.");

        var report = await RunAsync(h, app);

        Check(report, ServiceKey(svc)).Status.Should().Be(AppCheckStatus.Skipped);
        Check(report, ServiceKey(svc)).Detail.Should().Contain("cannot answer this yet");
        Check(report, "url").Status.Should().Be(AppCheckStatus.Passed);
        Check(report, "logs").Status.Should().Be(AppCheckStatus.Passed);
    }

    [Fact]
    public async Task A_test_that_hangs_is_skipped_saying_so()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app);
        h.Services.Test = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

        var report = await RunAsync(h, app, verifier: h.Verifier(connectionTestTimeout: TimeSpan.FromMilliseconds(50)));

        var check = Check(report, ServiceKey(svc));
        check.Status.Should().Be(AppCheckStatus.Skipped);
        check.Detail.Should().Contain("did not finish in time");
        Check(report, "logs").Status.Should().Be(AppCheckStatus.Passed, "the rest still run");
    }

    [Fact]
    public async Task Ten_databases_behind_a_hung_daemon_share_one_budget()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var services = Enumerable.Range(0, 10).Select(i => h.GivenService(app, name: $"db{i}")).ToList();
        h.Services.Test = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = await RunAsync(h, app, verifier: h.Verifier(servicesBudget: TimeSpan.FromMilliseconds(200)));

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        services.Should().OnlyContain(s => Check(report, ServiceKey(s)).Status == AppCheckStatus.Skipped);
    }

    [Fact]
    public async Task A_running_daemon_that_hangs_on_a_listing_cannot_hold_the_run()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.Engines.On(h.ServerId, EngineWith.Overrides(h.Docker,
            ("ListContainersAsync", args => EngineWith.HangAsync<IReadOnlyList<ContainerInfo>>((CancellationToken)args[1]!))));

        var report = await RunAsync(h, app, verifier: h.Verifier(engineTimeout: TimeSpan.FromMilliseconds(50)));

        Check(report, "running").Status.Should().Be(AppCheckStatus.Skipped);
        Check(report, "running").Detail.Should().Contain("in time");
        Check(report, "restarts").Status.Should().Be(AppCheckStatus.Passed);
    }

    [Fact]
    public async Task Only_this_apps_attachments_are_checked()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var other = h.GivenDeployedApp("other");
        var mine = h.GivenService(app, name: "mine");
        var theirs = h.GivenService(other, name: "theirs");

        var report = await RunAsync(h, app);

        report.Checks.Select(c => c.Key).Should().Contain(ServiceKey(mine)).And.NotContain(ServiceKey(theirs));
        h.Services.Tested.Should().Equal(mine.Id);
    }

    [Fact]
    public async Task Two_logical_databases_on_one_instance_are_one_check_and_one_container()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app);
        h.Db.AppManagedServices.Add(new Harbora.Domain.Services.AppManagedService
        {
            AppId = app.Id, ManagedServiceId = svc.Id, Alias = "second", AttachOrder = 9,
            ManagedServiceDatabaseId = Guid.CreateVersion7(), HasUnpublishedChanges = true
        });
        await h.Db.SaveChangesAsync();

        var report = await RunAsync(h, app);

        report.Checks.Count(c => c.Key == ServiceKey(svc)).Should().Be(1);
        h.Services.Tested.Should().ContainSingle();
        Check(report, ServiceKey(svc)).Status.Should().Be(AppCheckStatus.Warning,
            "either attachment still being unpublished means the app is missing something");
    }

    // ---- service: HasUnpublishedChanges ----

    [Fact]
    public async Task Unpublished_changes_outrank_a_pass()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app, unpublished: true);

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Warning);
        check.Detail.Should().Contain("does not have this database's current variables");
        check.Fix.Should().Contain("Redeploy");
        check.LinkPath.Should().Be($"/apps/{app.Id}/deployments");
    }

    [Fact]
    public async Task Unpublished_changes_do_not_outrank_a_failure()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app, unpublished: true);
        h.Services.Test = (_, _) => Task.FromResult<string?>(NothingListening);

        Check(await RunAsync(h, app), ServiceKey(svc)).Status.Should().Be(AppCheckStatus.Failed);
    }

    [Fact]
    public async Task Unpublished_changes_are_still_said_about_a_database_that_could_not_be_tested()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app, type: ManagedServiceType.MongoDb, unpublished: true);

        var check = Check(await RunAsync(h, app), ServiceKey(svc));

        check.Status.Should().Be(AppCheckStatus.Warning);
        check.Detail.Should().Contain(ConnectionProbe.WhyUnsupported(ManagedServiceType.MongoDb));
    }

    // ---- previous ----

    [Fact]
    public async Task A_passed_database_is_not_tested_again_in_the_same_run_and_everything_else_is_reread()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app);
        var svc = h.GivenService(app);
        h.Probe.Answer = new UptimeProbeResult(ProbeOutcome.Down, null, 10, "no answer");

        var first = await RunAsync(h, app);
        Check(first, "url").Status.Should().Be(AppCheckStatus.Pending);
        h.Services.Tested.Should().ContainSingle();

        h.Clock.UtcNow = Now.AddSeconds(10);
        h.Probe.Answer = new UptimeProbeResult(ProbeOutcome.Up, 200, 10, "answered 200.");
        var second = await RunAsync(h, app, previous: first);

        h.Services.Tested.Should().ContainSingle("each one costs a container");
        Check(second, ServiceKey(svc)).Status.Should().Be(AppCheckStatus.Passed);
        Check(second, "url").Status.Should().Be(AppCheckStatus.Passed);
        second.StartedAt.Should().Be(first.StartedAt, "it is the same run");
    }

    [Fact]
    public async Task A_database_that_stopped_since_it_passed_does_not_go_on_reading_as_a_pass()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app);
        var first = await RunAsync(h, app);

        svc.Status = ServiceStatus.Stopped;
        await h.Db.SaveChangesAsync();

        Check(await RunAsync(h, app, previous: first), ServiceKey(svc)).Status.Should().Be(AppCheckStatus.Failed);
    }

    [Fact]
    public async Task A_pass_from_before_a_redeploy_is_not_this_run()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenService(app);
        var first = await RunAsync(h, app);

        var next = h.GivenActiveDeployment(app, number: 8);
        h.GivenContainer(ContainerOf(app, 8), app);
        var second = await RunAsync(h, app, previous: first);

        h.Services.Tested.Should().HaveCount(2);
        second.DeploymentId.Should().Be(next.Id);
    }

    [Fact]
    public async Task Another_apps_report_is_not_this_run()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var other = h.GivenDeployedApp("other");
        var svc = h.GivenService(app);
        var forged = new AppVerificationReport(other.Id, null, Now, null,
            [new AppCheck(ServiceKey(svc), AppCheckStatus.Passed, "Passed", "تأیید")]);

        await RunAsync(h, app, previous: forged);

        h.Services.Tested.Should().ContainSingle();
    }

    [Fact]
    public async Task A_warning_for_unpublished_changes_is_not_retested_while_it_still_holds_and_is_once_it_clears()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app, unpublished: true);

        var first = await RunAsync(h, app);
        Check(await RunAsync(h, app, previous: first), ServiceKey(svc)).Status.Should().Be(AppCheckStatus.Warning);
        h.Services.Tested.Should().ContainSingle();

        var attachment = await h.Db.AppManagedServices.SingleAsync(a => a.AppId == app.Id);
        attachment.HasUnpublishedChanges = false;
        await h.Db.SaveChangesAsync();

        Check(await RunAsync(h, app, previous: first), ServiceKey(svc)).Status.Should().Be(AppCheckStatus.Passed);
        h.Services.Tested.Should().HaveCount(2, "the warning could have been a test that never ran");
    }

    // ---- url ----

    [Theory]
    [InlineData(ServiceKind.Worker)]
    [InlineData(ServiceKind.Private)]
    public async Task A_kind_with_no_public_traffic_has_no_address_check(ServiceKind kind)
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp(kind: kind);

        var report = await RunAsync(h, app);

        report.Checks.Select(c => c.Key).Should().NotContain("url");
        h.Probe.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task No_domain_is_skipped_saying_why()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();

        var url = Check(await RunAsync(h, app), "url");

        url.Status.Should().Be(AppCheckStatus.Skipped);
        url.Detail.Should().Contain("no domain");
    }

    [Fact]
    public async Task It_asks_the_primary_address_at_the_root_and_expects_an_ordinary_answer()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app, "other.example.com", primary: false);
        h.GivenDomain(app, "shop.example.com", primary: true);

        var url = Check(await RunAsync(h, app), "url");

        url.Status.Should().Be(AppCheckStatus.Passed);
        h.Probe.Asked.Should().ContainSingle().Which.Url.Should().Be(new Uri("https://shop.example.com/"));
    }

    [Fact]
    public async Task A_domain_with_ssl_off_is_asked_over_http_or_every_probe_would_blame_the_app()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app, ssl: false);

        await RunAsync(h, app);

        h.Probe.Asked.Single().Url.Should().Be(new Uri("http://shop.example.com/"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    public async Task A_status_below_500_at_the_root_passes_because_something_is_serving(int status)
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app);
        h.Probe.Answer = new UptimeProbeResult(ProbeOutcome.Down, status, 5, $"answered {status}, expected 200.");

        var url = Check(await RunAsync(h, app), "url");

        url.Status.Should().Be(AppCheckStatus.Passed);
        url.Detail.Should().Contain("no page at the root", "it must not read as a healthy home page");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task A_5xx_fails(int status)
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app);
        h.Probe.Answer = new UptimeProbeResult(ProbeOutcome.Down, status, 5, $"answered {status}, expected 200.");

        var url = Check(await RunAsync(h, app), "url");

        url.Status.Should().Be(AppCheckStatus.Failed);
        url.Detail.Should().Contain(status.ToString());
        url.Detail.Should().NotContain("expected 200", "a person would go looking for why it should be 200");
    }

    [Fact]
    public async Task No_answer_waits_for_the_certificate_and_then_fails_naming_dns_and_port_80()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app);
        h.Probe.Answer = new UptimeProbeResult(ProbeOutcome.Down, null, 10_000, "https://shop.example.com/ did not answer within 10s (timed out).");

        var waiting = await RunAsync(h, app);
        Check(waiting, "url").Status.Should().Be(AppCheckStatus.Pending);
        Check(waiting, "url").Fix.Should().BeNull("it has not failed yet");
        waiting.CompletedAt.Should().BeNull();

        h.Clock.UtcNow = InFiveMinutes;
        var final = await RunAsync(h, app, previous: waiting);
        var url = Check(final, "url");

        url.Status.Should().Be(AppCheckStatus.Failed);
        url.Detail.Should().Contain("did not answer within 10s");
        url.Fix.Should().Contain("DNS").And.Contain("port 80");
        url.FixFa.Should().Contain("DNS").And.Contain("80");
        final.CompletedAt.Should().Be(InFiveMinutes);
    }

    [Fact]
    public async Task A_probe_that_could_not_run_is_skipped_because_that_is_the_checker_and_not_the_app()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app);
        h.Probe.Answer = new UptimeProbeResult(ProbeOutcome.CouldNotRun, null, 1, "the check itself failed before it could ask.");

        var url = Check(await RunAsync(h, app, giveUpAt: Now), "url");

        url.Status.Should().Be(AppCheckStatus.Skipped);
        url.Detail.Should().Contain("the check itself failed");
    }

    [Fact]
    public async Task Nothing_deployed_is_not_a_fact_about_its_address()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenDomain(app);

        var url = Check(await RunAsync(h, app, giveUpAt: Now), "url");

        url.Status.Should().Be(AppCheckStatus.Skipped);
        h.Probe.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task Maintenance_mode_answers_503_on_purpose_so_it_is_not_a_failure()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp(configure: a => a.MaintenanceMode = true);
        h.GivenDomain(app);

        Check(await RunAsync(h, app), "url").Status.Should().Be(AppCheckStatus.Skipped);
        h.Probe.Asked.Should().BeEmpty();
    }

    // ---- logs ----

    [Fact]
    public async Task A_healthy_log_passes()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.Docker.ContainerLogs = "Connected to database\nGET /health 200 3ms\n";

        var logs = Check(await RunAsync(h, app), "logs");

        logs.Status.Should().Be(AppCheckStatus.Passed);
        logs.Detail.Should().Contain("200 lines");
    }

    [Fact]
    public async Task An_empty_log_passes_but_says_it_read_nothing()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();

        var logs = Check(await RunAsync(h, app), "logs");

        logs.Status.Should().Be(AppCheckStatus.Passed);
        logs.Detail.Should().Contain("has not written anything");
    }

    [Fact]
    public async Task Findings_are_a_warning_with_each_ones_sentence_and_a_short_quote()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.Docker.ContainerLogs =
            "Error: connect ECONNREFUSED 127.0.0.1:5432\nerror: relation \"users\" does not exist\n";

        var logs = Check(await RunAsync(h, app), "logs");

        logs.Status.Should().Be(AppCheckStatus.Warning);
        logs.Detail.Should().Contain("DATABASE_URL").And.Contain("127.0.0.1:5432").And.Contain("migrations");
        PersianText.IsMatch(logs.DetailFa!).Should().BeTrue();
        logs.LinkPath.Should().Be($"/apps/{app.Id}/logs");
    }

    [Fact]
    public async Task A_compose_stack_with_no_containers_left_is_not_a_clean_log()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp(configure: a => a.SourceType = AppSourceType.DockerCompose);
        h.GivenActiveDeployment(app);

        var report = await RunAsync(h, app);

        Check(report, "running").Status.Should().Be(AppCheckStatus.Failed);
        Check(report, "restarts").Status.Should().Be(AppCheckStatus.Skipped);
        Check(report, "logs").Status.Should().Be(AppCheckStatus.Skipped);
    }

    [Fact]
    public async Task Every_replicas_log_is_read()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp(configure: a => a.DesiredReplicas = 2);
        h.GivenActiveDeployment(app);
        var names = DeploymentPlanning.ReplicaContainerNames(app.WorkspaceId, app.Slug, 7, 2);
        h.GivenContainer(names[0], app);
        h.GivenContainer(names[1], app);
        h.Docker.ContainerLogsById[names[0]] = "fine";
        h.Docker.ContainerLogsById[names[1]] = "Error: Cannot find module 'pg'";

        Check(await RunAsync(h, app), "logs").Status.Should().Be(AppCheckStatus.Warning);
    }

    // ---- a secret never reaches a check ----

    [Fact]
    public async Task A_secret_in_a_log_line_never_reaches_a_check_or_the_stored_row()
    {
        const string envSecret = "env-secret-value-1234";
        const string dbPassword = "db-password-value-5678";
        const string neverHeardOf = "hardcoded-pw-9012";

        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.GivenContainer(ContainerOf(app), app, restartCount: 3);
        h.GivenSecret(app, "JWT_SECRET", envSecret);
        h.GivenService(app, name: "healthy", password: dbPassword);
        var broken = h.GivenService(app, name: "broken", password: dbPassword, status: ServiceStatus.Failed,
            configure: s => s.ErrorMessage = $"provision failed, password {dbPassword} rejected");
        var refusing = h.GivenService(app, name: "refusing", password: dbPassword);
        h.Services.Test = (id, _) => Task.FromResult<string?>(
            id == refusing.Id ? $"The connection failed. FATAL: bad login with {dbPassword} or {envSecret}" : null);

        h.Docker.ContainerLogs = string.Join('\n',
            $"error: relation \"{envSecret}\" does not exist",
            $"Error: Cannot find module '{dbPassword}'",
            $"connecting postgres://app:{neverHeardOf}@localhost:5432/shop: connect ECONNREFUSED 127.0.0.1:5432",
            $"boot failed: JWT_SECRET={envSecret} DATABASE_URL=postgres://app:{dbPassword}@db/shop");

        var report = await RunAsync(h, app);

        Check(report, "restarts").Status.Should().Be(AppCheckStatus.Failed, "so the tail, the widest quote there is, is in play");
        Check(report, "logs").Status.Should().Be(AppCheckStatus.Warning);
        Check(report, ServiceKey(broken)).Status.Should().Be(AppCheckStatus.Failed);
        Check(report, ServiceKey(refusing)).Status.Should().Be(AppCheckStatus.Failed);

        var everything = JsonSerializer.Serialize(report)
                         + (await h.Db.AppVerifications.IgnoreQueryFilters().SingleAsync()).ChecksJson;

        everything.Should().NotContain(envSecret);
        everything.Should().NotContain(dbPassword);
        everything.Should().NotContain(neverHeardOf);
        Check(report, "restarts").Detail.Should().Contain("***", "the line is still quoted, with the value gone");
    }

    [Fact]
    public async Task The_longer_of_two_overlapping_secrets_is_not_left_half_on_the_page()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        h.GivenActiveDeployment(app);
        h.GivenContainer(ContainerOf(app), app, restartCount: 1);
        h.GivenSecret(app, "SHORT", "abcd1234");
        h.GivenSecret(app, "LONG", "abcd1234-and-the-rest-of-it");
        h.Docker.ContainerLogs = "value abcd1234-and-the-rest-of-it here";

        var restarts = Check(await RunAsync(h, app), "restarts");

        restarts.Detail.Should().NotContain("and-the-rest-of-it");
    }

    // ---- RunAsync ----

    [Fact]
    public async Task A_server_that_cannot_be_reached_skips_what_needs_it_and_the_rest_still_run()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app);
        var svc = h.GivenService(app);
        h.Engines.Unreachable(h.ServerId, "Server 'x' cannot be reached.");

        var report = await RunAsync(h, app);

        Check(report, "running").Status.Should().Be(AppCheckStatus.Skipped);
        Check(report, "restarts").Status.Should().Be(AppCheckStatus.Skipped);
        Check(report, "logs").Status.Should().Be(AppCheckStatus.Skipped);
        Check(report, ServiceKey(svc)).Status.Should().Be(AppCheckStatus.Passed);
        Check(report, "url").Status.Should().Be(AppCheckStatus.Passed);
        report.Checks.Should().NotContain(c => c.Detail != null && c.Detail.Contains("Server 'x'"),
            "the exception's own words stay in the log");
    }

    public static TheoryData<string> Sessionless => new() { "system", "anonymous-request" };

    /// <summary>
    /// "system" is what the launch tick has. "anonymous-request" is the trap this codebase keeps falling
    /// into: scoped to no workspace, so a filtered read comes back empty and reports success.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sessionless))]
    public async Task It_works_with_no_session_and_reads_the_real_rows_rather_than_an_empty_filter(string scope)
    {
        var h = new VerifierHarness(scope == "system"
            ? SystemWorkspaceScope.Instance
            : new FixedWorkspaceScope(Guid.Empty));
        var app = h.GivenDeployedApp();
        h.GivenDomain(app);
        var svc = h.GivenService(app);

        var report = await RunAsync(h, app);

        report.DeploymentId.Should().Be(app.ActiveDeploymentId);
        report.Checks.Select(c => c.Key).Should().Equal("running", "restarts", ServiceKey(svc), "url", "logs");
        report.Checks.Should().OnlyContain(c => c.Status == AppCheckStatus.Passed);
        h.Probe.Asked.Should().ContainSingle();

        var row = await h.Db.AppVerifications.IgnoreQueryFilters().SingleAsync();
        row.WorkspaceId.Should().Be(app.WorkspaceId, "taken from the app, because there is no session to take it from");
    }

    [Fact]
    public async Task One_row_per_app_overwritten_with_complete_set_only_when_nothing_is_waiting()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenDomain(app);
        h.Probe.Answer = new UptimeProbeResult(ProbeOutcome.Down, null, 1, "no answer");

        var waiting = await RunAsync(h, app);

        var row = await h.Db.AppVerifications.IgnoreQueryFilters().SingleAsync();
        row.AppId.Should().Be(app.Id);
        row.DeploymentId.Should().Be(app.ActiveDeploymentId);
        row.StartedAt.Should().Be(Now);
        row.CompletedAt.Should().BeNull();
        row.ChecksJson.Should().Contain("\"Pending\"", "serialised by name, as the enum promises");

        h.Clock.UtcNow = Now.AddSeconds(30);
        h.Probe.Answer = new UptimeProbeResult(ProbeOutcome.Up, 200, 1, "answered 200.");
        await RunAsync(h, app, previous: waiting);

        (await h.Db.AppVerifications.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        row = await h.Db.AppVerifications.IgnoreQueryFilters().SingleAsync();
        row.StartedAt.Should().Be(Now);
        row.CompletedAt.Should().Be(Now.AddSeconds(30));
    }

    [Fact]
    public async Task An_app_that_no_longer_exists_is_an_answer_rather_than_a_throw_and_stores_nothing()
    {
        var h = new VerifierHarness();
        var appId = Guid.CreateVersion7();

        var report = await h.Verifier().RunAsync(appId, null, InFiveMinutes, default);

        report.HasFailures.Should().BeTrue();
        report.IsComplete.Should().BeTrue();
        (await h.Db.AppVerifications.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task The_callers_cancellation_propagates_when_it_was_already_cancelled()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var run = () => h.Verifier().RunAsync(app.Id, null, InFiveMinutes, cancelled.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_mistaken_for_a_timeout_when_it_arrives_mid_test()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        h.GivenService(app);
        using var caller = new CancellationTokenSource();
        h.Services.Test = async (_, ct) =>
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

        var run = () => h.Verifier().RunAsync(app.Id, null, InFiveMinutes, caller.Token);

        await run.Should().ThrowAsync<OperationCanceledException>(
            "a shutdown must not be recorded as \"the server did not answer\"");
    }

    // ---- GetLatestAsync ----

    [Fact]
    public async Task The_latest_is_read_back_as_it_was_stored()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        var svc = h.GivenService(app);
        var stored = await RunAsync(h, app);

        var latest = await h.Verifier().GetLatestAsync(app.Id, default);

        latest.Should().NotBeNull();
        latest!.AppId.Should().Be(app.Id);
        latest.DeploymentId.Should().Be(stored.DeploymentId);
        latest.CompletedAt.Should().Be(stored.CompletedAt);
        latest.Checks.Should().BeEquivalentTo(stored.Checks);
        latest.Checks.Should().Contain(c => c.Key == ServiceKey(svc));
    }

    [Fact]
    public async Task Never_checked_is_null()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();

        (await h.Verifier().GetLatestAsync(app.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Another_workspaces_latest_is_null_and_the_tick_can_read_any()
    {
        var h = new VerifierHarness();
        var app = h.GivenDeployedApp();
        await RunAsync(h, app);

        AppVerifier VerifierOver(IWorkspaceScope scope) =>
            new(h.Open(scope), h.Engines, h.Services, h.Probe, h.Protector,
                new Harbora.Infrastructure.Security.SecretRedactor(), h.Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AppVerifier>.Instance);

        (await VerifierOver(new FixedWorkspaceScope(Guid.CreateVersion7())).GetLatestAsync(app.Id, default))
            .Should().BeNull("it is not this workspace's");
        (await VerifierOver(new FixedWorkspaceScope(app.WorkspaceId)).GetLatestAsync(app.Id, default))
            .Should().NotBeNull();
        (await VerifierOver(SystemWorkspaceScope.Instance).GetLatestAsync(app.Id, default))
            .Should().NotBeNull("no session means unscoped, so the launch tick can read it");
    }

    private static async Task StoreRow(VerifierHarness h, App app, string json)
    {
        h.Db.AppVerifications.Add(new AppVerification
        {
            AppId = app.Id, WorkspaceId = app.WorkspaceId, StartedAt = Now, ChecksJson = json
        });
        await h.Db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"key\":\"running\"}")]
    [InlineData("[]")]
    [InlineData("[{\"status\":\"Passed\"}]")]
    public async Task A_row_with_nothing_readable_in_it_is_the_same_as_never_having_asked(string json)
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        await StoreRow(h, app, json);

        (await h.Verifier().GetLatestAsync(app.Id, default)).Should().BeNull(
            "an empty report reads as complete with no failures, which is good news nobody earned");
    }

    [Fact]
    public async Task A_status_this_build_has_never_heard_of_is_skipped_and_never_a_pass()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        await StoreRow(h, app, "[{\"key\":\"url\",\"status\":\"Quarantined\",\"title\":\"t\",\"titleFa\":\"ت\"}]");

        var latest = await h.Verifier().GetLatestAsync(app.Id, default);

        latest!.Checks.Single().Status.Should().Be(AppCheckStatus.Skipped);
    }

    [Fact]
    public async Task One_bad_element_costs_that_element_and_not_the_report()
    {
        var h = new VerifierHarness();
        var app = h.GivenApp();
        await StoreRow(h, app,
            "[{\"key\":\"running\",\"status\":\"Passed\",\"title\":\"t\",\"titleFa\":\"ت\",\"unknownField\":1}," +
            "{\"key\":17},\"nonsense\",{\"key\":\"url\",\"status\":\"Failed\",\"title\":\"t\",\"titleFa\":\"ت\"}]");

        var latest = await h.Verifier().GetLatestAsync(app.Id, default);

        latest!.Checks.Select(c => c.Key).Should().Equal("running", "url");
        latest.HasFailures.Should().BeTrue();
    }

    // ---- the probe's sentences in Persian ----

    [Theory]
    [InlineData("could not translate host name")]
    [InlineData("password authentication failed")]
    [InlineData("Connection refused")]
    [InlineData("HTTP 401")]
    [InlineData("HTTP 000")]
    [InlineData("server closed the connection unexpectedly")]
    [InlineData(null)]
    public void Every_sentence_the_probe_can_write_has_its_persian(string? output)
    {
        var sentence = ConnectionProbe.Explain(ManagedServiceType.PostgreSql, output);

        PersianText.IsMatch(ConnectionProbe.InPersian(sentence)).Should().BeTrue($"\"{sentence}\" is shown on a Persian page");
    }

    [Theory]
    [InlineData(ManagedServiceType.MongoDb)]
    [InlineData(ManagedServiceType.RabbitMq)]
    public void Every_reason_a_probe_is_missing_has_its_persian(ManagedServiceType type)
    {
        var sentence = ConnectionProbe.WhyUnsupported(type)!;

        PersianText.IsMatch(ConnectionProbe.InPersian(sentence)).Should().BeTrue();
    }

    [Fact]
    public void A_clients_own_words_are_kept_rather_than_guessed_at()
    {
        var sentence = ConnectionProbe.Explain(ManagedServiceType.PostgreSql, "server closed the connection unexpectedly");

        ConnectionProbe.InPersian(sentence).Should().Contain("server closed the connection unexpectedly");
        ConnectionProbe.InPersian("Some sentence nobody wrote here.").Should().Be("Some sentence nobody wrote here.");
    }
}
