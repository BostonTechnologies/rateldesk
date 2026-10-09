using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Authentication;

public static class ServiceIdentityEndpoints
{
    public static void MapServiceIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", async (IServicePublicSettingsResolver options, HttpContext http, CancellationToken ct) =>
        {
            var settings = (await options.ResolveAsync(ct)).Identity;
            NoStore(http);
            if (!settings.Enabled) return Results.NotFound();
            return Results.Ok(new { issuer = settings.Issuer, token_endpoint = settings.ApiBaseUrl.TrimEnd('/') + "/connect/token",
                jwks_uri = settings.ApiBaseUrl.TrimEnd('/') + "/.well-known/jwks.json", grant_types_supported = new[] { "client_credentials" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post" }, scopes_supported = ServiceIdentityScopes.All,
                token_endpoint_auth_signing_alg_values_supported = Array.Empty<string>() });
        }).AllowAnonymous().WithTags("Service Identity");
        app.MapGet("/.well-known/jwks.json", async (ServiceSigningKeyStore keys, IServicePublicSettingsResolver options, HttpContext http, CancellationToken ct) =>
        {
            if (!(await options.ResolveAsync(ct)).Identity.Enabled) return Results.NotFound();
            http.Response.Headers.CacheControl = "public,max-age=60,must-revalidate";
            try { return Results.Ok(await keys.GetJwksAsync(ct)); }
            catch (ServiceSigningKeyUnavailableException) { NoStore(http); return Results.Problem(statusCode: 503, title: "Service signing key unavailable"); }
        }).AllowAnonymous().WithTags("Service Identity");
        app.MapPost("/connect/token", IssueTokenAsync).AllowAnonymous().DisableAntiforgery().WithTags("Service Identity")
            .RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);

    }

    private static async Task<IResult> IssueTokenAsync(HttpContext http, IServicePrincipalRegistry registry, ServiceAccessTokenService tokens,
        IServicePublicSettingsResolver options, CancellationToken ct)
    {
        NoStore(http);
        try
        {
            if (!(await options.ResolveAsync(ct)).Identity.Enabled) return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503);
        }
        catch (Exception exception) when (exception is ArgumentException or ServiceClientConflictException)
        { return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503); }
        if (!string.Equals(http.Request.ContentType?.Split(';', 2)[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) || http.Request.ContentLength > 8192 || http.Request.Headers.Authorization.Count > 0) return Results.BadRequest(new { error = "invalid_request" });
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 8192;
        try
        {
            var form = await http.Request.ReadFormAsync(new FormOptions { ValueCountLimit = 8, ValueLengthLimit = 2048, KeyLengthLimit = 128, BufferBodyLengthLimit = 8192 }, ct);
            if (form.Any(x => x.Value.Count != 1) || form.Count > 4 || form.Keys.Any(x => x is not ("grant_type" or "client_id" or "client_secret" or "scope"))) return Results.BadRequest(new { error = "invalid_request" });
            if (form["grant_type"] != "client_credentials") return Results.BadRequest(new { error = "unsupported_grant_type" });
            var client = await registry.AuthenticateClientAsync(form["client_id"].ToString(), form["client_secret"].ToString(), ct);
            if (client is null) return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            var scopes = form.ContainsKey("scope") ? form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries) : registry.PermittedScopes(client.Principal, client.Credential);
            if (scopes.Length == 0 || scopes.Any(x => !registry.PermittedScopes(client.Principal, client.Credential).Contains(x, StringComparer.Ordinal)) || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length) return Results.BadRequest(new { error = "invalid_scope" });
            var issued = await tokens.IssueAsync(client, scopes, ct);
            return Results.Ok(new { access_token = issued.AccessToken, token_type = "Bearer", expires_in = issued.ExpiresIn, scope = issued.Scope });
        }
        catch (InvalidDataException) { return Results.BadRequest(new { error = "invalid_request" }); }
        catch (BadHttpRequestException) { return Results.BadRequest(new { error = "invalid_request" }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_scope" }); }
        catch (ServiceSigningKeyUnavailableException) { return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503); }
        catch (ServiceClientConflictException) { return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503); }
        catch (DbUpdateException) { return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503); }
    }

    private static void NoStore(HttpContext http) { http.Response.Headers.CacheControl = "no-store"; http.Response.Headers.Pragma = "no-cache"; }
}
