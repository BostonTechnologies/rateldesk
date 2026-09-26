using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Orchestration;

public sealed class OrchestrationResolvedSettings
{
    public string ProviderKey { get; init; } = "Orchestrator";
    public bool Enabled { get; init; }
    public string? BaseUrl { get; init; }
    public string? Audience { get; init; }
    public string? Authority { get; init; }
    public string? TokenEndpoint { get; init; }
    public string? Scope { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public bool AllowPrivateHttp { get; init; }
    public string? RemoteSystemName { get; init; }
    public string HealthPath { get; init; } = "/api/v1/health";
    public string IngestPath { get; init; } = "/api/v1/orchestration/ingest";
    public string CatalogPath { get; init; } = "/api/v1/orchestration/catalog";
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public DateTimeOffset? LastAppliedAtUtc { get; init; }
    public DateTimeOffset? LastTestedAtUtc { get; init; }
    public bool? LastTestSucceeded { get; init; }
    public int Revision { get; init; }
    public string Source { get; init; } = "database";
    public bool ManagedByDeployment { get; init; }
    public bool HasClientSecret { get; init; }
    public bool SecretUnavailable { get; init; }
    public string SecretState { get; init; } = "not-configured";
    public string? SourceKey { get; init; }
    public string ProfileFingerprint { get; init; } = string.Empty;
}

public sealed class NetclawResolvedSettings
{
    public string ProviderKey { get; init; } = "Netclaw";
    public bool Enabled { get; init; }
    public bool ConfiguredEnabled { get; init; }
    public bool RuntimeSupported { get; init; } = true;
    public string? RuntimeIssue { get; init; }
    public string Instance { get; init; } = "dev";
    public string? Endpoint { get; init; }
    public string? DeviceToken { get; init; }
    public bool AllowPrivateHttp { get; init; }
    public int IdleMinutes { get; init; } = 15;
    public int ConnectionCapacity { get; init; } = 25;
    public TimeSpan TurnInactivityTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ActivityHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public DateTimeOffset? LastAppliedAtUtc { get; init; }
    public DateTimeOffset? LastTestedAtUtc { get; init; }
    public bool? LastTestSucceeded { get; init; }
    public int Revision { get; init; }
    public string Source { get; init; } = "database";
    public bool ManagedByDeployment { get; init; }
    public bool HasDeviceToken { get; init; }
    public bool SecretUnavailable { get; init; }
    public string SecretState { get; init; } = "not-configured";
    public string? SourceKey { get; init; }
    public string ProfileFingerprint { get; init; } = string.Empty;
    public bool CanAdoptLegacySessions { get; init; }
}

public sealed class OrchestrationHealthResult
{
    public bool Success { get; init; }
    public int? StatusCode { get; init; }
    public string Message { get; init; } = string.Empty;
}

public class OrchestrationSubmissionUncertainException(
    string message,
    OrchestrationIngestResult? acknowledgement = null)
    : InvalidOperationException(message)
{
    public OrchestrationIngestResult? Acknowledgement { get; } = acknowledgement;
}

public sealed class OrchestrationAcknowledgementException(
    string message,
    OrchestrationIngestResult? acknowledgement = null)
    : OrchestrationSubmissionUncertainException(message, acknowledgement);

public sealed class OrchestrationSubmissionRejectedException(string message)
    : InvalidOperationException(message);

public sealed class RequestTaskPayloadBuildResult
{
    public bool Success { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public string JobName { get; init; } = string.Empty;
    public string? AutomationBindingId { get; init; }
    public string? OrchestrationRequestDefinitionId { get; init; }
    public string? OrchestrationJobDefinitionId { get; init; }
    public string? Error { get; init; }

    public static RequestTaskPayloadBuildResult Failed(string error) => new()
    {
        Success = false,
        Error = error
    };

    public static RequestTaskPayloadBuildResult Succeeded(
        string jobName,
        string payloadJson,
        string? automationBindingId = null,
        string? orchestrationRequestDefinitionId = null,
        string? orchestrationJobDefinitionId = null) => new()
        {
            Success = true,
            JobName = jobName,
            PayloadJson = payloadJson,
            AutomationBindingId = automationBindingId,
            OrchestrationRequestDefinitionId = orchestrationRequestDefinitionId,
            OrchestrationJobDefinitionId = orchestrationJobDefinitionId
        };
}

public interface IOrchestrationConnectivityService
{
    Task<OrchestrationConnectivitySettingsDto> GetOrchestrationSettingsAsync(CancellationToken cancellationToken = default);
    Task<OrchestrationConnectivityTestResultDto> TestOrchestrationConnectivityAsync(CancellationToken cancellationToken = default);
    Task<OrchestrationConnectivityTestResultDto> TestOrchestrationConnectivityAsync(
        OrchestrationResolvedSettings settings,
        CancellationToken cancellationToken = default,
        bool useTokenCache = true);
    Task<OrchestrationResolvedSettings> GetResolvedOrchestrationSettingsAsync(CancellationToken cancellationToken = default);
}

public interface IIntegrationProviderSettingsService
{
    Task<OrchestrationConnectivitySettingsDto> GetOrchestratorSettingsAsync(CancellationToken cancellationToken = default);
    Task<OrchestrationResolvedSettings> GetResolvedOrchestratorSettingsAsync(CancellationToken cancellationToken = default);
    Task<OrchestrationResolvedSettings> ResolveOrchestratorDraftAsync(
        UpdateOrchestrationConnectivitySettingsDto request,
        CancellationToken cancellationToken = default);
    Task<OrchestrationConnectivitySettingsDto> UpdateOrchestratorSettingsAsync(
        UpdateOrchestrationConnectivitySettingsDto request,
        CancellationToken cancellationToken = default);
    Task<bool> RecordOrchestratorTestAsync(int expectedRevision, string profileFingerprint, bool succeeded, CancellationToken cancellationToken = default);
    Task<NetclawConnectivitySettingsDto> GetNetclawSettingsAsync(CancellationToken cancellationToken = default);
    Task<NetclawResolvedSettings> GetResolvedNetclawSettingsAsync(CancellationToken cancellationToken = default);
    Task<NetclawResolvedSettings> ResolveNetclawDraftAsync(
        UpdateNetclawConnectivitySettingsDto request,
        CancellationToken cancellationToken = default);
    Task<NetclawConnectivitySettingsDto> UpdateNetclawSettingsAsync(
        UpdateNetclawConnectivitySettingsDto request,
        CancellationToken cancellationToken = default);
    Task<bool> RecordNetclawTestAsync(int expectedRevision, string profileFingerprint, bool succeeded, CancellationToken cancellationToken = default);
    Task ApplyNetclawRuntimeAsync(NetclawResolvedSettings settings, CancellationToken cancellationToken = default);
}

public sealed class IntegrationProviderConfigurationConflictException(string message) : InvalidOperationException(message);

public interface IOrchestrationTokenService
{
    Task<string> GetAccessTokenAsync(
        OrchestrationResolvedSettings settings,
        CancellationToken cancellationToken = default,
        bool useCache = true);
}

public interface IOrchestrationInternalClient
{
    Task<OrchestrationHealthResult> HealthAsync(
        OrchestrationResolvedSettings settings,
        CancellationToken cancellationToken = default,
        bool useTokenCache = true);
    Task<OrchestrationIngestResult> IngestAsync(OrchestrationResolvedSettings settings, OrchestrationIngestRequest request, CancellationToken cancellationToken = default);
}

public interface IOrchestrationProtectedDiagnosticsClient
{
    Task<OrchestrationHealthResult> IdentityAsync(
        OrchestrationResolvedSettings settings,
        CancellationToken cancellationToken = default,
        bool useTokenCache = true);
}

public interface IOrchestrationCatalogService
{
    Task<IReadOnlyList<OrchestrationCatalogJobDto>> ListJobsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrchestrationCatalogTenantDto>> ListTenantsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>> ListRequestDefinitionsAsync(CancellationToken cancellationToken = default);
    Task<OrchestrationCatalogRequestDefinitionDto> CreateRequestDefinitionAsync(CreateOrchestrationCatalogRequestDefinitionDto request, CancellationToken cancellationToken = default);
    Task<OrchestrationCatalogRequestDefinitionDto> SyncRequestDefinitionInputsAsync(
        string requestDefinitionId,
        IReadOnlyList<OrchestrationCatalogInputDefinitionDto> inputs,
        CancellationToken cancellationToken = default);
}

public interface IRequestTaskPayloadBuilder
{
    Task<RequestTaskPayloadBuildResult> BuildAsync(
        RequestTask task,
        string correlationId,
        CancellationToken cancellationToken = default);
}
