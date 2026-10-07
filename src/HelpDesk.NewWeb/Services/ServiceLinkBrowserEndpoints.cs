using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.ServiceLink;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;

namespace HelpDesk.NewWeb.Services;

/// <summary>The browser carries only bounded ceremony correlation. Proofs and credentials remain in the API's durable transaction.</summary>
public static class ServiceLinkBrowserEndpoints
{
    private const string Root = "/account/integration-credentials/link";
    private const string ApiRoot = "api/v1/admin/service-links";
    private const string SessionPurpose = "RatelDesk.ServiceLink.BrowserSession.v1";

    public static void MapServiceLinkBrowserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ServiceLinkContract.MetadataPath, async (HttpContext context, IHttpClientFactory clients) =>
        {
            ProtectResponse(context);
            try
            {
                using var response = await clients.CreateClient("SystemApiNoAuth").GetAsync(ServiceLinkContract.MetadataPath.TrimStart('/'),
                    HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                if (!response.IsSuccessStatusCode) return Results.StatusCode((int)response.StatusCode);
                var metadata = await ReadBoundedAsync<ServiceLinkMetadata>(response, context.RequestAborted);
                return metadata is null ? Results.StatusCode(503) : Results.Json(metadata);
            }
            catch (Exception exception) when (IsTransportFailure(exception, context)) { return Results.StatusCode(503); }
        }).AllowAnonymous();

