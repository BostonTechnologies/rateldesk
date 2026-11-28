using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using System.Text.Json;

namespace Helpdesk.Application.Orchestration;

public static class ProviderExecutionDiagnostics
{
    private const string ReviewDestination = "Open NetRatel → System connections → Paired Run as for this connection and job.";

    public static string? PairedRunAsReason(string? reason) => reason is "approval_required" or "approval_revoked"
        or "approval_scope_changed" or "connection_authority_unavailable" or "run_as_approval_denied" ? reason : null;

    public static string DescribePairedRunAs(string? reason) => reason switch
    {
        "approval_required" => $"The saved execution accounts need approval. {ReviewDestination}",
        "approval_revoked" => $"The saved execution-account approval was revoked. {ReviewDestination}",
        "approval_scope_changed" => $"The approved scope changed. Review the current targets and accounts. {ReviewDestination}",
        "connection_authority_unavailable" => "Review the saved connection, its retained tenant authority and its credentials in NetRatel System connections.",
        "run_as_approval_denied" => $"Ask an authorised NetRatel administrator to review and approve this scope. {ReviewDestination}",
        _ => $"Review the saved execution-account approval. {ReviewDestination}"
    };

    public static ProviderExecutionDiagnostic Rejection(int? httpStatus, string? code, OrchestrationIngestRequest request,
        string? mappingId, ProviderExecutionDiagnostic? provider = null)
    {
        var safeReason = provider is { Provider: "NetRatel", Stage: "admission", ExecutionCreated: false }
            && provider.CorrelationId == request.CorrelationId && (code ?? provider.Code) == "run_as_denied"
            ? PairedRunAsReason(provider.Reason) : null;
        var reason = (code ?? provider?.Code) switch
        {
            "run_as_denied" => ("run_as_denied", DescribePairedRunAs(safeReason)),
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
        return new ProviderExecutionDiagnostic("NetRatel", "admission", reason.Item1, reason.Item2, "configuration_required", request.CorrelationId,
            false, httpStatus, SafeReference(mappingId), SafeReference(request.OrchestrationJobDefinitionId),
            NodeId: provider?.NodeId, StepOrdinal: SafeOrdinal(provider?.StepOrdinal),
            RequestedExecutor: Engine(provider?.RequestedExecutor), RequiredShell: Engine(provider?.RequiredShell))
        {
            Reason = safeReason
        };
    }

    public static IReadOnlyList<ProviderExecutionDiagnostic> RecentRejections(RequestTask task)
    {
        if (task.AutomationAttemptHistoryJson is null) return [];
        try
        {
            using var history = JsonDocument.Parse(task.AutomationAttemptHistoryJson);
            if (history.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new List<ProviderExecutionDiagnostic>();
            foreach (var attempt in history.RootElement.EnumerateArray().Reverse().Take(5))
            {
                if (attempt.ValueKind != JsonValueKind.Object || !attempt.TryGetProperty("ProviderDiagnostic", out var stored)) continue;
                var diagnostic = stored.Deserialize<ProviderExecutionDiagnostic>(JsonSerializerOptions.Web);
                if (diagnostic is not { Provider: "NetRatel", Stage: "admission", ExecutionCreated: false }
                    || SafeReference(diagnostic.CorrelationId) is not { } correlation) continue;
                result.Add(Rejection(diagnostic.HttpStatus is >= 400 and <= 599 ? diagnostic.HttpStatus : null,
                    diagnostic.Code, new() { CorrelationId = correlation, OrchestrationJobDefinitionId = diagnostic.JobDefinitionId },
                    diagnostic.ConnectionId, diagnostic));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
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
