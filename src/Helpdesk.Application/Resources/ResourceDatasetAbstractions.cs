using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.DTOs.Resources;

namespace Helpdesk.Application.Resources;

public interface IDataManagementService
{
    Task<IReadOnlyList<DatasetDefinitionDto>> ListDatasetsAsync(string organizationId, CancellationToken cancellationToken = default);
    Task<DatasetDefinitionDto?> GetDatasetAsync(string datasetId, CancellationToken cancellationToken = default);
    Task<DatasetDefinitionDto> CreateDatasetAsync(CreateDatasetDefinitionDto dto, CancellationToken cancellationToken = default);
    Task<DatasetDefinitionDto?> UpdateDatasetAsync(string datasetId, UpdateDatasetDefinitionDto dto, CancellationToken cancellationToken = default);
    Task<bool> DeleteDatasetAsync(string datasetId, CancellationToken cancellationToken = default);
    Task<PagedResult<DatasetRowDto>> GetRowsAsync(string datasetId, string? query, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<PagedResult<DatasetOptionDto>> GetOptionsAsync(string datasetId, string organizationId, string? query, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<DatasetIngestCredentialCreatedDto> CreateCredentialAsync(string datasetId, CreateDatasetIngestCredentialDto dto, CancellationToken cancellationToken = default);
    Task<bool> RevokeCredentialAsync(string datasetId, string credentialId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatasetIngestCredentialDto>> ListCredentialsAsync(string datasetId, CancellationToken cancellationToken = default);
    Task<int> UpsertRowsAsync(string datasetId, string apiKey, UpsertDatasetRowsDto dto, CancellationToken cancellationToken = default);
    Task<TenantGraphDatasetSettingsDto?> GetGraphSettingsAsync(string organizationId, CancellationToken cancellationToken = default);
    Task<TenantGraphDatasetSettingsDto> UpsertGraphSettingsAsync(TenantGraphDatasetSettingsDto dto, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatasetDefinitionDto>> SyncBuiltInDatasetsAsync(string organizationId, CancellationToken cancellationToken = default);
}

/// <summary>Dataset row writes after the API resolves the caller's current organization authority.</summary>
public interface IOperatorDatasetWriter
{
    Task<int> UpsertRowsAsOperatorAsync(string datasetId, string authorizedOrganizationId,
        bool allowBuiltInSources, UpsertDatasetRowsDto dto, CancellationToken cancellationToken = default);
}

/// <summary>The dataset changed during a write; inspect current rows before submitting another update.</summary>
public sealed class DatasetWriteConflictException(string message) : Exception(message);

public interface IRequestFormDatasetBindingValidator
{
    Task<string?> ValidateAsync(string organizationId, IReadOnlyCollection<FormField> fields, CancellationToken cancellationToken = default);
}

public interface ISelfServiceDatasetBindingResolver
{
    Task<(bool Success, string PayloadJson, IReadOnlyList<string> Errors)> NormalizePayloadAsync(
        string organizationId,
        IReadOnlyCollection<FormField> fields,
        string payloadJson,
        CancellationToken cancellationToken = default);
}
