using Helpdesk.Application.RequestTasks;
using Helpdesk.Shared.Models;

namespace Helpdesk.Tests.Application.RequestTasks;

public sealed class RequestHoldPolicyTests
{
    [Theory][InlineData("blocked")][InlineData("FAILED")][InlineData("Cancelled")][InlineData("needSReconciliation")][InlineData("onhold")]
    public void Existing_status_casing_cannot_bypass_the_start_fence(string status)
    {
        Assert.True(RequestHoldPolicy.BlocksStart(new Request { WorkflowStatus = status }));
    }

    [Fact]
    public void An_operator_hold_survives_owned_reconciliation_metadata()
    {
        var request = new Request { OperatorHold = true, State = TicketState.OnHold,
            WorkflowStatus = "NeedsReconciliation", WorkflowBlockReason = AutomationTaskStatuses.ReconciliationHoldReason };
        Assert.True(RequestHoldPolicy.IsOperatorHeld(request));
        Assert.True(RequestHoldPolicy.BlocksStart(request));
        request.OperatorHold = false;
        Assert.False(RequestHoldPolicy.IsOperatorHeld(request));
        Assert.True(RequestHoldPolicy.BlocksStart(request));
    }

    [Fact]
    public void Ambiguous_legacy_holds_are_conservative_but_the_owned_timeout_hold_is_separate()
    {
        var request = new Request { OperatorHold = null, State = TicketState.OnHold };
        Assert.True(RequestHoldPolicy.IsOperatorHeld(request));
        request.WorkflowStatus = "NeedsReconciliation";
        request.WorkflowBlockReason = AutomationTaskStatuses.ReconciliationHoldReason;
        Assert.False(RequestHoldPolicy.IsOperatorHeld(request));
        Assert.True(RequestHoldPolicy.BlocksStart(request));
        request.WorkflowStatus = "Blocked"; request.WorkflowBlockReason = "Independent hold";
        Assert.True(RequestHoldPolicy.HasUnrelatedWorkflowHold(request));
        Assert.True(RequestHoldPolicy.BlocksStart(request));
    }
}
