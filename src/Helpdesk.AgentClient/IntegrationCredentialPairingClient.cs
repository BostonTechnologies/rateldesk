using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Helpdesk.AgentClient;

public sealed record PairingCapabilities(string Product, int ProtocolVersion, bool PairingSupported,
    string PublicOrigin, string VerificationUri, int DefaultLifetimeDays, int MaximumLifetimeDays)
{ public bool SetupVerificationSupported { get; init; } }
public sealed record PairingRequest(string Name, string Purpose, string? McpResourceUri = null)
{ public string GrantKind { get; init; } = "ordinary"; }
public sealed record PairingSession(string DeviceCode, string HumanCode, string VerificationUri,
    DateTimeOffset ExpiresAtUtc, int PollIntervalSeconds)
{ public override string ToString() => "PairingSession { deviceCapability = [REDACTED] }"; }
public sealed record PairedIntegrationCredential(Guid Id, string Prefix, string Secret, string Purpose,
    string OrganizationId, string[] Permissions, DateTimeOffset ExpiresAtUtc, string? McpResourceUri)
{
    public string GrantKind { get; init; } = "ordinary";
    public override string ToString() => "PairedIntegrationCredential { secret = [REDACTED] }";
}
public sealed record PairingPollResult(string Status, int PollIntervalSeconds, PairedIntegrationCredential? Credential, DateTimeOffset? DeliveryExpiresAtUtc = null)
{ public override string ToString() => $"PairingPollResult {{ status = {Status}, delivery = [REDACTED] }}"; }

/// <summary>Unauthenticated, bounded client for RatelDesk integration-credential pairing.</summary>
public sealed class IntegrationCredentialPairingClient : IDisposable
{
    private const string Prefix = "/api/v1/integration-credentials/pairing/";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _origin;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IntegrationCredentialPairingClient(string origin, HttpClient? client = null)
    {
        _origin = ValidateOrigin(origin);
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static Uri ValidateOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new AgentClientValidationException("Enter the instance HTTPS origin, with no credentials, path, query or fragment.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    public async Task<PairingCapabilities?> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = Deadline(cancellationToken);
        using var response = await _client.GetAsync(new Uri(_origin, Prefix + "capabilities"), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var capabilities = await ReadAsync<PairingCapabilities>(response, deadline.Token);
        if (capabilities.Product != "RatelDesk" || capabilities.ProtocolVersion != 1)
            throw new AgentClientValidationException("This endpoint does not advertise compatible RatelDesk pairing.");
        if (!capabilities.PairingSupported) return capabilities;
        if (ValidateOrigin(capabilities.PublicOrigin) != _origin)
            throw new AgentClientValidationException("The server advertises a different instance origin. Review and enter the intended origin explicitly.");
        ValidateVerificationUri(capabilities.VerificationUri);
        return capabilities;
    }

    public async Task<PairingSession> StartAsync(PairingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.GrantKind is not ("ordinary" or "setup-verification") || (request.GrantKind == "setup-verification" && request.Purpose != "mcp")) throw new AgentClientValidationException("Invalid pairing grant kind.");
        if (request.Purpose is not ("api" or "mcp")) throw new AgentClientValidationException("Pairing purpose must be api or mcp.");
        if (request.Purpose == "mcp" && request.McpResourceUri != new Uri(_origin, "/mcp").AbsoluteUri)
            throw new AgentClientValidationException("An MCP credential must bind to the reviewed instance's canonical HTTPS /mcp resource.");
        var session = await PostAsync<PairingSession>("start", request, cancellationToken);
        ValidateVerificationUri(session.VerificationUri);
        if (string.IsNullOrWhiteSpace(session.DeviceCode) || session.DeviceCode.Length > 1024 ||
            string.IsNullOrWhiteSpace(session.HumanCode) || session.HumanCode.Length > 32 ||
            session.ExpiresAtUtc <= DateTimeOffset.UtcNow || session.PollIntervalSeconds is < 1 or > 60)
            throw new AgentClientValidationException("The instance returned an invalid pairing session.");
        return session;
    }

    public async Task<PairingPollResult> PollAsync(string deviceCode, CancellationToken cancellationToken = default)
    {
        var result = await PostAsync<PairingPollResult>("poll", new { deviceCode }, cancellationToken);
        if (result.Status is not ("pending" or "slow_down" or "denied" or "expired" or "issued" or "consumed" or "cancelled") ||
            result.PollIntervalSeconds is < 1 or > 60)
            throw new AgentClientValidationException("The instance returned an invalid approval state.");
        if (result.Credential is not null && result.Status != "issued")
            throw new AgentClientValidationException("The instance returned a credential outside its approved delivery state.");
        if (result.Status == "issued" && (result.Credential is null || result.Credential.Id == Guid.Empty ||
            result.Credential.Secret?.StartsWith("rdk_", StringComparison.Ordinal) != true || result.Credential.Secret.Length > 8192 ||
            string.IsNullOrWhiteSpace(result.Credential.OrganizationId) || result.Credential.Permissions is null || result.Credential.Permissions.Length > 256))
            throw new AgentClientValidationException("The instance returned an invalid credential delivery.");
        if (result.Credential is { } credential && (credential.GrantKind is not ("ordinary" or "setup-verification") ||
            (credential.GrantKind == "setup-verification" && (credential.Purpose != "mcp" || credential.Permissions.Length != 0 ||
                credential.McpResourceUri != new Uri(_origin, "/mcp").AbsoluteUri || credential.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5)))))
            throw new AgentClientValidationException("The instance returned an invalid setup verification grant.");
        return result;
    }

    public Task AcknowledgeAsync(string deviceCode, Guid credentialId, CancellationToken cancellationToken = default) =>
        PostEmptyAsync("ack", new { deviceCode, credentialId }, cancellationToken);
    public Task CancelAsync(string deviceCode, CancellationToken cancellationToken = default) =>
        PostEmptyAsync("cancel", new { deviceCode }, cancellationToken);

    public Uri ValidateVerificationUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.GetLeftPart(UriPartial.Authority) != _origin.GetLeftPart(UriPartial.Authority) ||
            uri.AbsolutePath != "/account/integration-credentials/approve" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new AgentClientValidationException("The approval link is not the reviewed instance's trusted approval page.");
        return uri;
    }

    private async Task<T> PostAsync<T>(string endpoint, object body, CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_origin, Prefix + endpoint)) { Content = JsonContent.Create(body, options: JsonOptions) };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        return await ReadAsync<T>(response, deadline.Token);
    }

    private async Task PostEmptyAsync(string endpoint, object body, CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_origin, Prefix + endpoint)) { Content = JsonContent.Create(body, options: JsonOptions) };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new AgentClientValidationException($"The instance could not complete pairing {endpoint} (HTTP {(int)response.StatusCode}).");
    }
    private static CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new AgentClientValidationException($"The instance rejected the pairing request (HTTP {(int)response.StatusCode}).");
        if (response.Content.Headers.ContentLength > 65536)
            throw new AgentClientValidationException("The instance returned an oversized pairing response.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 65536) throw new AgentClientValidationException("The instance returned an oversized pairing response.");
            buffer.Write(chunk, 0, count);
        }
        try { return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new AgentClientValidationException("The instance returned an invalid pairing response."); }
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
