using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Orchestration;

public static class ProviderExecutionDiagnostics
{
    public static ProviderExecutionDiagnostic Rejection(int? httpStatus, string? code, OrchestrationIngestRequest request,
        string? mappingId, ProviderExecutionDiagnostic? provider = null)
    {
        var reason = (code ?? provider?.Code) switch
        {
            "run_as_denied" => ("run_as_denied", "Review the paired job's saved execution-account approval in NetRatel."),
            "required_shell_unavailable" => ("required_shell_unavailable", "Install the required shell or select a compatible execution engine."),
            "shell_executor_conflict" => ("shell_executor_conflict", "Review the step's runner and library payload executor in NetRatel."),
            "run_as_unsupported" => ("run_as_unsupported", "Install an agent version that supports the selected execution account."),
            "run_as_profile_unavailable" or "run_as_profile_ineligible" or "run_as_plan_changed" =>
                ("run_as_unavailable", "Review the current execution account and credential profile in NetRatel."),
            "target_unavailable" => ("target_unavailable", "Review the paired job's targets in NetRatel."),
            "access_denied" or "authentication_rejected" =>
                ("access_denied", "Review the tenant mapping and current paired credential in NetRatel."),
            _ => ("provider_rejected", "Review the paired job and connection in NetRatel.")
        };
        return new("NetRatel", "admission", reason.Item1, reason.Item2, "configuration_required", request.CorrelationId,
            false, httpStatus, SafeReference(mappingId), SafeReference(request.OrchestrationJobDefinitionId),
            NodeId: provider?.NodeId, StepOrdinal: SafeOrdinal(provider?.StepOrdinal),
            RequestedExecutor: Engine(provider?.RequestedExecutor), RequiredShell: Engine(provider?.RequiredShell));
    }

    public static ProviderExecutionDiagnostic? Outcome(RequestTask task, OrchestrationCallbackDto callback, string status,
        string correlationId, bool deadlineExpired = false)
    {
        if (status is not ("failed" or "cancelled" or "unknown")) return null;
        var provider = callback.Diagnostic is { Provider: "NetRatel" } incoming && incoming.CorrelationId == correlationId
            ? incoming : null;
        var details = (status, provider?.Code) switch
        {
            ("unknown", _) => ("delivery", "delivery_uncertain", deadlineExpired
                ? "The local deadline expired with the provider outcome still unknown. Reconcile the recorded attempt."
                : "Reconcile this attempt in NetRatel before retrying.", "reconciliation_required"),
            ("cancelled", _) or (_, "cancelled") => ("execution", "cancelled", "NetRatel confirmed cancellation.", "operator_review"),
            (_, "timed_out") => ("execution", "timed_out", "NetRatel confirmed the execution timed out.", "operator_review"),
            (_, "required_shell_unavailable") => ("native-shell-resolution", "required_shell_unavailable",
                "Install the required shell or select a compatible execution engine.", "configuration_required"),
            (_, "run_as_logon_failed") => ("native-account-logon", "run_as_logon_failed",
                "Check the saved account credentials and native logon rights on the execution node.", "configuration_required"),
            (_, "run_as_unsupported") => ("dispatch", "run_as_unsupported",
                "Install an agent version that supports the selected execution account.", "configuration_required"),
            (_, "run_as_denied" or "run_as_unavailable") => ("dispatch", "run_as_unavailable",
                "Review the paired job's saved execution-account approval and profile in NetRatel.", "configuration_required"),
            _ => ("execution", "execution_failed", "Review the linked execution in NetRatel.", "operator_review")
        };
        return new("NetRatel", details.Item1, details.Item2, details.Item3, details.Item4, correlationId,
            status == "unknown" && provider?.ExecutionCreated != true ? null : true,
            ConnectionId: SafeReference(task.OrchestrationLinkId), JobDefinitionId: SafeReference(task.OrchestrationJobDefinitionId),
            ProviderRequestId: SafeReference(task.OrchestrationExternalRequestId), ProviderRunId: SafeReference(task.OrchestrationExternalRunId),
            NodeId: provider?.NodeId, StepOrdinal: SafeOrdinal(provider?.StepOrdinal), RequestedExecutor: Engine(provider?.RequestedExecutor),
            ResolvedEngine: Engine(provider?.ResolvedEngine), RequiredShell: Engine(provider?.RequiredShell),
            ParentResult: provider?.ParentResult is "StoppedAfterNodeFailure" or "StoppedAfterUnknownExecution" ? provider.ParentResult : null);
    }

    private static string? SafeReference(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ? value : null;

    private static int? SafeOrdinal(int? value) => value is > 0 and <= 10_000 ? value : null;

    private static string? Engine(string? value) => value is "auto" or "pwsh" or "powershell" or "windows-powershell" or
        "bash" or "cmd" or "sh" or "bash/sh" or "powershell-compatible" ? value : null;
}
