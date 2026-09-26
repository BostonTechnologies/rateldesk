using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class OrchestrationInternalClient(
    IHttpClientFactory httpClientFactory,
    IOrchestrationTokenService tokenService,
    ILogger<OrchestrationInternalClient> logger) : IOrchestrationInternalClient, IOrchestrationProtectedDiagnosticsClient
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IOrchestrationTokenService _tokenService = tokenService;
    private readonly ILogger<OrchestrationInternalClient> _logger = logger;

    public async Task<OrchestrationHealthResult> HealthAsync(
        OrchestrationResolvedSettings settings,
        CancellationToken cancellationToken = default,
        bool useTokenCache = true)
    {
        var endpoint = BuildAbsoluteUri(settings.BaseUrl, settings.HealthPath, settings.AllowPrivateHttp);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        await AttachAuthHeaderAsync(settings, request, cancellationToken, useTokenCache);

        var client = _httpClientFactory.CreateClient("OrchestrationInternalApi");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var content = await ReadBoundedContentAsync(response.Content, timeout.Token);

        var message = response.IsSuccessStatusCode
            ? ParseHealth(content)
            : (Success: false, Message: $"HTTP {(int)response.StatusCode}: {ExtractErrorMessage(content, settings)}");

        return new OrchestrationHealthResult
        {
            Success = response.IsSuccessStatusCode && message.Success,
            StatusCode = (int)response.StatusCode,
            Message = message.Message
        };
    }

    public async Task<OrchestrationHealthResult> IdentityAsync(
        OrchestrationResolvedSettings settings,
        CancellationToken cancellationToken = default,
        bool useTokenCache = true)
    {
        var endpoint = BuildAbsoluteUri(settings.BaseUrl, "/api/v1/system/m2m/ping", settings.AllowPrivateHttp);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        await AttachAuthHeaderAsync(settings, request, cancellationToken, useTokenCache);
        var client = _httpClientFactory.CreateClient("OrchestrationInternalApi");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var content = await ReadBoundedContentAsync(response.Content, timeout.Token);
        var contractValid = response.IsSuccessStatusCode && HasIdentityContract(content);
        return new OrchestrationHealthResult
        {
            Success = contractValid,
            StatusCode = (int)response.StatusCode,
            Message = contractValid
                ? "Protected NetRatel identity probe succeeded."
                : response.IsSuccessStatusCode
                    ? "The protected identity response did not match the NetRatel contract."
                : $"HTTP {(int)response.StatusCode}: {ExtractErrorMessage(content, settings)}"
        };
    }

    public async Task<OrchestrationIngestResult> IngestAsync(
        OrchestrationResolvedSettings settings,
        OrchestrationIngestRequest requestPayload,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildAbsoluteUri(settings.BaseUrl, settings.IngestPath, settings.AllowPrivateHttp);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                ToNetRatelRequest(requestPayload),
                options: new JsonSerializerOptions(JsonSerializerDefaults.General))
        };

        await AttachAuthHeaderAsync(settings, request, cancellationToken);

        var client = _httpClientFactory.CreateClient("OrchestrationInternalApi");
        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using (response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                var content = await ReadBoundedContentAsync(response.Content, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var message = $"External NetRatel ingest failed ({(int)response.StatusCode}): {ExtractErrorMessage(content, settings)}";
                    if ((int)response.StatusCode is 408 or 429 or >= 500)
                        throw new OrchestrationSubmissionUncertainException(message);
                    if ((int)response.StatusCode is >= 400 and < 500)
                        throw new OrchestrationSubmissionRejectedException(message);
                    throw new InvalidOperationException(message);
                }

                if (string.IsNullOrWhiteSpace(content))
                    throw new OrchestrationAcknowledgementException("NetRatel accepted the HTTP request without returning an execution acknowledgement.");

                try
                {
                    var parsed = JsonSerializer.Deserialize<OrchestrationIngestResult>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (parsed is not null &&
                        !string.IsNullOrWhiteSpace(parsed.ExecutionId) &&
                        IsPositiveAcknowledgementStatus(parsed.Status))
                        return parsed;

                    if (parsed is not null && IsNegativeAcknowledgementStatus(parsed.Status))
                        throw new OrchestrationSubmissionRejectedException("NetRatel explicitly rejected the submitted operation.");
                }
            catch (JsonException exception)
            {
                _logger.LogDebug("NetRatel returned malformed ingest JSON. ExceptionType={ExceptionType}", exception.GetType().Name);
                }

                throw new OrchestrationAcknowledgementException("NetRatel returned a successful response without a valid execution identifier.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OrchestrationSubmissionUncertainException("The NetRatel submission timed out before its acknowledgement was received.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug("The NetRatel submission connection failed before acknowledgement. ExceptionType={ExceptionType}", exception.GetType().Name);
            throw new OrchestrationSubmissionUncertainException("The NetRatel submission could not be confirmed because the provider connection failed.");
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
                throw new InvalidOperationException("The NetRatel response exceeded the maximum supported size.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static (bool Success, string Message) ParseHealth(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "The NetRatel health response was empty.");

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                TryGetProperty(document.RootElement, "ok", out var ok) &&
                ok.ValueKind == JsonValueKind.True)
            {
                return (true, "NetRatel health contract accepted.");
            }
        }
        catch (JsonException)
        {
            // Treat a successful HTML or plain-text response as an incompatible
            // product, never as proof that the protected provider is healthy.
            return (false, "The health response did not match the NetRatel contract.");
        }

        return (false, "The health response did not match the NetRatel contract.");
    }

    private static bool HasIdentityContract(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            return TryGetProperty(document.RootElement, "claims", out var claims) && claims.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsPositiveAcknowledgementStatus(string? status)
        => status?.Trim().ToLowerInvariant() is
            "accepted" or "queued" or "submitted" or "started" or "running" or
            "in_progress" or "in-progress" or "processing" or "success" or "succeeded" or
            "already_exists" or "already-exists" or "duplicate";

    private static bool IsNegativeAcknowledgementStatus(string? status)
        => status?.Trim().ToLowerInvariant() is
            "rejected" or "denied" or "failed" or "failure" or "error" or "invalid" or
            "cancelled" or "canceled";

    private async Task AttachAuthHeaderAsync(
        OrchestrationResolvedSettings settings,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        bool useTokenCache = true)
    {
        var accessToken = await _tokenService.GetAccessTokenAsync(settings, cancellationToken, useTokenCache);
        request.Options.Set(IntegrationSafeHttpMessageHandler.AllowPrivateHttpOption, settings.AllowPrivateHttp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static Uri BuildAbsoluteUri(string? baseUrl, string path, bool allowPrivateHttp)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("External orchestration base URL is not configured.");
        }

        var uri = new Uri(new Uri(baseUrl, UriKind.Absolute), path);
        if (!IntegrationEndpointPolicy.IsAllowed(uri, allowPrivateHttp))
            throw new InvalidOperationException("The configured NetRatel endpoint is not allowed by the outbound integration policy.");
        return uri;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private string ExtractErrorMessage(string content, OrchestrationResolvedSettings settings)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            var message = TryGetString(root, "message", "detail", "title");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return IntegrationErrorSafety.ProviderMessage(message, 500, settings.ClientSecret);
            }
        }
        catch (JsonException exception)
        {
            _logger.LogDebug("The external orchestration provider error response was not JSON. ExceptionType={ExceptionType}", exception.GetType().Name);
        }

        return "Provider returned an incompatible error response.";
    }

    private static string? TryGetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var node)
                && node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static object ToNetRatelRequest(OrchestrationIngestRequest request) => new
    {
        request.RequestId,
        request.RequestTaskId,
        request.CorrelationId,
        request.AutomationBindingId,
        NetRatelRequestDefinitionId = request.OrchestrationRequestDefinitionId,
        NetRatelJobDefinitionId = request.OrchestrationJobDefinitionId,
        request.JobName,
        request.PayloadJson,
        request.CallbackUrl,
        request.ExpectedRuntimeSeconds,
        request.GraceSeconds,
        request.HardTimeoutSeconds
    };
}
