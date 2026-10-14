using Microsoft.Extensions.Hosting;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helpdesk.AgentClient;

public interface IHelpdeskAgentClient
{
    AgentClientConfiguration Configuration { get; }
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(string path, bool authenticated = true, CancellationToken cancellationToken = default);
    Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, bool authenticated = true, CancellationToken cancellationToken = default);
    Task<JsonArray> GetHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Supplies an API access token for a transport that has already authenticated
/// the current caller. Implementations must be request-scoped in behavior;
/// they must not retain an inbound caller credential in a shared client.
/// </summary>
public interface IAgentAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls the Helpdesk API using immutable, deployment-owned configuration.
/// It never inspects inbound HTTP context or forwards inbound credentials.
/// </summary>
public sealed class HelpdeskAgentClient : IHelpdeskAgentClient
{
    public const string ApiHttpClientName = "Helpdesk.AgentClient.Api";
    public const string AuthHttpClientName = "Helpdesk.AgentClient.Auth";
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AuthRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly CancellationToken _applicationStopping;
    private readonly IAgentAccessTokenProvider? _accessTokenProvider;
    private readonly object _tokenRefreshLock = new();
    private TokenCacheEntry? _tokenCache;
    private Task<TokenCacheEntry>? _tokenRefresh;

    public HelpdeskAgentClient(AgentClientConfiguration configuration, Func<HttpMessageHandler>? handlerFactory = null)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _handlerFactory = handlerFactory;
    }

    public HelpdeskAgentClient(
        AgentClientConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IHostApplicationLifetime applicationLifetime,
        IAgentAccessTokenProvider? accessTokenProvider = null)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _applicationStopping = applicationLifetime?.ApplicationStopping ?? throw new ArgumentNullException(nameof(applicationLifetime));
        _accessTokenProvider = accessTokenProvider;
    }

    public AgentClientConfiguration Configuration { get; }

    public Task<JsonNode?> GetAsync(string path, bool authenticated = true, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, path, null, authenticated, cancellationToken);

    public async Task<JsonNode?> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body = null,
        bool authenticated = true,
        CancellationToken cancellationToken = default)
    {
        var configuration = Configuration.Resolve();
        using var client = CreateApiClient(configuration);
        using var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        if (authenticated)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));

        using var requestCancellation = CreateRequestCancellation(cancellationToken, ApiRequestTimeout);
        using var response = await SendAndReadHeadersAsync(
            client,
            request,
            requestCancellation.Token,
            cancellationToken,
            "api_request_timeout",
            "Helpdesk API request timed out.").ConfigureAwait(false);
        var text = await ReadResponseAsync(response.Content, requestCancellation.Token, cancellationToken, "api_request_timeout", "Helpdesk API response timed out.").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentClientRemoteException("api_request_failed", $"Helpdesk API request failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode, null);

        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public async Task<JsonArray> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        // Bound the entire diagnostic, including token acquisition and response bodies.
        // Independent API timeouts would otherwise accumulate beyond the MCP budget.
        using var healthCancellation = CreateRequestCancellation(cancellationToken, HealthRequestTimeout);
        var values = new JsonArray();
        foreach (var (name, path, authenticated) in new[]
                 {
                     ("live", "/health/live", false),
                     ("ready", "/health/ready", false),
                     ("auth", UsesCurrentUserEndpoint(Configuration.Resolve().CredentialMode) ? "/api/v1/auth/me" : "/api/v1/auth/ai-agent/status", true)
                 })
        {
            try
            {
                values.Add(new JsonObject
                {
                    ["name"] = name,
                    ["success"] = true,
                    ["data"] = await GetAsync(path, authenticated, healthCancellation.Token).ConfigureAwait(false)
                });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_applicationStopping.IsCancellationRequested && healthCancellation.IsCancellationRequested)
            {
                values.Add(new JsonObject
                {
                    ["name"] = name,
                    ["success"] = false,
                    ["statusCode"] = 504,
                    ["error"] = "health_check_timeout"
                });
            }
            catch (AgentClientRemoteException exception)
            {
                values.Add(new JsonObject
                {
                    ["name"] = name,
                    ["success"] = false,
                    ["statusCode"] = exception.StatusCode,
                    ["error"] = exception.Code
                });
            }
        }

        return values;
    }

    private static bool UsesCurrentUserEndpoint(string credentialMode) =>
        string.Equals(credentialMode, "integration", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(credentialMode, "gateway", StringComparison.OrdinalIgnoreCase);

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_accessTokenProvider is not null)
            return await _accessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var configuration = Configuration.Resolve();
        if (string.Equals(configuration.CredentialMode, "integration", StringComparison.Ordinal))
            return configuration.IntegrationCredential!;
        var cached = Volatile.Read(ref _tokenCache);
        if (HasUsableToken(cached)) return cached!.Value;

        Task<TokenCacheEntry> refresh;
        TaskCompletionSource<TokenCacheEntry>? refreshCompletion = null;
        lock (_tokenRefreshLock)
        {
            cached = _tokenCache;
            if (HasUsableToken(cached)) return cached!.Value;
            if (_tokenRefresh is null)
            {
                refreshCompletion = new TaskCompletionSource<TokenCacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tokenRefresh = refreshCompletion.Task;
            }
            refresh = _tokenRefresh;
        }

        if (refreshCompletion is not null)
            _ = CompleteAccessTokenRefreshAsync(refreshCompletion, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return (await refresh.WaitAsync(cancellationToken).ConfigureAwait(false)).Value;
    }

    private async Task CompleteAccessTokenRefreshAsync(TaskCompletionSource<TokenCacheEntry> refreshCompletion, CancellationToken cancellationToken)
    {
        try
        {
            refreshCompletion.TrySetResult(await RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException exception)
        {
            refreshCompletion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            refreshCompletion.TrySetException(exception);
        }
        finally
        {
            lock (_tokenRefreshLock)
            {
                if (ReferenceEquals(_tokenRefresh, refreshCompletion.Task))
                    _tokenRefresh = null;
            }
        }
    }

    private async Task<TokenCacheEntry> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        var configuration = Configuration.Resolve();
        if (!string.Equals(configuration.CredentialMode, "authentik", StringComparison.Ordinal))
            throw new AgentClientValidationException("Only the Authentik credential provider can mint an access token.");
        using var client = CreateAuthClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.AuthentikTokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = configuration.AuthentikClientId!,
                ["username"] = configuration.AuthentikUsername!,
                ["password"] = configuration.AuthentikAppPassword!,
                ["scope"] = configuration.AuthentikScope!
            })
        };
        using var requestCancellation = CreateRequestCancellation(cancellationToken, AuthRequestTimeout);
        using var response = await SendAndReadHeadersAsync(
            client,
            request,
            requestCancellation.Token,
            cancellationToken,
            "auth_token_timeout",
            "Authentik token mint timed out.").ConfigureAwait(false);
        var text = await ReadResponseAsync(response.Content, requestCancellation.Token, cancellationToken, "auth_token_timeout", "Authentik token response timed out.").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentClientRemoteException("auth_token_failed", $"Authentik token mint failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode, null);

        using var document = JsonDocument.Parse(text);
        var token = document.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(token))
            throw new AgentClientValidationException("Authentik token response did not include access_token.");

        var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? Math.Max(seconds, 60)
            : 300;
        var refreshed = new TokenCacheEntry(token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        Volatile.Write(ref _tokenCache, refreshed);
        return refreshed;
    }

    private static bool HasUsableToken(TokenCacheEntry? cache)
        => cache is not null && cache.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1);

    private HttpClient CreateApiClient(ResolvedAgentClientConfiguration configuration)
    {
        if (_httpClientFactory is not null) return _httpClientFactory.CreateClient(ApiHttpClientName);

        var client = CreateFallbackClient();
        client.BaseAddress = configuration.ApiBaseUrl;
        client.Timeout = ApiRequestTimeout;
        return client;
    }

    private HttpClient CreateAuthClient()
    {
        if (_httpClientFactory is not null) return _httpClientFactory.CreateClient(AuthHttpClientName);

        var client = CreateFallbackClient();
        client.Timeout = AuthRequestTimeout;
        return client;
    }

    private HttpClient CreateFallbackClient()
        => _handlerFactory is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(_handlerFactory(), disposeHandler: true);

    private CancellationTokenSource CreateRequestCancellation(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _applicationStopping);
        linked.CancelAfter(timeout);
        return linked;
    }

    private async Task<HttpResponseMessage> SendAndReadHeadersAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken requestCancellation,
        CancellationToken callerCancellation,
        string timeoutCode,
        string timeoutMessage)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested && !_applicationStopping.IsCancellationRequested)
        {
            throw new AgentClientRemoteException(timeoutCode, timeoutMessage, 504, null);
        }
    }

    private async Task<string> ReadResponseAsync(
        HttpContent content,
        CancellationToken requestCancellation,
        CancellationToken callerCancellation,
        string timeoutCode,
        string timeoutMessage)
    {
        try
        {
            return await ReadBoundedContentAsync(content, requestCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested && !_applicationStopping.IsCancellationRequested)
        {
            throw new AgentClientRemoteException(timeoutCode, timeoutMessage, 504, null);
        }
    }

    private static async Task<string> ReadBoundedContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new AgentClientRemoteException("api_response_too_large", "Helpdesk API response exceeded the maximum supported size.", 502, null);
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private sealed record TokenCacheEntry(string Value, DateTimeOffset ExpiresAt);
}

public sealed record ConfirmationDetails(string ConfirmField, bool RequiredValue, string Operation, IReadOnlyList<string> AffectedIds);
public static class MutationPolicy
{
    public static bool RequiresConfirmation(string operation) => operation is not ("get" or "list" or "status" or "show" or "search" or "summary" or "capabilities" or "schema" or "enums");
    public static ConfirmationDetails Confirmation(string operation, params string[] ids) => new("confirm", true, operation, ids);
}
