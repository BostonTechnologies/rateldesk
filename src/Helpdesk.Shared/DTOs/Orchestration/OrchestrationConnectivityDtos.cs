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
    public bool SecretUnavailable { get; set; }
    public string SecretState { get; set; } = "not-configured";
    public string HealthPath { get; set; } = "/internal/health";
    public string IngestPath { get; set; } = "/internal/ingest";
    public string CatalogPath { get; set; } = "/internal/catalog";
    public string? ClientId { get; set; }
    public bool AllowPrivateHttp { get; set; }
    public string? SourceKey { get; set; }
    public string ProfileFingerprint { get; set; } = string.Empty;
    public string? PeerInstanceId { get; set; }
    public string? PeerTenantId { get; set; }
    public string? LocalTenantId { get; set; }
    public string? LinkId { get; set; }
    public long LinkRevision { get; set; }
    public long CredentialRevision { get; set; }
    public bool ManagedSenderEnabled { get; set; } = true;
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
    public bool AllowPrivateHttp { get; set; }
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
    public string? DaemonAddress { get; set; }
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
    public bool SecretUnavailable { get; set; }
    public string SecretState { get; set; } = "not-configured";
    public string? SourceKey { get; set; }
    public string ProfileFingerprint { get; set; } = string.Empty;
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

public sealed class PairNetclawDeviceDto
{
    public string? PairingCode { get; set; }
    public int? ExpectedRevision { get; set; }
    public bool Enabled { get; set; }
    public string? Instance { get; set; }
    public string? Endpoint { get; set; }
    public bool AllowPrivateHttp { get; set; }
    public string? LegacyOwnershipReviewToken { get; set; }
    public int IdleMinutes { get; set; } = 15;
    public int ConnectionCapacity { get; set; } = 25;
    public TimeSpan TurnInactivityTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ActivityHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed record NetclawLegacySessionReviewDto(
    int ConversationCount,
    string CanonicalEndpoint,
    string ReviewToken);

public sealed record NetclawLegacySessionReviewConflictDto(
    string Code,
    string Message,
    string CanonicalEndpoint,
    string DaemonAddress,
    int LegacyConversationCount,
    string LegacyOwnershipReviewToken);

public sealed class ConfirmNetclawLegacySessionsDto
{
    public string? HistoricalInstance { get; set; }
    public string? HistoricalEndpoint { get; set; }
    public bool AllowPrivateHttp { get; set; }
    public int ExpectedEligibleConversations { get; set; }
    public IReadOnlyList<Guid> ConversationIds { get; set; } = [];
}

public sealed record NetclawLegacySessionConfirmationDto(int BoundConversations, string ProviderProfileFingerprint);
public sealed record NetclawUnboundLegacySessionDto(
    Guid ConversationId,
    string OrganizationId,
    string TicketId,
    string TicketType,
    DateTimeOffset LastActivityUtc);

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
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public OrchestrationSubmissionDisposition Disposition { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public OrchestrationExecutionOutcome Outcome { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRemoteIdentity =>
        !string.IsNullOrWhiteSpace(ExecutionId) ||
        !string.IsNullOrWhiteSpace(RequestId) ||
        !string.IsNullOrWhiteSpace(RunId);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasExecutionIdentity => !string.IsNullOrWhiteSpace(ExecutionId);
}

public static class OrchestrationIngestClassifier
{
    public static void Apply(OrchestrationIngestResult result, bool hasStatus)
    {
        var status = result.Status?.Trim().ToLowerInvariant() ?? string.Empty;
        var outcome = status switch
        {
            "completed" or "complete" or "done" or "success" or "succeeded" => OrchestrationExecutionOutcome.Completed,
            "failed" or "failure" or "error" or "rejected" or "denied" or "invalid" => OrchestrationExecutionOutcome.Failed,
            "cancelled" or "canceled" => OrchestrationExecutionOutcome.Cancelled,
            "accepted" or "queued" or "submitted" or "started" or "running" or
            "in_progress" or "in-progress" or "processing" or "pending" or
            "already_exists" or "already-exists" or "duplicate" or "existing" => OrchestrationExecutionOutcome.Processing,
            _ => OrchestrationExecutionOutcome.Unknown
        };

        result.Outcome = outcome;
        if (!result.HasRemoteIdentity)
        {
            result.Disposition = outcome is OrchestrationExecutionOutcome.Failed
                ? OrchestrationSubmissionDisposition.Rejected
                : OrchestrationSubmissionDisposition.Unknown;
            return;
        }

        // NetRatel's successful ingest contract supplies ExecutionId explicitly.
        // RequestId and RunId are useful for reconciliation, but neither proves
        // that a remote execution was acknowledged.
        if (!hasStatus || outcome is OrchestrationExecutionOutcome.Unknown || !result.HasExecutionIdentity)
        {
            result.Disposition = OrchestrationSubmissionDisposition.Unknown;
            return;
        }

        result.Disposition = status is "already_exists" or "already-exists" or "duplicate" or "existing"
            || outcome is OrchestrationExecutionOutcome.Completed or OrchestrationExecutionOutcome.Failed or OrchestrationExecutionOutcome.Cancelled
            ? OrchestrationSubmissionDisposition.Existing
            : OrchestrationSubmissionDisposition.Admitted;
    }
}

public enum OrchestrationSubmissionDisposition
{
    Unknown,
    Rejected,
    Admitted,
    Existing
}

public enum OrchestrationExecutionOutcome
{
    Unknown,
    Processing,
    Completed,
    Failed,
    Cancelled
}