        app.MapPost(Root + "/start", async (HttpContext context, IHttpClientFactory clients,
            IAntiforgery antiforgery, IDataProtectionProvider protection, IConfiguration configuration) =>
        {
            ProtectResponse(context);
            if (!await ValidFormAsync(context, antiforgery)) return Failure("form-expired");
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var binding = GetSessionBinding(context, protection, configuration, create: true);
            if (binding is null) return Failure("session-expired");
            var request = new ServiceLinkStartRequest(Bounded(form["peerWebBaseUrl"].ToString(), 2048),
                Bounded(form["localTenantId"].ToString(), 256), NullIfEmpty(form["requestedResponderTenantId"].ToString()), [], binding)
            {
                LocalCustomerIds = Values(form["customerIds"]), InboundScopes = Values(form["inboundScopes"]),
                OutboundScopes = Values(form["outboundScopes"])
            };
            try
            {
                using var response = await clients.CreateClient("ServiceLinkApi").PostAsJsonAsync(ApiRoot + "/start", request, context.RequestAborted);
                if (!response.IsSuccessStatusCode) return Failure(await FailureCodeAsync(response, context.RequestAborted));
                var navigation = await ReadBoundedAsync<ServiceLinkNavigation>(response, context.RequestAborted);
                return navigation is null ? Failure("needs-attention") : await PinnedNavigationAsync(context, clients, navigation, responderApproval: true);
            }
            catch (Exception exception) when (IsTransportFailure(exception, context)) { return Failure("needs-attention"); }
        }).RequireAuthorization(policy => policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin));

        app.MapPost(Root + "/continue", async (HttpContext context, IHttpClientFactory clients,
            IAntiforgery antiforgery, IDataProtectionProvider protection, IConfiguration configuration) =>
        {
            ProtectResponse(context);
            if (!await ValidFormAsync(context, antiforgery)) return Failure("form-expired");
            var binding = GetSessionBinding(context, protection, configuration, create: false);
            if (binding is null) return Failure("session-expired");
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var attempt = Bounded(form["attemptId"].ToString(), 128);
            try
            {
                using var response = await clients.CreateClient("ServiceLinkApi").PostAsJsonAsync(ApiRoot + "/attempts/" + Uri.EscapeDataString(attempt) + "/continue",
                    new ServiceLinkContinueRequest(binding), context.RequestAborted);
                if (!response.IsSuccessStatusCode) return Failure(await FailureCodeAsync(response, context.RequestAborted));
                var navigation = await ReadBoundedAsync<ServiceLinkNavigation>(response, context.RequestAborted);
                return navigation is null ? Failure("needs-attention") : await PinnedNavigationAsync(context, clients, navigation, responderApproval: true);
            }
            catch (Exception exception) when (IsTransportFailure(exception, context)) { return Failure("needs-attention"); }
        }).RequireAuthorization(policy => policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin));

        app.MapGet(Root + "/resume-sign-in", (HttpContext context, IDataProtectionProvider protection, IConfiguration configuration) =>
        {
            ProtectResponse(context);
            var name = ContinuationCookieName(configuration);
            if (!context.Request.Cookies.TryGetValue(name, out var cookie)) return Failure("session-expired");
            context.Response.Cookies.Delete(name, ContinuationCookieOptions(configuration));
            try
            {
                var target = protection.CreateProtector(SessionPurpose + ".Continuation").ToTimeLimitedDataProtector().Unprotect(cookie);
                if (!target.StartsWith(Root + "/approve?", StringComparison.Ordinal) && !target.StartsWith(Root + "/callback?", StringComparison.Ordinal))
                    return Failure("invalid-proof");
                return Results.LocalRedirect(target);
            }
            catch (CryptographicException) { return Failure("session-expired"); }
        }).RequireAuthorization();

        // Capture the correlation at the server, then remove it from the browser address before rendering consent.
        app.MapGet(Root + "/approve", async (HttpContext context, IHttpClientFactory clients,
            IDataProtectionProvider protection, IConfiguration configuration) =>
        {
            ProtectResponse(context);

            var query = context.Request.Query;
            if (!Single(query, "initiator_web_base_url", 2048, out var origin) ||
                !Single(query, "attempt_id", 128, out var attempt) || !Single(query, "browser_state", 256, out var state))
                return Failure("invalid-proof");
            if (context.User.Identity?.IsAuthenticated != true)
                return StageSignIn(context, protection, configuration, Root + "/approve" + context.Request.QueryString);
            if (!context.User.IsInRole(HelpdeskPermissions.HelpdeskAdmin)) return Failure("not-authorized");
            try
            {
                using var response = await clients.CreateClient("ServiceLinkApi").PostAsJsonAsync(ApiRoot + "/remote-review",
                    new ServiceLinkRemoteReviewRequest(origin, attempt, state), context.RequestAborted);
                if (!response.IsSuccessStatusCode) return Failure(await FailureCodeAsync(response, context.RequestAborted));
                return Results.LocalRedirect(Root + "/respond/" + Uri.EscapeDataString(attempt));
            }
            catch (Exception exception) when (IsTransportFailure(exception, context)) { return Failure("needs-attention"); }
        }).AllowAnonymous();

        app.MapGet(Root + "/callback", async (HttpContext context, IHttpClientFactory clients,
            IDataProtectionProvider protection, IConfiguration configuration) =>
        {
            ProtectResponse(context);
            var query = context.Request.Query;
            if (!Single(query, "attempt_id", 128, out var attempt) ||
                !Single(query, "pairing_code", 256, out var code) || !Single(query, "browser_state", 256, out var state) ||
                !Single(query, "responder_instance_id", 256, out var instance) || !Single(query, "oauth_issuer", 2048, out var issuer))
                return Failure("invalid-proof");
            if (context.User.Identity?.IsAuthenticated != true)
                return StageSignIn(context, protection, configuration, Root + "/callback" + context.Request.QueryString);
            if (!context.User.IsInRole(HelpdeskPermissions.HelpdeskAdmin)) return Failure("not-authorized");
            var binding = GetSessionBinding(context, protection, configuration, create: false);
            if (binding is null) return Failure("session-expired");
            try
            {
                using var response = await clients.CreateClient("ServiceLinkApi").PostAsJsonAsync(ApiRoot + "/callback",
                    new ServiceLinkCallbackRequest(attempt, code, state, instance, issuer, binding), context.RequestAborted);
                if (!response.IsSuccessStatusCode) return Failure(await FailureCodeAsync(response, context.RequestAborted));
                return Results.LocalRedirect(Root + "/review/" + Uri.EscapeDataString(attempt));
            }
            catch (Exception exception) when (IsTransportFailure(exception, context)) { return Failure("needs-attention"); }
        }).AllowAnonymous();

        app.MapPost(Root + "/confirm", async (HttpContext context, IHttpClientFactory clients,
            IAntiforgery antiforgery, IDataProtectionProvider protection, IConfiguration configuration) =>
        {
            ProtectResponse(context);
            if (!await ValidFormAsync(context, antiforgery)) return Failure("form-expired");
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var binding = GetSessionBinding(context, protection, configuration, create: false);
            var attempt = Bounded(form["attemptId"].ToString(), 128);
            if (binding is null || form["confirmed"].ToString() != "true") return Failure("invalid-proof");
            try
            {
                using var response = await clients.CreateClient("ServiceLinkApi").PostAsJsonAsync(ApiRoot + "/attempts/" + Uri.EscapeDataString(attempt) + "/approve",
                    new ServiceLinkLocalApproveRequest(Bounded(form["grantHash"].ToString(), 64), binding), context.RequestAborted);
                return !response.IsSuccessStatusCode ? Failure(await FailureCodeAsync(response, context.RequestAborted)) :
                    Results.LocalRedirect(Root + "/review/" + Uri.EscapeDataString(attempt));
            }
            catch (Exception exception) when (IsTransportFailure(exception, context)) { return Failure("needs-attention"); }
        }).RequireAuthorization(policy => policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin));

        app.MapPost(Root + "/respond", async (HttpContext context, IHttpClientFactory clients, IAntiforgery antiforgery) =>
        {
            ProtectResponse(context);
            if (!await ValidFormAsync(context, antiforgery)) return Failure("form-expired");
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var attempt = Bounded(form["attemptId"].ToString(), 128);
            try
            {
                // The client posts its reviewed descriptor hash, while grants are rebuilt from the durable pinned descriptor.
                using var statusResponse = await clients.CreateClient("ServiceLinkApi").GetAsync(ApiRoot + "/attempts/" + Uri.EscapeDataString(attempt), context.RequestAborted);
                if (!statusResponse.IsSuccessStatusCode) return Failure("invalid-proof");
                var status = await ReadBoundedAsync<ServiceLinkAdminStatus>(statusResponse, context.RequestAborted);
                if (status is null || status.Descriptor.DescriptorHash != form["descriptorHash"].ToString() || form["confirmed"].ToString() != "true")
                    return Failure("invalid-proof");
                var localTenant = Bounded(form["localTenantId"].ToString(), 256);
                var customerIds = Values(form["customerIds"]);
                var inboundScopes = Values(form["inboundScopes"]);
                var outboundScopes = Values(form["outboundScopes"]);
                var grants = status.Descriptor.RequestedGrants.Select(grant => grant.TargetProduct == "rateldesk"
                    ? grant with
                    {
                        TargetTenantId = localTenant, Scopes = inboundScopes,
                        ResourceConstraints = grant.ResourceConstraints with { OrganizationId = localTenant, CustomerIds = customerIds }
                    }
                    : grant with { CallerTenantId = localTenant, Scopes = outboundScopes }).ToArray();
                using var response = await clients.CreateClient("ServiceLinkApi").PostAsJsonAsync(ApiRoot + "/remote-approve",
                    new ServiceLinkRemoteApproveRequest(attempt, localTenant, grants, ""), context.RequestAborted);
                if (!response.IsSuccessStatusCode) return Failure(await FailureCodeAsync(response, context.RequestAborted));
                var navigation = await ReadBoundedAsync<ServiceLinkNavigation>(response, context.RequestAborted);
                return navigation is null ? Failure("needs-attention") : await PinnedNavigationAsync(context, clients, navigation, responderApproval: false);
            }
            catch (Exception exception) when (IsTransportFailure(exception, context)) { return Failure("needs-attention"); }
        }).RequireAuthorization(policy => policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin));
    }

    private static string ContinuationCookieName(IConfiguration configuration) => configuration.GetValue<bool>("Authentication:AllowInsecureLocalhost")
        ? "RatelDesk.ServiceLink.Continuation" : "__Host-RatelDesk.ServiceLink.Continuation";
    private static CookieOptions ContinuationCookieOptions(IConfiguration configuration) => new()
    {
        HttpOnly = true, Secure = !configuration.GetValue<bool>("Authentication:AllowInsecureLocalhost"),
        SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true, MaxAge = TimeSpan.FromMinutes(10)
    };
    public static string SignInDestination(HttpContext? context, IConfiguration configuration) =>
        context?.Request.Cookies.ContainsKey(ContinuationCookieName(configuration)) == true ? Root + "/resume-sign-in" : "/home";
    private static IResult StageSignIn(HttpContext context, IDataProtectionProvider protection, IConfiguration configuration, string target)
    {
        if (target.Length > 2500) return Failure("invalid-proof");
        var cookie = protection.CreateProtector(SessionPurpose + ".Continuation").ToTimeLimitedDataProtector().Protect(target, TimeSpan.FromMinutes(10));
        context.Response.Cookies.Append(ContinuationCookieName(configuration), cookie, ContinuationCookieOptions(configuration));
        return Results.LocalRedirect("/login?ReturnUrl=" + Uri.EscapeDataString(Root + "/resume-sign-in"));
    }

    private static async Task<IResult> PinnedNavigationAsync(HttpContext context, IHttpClientFactory clients, ServiceLinkNavigation navigation, bool responderApproval)
    {
        using var response = await clients.CreateClient("ServiceLinkApi").GetAsync(ApiRoot + "/attempts/" + Uri.EscapeDataString(navigation.AttemptId), context.RequestAborted);
        if (!response.IsSuccessStatusCode) return Failure("invalid-proof");
        var status = await ReadBoundedAsync<ServiceLinkAdminStatus>(response, context.RequestAborted);
        var endpoint = responderApproval ? status?.Descriptor.ResponderEndpointSnapshot.ApprovalEndpoint : status?.Descriptor.InitiatorCallbackEndpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var pinned) || !Uri.TryCreate(navigation.NavigationUrl, UriKind.Absolute, out var actual) ||
            actual.Scheme is not ("https" or "http") || actual.GetLeftPart(UriPartial.Path) != pinned.GetLeftPart(UriPartial.Path) ||
            !string.IsNullOrEmpty(actual.UserInfo) || !string.IsNullOrEmpty(actual.Fragment)) return Failure("invalid-proof");
        return Results.Redirect(actual.AbsoluteUri);
    }

    private static string? GetSessionBinding(HttpContext context, IDataProtectionProvider protection, IConfiguration configuration, bool create)
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(actor)) return null;
        var insecure = configuration.GetValue<bool>("Authentication:AllowInsecureLocalhost");
        var name = insecure ? "RatelDesk.ServiceLink" : "__Host-RatelDesk.ServiceLink";
        var protector = protection.CreateProtector(SessionPurpose).ToTimeLimitedDataProtector();
        string? random = null;
        if (context.Request.Cookies.TryGetValue(name, out var cookie))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<BrowserLinkSession>(protector.Unprotect(cookie));
                if (payload?.Actor == actor) random = payload.Random;
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException) { random = null; }
        }
        if (random is null && create)
        {
            random = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(name, protector.Protect(JsonSerializer.Serialize(new BrowserLinkSession(actor, random)), TimeSpan.FromHours(1)),
                new CookieOptions { HttpOnly = true, Secure = !insecure, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true });
        }
        return random is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actor + "\n" + random)));
    }

    public static void ProtectResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static async Task<bool> ValidFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!context.Request.HasFormContentType || context.Request.ContentLength is > 16384) return false;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = 16384;
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (Exception exception) when (exception is AntiforgeryValidationException or BadHttpRequestException or InvalidDataException) { return false; }
    }
    private static bool Single(IQueryCollection query, string key, int limit, out string value)
    {
        value = query[key].ToString();
        return query[key].Count == 1 && value.Length is > 0 && value.Length <= limit && !value.Any(char.IsControl);
    }
    private static string Bounded(string value, int limit) => value.Length <= limit && !value.Any(char.IsControl) ? value.Trim() : "";
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : Bounded(value, 256);
    private static string[] Values(Microsoft.Extensions.Primitives.StringValues values) => values.Count > 64 ? [] :
        values.Select(value => Bounded(value ?? "", 256)).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static IResult Failure(string code) => Results.LocalRedirect(Root + "/result?status=" + code);
    private static string ErrorCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Conflict => "needs-attention", HttpStatusCode.Forbidden => "not-authorized",
        HttpStatusCode.Gone => "expired", HttpStatusCode.ServiceUnavailable => "service-unavailable",
        HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => "needs-attention", _ => "invalid-proof"
    };
    private static async Task<string> FailureCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Only a small allowlisted error code enters a browser redirect, never peer text or response payloads.
        try
        {
            using var problem = await ReadBoundedAsync<JsonDocument>(response, cancellationToken);
            if (problem?.RootElement.TryGetProperty("code", out var code) == true && code.ValueKind == JsonValueKind.String)
                return code.GetString() switch
                {
                    "upgrade-required" or "unsupported-contract" or "service-link-unsupported" or "unsupported-client-authentication" => "upgrade-required",
                    "unsupported-peer" => "unsupported-peer", "deployment-owned-profile" => "deployment-managed",
                    "profile-ownership-conflict" => "profile-occupied", "source-identity-unavailable" => "source-unavailable",
                    "service-link-unavailable" => "service-unavailable", "attempt-expired" => "expired",
                    "callback-required" => "callback-required",
                    "peer-unavailable" or "peer-timeout" or "peer-operation-failed" => "needs-attention",
                    _ => ErrorCode(response.StatusCode)
                };
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) { return ErrorCode(response.StatusCode); }
        return ErrorCode(response.StatusCode);
    }
    private static bool IsTransportFailure(Exception exception, HttpContext context) => exception is HttpRequestException or JsonException or InvalidDataException ||
        exception is OperationCanceledException && !context.RequestAborted.IsCancellationRequested;
    private static async Task<T?> ReadBoundedAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int limit = 131072;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Service-link response exceeds its bound.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[limit + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (count == 0) break;
            total += count;
        }
        if (total > limit) throw new InvalidDataException("Service-link response exceeds its bound.");
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, total), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    private sealed record BrowserLinkSession(string Actor, string Random);
}
