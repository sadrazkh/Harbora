using FluentAssertions;
using Harbora.Domain.Common;
using Xunit;
using static Harbora.Postgres.Tests.UpgradeFromPreviousRelease;

namespace Harbora.Postgres.Tests;

/// <summary>
/// <c>20260911005952_DeploymentActiveIndex</c> across a real upgrade from an install holding two
/// in-flight deployments of one app.
///
/// <para>
/// The code before that migration allowed the state on purpose — a second deploy of an app queued
/// behind the first rather than racing it — and the migration's UNIQUE partial index cannot be built
/// over it. It first shipped with no settling step, so such an install could not upgrade at all: the
/// migration threw and the panel would not boot. The shared upgrade seed already holds exactly this
/// pair (app one, a queued #1 behind a building #2), and every upgrade test failed on
/// "could not create unique index IX_Deployments_ActiveDeployment" the first time CI ran the Postgres
/// lane after seven weeks off. These pin what the settling step must leave behind.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class ActiveDeploymentSettlingTests(PostgresLane lane)
{
    [PostgresFact]
    public async Task The_newest_in_flight_deployment_of_an_app_survives_the_upgrade_untouched()
    {
        var upgraded = await lane.UpgradedAsync();

        var newest = await UpgradedReads.DeploymentAsync(upgraded.ConnectionString, Seeded.SecondDeploymentOfAppOne);

        newest.Status.Should().Be(DeploymentStatus.Building);
        newest.ErrorMessage.Should().BeNull();
    }

    [PostgresFact]
    public async Task The_older_one_is_settled_failed_with_a_reason_a_person_can_read()
    {
        var upgraded = await lane.UpgradedAsync();

        var older = await UpgradedReads.DeploymentAsync(upgraded.ConnectionString, Seeded.DeploymentOfAppOne);

        older.Status.Should().Be(DeploymentStatus.Failed, "it can never run now: the index refuses a second in-flight row");
        older.ErrorMessage.Should().StartWith("Settled during an upgrade");
        older.FinishedAt.Should().NotBeNull();
    }

    [PostgresFact]
    public async Task An_app_with_a_single_in_flight_deployment_is_not_touched()
    {
        var upgraded = await lane.UpgradedAsync();

        var lone = await UpgradedReads.DeploymentAsync(upgraded.ConnectionString, Seeded.DeploymentOfAppTwo);

        lone.Status.Should().Be(DeploymentStatus.Queued);
        lone.ErrorMessage.Should().BeNull();
    }
}
