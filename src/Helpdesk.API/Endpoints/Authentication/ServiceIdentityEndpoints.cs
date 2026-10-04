using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.Authentication;

public sealed record RotateServiceClientRequest(long ExpectedCredentialRevision);

public static class ServiceIdentityEndpoints
{
    public static void MapServiceIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", (IOptionsMonitor<ServiceIdentityOptions> options, HttpContext http) =>
        {
            var settings = options.CurrentValue;
            NoStore(http);
            if (!settings.Enabled) return Results.NotFound();
            return Results.Ok(new { issuer = settings.Issuer, token_endpoint = settings.ApiBaseUrl.TrimEnd('/') + "/connect/token",
                jwks_uri = settings.ApiBaseUrl.TrimEnd('/') + "/.well-known/jwks.json", grant_types_supported = new[] { "client_credentials" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post" }, scopes_supported = ServiceIdentityScopes.All,
                token_endpoint_auth_signing_alg_values_supported = Array.Empty<string>() });
        }).AllowAnonymous().WithTags("Service Identity");
        app.MapGet("/.well-known/jwks.json", async (ServiceSigningKeyStore keys, IOptionsMonitor<ServiceIdentityOptions> options, HttpContext http, CancellationToken ct) =>
        {
            if (!options.CurrentValue.Enabled) return Results.NotFound();
            http.Response.Headers.CacheControl = "public,max-age=60,must-revalidate";
            try { return Results.Ok(await keys.GetJwksAsync(ct)); }
            catch (ServiceSigningKeyUnavailableException) { NoStore(http); return Results.Problem(statusCode: 503, title: "Service signing key unavailable"); }
        }).AllowAnonymous().WithTags("Service Identity");
        app.MapPost("/connect/token", IssueTokenAsync).AllowAnonymous().DisableAntiforgery().WithTags("Service Identity")
            .RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);

        var group = app.MapGroup("/api/v1/admin/service-clients").RequireAuthorization(ServiceIdentityServiceCollectionExtensions.ManagementPolicy).WithTags("Service Clients");
        group.MapGet("/", async (IServicePrincipalRegistry registry, HttpContext http, CancellationToken ct) =>
        {
            NoStore(http);
            try { return Results.Ok(await registry.ListAsync(ct)); }
            catch (ServiceClientConflictException ex) { return Conflict(ex.Message); }
        });
        group.MapPost("/", async ([FromBody] ServiceClientCreateRequest request, IServicePrincipalRegistry registry, IIntegrationCredentialOwnerResolver owners,
            ClaimsPrincipal principal, IOptionsMonitor<ServiceIdentityOptions> options, HttpContext http, CancellationToken ct) =>
        {
            NoStore(http);
            // Link clients are created solely by the bound consent state machine, never by a manual body.
            if (request.LinkId is not null || request.AttemptId is not null || request.GrantHash is not null || request.DescriptorHash is not null || request.DirectionId is not null) return Results.BadRequest(new { code = "invalid-client-request", error = "Reciprocal clients require the guided consent workflow." });
            try
            {
                var owner = await owners.ResolveAsync(principal, ct);
                if (owner is null) return Results.Forbid();
                var created = await registry.CreateAsync(request, owner.UserId, ct: ct);
                return Results.Created($"/api/v1/admin/service-clients/{created.Principal.Id:D}", Reveal(created, options.CurrentValue));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "invalid-client-request", error = ex.Message }); }
            catch (ServiceClientConflictException ex) { return Conflict(ex.Message); }
            catch (DbUpdateConcurrencyException) { return Conflict("The registration changed; reload it."); }
            catch (DbUpdateException) { return Conflict("The client or source identity already exists."); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 503, title: ex.Message); }
        }).RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);
        group.MapPost("/{id:guid}/rotate", async (Guid id, [FromBody] RotateServiceClientRequest request, IServicePrincipalRegistry registry,
            IOptionsMonitor<ServiceIdentityOptions> options, HttpContext http, CancellationToken ct) =>
        {
            NoStore(http);
            try { return Results.Ok(Reveal(await registry.RotateAsync(id, request.ExpectedCredentialRevision, ct), options.CurrentValue)); }
            catch (ServiceClientConflictException ex) { return Conflict(ex.Message); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateException) { return Conflict("A concurrent credential change won; reload the registration."); }
        }).RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);
        group.MapPost("/{id:guid}/revoke", async (Guid id, IServicePrincipalRegistry registry, HttpContext http, CancellationToken ct) =>
        {
            NoStore(http);
            try { await registry.RevokeAsync(id, ct); return Results.Ok(new { id, status = "revoked" }); }
            catch (ServiceClientConflictException ex) { return Conflict(ex.Message); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Conflict("The registration changed; reload it."); }
        });
        group.MapPost("/signing-keys/rotate", async (ServiceSigningKeyStore keys, HttpContext http, CancellationToken ct) =>
        {
            NoStore(http);
            try { return Results.Ok(new { kid = await keys.RotateAsync(ct) }); }
            catch (ServiceSigningKeyUnavailableException) { return Results.Problem(statusCode: 503, title: "Service signing key unavailable"); }
            catch (DbUpdateException) { return Conflict("A concurrent signing-key rotation won; reload the public keys."); }
        });
    }

    private static async Task<IResult> IssueTokenAsync(HttpContext http, IServicePrincipalRegistry registry, ServiceAccessTokenService tokens,
        IOptionsMonitor<ServiceIdentityOptions> options, CancellationToken ct)
    {
        NoStore(http);
        if (!options.CurrentValue.Enabled) return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503);
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

    private static object Reveal(CreatedServiceClient created, ServiceIdentityOptions options) => new
    {
        client = ServicePrincipalRegistry.Metadata(created.Principal, created.CredentialExpiresAtUtc) with { CredentialRevision = created.CredentialRevision },
        clientSecret = created.ClientSecret, issuer = options.Issuer, tokenEndpoint = options.ApiBaseUrl.TrimEnd('/') + "/connect/token", audience = options.Audience,
        scopes = ServicePrincipalRegistry.ReadArray(created.Principal.AllowedScopesJson),
        dockerEnvironmentExample = $"# RatelDesk deployment-managed provisioning alternative: use a DISTINCT client ID to avoid shadowing this web-managed client.\nServiceIdentity__Enabled=true\nServiceIdentity__Issuer={options.Issuer}\nServiceIdentity__ApiBaseUrl={options.ApiBaseUrl}\nServiceIdentity__WebBaseUrl={options.WebBaseUrl}\nServiceIdentity__Audience={options.Audience}\nServiceIdentity__InstanceId={options.InstanceId}\nServiceIdentity__Clients__0__ClientId=<distinct-deployment-client-id>\nServiceIdentity__Clients__0__ClientSecret=<new-independent-random-secret>\nServiceIdentity__Clients__0__OrganizationId={created.Principal.OrganizationId}\nServiceIdentity__Clients__0__PeerInstanceId={created.Principal.PeerInstanceId}\nServiceIdentity__Clients__0__PeerTenantId={created.Principal.PeerTenantId}\n" + string.Join('\n', ServicePrincipalRegistry.ReadArray(created.Principal.AllowedScopesJson).Select((scope, i) => $"ServiceIdentity__Clients__0__Scopes__{i}={scope}")) + "\n" + string.Join('\n', ServicePrincipalRegistry.ReadArray(created.Principal.CustomerIdsJson).Select((customer, i) => $"ServiceIdentity__Clients__0__CustomerIds__{i}={customer}")) + (created.Principal.SourceInstanceId is null ? "" : $"\nServiceIdentity__Clients__0__SourceInstanceId={created.Principal.SourceInstanceId:D}") + $"\nServiceIdentity__Clients__0__ResourceConstraintsJson='{created.Principal.ResourceConstraintsJson}'"
    };
    private static IResult Conflict(string message) => Results.Conflict(new { code = "service-client-conflict", error = message });
    private static void NoStore(HttpContext http) { http.Response.Headers.CacheControl = "no-store"; http.Response.Headers.Pragma = "no-cache"; }
}
