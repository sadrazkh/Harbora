using FluentAssertions;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 10: the span a self-serve export's artifact is kept for, as distinct from a single
/// minted download link's own lifetime.
/// </summary>
public sealed class DatabaseExportPlanTests
{
    [Fact]
    public void The_artifacts_lifetime_outlives_a_single_download_link()
    {
        // The whole reason the two spans are separate values: a customer whose first link lapses can
        // mint a second one against the same artifact without re-running the export, right up until
        // the artifact itself expires.
        DatabaseExportPlan.ArtifactLifetime.Should().BeGreaterThan(AdminerSession.Lifetime);
    }

    [Fact]
    public void The_artifacts_lifetime_is_positive()
    {
        DatabaseExportPlan.ArtifactLifetime.Should().BePositive();
    }
}
