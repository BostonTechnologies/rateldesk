namespace Helpdesk.Application.RequestTasks;

public static class AutomationTaskStatuses
{
    public const string SubmitFailedManualRetry = "SubmitFailedManualRetry";
    public const string SubmitRejectedManualRetry = "SubmitRejectedManualRetry";
    public const string SubmitUncertainManualReconcile = "SubmitUncertainManualReconcile";
    public const string ExecutionUnknownManualReconcile = "ExecutionUnknownManualReconcile";
    public const string MissingBinding = "Automation task is missing an enabled External orchestration binding target.";
    public const string DisabledBinding = "The saved NetRatel automation binding is disabled.";
    public const string ReconciliationHoldReason = "Automation outcome is unknown. Reconcile the recorded attempt before continuing.";

    public static string? SafeSubmissionFailure(string? status, string? reason, bool administrator = false)
        => status switch
        {
            SubmitFailedManualRetry when reason == MissingBinding || reason == DisabledBinding => administrator
                ? $"The job did not start. {reason} Review this task’s automation binding in the request form editor."
                : "The job did not start. Ask support to review the request form’s automation configuration.",
            SubmitRejectedManualRetry => "NetRatel rejected submission. Ask support to check the recorded attempt before retrying.",
            SubmitUncertainManualReconcile or ExecutionUnknownManualReconcile => ReconciliationHoldReason,
            _ => null
        };

    public static bool RequiresManualRetry(string? status)
        => string.Equals(status, SubmitFailedManualRetry, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SubmitRejectedManualRetry, StringComparison.OrdinalIgnoreCase);

    public static bool RequiresManualReconciliation(string? status)
        => string.Equals(status, SubmitUncertainManualReconcile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, ExecutionUnknownManualReconcile, StringComparison.OrdinalIgnoreCase);

    public static bool RequiresOperatorAction(string? status)
        => RequiresManualRetry(status) || RequiresManualReconciliation(status);
}
