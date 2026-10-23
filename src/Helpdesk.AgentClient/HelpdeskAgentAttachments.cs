using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helpdesk.AgentClient;

/// <summary>Bounded binary support without changing existing JSON client implementations.</summary>
public interface IHelpdeskAgentAttachmentClient
{
    Task<JsonNode?> UploadAttachmentAsync(string path, string fileName, string contentType, byte[] content, CancellationToken cancellationToken = default);
    Task<JsonNode?> DownloadAttachmentAsync(string path, CancellationToken cancellationToken = default);
    Task<JsonNode?> UploadTemplateImageAsync(string path, string fileName, string contentType, byte[] content, CancellationToken cancellationToken = default);
}

public sealed partial class HelpdeskAgentClient
{
    public Task<JsonNode?> UploadAttachmentAsync(string path, string fileName, string contentType, byte[] content, CancellationToken cancellationToken = default)
        => UploadBinaryAsync(path, "files", fileName, contentType, content, cancellationToken);

    public Task<JsonNode?> UploadTemplateImageAsync(string path, string fileName, string contentType, byte[] content, CancellationToken cancellationToken = default)
        => UploadBinaryAsync(path, "file", fileName, contentType, content, cancellationToken);

    private async Task<JsonNode?> UploadBinaryAsync(string path, string fieldName, string fileName, string contentType, byte[] content, CancellationToken cancellationToken)
    {
        if (content.Length is 0 or > MaximumResponseBytes) throw new AgentClientValidationException("Attachment content must contain between 1 byte and 1 MiB.");
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, fieldName, fileName);
        return await SendAttachmentAsync(HttpMethod.Post, path, form, cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonNode?> DownloadAttachmentAsync(string path, CancellationToken cancellationToken = default)
        => SendAttachmentAsync(HttpMethod.Get, path, null, cancellationToken);

    private async Task<JsonNode?> SendAttachmentAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var client = CreateApiClient(Configuration.Resolve());
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        var correlationId = method == HttpMethod.Post ? $"corr-{Guid.NewGuid():N}" : null;
        if (correlationId is not null) request.Headers.Add("X-Correlation-Id", correlationId);
        using var requestCancellation = CreateRequestCancellation(cancellationToken, ApiRequestTimeout);
        try
        {
            using var response = await SendAndReadHeadersAsync(client, request, requestCancellation.Token, cancellationToken,
                "api_request_timeout", "Helpdesk attachment request timed out.").ConfigureAwait(false);
            correlationId = SafeCorrelation(response) ?? correlationId;
            if (!response.IsSuccessStatusCode)
                throw new AgentClientRemoteException("api_request_failed", $"Helpdesk API request failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode, null) { CorrelationId = correlationId };
            if (method == HttpMethod.Post)
            {
                var text = await ReadResponseAsync(response.Content, requestCancellation.Token, cancellationToken,
                    "api_request_timeout", "Helpdesk attachment response timed out.").ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                throw new AgentClientRemoteException("attachment_too_large", "Attachment exceeds the 1 MiB MCP download limit.", 413, null);
            await using var stream = await response.Content.ReadAsStreamAsync(requestCancellation.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(bytes, requestCancellation.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes)
                    throw new AgentClientRemoteException("attachment_too_large", "Attachment exceeds the 1 MiB MCP download limit.", 413, null);
                buffer.Write(bytes, 0, read);
            }
            return new JsonObject
            {
                ["fileName"] = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'),
                ["contentType"] = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
                ["sizeBytes"] = buffer.Length,
                ["contentBase64"] = Convert.ToBase64String(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_applicationStopping.IsCancellationRequested && requestCancellation.IsCancellationRequested)
        {
            throw new AgentClientRemoteException("api_request_timeout", "Helpdesk attachment request timed out.", 504, null) { CorrelationId = correlationId };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new AgentClientRemoteException("api_connection_lost", "Helpdesk attachment connection ended before its response completed.", 502, null) { CorrelationId = correlationId };
        }
        catch (JsonException)
        {
            throw new AgentClientRemoteException("api_response_invalid", "Helpdesk API returned an invalid attachment response.", 502, null) { CorrelationId = correlationId };
        }
    }
}
