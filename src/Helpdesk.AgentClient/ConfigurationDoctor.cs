using System.Net.Http.Headers;

namespace Helpdesk.AgentClient;

public sealed record ConfigurationDoctorCheck(string Name, bool Ok, string Message);
public sealed record ConfigurationDoctorReport(bool ConfigurationValid, IReadOnlyList<ConfigurationDoctorCheck> Checks);

/// <summary>Explicit, bounded read-only diagnostics. Inspection never creates a credential or business object.</summary>
public static class ConfigurationDoctor
{
    public static async Task<ConfigurationDoctorReport> CheckAsync(ConfigurationResolution resolution, bool network = false, bool authenticate = false, CancellationToken ct = default)
    {
        var checks = new List<ConfigurationDoctorCheck>();
        try
        {
            if (resolution.Configuration.CredentialPurpose == "mcp")
            {
                AgentClientConfigurationValidator.ValidateChoice(resolution.Configuration);
                AgentClientConfigurationValidator.ValidateHttpClientProfile(resolution.Configuration);
                if (resolution.Configuration.IntegrationCredentialFile is { } file) _ = SecureConfigurationFiles.ReadText(file, 16384, true);
            }
            else resolution.Validate();
            checks.Add(new("configuration", true, "Configuration is valid; no network check has been performed."));
        }
        catch (AgentClientValidationException exception)
        {
            checks.Add(new("configuration", false, exception.Message));
            return new(false, checks);
        }
        if (!network && !authenticate) return new(true, checks);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (resolution.Configuration.CredentialPurpose == "mcp")
            {
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 65536 };
                var oauth = resolution.Configuration.HttpClientAuthentication == "oauth";
                var endpoint = oauth
                    ? new Uri(new Uri(resolution.Configuration.ApiBaseUrl!), "/.well-known/oauth-protected-resource/mcp").AbsoluteUri
                    : resolution.Configuration.McpResourceUri;
                if (oauth && authenticate)
                    checks.Add(new("mcp-authentication", false, "OAuth sign-in is managed by the selected MCP client; authenticated initialize and a read-only tool result remain unverified."));
                if (oauth && !network) return new(true, checks);
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Accept.ParseAdd(oauth ? "application/json" : "text/event-stream");
                if (authenticate && !oauth)
                {
                    var token = resolution.Configuration.IntegrationCredential ?? SecureConfigurationFiles.ReadText(resolution.Configuration.IntegrationCredentialFile!, 16384, true).TrimEnd('\r', '\n');
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AgentClientConfigurationValidator.IntegrationSecret(token));
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                var metadataResponded = response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json";
                checks.Add(new(oauth ? "oauth-metadata-reachability" : authenticate ? "mcp-authentication" : "mcp-reachability", oauth ? metadataResponded : authenticate ? response.IsSuccessStatusCode : (int)response.StatusCode < 500,
                    $"HTTP {(int)response.StatusCode}; metadata reachability, authenticated initialize and a permitted tool call are separate validation steps."));
            }
            else
            {
                var client = new HelpdeskAgentClient(resolution.Configuration);
                if (network)
                {
                    using var reachability = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    { BaseAddress = resolution.Validate().ApiBaseUrl, Timeout = Timeout.InfiniteTimeSpan };
                    using var response = await reachability.GetAsync("/health/live", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    checks.Add(new("reachability", response.IsSuccessStatusCode, $"The live endpoint responded with HTTP {(int)response.StatusCode}."));
                }
                if (authenticate)
                {
                    await client.GetAsync("/api/v1/auth/me", true, deadline.Token).ConfigureAwait(false);
                    checks.Add(new("authentication", true, "The current account endpoint accepted the selected credential."));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { checks.Add(new("network", false, "The read-only check exceeded its 15-second deadline.")); }
        catch (AgentClientValidationException exception) { checks.Add(new("network", false, exception.Message)); }
        catch (Exception exception) when (exception is HttpRequestException or AgentClientRemoteException or System.Text.Json.JsonException)
        { checks.Add(new("network", false, "The selected endpoint did not complete the read-only check. Check reachability and current credential access.")); }
        return new(true, checks);
    }
}
