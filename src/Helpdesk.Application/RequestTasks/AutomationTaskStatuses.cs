namespace Helpdesk.Application.RequestTasks;

public static class AutomationTaskStatuses
{
    public const string SubmitFailedManualRetry = "SubmitFailedManualRetry";
    public const string SubmitRejectedManualRetry = "SubmitRejectedManualRetry";
    public const string SubmitUncertainManualReconcile = "SubmitUncertainManualReconcile";

    public static bool RequiresManualRetry(string? status)
        => string.Equals(status, SubmitFailedManualRetry, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SubmitRejectedManualRetry, StringComparison.OrdinalIgnoreCase);

    public static bool RequiresManualReconciliation(string? status)
        => string.Equals(status, SubmitUncertainManualReconcile, StringComparison.OrdinalIgnoreCase);

    public static bool RequiresOperatorAction(string? status)
        => RequiresManualRetry(status) || RequiresManualReconciliation(status);
}
