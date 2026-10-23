using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.DTOs.Orchestration;
using Microsoft.Extensions.Options;

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
        => ListJobsAsync(null, null, cancellationToken);

    public Task<IReadOnlyList<OrchestrationCatalogTenantDto>> ListTenantsAsync(CancellationToken cancellationToken = default)
        => ListTenantsAsync(null, null, cancellationToken);

    public Task<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>> ListRequestDefinitionsAsync(CancellationToken cancellationToken = default)
        => ListRequestDefinitionsAsync(null, null, cancellationToken);

    public Task<IReadOnlyList<OrchestrationCatalogJobDto>> ListJobsAsync(string? organizationId, Guid? connectionId, CancellationToken cancellationToken = default)
        => GetCatalogAsync<OrchestrationCatalogJobDto>("jobs", organizationId, connectionId, cancellationToken);

    public Task<IReadOnlyList<OrchestrationCatalogTenantDto>> ListTenantsAsync(string? organizationId, Guid? connectionId, CancellationToken cancellationToken = default)
        => GetCatalogAsync<OrchestrationCatalogTenantDto>("tenants", organizationId, connectionId, cancellationToken);

    public Task<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>> ListRequestDefinitionsAsync(string? organizationId, Guid? connectionId, CancellationToken cancellationToken = default)
        => GetCatalogAsync<OrchestrationCatalogRequestDefinitionDto>("request-definitions", organizationId, connectionId, cancellationToken);

    public Task<OrchestrationCatalogRequestDefinitionDto> CreateRequestDefinitionAsync(
        CreateOrchestrationCatalogRequestDefinitionDto requestPayload,
        CancellationToken cancellationToken = default)
        => CreateRequestDefinitionAsync(requestPayload, null, null, cancellationToken);

    public async Task<OrchestrationCatalogRequestDefinitionDto> CreateRequestDefinitionAsync(
        CreateOrchestrationCatalogRequestDefinitionDto requestPayload,
        string? organizationId,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(organizationId, connectionId, cancellationToken);
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("External orchestration connectivity is disabled.");
        }

        var tenantId = RequirePeerTenantId(settings);
        if (requestPayload.TenantId != tenantId)
            throw new InvalidOperationException("The target tenant must match the selected automation system connection.");

        var endpoint = await BuildAbsoluteUriAsync(settings, BuildCatalogPath(settings.CatalogPath, "request-definitions"), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestPayload, options: WireJsonOptions)
        };

        await AttachAuthHeaderAsync(settings, request, cancellationToken);

        var client = _httpClientFactory.CreateClient(PairingTransport.ClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var content = await ReadBoundedContentAsync(response.Content, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"External orchestration request-definition create failed ({(int)response.StatusCode}): {ExtractErrorMessage(content, settings)}");
        }

        NetRatelCatalogRequestDefinitionDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<NetRatelCatalogRequestDefinitionDto>(content, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("External orchestration returned an incompatible request-definition response.");
        }
        if (parsed is null)
        {
            throw new InvalidOperationException("External orchestration returned an empty response for request-definition creation.");
        }

        ValidateRequestDefinitionContract(parsed);
        RequireMatchingTenant(parsed.TenantId, tenantId);
        return MapRequestDefinition(parsed);
    }

    public Task<OrchestrationCatalogRequestDefinitionDto> SyncRequestDefinitionInputsAsync(
        string requestDefinitionId,
        IReadOnlyList<OrchestrationCatalogInputDefinitionDto> inputs,
        CancellationToken cancellationToken = default)
        => SyncRequestDefinitionInputsAsync(requestDefinitionId, inputs, null, null, cancellationToken);

    public async Task<OrchestrationCatalogRequestDefinitionDto> SyncRequestDefinitionInputsAsync(
        string requestDefinitionId,
        IReadOnlyList<OrchestrationCatalogInputDefinitionDto> inputs,
        string? organizationId,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(organizationId, connectionId, cancellationToken);
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("External orchestration connectivity is disabled.");
        }

        var normalizedRequestDefinitionId = string.IsNullOrWhiteSpace(requestDefinitionId)
            ? throw new InvalidOperationException("Request definition id is required.")
            : requestDefinitionId.Trim();

        var tenantId = RequirePeerTenantId(settings);
        // Verify the target using the same resolved connection before changing its inputs.
        var definitions = await GetCatalogAsync<OrchestrationCatalogRequestDefinitionDto>("request-definitions", settings, cancellationToken);
        if (!definitions.Any(x => string.Equals(x.RequestDefinitionId, normalizedRequestDefinitionId, StringComparison.Ordinal)))
            throw new InvalidOperationException("The request definition is unavailable in the selected automation connection tenant.");

        var endpoint = await BuildAbsoluteUriAsync(settings, BuildCatalogPath(settings.CatalogPath, $"request-definitions/{Uri.EscapeDataString(normalizedRequestDefinitionId)}/inputs/sync"), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                Inputs = inputs
            }, options: WireJsonOptions)
        };

        await AttachAuthHeaderAsync(settings, request, cancellationToken);

        var client = _httpClientFactory.CreateClient(PairingTransport.ClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var content = await ReadBoundedContentAsync(response.Content, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"External orchestration request-definition input sync failed ({(int)response.StatusCode}): {ExtractErrorMessage(content, settings)}");
        }

        NetRatelCatalogRequestDefinitionDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<NetRatelCatalogRequestDefinitionDto>(content, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("External orchestration returned an incompatible request-definition response.");
        }
        if (parsed is null)
        {
            throw new InvalidOperationException("External orchestration returned an empty response for request-definition input sync.");
        }

        ValidateRequestDefinitionContract(parsed);
        RequireMatchingTenant(parsed.TenantId, tenantId);
        if (!string.Equals(parsed.RequestDefinitionId, normalizedRequestDefinitionId, StringComparison.Ordinal))
            throw new InvalidOperationException("External orchestration returned a different request definition after input sync.");
        return MapRequestDefinition(parsed);
    }

    private async Task<IReadOnlyList<T>> GetCatalogAsync<T>(string path, string? organizationId, Guid? connectionId, CancellationToken cancellationToken)
        => await GetCatalogAsync<T>(path, await ResolveSettingsAsync(organizationId, connectionId, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<T>> GetCatalogAsync<T>(string path, OrchestrationResolvedSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return [];
        }
        var tenantId = RequirePeerTenantId(settings);

        var endpoint = await BuildAbsoluteUriAsync(settings, BuildCatalogPath(settings.CatalogPath, path), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        await AttachAuthHeaderAsync(settings, request, cancellationToken);

        var client = _httpClientFactory.CreateClient(PairingTransport.ClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var content = await ReadBoundedContentAsync(response.Content, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"External orchestration catalog request failed ({(int)response.StatusCode}): {ExtractErrorMessage(content, settings)}");
        }

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("External orchestration returned an empty catalogue response.");

        if (typeof(T) == typeof(OrchestrationCatalogRequestDefinitionDto))
        {
            try
            {
                var wire = JsonSerializer.Deserialize<List<NetRatelCatalogRequestDefinitionDto>>(content, JsonOptions)
                    ?? throw new InvalidOperationException("External orchestration returned a null request-definition catalogue.");
                foreach (var item in wire)
                    ValidateRequestDefinitionContract(item);
                return wire.Where(item => item.TenantId == tenantId).Select(item => (T)(object)MapRequestDefinition(item)).ToArray();
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("External orchestration returned an incompatible request-definition catalogue.");
            }
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<T>>(content, JsonOptions)
                ?? throw new InvalidOperationException("External orchestration returned a null catalogue.");
            if (typeof(T) == typeof(OrchestrationCatalogJobDto))
            {
                foreach (var item in parsed.Cast<OrchestrationCatalogJobDto>())
                    if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
                        throw new InvalidOperationException("External orchestration returned an incompatible job catalogue.");
            }
            else if (typeof(T) == typeof(OrchestrationCatalogTenantDto))
            {
                foreach (var item in parsed.Cast<OrchestrationCatalogTenantDto>())
                    if (item.TenantId <= 0 || string.IsNullOrWhiteSpace(item.Name))
                        throw new InvalidOperationException("External orchestration returned an incompatible tenant catalogue.");
            }

            return parsed.Where(item => item switch
            {
                OrchestrationCatalogJobDto job => job.TenantId == tenantId,
                OrchestrationCatalogTenantDto tenant => tenant.TenantId == tenantId && tenant.IsActive,
                _ => false
            }).ToArray();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("External orchestration returned an incompatible catalogue response.");
        }
    }

    private async Task<OrchestrationResolvedSettings> ResolveSettingsAsync(string? organizationId, Guid? connectionId, CancellationToken cancellationToken)
    {
        var settings = organizationId is null && connectionId is null
            ? await _connectivityService.GetResolvedOrchestrationSettingsAsync(cancellationToken)
            : await _connectivityService.GetResolvedOrchestrationSettingsAsync(organizationId, connectionId, cancellationToken);
        if (settings.Enabled && (settings.Pairing is null
            || (organizationId is not null && !string.Equals(settings.Pairing.LocalTenantId, organizationId, StringComparison.Ordinal))
            || (connectionId is not null && (!Guid.TryParse(settings.Pairing.MappingId, out var selectedId) || selectedId != connectionId))))
            throw new InvalidOperationException("The resolved automation connection does not match the requested organization and connection.");
        if (!settings.Enabled && (organizationId is not null || connectionId is not null))
            throw new InvalidOperationException("The selected automation system connection is unavailable for this organization.");
        return settings;
    }

    private static int RequirePeerTenantId(OrchestrationResolvedSettings settings)
        => int.TryParse(settings.Pairing?.PeerTenantId, NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId) && tenantId > 0
            ? tenantId
            : throw new InvalidOperationException("The selected automation system connection has an incompatible peer tenant identifier.");

    private static void RequireMatchingTenant(int? actualTenantId, int expectedTenantId)
    {
        if (actualTenantId != expectedTenantId)
            throw new InvalidOperationException("External orchestration returned a target outside the selected connection tenant.");
    }

    private async Task AttachAuthHeaderAsync(
        OrchestrationResolvedSettings settings,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var accessToken = await _tokenService.GetAccessTokenAsync(settings, cancellationToken);
        PairingOutboundNetwork.PrepareRequest(request, settings);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task<Uri> BuildAbsoluteUriAsync(OrchestrationResolvedSettings settings, string path, CancellationToken cancellationToken)
    {
        var baseUrl = settings.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("External orchestration base URL is not configured.");
        }

        var uri = new Uri(new Uri(baseUrl, UriKind.Absolute), path);
        PairingOutboundNetwork.Validate(uri, "NetRatel endpoint", settings);
        return uri;
    }

    private static string BuildCatalogPath(string catalogPath, string suffix)
        => $"{catalogPath.TrimEnd('/')}/{suffix.TrimStart('/')}";

    private static void ValidateRequestDefinitionContract(NetRatelCatalogRequestDefinitionDto item)
    {
        if (string.IsNullOrWhiteSpace(item.RequestDefinitionId)
            || string.IsNullOrWhiteSpace(item.RequestDefinitionName)
            || string.IsNullOrWhiteSpace(item.ClientIdentity)
            || item.Inputs is null)
        {
            throw new InvalidOperationException("External orchestration returned an incompatible request-definition catalogue.");
        }
    }

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

    private static string ExtractErrorMessage(string content, OrchestrationResolvedSettings settings)
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
                        return IntegrationErrorSafety.ProviderMessage(value, 256, settings.ClientSecret);
                }
            }
        }
        catch (JsonException)
        {
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
