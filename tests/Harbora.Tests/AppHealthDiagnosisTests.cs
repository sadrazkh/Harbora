using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whether the status badge in the panel matches what the containers are doing.
///
/// It could be wrong in both directions. Crash detection only looked for "exited", but app containers
/// run under <c>unless-stopped</c>, so Docker revives a container that dies on startup and it reports
/// "restarting" — verified live: a crash-looping app kept its Running badge across several collector
/// passes and raised no alert. And nothing ever cleared Crashed, so an app that recovered stayed
/// marked as broken until the next deploy.
/// </summary>
public class AppHealthDiagnosisTests
{
    private static ContainerInfo Container(string state, string name = "harbora-shop-1") =>
        new("id-" + name, name, "img", state, state, new Dictionary<string, string> { ["harbora.app"] = "shop" });

    // ---- reading the containers ----

    [Fact]
    public void A_restarting_container_is_a_crash_loop()
        => AppHealthDiagnosis.Observe([Container("restarting")]).Should().Be(ObservedAppState.CrashLooping);

    [Fact]
    public void A_crash_loop_outranks_a_container_that_is_up()
    {
        // A restarting container flaps through "running", and during a cutover the old container is
        // healthy while the new one is already failing. Reporting "running" would hide both.
        var observed = AppHealthDiagnosis.Observe([Container("running", "shop-1"), Container("restarting", "shop-2")]);

        observed.Should().Be(ObservedAppState.CrashLooping);
    }

    [Fact]
    public void An_exited_container_alongside_a_running_one_is_not_a_crash()
    {
        // Normal mid-cutover shape: the previous container has stopped, the new one serves traffic.
        var observed = AppHealthDiagnosis.Observe([Container("exited", "shop-1"), Container("running", "shop-2")]);

        observed.Should().Be(ObservedAppState.Running);
    }

    [Fact]
    public void No_containers_at_all_is_not_a_verdict()
        => AppHealthDiagnosis.Observe([]).Should().Be(ObservedAppState.Missing);

    // ---- deciding what to do about it ----

    [Fact]
    public void A_running_app_that_starts_crash_looping_is_marked_crashed()
        => AppHealthDiagnosis.NextStatus(AppStatus.Running, ObservedAppState.CrashLooping)
            .Should().Be(AppStatus.Crashed);

    [Fact]
    public void A_crashed_app_whose_container_recovers_goes_back_to_running()
    {
        // Docker restarts things successfully, and operators fix them. Without this the badge stayed
        // red until the next deploy, which is its own kind of lie.
        AppHealthDiagnosis.NextStatus(AppStatus.Crashed, ObservedAppState.Running)
            .Should().Be(AppStatus.Running);
    }

    [Fact]
    public void An_app_the_user_stopped_is_left_alone()
    {
        // Its containers are exited because that was asked for. Calling that a crash would alert on
        // an intentional action and fight the user's own button.
        AppHealthDiagnosis.NextStatus(AppStatus.Stopped, ObservedAppState.Exited).Should().BeNull();
        AppHealthDiagnosis.NextStatus(AppStatus.Stopped, ObservedAppState.Running).Should().BeNull();
    }

    [Fact]
    public void An_app_mid_deploy_is_left_to_the_pipeline()
    {
        // Containers are legitimately half-up while a deploy runs; the monitor overruling that would
        // make the pipeline's own status meaningless.
        AppHealthDiagnosis.NextStatus(AppStatus.Deploying, ObservedAppState.Exited).Should().BeNull();
        AppHealthDiagnosis.NextStatus(AppStatus.Deploying, ObservedAppState.CrashLooping).Should().BeNull();
    }

    [Fact]
    public void An_already_crashed_app_is_not_re_reported_every_pass()
    {
        // The collector runs every 30 seconds; re-alerting each time would bury the first report.
        AppHealthDiagnosis.NextStatus(AppStatus.Crashed, ObservedAppState.CrashLooping).Should().BeNull();
    }

    [Fact]
    public void An_app_with_no_containers_is_not_declared_crashed()
    {
        // It may simply never have been deployed, or be between deployments.
        AppHealthDiagnosis.NextStatus(AppStatus.Running, ObservedAppState.Missing).Should().BeNull();
    }

    [Fact]
    public void A_healthy_app_that_stays_healthy_is_not_written_to()
        => AppHealthDiagnosis.NextStatus(AppStatus.Running, ObservedAppState.Running).Should().BeNull();
}
