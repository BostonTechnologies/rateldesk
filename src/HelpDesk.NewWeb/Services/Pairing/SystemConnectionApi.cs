using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Shared.Pairing;

namespace HelpDesk.NewWeb.Services.Pairing;

public sealed class SystemConnectionApi(IHttpClientFactory clients)
{
    private const string Root = "api/v1/admin/system-connections";
    private HttpClient Api => clients.CreateClient("SystemPairingApi");

    public Task<List<PairingConnectionDto>> ListAsync(CancellationToken ct) => SendAsync<List<PairingConnectionDto>>(HttpMethod.Get, Root + "/", null, ct);
    public Task<PairingCodeDto> GenerateAsync(CancellationToken ct) => SendAsync<PairingCodeDto>(HttpMethod.Post, Root + "/code", null, ct);
    public Task<PairingConnectionDto> PairAsync(PairingConnectRequest request, CancellationToken ct) => SendAsync<PairingConnectionDto>(HttpMethod.Post, Root + "/pair", request, ct);
    public Task<PairingSetupDirectory> DirectoryAsync(string pairId, CancellationToken ct) => SendAsync<PairingSetupDirectory>(HttpMethod.Get, Root + "/" + Part(pairId) + "/directory", null, ct);
    public Task<PairingConnectionDto> SaveAsync(PairingMapping mapping, CancellationToken ct) => SendAsync<PairingConnectionDto>(HttpMethod.Put, MappingPath(mapping.PairId, mapping.Id), mapping, ct);
    public Task<PairingTestResult> TestAsync(PairingMapping mapping, CancellationToken ct) => SendAsync<PairingTestResult>(HttpMethod.Post, MappingPath(mapping.PairId, mapping.Id) + "/test", null, ct);
    public async Task DeleteAsync(PairingConnectionDto connection, CancellationToken ct)
    {
        var path = connection.Mapping is { } mapping ? MappingPath(connection.PairId, mapping.Id) : Root + "/" + Part(connection.PairId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await Api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, ct);
    }

    public static string? NormalizeAddress(string address)
    {
        var value = address.Trim().TrimEnd('/');
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
               !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/"
            ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    private static string Part(string value) => Uri.EscapeDataString(value);
    private static string MappingPath(string pairId, string id) => Root + "/" + Part(pairId) + "/mappings/" + Part(id);
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await Api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, ct);
        await response.Content.LoadIntoBufferAsync(256 * 1024, ct);
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new JsonException("The connection service returned an empty response.");
    }

    private static async Task<SystemConnectionException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? message = null, reference = null;
        try
        {
            await response.Content.LoadIntoBufferAsync(16 * 1024, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            message = Read(root, "message") ?? Read(root, "detail");
            reference = Read(root, "reference") ?? Read(root, "traceId") ?? Read(root, "code");
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException) { }
        message = string.IsNullOrWhiteSpace(message) ? response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Sign in again, then retry this action.",
            System.Net.HttpStatusCode.Forbidden => "Your account no longer has permission for this connection or selected tenant.",
            System.Net.HttpStatusCode.TooManyRequests => "Too many pairing requests. Wait briefly, then retry.",
            _ => $"The connection service could not complete this action (HTTP {(int)response.StatusCode}). Retry or review the selected address and mapping."
        } : message;
        reference = reference is { Length: <= 128 } && reference.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.') ? reference : $"HTTP-{(int)response.StatusCode}";
        return new SystemConnectionException(message, reference);
    }

    private static string? Read(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 1500 } text ? text : null;
}

public sealed class SystemConnectionException(string message, string reference) : Exception(message)
{
    public string Reference { get; } = reference;
}
