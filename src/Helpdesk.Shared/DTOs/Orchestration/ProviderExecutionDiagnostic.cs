namespace Helpdesk.Shared.DTOs.Orchestration;

public sealed record ProviderExecutionDiagnostic(string Provider, string Stage, string Code, string Message,
    string RetryDisposition, string CorrelationId, bool? ExecutionCreated, int? HttpStatus = null,
    string? ConnectionId = null, string? JobDefinitionId = null, string? ProviderRequestId = null,
    string? ProviderRunId = null, Guid? NodeId = null, int? StepOrdinal = null,
    string? RequestedExecutor = null, string? ResolvedEngine = null, string? RequiredShell = null,
    string? ParentResult = null, string? ProviderRequestUrl = null, string? ProviderRunUrl = null);

public sealed record AutomationExecutionReadinessDto(bool? Ready, string Code, string Message);
