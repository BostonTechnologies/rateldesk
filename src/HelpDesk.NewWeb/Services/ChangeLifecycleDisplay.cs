using Helpdesk.Shared.Models;
using MudBlazor;

namespace HelpDesk.NewWeb.Services;

internal static class ChangeLifecycleDisplay
{
    public static bool IsTerminal(ChangeLifecycleState state) => state is ChangeLifecycleState.Cancelled
        or ChangeLifecycleState.ImplementedSuccess or ChangeLifecycleState.ImplementedBackedOut or ChangeLifecycleState.ImplementedFailed;

    public static string Label(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.PendingApproval => "Pending Approval",
        ChangeLifecycleState.ApprovedForImplementation => "Approved For Implementation",
        ChangeLifecycleState.ImplementationInProgress => "Implementation In Progress",
        ChangeLifecycleState.ImplementedSuccess => "Implemented - Success",
        ChangeLifecycleState.ImplementedBackedOut => "Implemented - Backed Out",
        ChangeLifecycleState.ImplementedFailed => "Implemented - Failed",
        _ => state.ToString()
    };

    public static Color StatusColor(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.Submitted => Color.Info,
        ChangeLifecycleState.PendingApproval or ChangeLifecycleState.ImplementedBackedOut => Color.Warning,
        ChangeLifecycleState.ApprovedForImplementation => Color.Primary,
        ChangeLifecycleState.ImplementationInProgress => Color.Secondary,
        ChangeLifecycleState.ImplementedSuccess => Color.Success,
        ChangeLifecycleState.ImplementedFailed => Color.Error,
        _ => Color.Default
    };
}
