using System.Net;
using System.Text.Json;

namespace Helpdesk.AgentClient;

public sealed record OAuthProtectedResourceMetadata(string Resource, string[] AuthorizationServers, string[]? ScopesSupported = null);

/// <summary>Read-only discovery for an external MCP client's OAuth configuration. No application credentials are sent.</summary>
public sealed class OAuthProtectedResourceClient(string origin, HttpClient? client = null) : IDisposable
{
    private readonly Uri _origin = IntegrationCredentialPairingClient.ValidateOrigin(origin);
    private readonly HttpClient _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly bool _ownsClient = client is null;

    public async Task<OAuthProtectedResourceMetadata> DiscoverAsync(IEnumerable<string>? expectedAuthorities = null, CancellationToken cancellationToken = default)
    {
        if (_client.DefaultRequestHeaders.Authorization is not null)
            throw new AgentClientValidationException("Protected-resource discovery must not send an existing application credential.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _client.GetAsync(new Uri(_origin, "/.well-known/oauth-protected-resource/mcp"), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != HttpStatusCode.OK) throw new AgentClientValidationException("The selected instance does not advertise external OAuth MCP configuration at its canonical protected-resource endpoint.");
        if (response.Content.Headers.ContentLength > 65536) throw new AgentClientValidationException("Protected-resource metadata is oversized.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(); var chunk = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(chunk, deadline.Token)) > 0)
        {
            if (buffer.Length + count > 65536) throw new AgentClientValidationException("Protected-resource metadata is oversized.");
            buffer.Write(chunk, 0, count);
        }
        OAuthProtectedResourceMetadata metadata;
        try { metadata = JsonSerializer.Deserialize<OAuthProtectedResourceMetadata>(buffer.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException(); }
        catch (JsonException) { throw new AgentClientValidationException("The selected instance returned invalid protected-resource metadata."); }
        if (metadata.Resource != new Uri(_origin, "/mcp").AbsoluteUri || metadata.AuthorizationServers is not { Length: > 0 and <= 8 } ||
            metadata.AuthorizationServers.Any(authority => !Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) ||
            metadata.AuthorizationServers.Distinct(StringComparer.Ordinal).Count() != metadata.AuthorizationServers.Length)
            throw new AgentClientValidationException("Protected-resource metadata must bind this exact HTTPS /mcp resource to explicit HTTPS authorization servers.");
        if (expectedAuthorities is { } expected && expected.Any() && !expected.Order(StringComparer.Ordinal).SequenceEqual(metadata.AuthorizationServers.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new AgentClientValidationException("The instance advertises different OAuth authorities than the imported client configuration. Review a separate reconfiguration explicitly.");
        return metadata;
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
