namespace Helpdesk.Shared.DTOs.Orchestration;

public sealed record OrchestrationExecutionQuery(
    string RequestId,
    string RequestTaskId,
    string CorrelationId,
    string OrchestrationRequestId,
    string OrchestrationRunId);
