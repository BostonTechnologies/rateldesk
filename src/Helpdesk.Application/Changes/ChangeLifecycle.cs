using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Changes;

public static class ChangeLifecycle
{
    public static IReadOnlyList<ChangeLifecycleState> TerminalStates { get; } = Array.AsReadOnly(new[]
    {
        ChangeLifecycleState.ImplementedSuccess,
        ChangeLifecycleState.ImplementedBackedOut,
        ChangeLifecycleState.Cancelled,
        ChangeLifecycleState.ImplementedFailed
    });

    public static bool IsTerminal(ChangeLifecycleState state) => TerminalStates.Contains(state);

    public static bool IsImplemented(ChangeLifecycleState state) => state is ChangeLifecycleState.ImplementedSuccess
        or ChangeLifecycleState.ImplementedBackedOut or ChangeLifecycleState.ImplementedFailed;

    public static bool CanCancel(ChangeLifecycleState state) => state is ChangeLifecycleState.Draft
        or ChangeLifecycleState.Submitted or ChangeLifecycleState.PendingApproval
        or ChangeLifecycleState.ApprovedForImplementation or ChangeLifecycleState.ImplementationInProgress;

    public static bool RequiresComment(ChangeLifecycleState current, ChangeLifecycleState destination) =>
        destination == ChangeLifecycleState.ImplementedFailed ||
        destination == ChangeLifecycleState.Cancelled && current != ChangeLifecycleState.Draft;

    public static string? ValidateTransition(ChangeLifecycleState current, ChangeLifecycleState destination, bool detail = false)
    {
        if (current == destination) return null;
        if (IsTerminal(current)) return "This change has reached a terminal outcome.";
        if (destination == ChangeLifecycleState.Cancelled && CanCancel(current)) return null;
        if (destination == ChangeLifecycleState.Draft && (current == ChangeLifecycleState.PendingApproval || detail && CanCancel(current))) return null;
        if (IsImplemented(destination) || destination == ChangeLifecycleState.ImplementationInProgress)
            return current is ChangeLifecycleState.ApprovedForImplementation or ChangeLifecycleState.ImplementationInProgress
                ? null : "Change must be approved before implementation.";

        return (current, destination) switch
        {
            (ChangeLifecycleState.Draft, ChangeLifecycleState.Submitted) => null,
            (ChangeLifecycleState.Submitted, ChangeLifecycleState.PendingApproval or ChangeLifecycleState.ApprovedForImplementation) when detail => null,
            (ChangeLifecycleState.PendingApproval, ChangeLifecycleState.ApprovedForImplementation) => null,
            _ => "That change lifecycle transition is not allowed."
        };
    }

    public static string Label(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.PendingApproval => "Pending Approval",
        ChangeLifecycleState.ApprovedForImplementation => "Approved for Implementation",
        ChangeLifecycleState.ImplementationInProgress => "Implementation In Progress",
        ChangeLifecycleState.ImplementedSuccess => "Implemented - Success",
        ChangeLifecycleState.ImplementedBackedOut => "Implemented - Backed Out",
        ChangeLifecycleState.ImplementedFailed => "Implemented - Failed",
        _ => state.ToString()
    };

    public static string CompletionLabel(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.ImplementedSuccess => "Success",
        ChangeLifecycleState.ImplementedBackedOut => "Backed Out",
        ChangeLifecycleState.ImplementedFailed => "Failed",
        _ => string.Empty
    };
}
