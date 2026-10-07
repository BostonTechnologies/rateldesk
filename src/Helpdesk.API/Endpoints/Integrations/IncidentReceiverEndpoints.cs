using System.Security.Claims;
using System.Text.Json;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.DTOs.Incident;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.Integrations;

public static class IncidentReceiverEndpoints
{
    public static void MapIncidentReceiverEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations/netratel").WithTags("NetRatel Incident Receiver").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!IncidentReceiverContract.TrySource(context.HttpContext.Request.Headers, out _))
                return IncidentReceiverContract.Problem(400, "invalid-source-header");
            return await next(context);
        });
        group.MapGet("/capabilities", async (HttpContext http, IIncidentReceiverAuthorization authorization,
            IncidentReceiver receiver, IOptions<StorageOptions> options, CancellationToken ct) =>
        {
            IncidentReceiverContract.TrySource(http.Request.Headers, out var sourceId);
            var approved = await authorization.ResolveAsync(http.User, sourceId, IncidentReceiverOperation.ReadReceipt, ct);
            if (approved is null) return IncidentReceiverContract.Problem(403, "source-not-authorized");
            var receiverId = await receiver.ReceiverIdAsync(ct);
            if (receiverId is null) return IncidentReceiverContract.Problem(503, "receiver-not-initialized");
            var publicSettings = http.RequestServices.GetService<IServicePublicSettingsResolver>();
            var effective = publicSettings is null ? null : await publicSettings.ResolveAsync(ct);
            // A configured receiver path prefix keeps its existing meaning. Managed setup supplies the API only when it is absent.
            var configured = string.IsNullOrWhiteSpace(options.Value.PublicApiBaseUrl) ? effective?.Identity.ApiBaseUrl : options.Value.PublicApiBaseUrl;
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var api) || api.Scheme is not ("https" or "http") ||
                api.UserInfo.Length != 0 || api.Query.Length != 0 || api.Fragment.Length != 0)
                return IncidentReceiverContract.Problem(503, "receiver-api-url-not-configured");
            var apiBase = api.AbsoluteUri.TrimEnd('/');
            return Results.Ok(new
            {
                contractVersion = IncidentReceiverContract.Version, receiverInstanceId = receiverId.Value.ToString("D"),
                sourceInstanceId = sourceId.ToString("D"), sourceNamespaceId = approved.Source.SourceNamespaceId.ToString("D"),
                organizationName = approved.Organization.Name, customerName = approved.Customer.Name,
                createEndpoint = apiBase + "/api/v1/incidents/",
                receiptEndpointTemplate = apiBase + "/api/v1/integrations/netratel/incident-receipts/{key}",
                targetValidationEndpoint = apiBase + "/api/v1/integrations/netratel/targets/validate",
                keyHeader = IncidentReceiverContract.KeyHeader, sourceHeader = IncidentReceiverContract.SourceHeader,
                maxKeyLength = 256, keyPattern = "^[A-Za-z0-9._~-]{1,256}$",
                minimumReceiptRetentionSeconds = 7776000, maximumAutomaticReplaySeconds = 2592000,
                receiptEvictionEnabled = false, atomicIncidentReceiptAndEffects = true,
                supportsReceiptLookup = true, supportsSafeSameKeyReplay = true,
                authenticationModes = (effective?.Identity.Enabled ?? http.RequestServices.GetService<IOptions<ServiceIdentityOptions>>()?.Value.Enabled) == true
                    ? new[] { "api_bearer", "oauth_client_credentials" } : new[] { "api_bearer" }
            });
        });
        // Kestrel removes literal dot segments from Request.Path. Preserve the original contract using RawTarget
        // at the two normalized landing paths; only exact canonical receipt paths with . or .. qualify.
        group.MapGet("/incident-receipts/", ReadDotSegmentAsync).ExcludeFromDescription();
        group.MapGet("/", ReadDotSegmentAsync).ExcludeFromDescription();
        group.MapGet("/incident-receipts/{key}", async (string key, HttpContext http, IncidentReceiver receiver, CancellationToken ct) =>
        {
            // Inspect the raw target as well as the decoded route value: encoded unreserved or dot/slash variants are not accepted.
            var raw = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
            var rawKey = raw?.Split('?', 2)[0].Split('/').LastOrDefault();
            if (!IncidentReceiverContract.ValidKey(key) || rawKey?.Contains('%') == true || http.Request.QueryString.HasValue)
                return IncidentReceiverContract.Problem(400, "invalid-idempotency-key");
            IncidentReceiverContract.TrySource(http.Request.Headers, out var sourceId);
            return await receiver.LookupAsync(http.User, sourceId, key, http, ct);
        });
        group.MapPost("/targets/validate", async (HttpContext http, IIncidentReceiverAuthorization authorization,
            IncidentReceiver receiver, CancellationToken ct) =>
        {
            IncidentReceiverContract.TrySource(http.Request.Headers, out var sourceId);
            var approved = await authorization.ResolveAsync(http.User, sourceId, IncidentReceiverOperation.ValidateTarget, ct);
            if (approved is null) return IncidentReceiverContract.Problem(403, "source-not-authorized");
            var dto = await ReadBoundedAsync<ValidateIncidentTargetDto>(http.Request, ct);
            if (dto is null || !IncidentReceiverContract.ValidTarget(dto))
                return IncidentReceiverContract.Problem(400, "invalid-target-request");
            if (dto.OrganizationId != approved.Organization.Id || dto.CustomerId != approved.Customer.Id)
                return IncidentReceiverContract.Problem(403, "target-not-authorized");
            var categories = IncidentReceiverContract.Categories(dto.CategoryIds);
            var error = await receiver.ValidateSelectionAsync(dto.AssignedToId, categories, approved, ct);
            if (error is not null) return error;
            var receiverId = await receiver.ReceiverIdAsync(ct);
            if (receiverId is null) return IncidentReceiverContract.Problem(503, "receiver-not-initialized");
            return Results.Ok(new
            {
                contractVersion = IncidentReceiverContract.Version, receiverInstanceId = receiverId.Value.ToString("D"),
                sourceInstanceId = sourceId.ToString("D"), sourceNamespaceId = approved.Source.SourceNamespaceId.ToString("D"), valid = true,
                mapping = new { organizationId = approved.Organization.Id, customerId = approved.Customer.Id,
                    assignedToId = string.IsNullOrWhiteSpace(dto.AssignedToId) ? null : dto.AssignedToId,
                    categoryIds = categories.Select(id => id.ToString("D")).ToArray() }
            });
        });
    }

    private static async Task<IResult> ReadDotSegmentAsync(HttpContext http, IncidentReceiver receiver, CancellationToken ct)
    {
        var raw = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
        var prefix = $"{http.Request.PathBase}/api/v1/integrations/netratel/incident-receipts/";
        var key = raw == prefix + "." ? "." : raw == prefix + ".." ? ".." : null;
        if (key is null) return Results.NotFound();
        IncidentReceiverContract.TrySource(http.Request.Headers, out var sourceId);
        return await receiver.LookupAsync(http.User, sourceId, key, http, ct);
    }

    public static async Task<T?> ReadBoundedAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType() || request.ContentLength > IncidentReceiverContract.MaximumBodyBytes) return null;
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            if (body.Length + count > IncidentReceiverContract.MaximumBodyBytes) return null;
            body.Write(buffer, 0, count);
        }
        try { return JsonSerializer.Deserialize<T>(body.ToArray(), IncidentReceiverContract.Json); }
        catch (JsonException) { return null; }
    }
}
