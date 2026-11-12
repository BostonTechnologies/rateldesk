namespace Helpdesk.Shared.DTOs.Orchestration;

public sealed class OrchestrationCallbackDto
{
    public string? CorrelationId { get; set; }
    public string? RequestTaskId { get; set; }
    public string? RequestId { get; set; }
    public string? ExecutionId { get; set; }
    public string? OrchestrationRequestId { get; set; }
    public string? OrchestrationRunId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorJson { get; set; }
    public string? WorklogSummary { get; set; }
    public bool? ExpectedRuntimeExceeded { get; set; }
    public int? ExpectedRuntimeSeconds { get; set; }
    public int? GraceSeconds { get; set; }
    public int? HardTimeoutSeconds { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
