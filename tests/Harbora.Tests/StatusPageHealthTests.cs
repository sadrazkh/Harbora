using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Infrastructure.Status;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 7 (2026-08-20 platform-options plan): the public status page's four-word vocabulary
/// (operational / degraded / maintenance / unknown), derived purely from <see cref="AppStatus"/> — the
/// exact column <c>AppsController.Index</c> reads for the panel's own Apps list — plus whether a
/// deployment has ever gone live and <c>App.MaintenanceMode</c> (P5, landed after this class was first
/// written — see <see cref="StatusPageHealth"/>'s own doc for why wiring it in changed nothing else).
/// No Docker call, no node inspection: a second, live derivation is exactly the drift this sub-project
/// was told to avoid.
/// </summary>
public class StatusPageHealthTests
{
    [Fact]
    public void An_app_that_has_never_deployed_is_unknown_not_a_comfortable_dot()
    {
        StatusPageHealth.Resolve(AppStatus.Created, hasEverServed: false, maintenanceMode: false)
            .Should().Be(PublicAppState.Unknown);
    }

    [Fact]
    public void A_first_deploy_still_in_flight_with_nothing_ever_served_is_unknown()
    {
        StatusPageHealth.Resolve(AppStatus.Deploying, hasEverServed: false, maintenanceMode: false)
            .Should().Be(PublicAppState.Unknown);
    }

    [Fact]
    public void A_redeploy_over_a_working_release_stays_operational()
    {
        // Zero-downtime cutover: the previous release keeps serving until the new one is wired in, so
        // "Deploying" over a release that has shipped before is not down.
        StatusPageHealth.Resolve(AppStatus.Deploying, hasEverServed: true, maintenanceMode: false)
            .Should().Be(PublicAppState.Operational);
    }

    [Fact]
    public void A_running_app_is_operational()
    {
        StatusPageHealth.Resolve(AppStatus.Running, hasEverServed: true, maintenanceMode: false)
            .Should().Be(PublicAppState.Operational);
    }

    [Theory]
    [InlineData(AppStatus.Stopped)]
    [InlineData(AppStatus.Failed)]
    [InlineData(AppStatus.Crashed)]
    public void Stopped_failed_and_crashed_all_read_as_degraded(AppStatus status)
    {
        // The public vocabulary has no fifth word for "down" — a deliberately stopped app is not
        // "unknown" (its state is fully known) and is not "operational" (it is not serving), so it
        // reads as degraded, the same honest-but-imprecise bucket the plan's four states allow for.
        StatusPageHealth.Resolve(status, hasEverServed: true, maintenanceMode: false)
            .Should().Be(PublicAppState.Degraded);
    }

    [Theory]
    [InlineData(AppStatus.Running)]
    [InlineData(AppStatus.Crashed)]
    [InlineData(AppStatus.Created)]
    [InlineData(AppStatus.Deploying)]
    public void Maintenance_mode_outranks_every_other_signal(AppStatus status)
    {
        // A deliberate operator action (App.MaintenanceMode, written only after the proxy apply
        // actually succeeded — App.cs's own doc) says more about what a visitor will see right now
        // than any AppStatus/deployment-history combination could — it wins outright.
        StatusPageHealth.Resolve(status, hasEverServed: true, maintenanceMode: true)
            .Should().Be(PublicAppState.Maintenance);
        StatusPageHealth.Resolve(status, hasEverServed: false, maintenanceMode: true)
            .Should().Be(PublicAppState.Maintenance);
    }

    // ---- 2.1 (2026-09 market-gaps round two): a real outside-in probe overrides App.Status --------
    //
    // This is the fix 2.1 exists for: before latestProbeOutcome existed, every state above came from
    // what Harbora believes it started, never from anything that actually answered a request.

    [Fact]
    public void A_passing_probe_reads_operational_even_though_App_Status_alone_would_not_say_so()
    {
        // AppStatus.Deploying + hasEverServed:false alone reads Unknown (see the test above it) — a
        // real passing probe is stronger evidence than that combination and must win.
        StatusPageHealth.Resolve(AppStatus.Deploying, hasEverServed: false, maintenanceMode: false,
                latestProbeOutcome: UptimeCheckOutcome.Up)
            .Should().Be(PublicAppState.Operational);
    }

    [Fact]
    public void A_failing_probe_reads_degraded_even_though_App_Status_says_Running()
    {
        // The exact scenario 2.1's brief names: a container running happily while its app answers
        // nothing (or the wrong thing) to every request must not look healthy.
        StatusPageHealth.Resolve(AppStatus.Running, hasEverServed: true, maintenanceMode: false,
                latestProbeOutcome: UptimeCheckOutcome.Down)
            .Should().Be(PublicAppState.Degraded);
    }

    [Fact]
    public void A_probe_that_could_not_run_reads_unknown_never_a_green_dot_and_never_a_failure()
    {
        StatusPageHealth.Resolve(AppStatus.Running, hasEverServed: true, maintenanceMode: false,
                latestProbeOutcome: UptimeCheckOutcome.CouldNotRun)
            .Should().Be(PublicAppState.Unknown);
    }

    [Fact]
    public void Maintenance_mode_still_outranks_a_probe_result()
    {
        StatusPageHealth.Resolve(AppStatus.Running, hasEverServed: true, maintenanceMode: true,
                latestProbeOutcome: UptimeCheckOutcome.Down)
            .Should().Be(PublicAppState.Maintenance);
    }

    [Fact]
    public void No_probe_configured_falls_back_to_the_AppStatus_derivation_unchanged()
    {
        // The default parameter (no probe result at all) must reproduce every pre-2.1 test above —
        // apps with no UptimeCheck configured are not worse off than before this sub-project shipped.
        StatusPageHealth.Resolve(AppStatus.Running, hasEverServed: true, maintenanceMode: false)
            .Should().Be(PublicAppState.Operational);
    }
}
