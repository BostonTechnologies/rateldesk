using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Shared.DTOs.Orchestration;

namespace HelpDesk.NewWeb.Services;

public static class OrchestrationApiErrors
{
    public static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<OrchestrationProviderErrorDto>();
            if (!string.IsNullOrWhiteSpace(error?.Message)) return error.Message;
        }
        catch (JsonException)
        {
            return $"NetRatel request failed (HTTP {(int)response.StatusCode}). Refresh this saved connection and retry.";
        }
        return $"NetRatel request failed (HTTP {(int)response.StatusCode}). Refresh this saved connection and retry.";
    }

    public static async Task<T?> GetAsync<T>(HttpClient api, string path)
    {
        using var response = await api.GetAsync(path);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ReadAsync(response));
        try { return await response.Content.ReadFromJsonAsync<T>(); }
        catch (JsonException) { throw new InvalidOperationException("NetRatel returned an incompatible response. Refresh this connection or upgrade the compatible product pair."); }
    }
}
