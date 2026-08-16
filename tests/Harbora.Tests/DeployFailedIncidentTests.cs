using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A failed deployment opens an <c>AlertIncident</c> — and, unlike a threshold or a crash, that
/// incident has no way to close itself: the next deployment succeeding is a different fact about a
/// different attempt (2026-08-16 monitoring-alerting spec §M4). It can only be closed by a person
/// acknowledging it or by the bounded auto-expiry backstop.
/// </summary>
public class DeployFailedIncidentTests
{
    [Fact]
    public async Task A_failed_deployment_opens_an_incident_that_is_not_closed_on_its_own()
    {
        using var h = new PipelineHarness().WithHealthPath();
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        var incident = h.Db.AlertIncidents.Should().ContainSingle().Subject;
        incident.Condition.Should().Be(AlertEvent.DeployFailed);
        incident.SubjectRef.Should().Be(deployment.Id.ToString());
        incident.WorkspaceId.Should().Be(h.Workspace.Id);
        incident.ClosedAt.Should().BeNull("nothing re-evaluates a finished deployment; it stays open until a person or the expiry backstop closes it");
    }

    [Fact]
    public async Task Two_separate_failed_deployments_of_the_same_app_open_two_independently_closeable_incidents()
    {
        using var h = new PipelineHarness().WithHealthPath();
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;

        var first = h.QueueDeployment(number: 1);
        await h.RunAsync(first);

        var second = h.QueueDeployment(number: 2);
        await h.RunAsync(second);

        h.Db.AlertIncidents.Count(i => i.Condition == AlertEvent.DeployFailed).Should().Be(2,
            "each failed deployment is its own attempt and its own incident, not a repeat of the last one's");
    }
}
