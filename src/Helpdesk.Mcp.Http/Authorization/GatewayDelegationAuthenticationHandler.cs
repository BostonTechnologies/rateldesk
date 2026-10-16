using System.Security.Claims;
using System.Net;
using System.Text.Json;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Http.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Helpdesk.Mcp.Http.Authorization;

/// <summary>
/// Authenticates a locally paired HTTP MCP caller by exchanging its MCP-only
/// opaque credential at the configured API. The inbound credential is used
/// only for that narrow exchange and is never retained or sent to business
/// endpoints.
/// </summary>
public sealed class GatewayDelegationAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    IHttpClientFactory clients,
    IOptions<HelpdeskMcpHttpOptions> mcpOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "HelpdeskMcpGateway";
    public const string ExecutionTokenItemKey = "RatelDesk.Mcp.ExecutionToken";
    internal const string DelegationFailureItemKey = "RatelDesk.Mcp.DelegationFailure";
    public const string DelegationClientName = "Helpdesk.Mcp.Http.Delegation";
    internal const int DefaultExchangeTimeoutSeconds = 20;
    internal const int MaximumDelegationResponseBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer rdk_", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(mcpOptions.Value.DelegationTimeoutSeconds));
        try
        {
            using var delegationRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
            delegationRequest.Headers.TryAddWithoutValidation("Authorization", authorization);
            delegationRequest.Headers.TryAddWithoutValidation("X-RatelDesk-Mcp-Resource", mcpOptions.Value.PublicResourceUri.TrimEnd('/'));
            using var response = await clients.CreateClient(DelegationClientName)
                .SendAsync(delegationRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Fail(GatewayDelegationFailure.InvalidCredential);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return Fail(GatewayDelegationFailure.Forbidden);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Fail(GatewayDelegationFailure.Throttled(GetBoundedRetryAfter(response)));
            if (!response.IsSuccessStatusCode)
                return Fail(response.StatusCode >= HttpStatusCode.InternalServerError
                    ? GatewayDelegationFailure.Unavailable
                    : GatewayDelegationFailure.BadGateway);

            var delegated = await ReadDelegatedCredentialAsync(response.Content, deadline.Token).ConfigureAwait(false);
            if (delegated is null || string.IsNullOrWhiteSpace(delegated.AccessToken) || delegated.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                string.IsNullOrWhiteSpace(delegated.UserId))
            {
                return Fail(GatewayDelegationFailure.BadGateway);
            }

            Context.Items[ExecutionTokenItemKey] = delegated.AccessToken;
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, delegated.UserId),
                new(ClaimTypes.Name, delegated.Name ?? delegated.UserId),
                new("auth_mode", "mcp"),
                new("token_use", "mcp_gateway")
            };
            if (delegated.SetupVerification) claims.Add(new("setup_verification", "true"));
            if (!string.IsNullOrWhiteSpace(delegated.PrimaryOrganizationId))
                claims.Add(new Claim("organization_id", delegated.PrimaryOrganizationId));
            foreach (var permission in delegated.Permissions ?? [])
                claims.Add(new Claim("delegated_permission", permission));
            foreach (var organizationId in delegated.AllowedOrganizationIds ?? [])
                claims.Add(new Claim("allowed_organization_id", organizationId));

            var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(GatewayDelegationFailure.DeadlineExceeded);
        }
        catch (DelegationResponseException)
        {
            return Fail(GatewayDelegationFailure.BadGateway);
        }
        catch (IOException)
        {
            return Fail(GatewayDelegationFailure.BadGateway);
        }
        catch (HttpRequestException)
        {
            return Fail(GatewayDelegationFailure.Unavailable);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Response.HasStarted)
            return Task.CompletedTask;

        var failure = Context.Items[DelegationFailureItemKey] as GatewayDelegationFailure;
        Response.StatusCode = failure?.StatusCode ?? StatusCodes.Status401Unauthorized;
        if (failure?.RetryAfterSeconds is { } retryAfterSeconds)
            Response.Headers["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Response.StatusCode == StatusCodes.Status401Unauthorized)
            Response.Headers["WWW-Authenticate"] = "Bearer";
        return Task.CompletedTask;
    }

    private AuthenticateResult Fail(GatewayDelegationFailure failure)
    {
        Context.Items[DelegationFailureItemKey] = failure;
        return AuthenticateResult.Fail(failure.SafeDiagnostic);
    }

    private static int? GetBoundedRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        if (delay is null || delay <= TimeSpan.Zero)
            return null;

        return Math.Clamp((int)Math.Ceiling(delay.Value.TotalSeconds), 1, 60);
    }

    private static async Task<DelegatedCredential?> ReadDelegatedCredentialAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumDelegationResponseBytes)
            throw new DelegationResponseException();

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;

            if (buffer.Length + count > MaximumDelegationResponseBytes)
                throw new DelegationResponseException();
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return JsonSerializer.Deserialize<DelegatedCredential>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new DelegationResponseException(exception);
        }
    }

    internal sealed record GatewayDelegationFailure(int StatusCode, string SafeDiagnostic, int? RetryAfterSeconds = null)
    {
        public static GatewayDelegationFailure InvalidCredential { get; } = new(StatusCodes.Status401Unauthorized, "The MCP credential could not be delegated for this gateway.");
        public static GatewayDelegationFailure Forbidden { get; } = new(StatusCodes.Status403Forbidden, "The MCP credential is not permitted for this gateway.");
        public static GatewayDelegationFailure Unavailable { get; } = new(StatusCodes.Status503ServiceUnavailable, "The MCP delegation service is temporarily unavailable.");
        public static GatewayDelegationFailure DeadlineExceeded { get; } = new(StatusCodes.Status504GatewayTimeout, "The MCP delegation service did not respond before the gateway deadline.");
        public static GatewayDelegationFailure BadGateway { get; } = new(StatusCodes.Status502BadGateway, "The MCP gateway received an invalid delegation response.");

        public static GatewayDelegationFailure Throttled(int? retryAfterSeconds) =>
            new(StatusCodes.Status429TooManyRequests, "The MCP delegation service is throttling this request.", retryAfterSeconds);
    }

    private sealed class DelegationResponseException(Exception? innerException = null) : Exception("Invalid delegation response.", innerException);

    private sealed record DelegatedCredential(
        string AccessToken,
        DateTimeOffset ExpiresAtUtc,
        string UserId,
        string? Name,
        string? PrimaryOrganizationId,
        string[]? Permissions,
        string[]? AllowedOrganizationIds)
    { public bool SetupVerification { get; init; } }
}

/// <summary>Gets only the API-issued execution token for the active request.</summary>
public sealed class GatewayExecutionTokenProvider(IHttpContextAccessor httpContextAccessor) : IAgentAccessTokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = httpContextAccessor.HttpContext?.Items[GatewayDelegationAuthenticationHandler.ExecutionTokenItemKey] as string;
        return string.IsNullOrWhiteSpace(token)
            ? Task.FromException<string>(new AgentClientValidationException("No delegated MCP execution credential is available for this request."))
            : Task.FromResult(token);
    }
}
