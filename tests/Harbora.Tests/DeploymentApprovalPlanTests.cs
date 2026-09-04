using FluentAssertions;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rules of one approval decision (5.2, 2026-09 market-gaps round two, "approval gate on
/// deploying to a protected environment"), isolated from the database — see
/// <see cref="DeploymentApprovalPlan"/>'s own doc.
/// </summary>
public class DeploymentApprovalPlanTests
{
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    [Fact]
    public void A_second_person_may_decide_a_pending_request()
    {
        DeploymentApprovalPlan.RefuseDecision(Requester, Approver, DeploymentApprovalDecision.Pending)
            .Should().BeNull();
    }

    [Fact]
    public void The_requester_cannot_approve_or_reject_their_own_deployment()
    {
        var refusal = DeploymentApprovalPlan.RefuseDecision(
            Requester, Requester, DeploymentApprovalDecision.Pending);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("cannot also approve or reject it");
    }

    [Theory]
    [InlineData(DeploymentApprovalDecision.Approved)]
    [InlineData(DeploymentApprovalDecision.Rejected)]
    [InlineData(DeploymentApprovalDecision.Expired)]
    public void An_already_decided_approval_cannot_be_decided_again(DeploymentApprovalDecision decided)
    {
        // Even by a legitimate second person — the row is settled, not merely disputed.
        var refusal = DeploymentApprovalPlan.RefuseDecision(Requester, Approver, decided);

        refusal.Should().NotBeNull();
        refusal.Should().Contain(decided.ToString());
    }

    [Fact]
    public void An_already_decided_approval_refuses_even_the_requester_with_the_settled_reason_first()
    {
        // Both refusals apply here (already decided, AND it is the requester) — the settled-state
        // message is the one that fires, because "this is over" is true regardless of who is asking.
        var refusal = DeploymentApprovalPlan.RefuseDecision(
            Requester, Requester, DeploymentApprovalDecision.Approved);

        refusal.Should().Contain("Approved");
    }

    [Fact]
    public void Nobody_else_eligible_means_the_gate_approves_itself()
    {
        DeploymentApprovalPlan.AutoApproveForLackOfSecondApprover(0).Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public void Anybody_else_eligible_means_the_gate_waits(int eligibleApprovers)
    {
        DeploymentApprovalPlan.AutoApproveForLackOfSecondApprover(eligibleApprovers).Should().BeFalse();
    }
}
