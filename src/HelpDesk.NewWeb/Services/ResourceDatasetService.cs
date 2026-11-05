using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Polly.Timeout;
using Polly.CircuitBreaker;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Resources;

namespace HelpDesk.NewWeb.Services;

public interface IResourceDatasetService
{
    Task<IReadOnlyList<DatasetDefinitionDto>> ListAsync(string organizationId);
    Task<DatasetDefinitionDto?> GetAsync(string datasetId);
    Task<DatasetDefinitionDto?> CreateAsync(CreateDatasetDefinitionDto dto);
    async Task<DatasetCreateResult> CreateWithResultAsync(CreateDatasetDefinitionDto dto, CancellationToken cancellationToken = default)
        => new() { Dataset = await CreateAsync(dto) };
    Task<DatasetDefinitionDto?> GetCreateResultAsync(string organizationId, Guid requestId, CancellationToken cancellationToken = default)
        => Task.FromException<DatasetDefinitionDto?>(new NotSupportedException("Creation reconciliation is unavailable."));
    Task<DatasetDefinitionDto?> UpdateAsync(string datasetId, UpdateDatasetDefinitionDto dto);
    Task<bool> DeleteAsync(string datasetId);
    Task<PagedResult<DatasetRowDto>> GetRowsAsync(string datasetId, string? query, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatasetIngestCredentialDto>> GetCredentialsAsync(string datasetId);
    Task<DatasetIngestCredentialCreatedDto?> CreateCredentialAsync(string datasetId, CreateDatasetIngestCredentialDto dto);
    Task<bool> RevokeCredentialAsync(string datasetId, string credentialId);
    Task<TenantGraphDatasetSettingsDto?> GetGraphSettingsAsync(string organizationId);
    Task<TenantGraphDatasetSettingsDto?> SaveGraphSettingsAsync(TenantGraphDatasetSettingsDto dto);
    Task<IReadOnlyList<DatasetDefinitionDto>> SyncBuiltInsAsync(string organizationId);
    Task<GraphConnectionTestResultDto> TestGraphConnectionAsync(GraphConnectionTestRequestDto dto, CancellationToken cancellationToken = default)
        => Task.FromException<GraphConnectionTestResultDto>(new NotSupportedException("Graph connection testing is unavailable."));
    Task<GraphDatasetImportStatusDto> SaveAndImportGraphAsync(GraphDatasetImportRequestDto dto, CancellationToken cancellationToken = default)
        => Task.FromException<GraphDatasetImportStatusDto>(new NotSupportedException("Guided Graph import is unavailable."));
    Task<GraphDatasetImportStatusDto?> GetGraphImportStatusAsync(string organizationId, CancellationToken cancellationToken = default)
        => Task.FromResult<GraphDatasetImportStatusDto?>(null);
    async Task<GraphDatasetImportStatusDto?> GetGraphImportRequestStatusAsync(string organizationId, Guid requestId, CancellationToken cancellationToken = default)
    {
        var status = await GetGraphImportStatusAsync(organizationId, cancellationToken);
        return status?.RequestId == requestId ? status : null;
    }
    Task<GraphDatasetImportStatusDto> ReconcileInterruptedGraphImportAsync(string organizationId, Guid requestId, CancellationToken cancellationToken = default)
        => Task.FromException<GraphDatasetImportStatusDto>(new NotSupportedException("Import reconciliation is unavailable."));
    Task<PagedResult<DatasetOptionDto>> SearchOptionsAsync(string datasetId, string? query, int page, int pageSize);
}

public sealed class DatasetCreateResult
{
    public DatasetDefinitionDto? Dataset { get; init; }
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
    public string? Message { get; init; }
    public bool IsUncertain { get; init; }
}

public class ResourceDatasetService(IHttpClientFactory httpClientFactory) : IResourceDatasetService
{
    private readonly HttpClient _http = httpClientFactory.CreateClient("HelpdeskApi");
    private readonly HttpClient _commands = httpClientFactory.CreateClient(HelpdeskCommandHttpClients.CommandsClientName);

    public async Task<IReadOnlyList<DatasetDefinitionDto>> ListAsync(string organizationId)
        => await _http.GetFromJsonAsync<List<DatasetDefinitionDto>>($"api/v1/resources/datasets?organizationId={Uri.EscapeDataString(organizationId)}")
           ?? [];

    public Task<DatasetDefinitionDto?> GetAsync(string datasetId)
        => _http.GetFromJsonAsync<DatasetDefinitionDto>($"api/v1/resources/datasets/{datasetId}");

    public async Task<DatasetDefinitionDto?> CreateAsync(CreateDatasetDefinitionDto dto)
    {
        var response = await _commands.PostAsJsonAsync("api/v1/resources/datasets", dto);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<DatasetDefinitionDto>()
            : null;
    }

