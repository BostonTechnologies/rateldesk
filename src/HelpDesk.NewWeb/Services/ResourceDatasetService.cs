using System.Net.Http.Json;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Resources;

namespace HelpDesk.NewWeb.Services;

public interface IResourceDatasetService
{
    Task<IReadOnlyList<DatasetDefinitionDto>> ListAsync(string organizationId);
    Task<DatasetDefinitionDto?> GetAsync(string datasetId);
    Task<DatasetDefinitionDto?> CreateAsync(CreateDatasetDefinitionDto dto);
    Task<DatasetDefinitionDto?> UpdateAsync(string datasetId, UpdateDatasetDefinitionDto dto);
    Task<bool> DeleteAsync(string datasetId);
    Task<PagedResult<DatasetRowDto>> GetRowsAsync(string datasetId, string? query, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatasetIngestCredentialDto>> GetCredentialsAsync(string datasetId);
    Task<DatasetIngestCredentialCreatedDto?> CreateCredentialAsync(string datasetId, CreateDatasetIngestCredentialDto dto);
    Task<bool> RevokeCredentialAsync(string datasetId, string credentialId);
    Task<TenantGraphDatasetSettingsDto?> GetGraphSettingsAsync(string organizationId);
    Task<TenantGraphDatasetSettingsDto?> SaveGraphSettingsAsync(TenantGraphDatasetSettingsDto dto);
    Task<IReadOnlyList<DatasetDefinitionDto>> SyncBuiltInsAsync(string organizationId);
    Task<PagedResult<DatasetOptionDto>> SearchOptionsAsync(string datasetId, string? query, int page, int pageSize);
}

public class ResourceDatasetService(IHttpClientFactory httpClientFactory) : IResourceDatasetService
{
    private readonly HttpClient _http = httpClientFactory.CreateClient("HelpdeskApi");

    public async Task<IReadOnlyList<DatasetDefinitionDto>> ListAsync(string organizationId)
        => await _http.GetFromJsonAsync<List<DatasetDefinitionDto>>($"api/v1/resources/datasets?organizationId={Uri.EscapeDataString(organizationId)}")
           ?? [];

    public Task<DatasetDefinitionDto?> GetAsync(string datasetId)
        => _http.GetFromJsonAsync<DatasetDefinitionDto>($"api/v1/resources/datasets/{datasetId}");

    public async Task<DatasetDefinitionDto?> CreateAsync(CreateDatasetDefinitionDto dto)
    {
        var response = await _http.PostAsJsonAsync("api/v1/resources/datasets", dto);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<DatasetDefinitionDto>()
            : null;
    }

    public async Task<DatasetDefinitionDto?> UpdateAsync(string datasetId, UpdateDatasetDefinitionDto dto)
    {
        var response = await _http.PutAsJsonAsync($"api/v1/resources/datasets/{datasetId}", dto);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<DatasetDefinitionDto>()
            : null;
    }

    public async Task<bool> DeleteAsync(string datasetId)
        => (await _http.DeleteAsync($"api/v1/resources/datasets/{datasetId}")).IsSuccessStatusCode;

    public async Task<PagedResult<DatasetRowDto>> GetRowsAsync(string datasetId, string? query, int page, int pageSize, CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<PagedResult<DatasetRowDto>>(
               $"api/v1/resources/datasets/{datasetId}/rows?query={Uri.EscapeDataString(query ?? string.Empty)}&page={page}&pageSize={pageSize}", cancellationToken)
           ?? new PagedResult<DatasetRowDto>();

    public async Task<IReadOnlyList<DatasetIngestCredentialDto>> GetCredentialsAsync(string datasetId)
        => await _http.GetFromJsonAsync<List<DatasetIngestCredentialDto>>($"api/v1/resources/datasets/{datasetId}/credentials")
           ?? [];

    public async Task<DatasetIngestCredentialCreatedDto?> CreateCredentialAsync(string datasetId, CreateDatasetIngestCredentialDto dto)
    {
        var response = await _http.PostAsJsonAsync($"api/v1/resources/datasets/{datasetId}/credentials", dto);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<DatasetIngestCredentialCreatedDto>()
            : null;
    }

    public async Task<bool> RevokeCredentialAsync(string datasetId, string credentialId)
        => (await _http.DeleteAsync($"api/v1/resources/datasets/{datasetId}/credentials/{credentialId}")).IsSuccessStatusCode;

    public async Task<TenantGraphDatasetSettingsDto?> GetGraphSettingsAsync(string organizationId)
    {
        var response = await _http.GetAsync($"api/v1/resources/datasets/graph-settings?organizationId={Uri.EscapeDataString(organizationId)}");
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TenantGraphDatasetSettingsDto>()
            : null;
    }

    public async Task<TenantGraphDatasetSettingsDto?> SaveGraphSettingsAsync(TenantGraphDatasetSettingsDto dto)
    {
        var response = await _http.PutAsJsonAsync("api/v1/resources/datasets/graph-settings", dto);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TenantGraphDatasetSettingsDto>()
            : null;
    }

    public async Task<IReadOnlyList<DatasetDefinitionDto>> SyncBuiltInsAsync(string organizationId)
    {
        var response = await _http.PostAsync($"api/v1/resources/datasets/graph-settings/{Uri.EscapeDataString(organizationId)}/sync", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<DatasetDefinitionDto>>()) ?? [];
    }

    public async Task<PagedResult<DatasetOptionDto>> SearchOptionsAsync(string datasetId, string? query, int page, int pageSize)
        => await _http.GetFromJsonAsync<PagedResult<DatasetOptionDto>>(
               $"api/v1/self-service/datasets/{datasetId}/options?query={Uri.EscapeDataString(query ?? string.Empty)}&page={page}&pageSize={pageSize}")
           ?? new PagedResult<DatasetOptionDto>();
}
