namespace Helpdesk.Shared.DTOs.Orchestration;

public sealed class OrchestrationConnectivitySettingsDto
{
    public string ProviderKey { get; set; } = "Orchestrator";
    public bool Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public string? Audience { get; set; }
    public string? Scope { get; set; }
    public string? Authority { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? RemoteSystemName { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
    public DateTimeOffset? LastTestedAtUtc { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public int Revision { get; set; }
    public string Source { get; set; } = "database";
    public bool ManagedByDeployment { get; set; }
    public bool HasClientSecret { get; set; }
    public string HealthPath { get; set; } = "/internal/health";
    public string IngestPath { get; set; } = "/internal/ingest";
    public string CatalogPath { get; set; } = "/internal/catalog";
    public string? ClientId { get; set; }
}

public sealed class UpdateOrchestrationConnectivitySettingsDto
{
    public int? ExpectedRevision { get; set; }
    public bool Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public string? Audience { get; set; }
    public string? Authority { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? Scope { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public bool ClearClientSecret { get; set; }
    public string? RemoteSystemName { get; set; }
    public string? HealthPath { get; set; }
    public string? IngestPath { get; set; }
    public string? CatalogPath { get; set; }
}

public sealed class NetclawConnectivitySettingsDto
{
    public string ProviderKey { get; set; } = "Netclaw";
    public bool Enabled { get; set; }
    public bool RuntimeSupported { get; set; } = true;
    public string? RuntimeIssue { get; set; }
    public string Instance { get; set; } = "dev";
    public string? Endpoint { get; set; }
    public bool AllowPrivateHttp { get; set; }
    public int IdleMinutes { get; set; } = 15;
    public int ConnectionCapacity { get; set; } = 25;
    public TimeSpan TurnInactivityTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ActivityHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
    public DateTimeOffset? LastTestedAtUtc { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public int Revision { get; set; }
    public string Source { get; set; } = "database";
    public bool ManagedByDeployment { get; set; }
    public bool HasDeviceToken { get; set; }
}

public sealed class NetclawConnectivityTestResultDto
{
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? SessionProtocol { get; set; }
    public IReadOnlyList<NetclawConnectivityProbeDto> Probes { get; set; } = [];
}

public sealed class NetclawConnectivityProbeDto
{
    public string ProbeName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset CheckedAtUtc { get; set; }
}

public sealed class UpdateNetclawConnectivitySettingsDto
{
    public int? ExpectedRevision { get; set; }
    public bool Enabled { get; set; }
    public string? Instance { get; set; }
    public string? Endpoint { get; set; }
    public string? DeviceToken { get; set; }
    public bool ClearDeviceToken { get; set; }
    public bool AllowPrivateHttp { get; set; }
    public int IdleMinutes { get; set; } = 15;
    public int ConnectionCapacity { get; set; } = 25;
    public TimeSpan TurnInactivityTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ActivityHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed class OrchestrationConnectivityTestResultDto
{
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<OrchestrationConnectivityProbeResultDto> Probes { get; set; } = [];
}

public enum OrchestrationConnectivityTrafficLight
{
    Green,
    Amber,
    Red
}

public sealed class OrchestrationConnectivityProbeResultDto
{
    public string ProbeName { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public OrchestrationConnectivityTrafficLight Status { get; set; }
    public int? HttpStatus { get; set; }
    public long? LatencyMs { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? RemoteSystemName { get; set; }
    public DateTimeOffset CheckedAtUtc { get; set; }
}

public sealed class OrchestrationIngestRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string RequestTaskId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? AutomationBindingId { get; set; }
    public string? OrchestrationRequestDefinitionId { get; set; }
    public string? OrchestrationJobDefinitionId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string? CallbackUrl { get; set; }
    public int? ExpectedRuntimeSeconds { get; set; }
    public int? GraceSeconds { get; set; }
    public int? HardTimeoutSeconds { get; set; }
}

public sealed class OrchestrationIngestResult
{
    public string? RequestId { get; set; }
    public string? RunId { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    public string Status { get; set; } = "Submitted";
    public string? Message { get; set; }
}
