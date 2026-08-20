using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Status;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 7 (2026-08-20 platform-options plan): the public status page's four-word vocabulary
/// (operational / degraded / maintenance / unknown), derived purely from <see cref="AppStatus"/> — the
/// exact column <c>AppsController.Index</c> reads for the panel's own Apps list — plus whether a
/// deployment has ever gone live. No Docker call, no node inspection: see <see cref="StatusPageHealth"/>'s
/// own doc for why a second, live derivation is exactly the drift this sub-project was told to avoid.
/// </summary>
public class StatusPageHealthTests
{
    [Fact]
    public void An_app_that_has_never_deployed_is_unknown_not_a_comfortable_dot()
    {
        StatusPageHealth.Resolve(AppStatus.Created, hasEverServed: false).Should().Be(PublicAppState.Unknown);
    }

    [Fact]
    public void A_first_deploy_still_in_flight_with_nothing_ever_served_is_unknown()
    {
        StatusPageHealth.Resolve(AppStatus.Deploying, hasEverServed: false).Should().Be(PublicAppState.Unknown);
    }

    [Fact]
    public void A_redeploy_over_a_working_release_stays_operational()
    {
        // Zero-downtime cutover: the previous release keeps serving until the new one is wired in, so
        // "Deploying" over a release that has shipped before is not down.
        StatusPageHealth.Resolve(AppStatus.Deploying, hasEverServed: true).Should().Be(PublicAppState.Operational);
    }

    [Fact]
    public void A_running_app_is_operational()
    {
        StatusPageHealth.Resolve(AppStatus.Running, hasEverServed: true).Should().Be(PublicAppState.Operational);
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
        StatusPageHealth.Resolve(status, hasEverServed: true).Should().Be(PublicAppState.Degraded);
    }
}
