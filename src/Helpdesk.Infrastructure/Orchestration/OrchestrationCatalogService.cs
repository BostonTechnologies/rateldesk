using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class OrchestrationCatalogService(
    IHttpClientFactory httpClientFactory,
    IOrchestrationTokenService tokenService,
    IOrchestrationConnectivityService connectivityService) : IOrchestrationCatalogService
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.General);

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IOrchestrationTokenService _tokenService = tokenService;
    private readonly IOrchestrationConnectivityService _connectivityService = connectivityService;

    public Task<IReadOnlyList<OrchestrationCatalogJobDto>> ListJobsAsync(CancellationToken cancellationToken = default)
        => GetCatalogAsync<OrchestrationCatalogJobDto>("jobs", cancellationToken);

    public Task<IReadOnlyList<OrchestrationCatalogTenantDto>> ListTenantsAsync(CancellationToken cancellationToken = default)
        => GetCatalogAsync<OrchestrationCatalogTenantDto>("tenants", cancellationToken);

    public Task<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>> ListRequestDefinitionsAsync(CancellationToken cancellationToken = default)
        => GetCatalogAsync<OrchestrationCatalogRequestDefinitionDto>("request-definitions", cancellationToken);

    public async Task<OrchestrationCatalogRequestDefinitionDto> CreateRequestDefinitionAsync(
        CreateOrchestrationCatalogRequestDefinitionDto requestPayload,
        CancellationToken cancellationToken = default)
    {
        var settings = await _connectivityService.GetResolvedOrchestrationSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("External orchestration connectivity is disabled.");
        }

        var endpoint = BuildAbsoluteUri(settings.BaseUrl, BuildCatalogPath(settings.CatalogPath, "request-definitions"));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestPayload, options: WireJsonOptions)
        };

        await AttachAuthHeaderAsync(settings, request, cancellationToken);

        var client = _httpClientFactory.CreateClient("OrchestrationInternalApi");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var content = await ReadBoundedContentAsync(response.Content, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"External orchestration request-definition create failed ({(int)response.StatusCode}): {ExtractErrorMessage(content)}");
        }

        NetRatelCatalogRequestDefinitionDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<NetRatelCatalogRequestDefinitionDto>(content, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("External orchestration returned an incompatible request-definition response.", exception);
        }
        if (parsed is null)
        {
            throw new InvalidOperationException("External orchestration returned an empty response for request-definition creation.");
        }

        return MapRequestDefinition(parsed);
    }

    public async Task<OrchestrationCatalogRequestDefinitionDto> SyncRequestDefinitionInputsAsync(
        string requestDefinitionId,
        IReadOnlyList<OrchestrationCatalogInputDefinitionDto> inputs,
        CancellationToken cancellationToken = default)
    {
        var settings = await _connectivityService.GetResolvedOrchestrationSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("External orchestration connectivity is disabled.");
        }

        var normalizedRequestDefinitionId = string.IsNullOrWhiteSpace(requestDefinitionId)
            ? throw new InvalidOperationException("Request definition id is required.")
            : requestDefinitionId.Trim();

        var endpoint = BuildAbsoluteUri(settings.BaseUrl, BuildCatalogPath(settings.CatalogPath, $"request-definitions/{normalizedRequestDefinitionId}/inputs/sync"));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                Inputs = inputs
            }, options: WireJsonOptions)
        };

        await AttachAuthHeaderAsync(settings, request, cancellationToken);

        var client = _httpClientFactory.CreateClient("OrchestrationInternalApi");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var content = await ReadBoundedContentAsync(response.Content, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"External orchestration request-definition input sync failed ({(int)response.StatusCode}): {ExtractErrorMessage(content)}");
        }

        NetRatelCatalogRequestDefinitionDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<NetRatelCatalogRequestDefinitionDto>(content, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("External orchestration returned an incompatible request-definition response.", exception);
        }
        if (parsed is null)
        {
            throw new InvalidOperationException("External orchestration returned an empty response for request-definition input sync.");
        }

        return MapRequestDefinition(parsed);
    }

    private async Task<IReadOnlyList<T>> GetCatalogAsync<T>(string path, CancellationToken cancellationToken)
    {
        var settings = await _connectivityService.GetResolvedOrchestrationSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return [];
        }

        var endpoint = BuildAbsoluteUri(settings.BaseUrl, BuildCatalogPath(settings.CatalogPath, path));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        await AttachAuthHeaderAsync(settings, request, cancellationToken);

        var client = _httpClientFactory.CreateClient("OrchestrationInternalApi");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var content = await ReadBoundedContentAsync(response.Content, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"External orchestration catalog request failed ({(int)response.StatusCode}): {ExtractErrorMessage(content)}");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        if (typeof(T) == typeof(OrchestrationCatalogRequestDefinitionDto))
        {
            try
            {
                var wire = JsonSerializer.Deserialize<List<NetRatelCatalogRequestDefinitionDto>>(content, JsonOptions) ?? [];
                return wire.Select(item => (T)(object)MapRequestDefinition(item)).ToArray();
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("External orchestration returned an incompatible request-definition catalogue.", exception);
            }
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<T>>(content, JsonOptions);
            return parsed ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("External orchestration returned an incompatible catalogue response.", exception);
        }
    }

    private async Task AttachAuthHeaderAsync(
        OrchestrationResolvedSettings settings,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var accessToken = await _tokenService.GetAccessTokenAsync(settings, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static Uri BuildAbsoluteUri(string? baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("External orchestration base URL is not configured.");
        }

        var uri = new Uri(new Uri(baseUrl, UriKind.Absolute), path);
        if (!IntegrationEndpointPolicy.IsAllowed(uri))
            throw new InvalidOperationException("The configured NetRatel endpoint is not allowed by the outbound integration policy.");
        return uri;
    }

    private static string BuildCatalogPath(string catalogPath, string suffix)
        => $"{catalogPath.TrimEnd('/')}/{suffix.TrimStart('/')}";

    private static async Task<string> ReadBoundedContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException("The external orchestration catalogue response exceeded the maximum supported size.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static string ExtractErrorMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "provider returned no error details";
        try
        {
            using var document = JsonDocument.Parse(content);
            foreach (var name in new[] { "message", "detail", "title", "error", "code" })
            {
                if (document.RootElement.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim()[..Math.Min(value.Trim().Length, 256)];
                }
            }
        }
        catch (JsonException)
        {
            // Do not return an arbitrary upstream body to an administrator.
            return "provider returned an invalid error response";
        }

        return "provider returned an incompatible error response";
    }

    private static OrchestrationCatalogRequestDefinitionDto MapRequestDefinition(NetRatelCatalogRequestDefinitionDto source)
        => new()
        {
            RequestDefinitionId = source.RequestDefinitionId,
            RequestDefinitionName = source.RequestDefinitionName,
            DisplayName = source.DisplayName,
            Description = source.Description,
            FolderPath = source.FolderPath,
            TenantId = source.TenantId,
            TenantName = source.TenantName,
            OrchestrationJobDefinitionId = source.NetRatelJobDefinitionId,
            OrchestrationJobDefinitionName = source.NetRatelJobDefinitionName,
            ClientIdentity = source.ClientIdentity,
            ClientDisplayName = source.ClientDisplayName,
            ClientHostName = source.ClientHostName,
            ClientName = source.ClientName,
            ClientShortId = source.ClientShortId,
            ScriptType = source.ScriptType,
            ExpectedRuntimeSeconds = source.ExpectedRuntimeSeconds,
            GraceSeconds = source.GraceSeconds,
            HardTimeoutSeconds = source.HardTimeoutSeconds,
            Inputs = source.Inputs
        };

    private sealed class NetRatelCatalogRequestDefinitionDto
    {
        public string RequestDefinitionId { get; set; } = string.Empty;
        public string RequestDefinitionName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string FolderPath { get; set; } = "/";
        public int? TenantId { get; set; }
        public string? TenantName { get; set; }
        public string? NetRatelJobDefinitionId { get; set; }
        public string? NetRatelJobDefinitionName { get; set; }
        public string ClientIdentity { get; set; } = string.Empty;
        public string? ClientDisplayName { get; set; }
        public string? ClientHostName { get; set; }
        public string? ClientName { get; set; }
        public string? ClientShortId { get; set; }
        public string? ScriptType { get; set; }
        public IReadOnlyList<OrchestrationCatalogInputDefinitionDto> Inputs { get; set; } = [];
        public int ExpectedRuntimeSeconds { get; set; } = 1800;
        public int GraceSeconds { get; set; }
        public int HardTimeoutSeconds { get; set; } = 1800;
    }
}