    public async Task<DatasetCreateResult> CreateWithResultAsync(CreateDatasetDefinitionDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _commands.PostAsJsonAsync("api/v1/resources/datasets", dto, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var dataset = await response.Content.ReadFromJsonAsync<DatasetDefinitionDto>(cancellationToken);
                return dataset is null || string.IsNullOrWhiteSpace(dataset.Id) ||
                    !string.Equals(dataset.OrganizationId, dto.OrganizationId?.Trim(), StringComparison.Ordinal)
                    ? new() { IsUncertain = true, Message = "The server response did not confirm creation. Reconcile this request before retrying." }
                    : new() { Dataset = dataset };
            }
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var validation = await response.Content.ReadFromJsonAsync<DatasetValidationResponse>(cancellationToken);
                return new() { Errors = validation?.Errors ?? [], Message = validation?.Title ?? validation?.Message ?? "Correct the dataset fields and try again." };
            }
            return new()
            {
                Message = response.StatusCode == HttpStatusCode.Conflict
                    ? "The dataset list changed. Reconcile this creation request before retrying."
                    : response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                        ? "You no longer have permission to create a dataset in this organization."
                        : "The server could not confirm creation. Reconcile this request before retrying.",
                IsUncertain = response.StatusCode == HttpStatusCode.Conflict || (int)response.StatusCode >= 500
            };
        }
        catch (HttpRequestException)
        {
            return new() { IsUncertain = true, Message = "The connection ended before creation was confirmed. Reconcile this request before retrying." };
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutRejectedException or TimeoutException or BrokenCircuitException or JsonException)
        {
            return new() { IsUncertain = true, Message = "Creation was not confirmed before the request ended. Reconcile this request before retrying." };
        }
    }

    public async Task<DatasetDefinitionDto?> GetCreateResultAsync(string organizationId, Guid requestId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync($"api/v1/resources/datasets/create-requests/{requestId:D}?organizationId={Uri.EscapeDataString(organizationId)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<DatasetDefinitionDto>(cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is TimeoutRejectedException or TimeoutException or OperationCanceledException or JsonException or BrokenCircuitException)
        {
            throw new HttpRequestException("Creation status was not confirmed. Preserve the same draft and check its status again before retrying.");
        }
    }

    public async Task<DatasetDefinitionDto?> UpdateAsync(string datasetId, UpdateDatasetDefinitionDto dto)
    {
        var response = await _commands.PutAsJsonAsync($"api/v1/resources/datasets/{datasetId}", dto);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<DatasetDefinitionDto>()
            : null;
    }

    public async Task<bool> DeleteAsync(string datasetId)
        => (await _commands.DeleteAsync($"api/v1/resources/datasets/{datasetId}")).IsSuccessStatusCode;

    public async Task<PagedResult<DatasetRowDto>> GetRowsAsync(string datasetId, string? query, int page, int pageSize, CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<PagedResult<DatasetRowDto>>(
               $"api/v1/resources/datasets/{datasetId}/rows?query={Uri.EscapeDataString(query ?? string.Empty)}&page={page}&pageSize={pageSize}", cancellationToken)
           ?? new PagedResult<DatasetRowDto>();

    public async Task<IReadOnlyList<DatasetIngestCredentialDto>> GetCredentialsAsync(string datasetId)
        => await DatasetRequestAsync(async () => await _http.GetFromJsonAsync<List<DatasetIngestCredentialDto>>(
            $"api/v1/resources/datasets/{datasetId}/credentials") ?? []);

    public async Task<DatasetIngestCredentialCreatedDto?> CreateCredentialAsync(string datasetId, CreateDatasetIngestCredentialDto dto)
    {
        return await DatasetRequestAsync(async () =>
        {
            using var response = await _commands.PostAsJsonAsync($"api/v1/resources/datasets/{datasetId}/credentials", dto);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<DatasetIngestCredentialCreatedDto>()
                : null;
        });
    }

    public async Task<bool> RevokeCredentialAsync(string datasetId, string credentialId)
        => (await _commands.DeleteAsync($"api/v1/resources/datasets/{datasetId}/credentials/{credentialId}")).IsSuccessStatusCode;

    public async Task<TenantGraphDatasetSettingsDto?> GetGraphSettingsAsync(string organizationId)
    {
        return await GraphRequestAsync(async () =>
        {
            using var response = await _http.GetAsync($"api/v1/resources/datasets/graph-settings?organizationId={Uri.EscapeDataString(organizationId)}");
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TenantGraphDatasetSettingsDto>();
        }, CancellationToken.None);
    }

    public async Task<TenantGraphDatasetSettingsDto?> SaveGraphSettingsAsync(TenantGraphDatasetSettingsDto dto)
    {
        return await GraphRequestAsync(async () =>
        {
            using var response = await _commands.PutAsJsonAsync("api/v1/resources/datasets/graph-settings", dto);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<TenantGraphDatasetSettingsDto>()
                : null;
        }, CancellationToken.None);
    }

    public async Task<IReadOnlyList<DatasetDefinitionDto>> SyncBuiltInsAsync(string organizationId)
    {
        var response = await _commands.PostAsync($"api/v1/resources/datasets/graph-settings/{Uri.EscapeDataString(organizationId)}/sync", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<DatasetDefinitionDto>>()) ?? [];
    }

    public async Task<GraphConnectionTestResultDto> TestGraphConnectionAsync(GraphConnectionTestRequestDto dto, CancellationToken cancellationToken = default)
    {
        return await GraphRequestAsync(async () =>
        {
            using var response = await _http.PostAsJsonAsync("api/v1/resources/datasets/graph-settings/test", dto, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GraphConnectionTestResultDto>(cancellationToken)
                ?? throw new HttpRequestException("The server did not return a connection-test result.");
        }, cancellationToken);
    }

    public async Task<GraphDatasetImportStatusDto> SaveAndImportGraphAsync(GraphDatasetImportRequestDto dto, CancellationToken cancellationToken = default)
    {
        return await GraphRequestAsync(async () =>
        {
            using var response = await _commands.PostAsJsonAsync("api/v1/resources/datasets/graph-settings/import", dto, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GraphDatasetImportStatusDto>(cancellationToken)
                ?? throw new HttpRequestException("The server did not confirm the import; reload recorded results.");
        }, cancellationToken);
    }

    public async Task<GraphDatasetImportStatusDto?> GetGraphImportStatusAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        return await GraphRequestAsync(async () =>
        {
            using var response = await _http.GetAsync($"api/v1/resources/datasets/graph-settings/{Uri.EscapeDataString(organizationId)}/import-status", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GraphDatasetImportStatusDto>(cancellationToken);
        }, cancellationToken);
    }

    public async Task<GraphDatasetImportStatusDto> ReconcileInterruptedGraphImportAsync(string organizationId, Guid requestId, CancellationToken cancellationToken = default)
    {
        return await GraphRequestAsync(async () =>
        {
            using var response = await _commands.PostAsync($"api/v1/resources/datasets/graph-settings/{Uri.EscapeDataString(organizationId)}/import-status/{requestId:D}/reconcile", null, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GraphDatasetImportStatusDto>(cancellationToken)
                ?? throw new HttpRequestException("The server did not confirm reconciliation; reload recorded results.");
        }, cancellationToken);
    }

    public async Task<GraphDatasetImportStatusDto?> GetGraphImportRequestStatusAsync(string organizationId, Guid requestId, CancellationToken cancellationToken = default)
    {
        return await GraphRequestAsync(async () =>
        {
            using var response = await _http.GetAsync($"api/v1/resources/datasets/graph-settings/{Uri.EscapeDataString(organizationId)}/import-status?requestId={requestId:D}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GraphDatasetImportStatusDto>(cancellationToken);
        }, cancellationToken);
    }


    private static async Task<T> DatasetRequestAsync<T>(Func<Task<T>> request)
    {
        try { return await request(); }
        catch (Exception error) when (error is HttpRequestException or TimeoutRejectedException or TimeoutException or OperationCanceledException or JsonException or BrokenCircuitException)
        {
            throw new HttpRequestException("The dataset operation was not confirmed. Keep the named draft and read current key metadata before retrying.");
        }
    }

    private static async Task<T> GraphRequestAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
    {
        try { return await request(); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is HttpRequestException or TimeoutRejectedException or TimeoutException or OperationCanceledException or JsonException or BrokenCircuitException)
        {
            var status = (error as HttpRequestException)?.StatusCode;
            throw new HttpRequestException(status switch
            {
                HttpStatusCode.BadRequest => "Test the current connection and every selected dataset again before importing.",
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "Permission to manage this organization's Graph integration is unavailable. Reload the organization context.",
                HttpStatusCode.Conflict => "The import needs attention or may already be running. Reload the saved connection and recorded results before testing or retrying.",
                _ => "The Graph operation was not confirmed. Keep the draft and reload recorded results before retrying."
            }, inner: null, status);

        }
    }

    private sealed class DatasetValidationResponse
    {
        public Dictionary<string, string[]>? Errors { get; set; }
        public string? Title { get; set; }
        public string? Message { get; set; }
    }

    public async Task<PagedResult<DatasetOptionDto>> SearchOptionsAsync(string datasetId, string? query, int page, int pageSize)
        => await _http.GetFromJsonAsync<PagedResult<DatasetOptionDto>>(
               $"api/v1/self-service/datasets/{datasetId}/options?query={Uri.EscapeDataString(query ?? string.Empty)}&page={page}&pageSize={pageSize}")
           ?? new PagedResult<DatasetOptionDto>();
}
