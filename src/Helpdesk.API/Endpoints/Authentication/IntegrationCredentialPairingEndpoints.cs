using System.Security.Claims;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Shared.DTOs.Auth;

namespace Helpdesk.API.Endpoints.Authentication;

public static class IntegrationCredentialPairingEndpoints
{
    public static void MapIntegrationCredentialPairingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integration-credentials/pairing").WithTags("Integration credential pairing")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                try { return await next(context); }
                catch (CredentialGrantException error)
                { return error.Errors is null ? Results.Forbid() : Results.ValidationProblem(error.Errors); }
                catch (PairingRateLimitException)
                { context.HttpContext.Response.Headers.RetryAfter = "60"; return Results.StatusCode(429); }
                catch (PairingUnavailableException) { return Results.StatusCode(503); }
            });
        group.MapGet("/capabilities", (IntegrationCredentialPairingService service) => Results.Ok(service.Capabilities())).AllowAnonymous();
        group.MapPost("/start", async (IntegrationPairingStartRequest request, HttpContext http, IntegrationCredentialPairingService service, CancellationToken ct) =>
            Results.Ok(await service.StartAsync(request, Remote(http), ct))).AllowAnonymous().RequireRateLimiting("IntegrationPairing");
        group.MapPost("/poll", async (IntegrationPairingDeviceRequest request, IntegrationCredentialPairingService service, CancellationToken ct) =>
            Results.Ok(await service.PollAsync(request.DeviceCode, ct))).AllowAnonymous().RequireRateLimiting("IntegrationPairing");
        group.MapPost("/ack", async (IntegrationPairingAckRequest request, IntegrationCredentialPairingService service, CancellationToken ct) =>
            await service.AckAsync(request.DeviceCode, request.CredentialId, ct) ? Results.NoContent() : Results.Conflict())
            .AllowAnonymous().RequireRateLimiting("IntegrationPairing");
        group.MapPost("/cancel", async (IntegrationPairingDeviceRequest request, IntegrationCredentialPairingService service, CancellationToken ct) =>
        { await service.CancelAsync(request.DeviceCode, ct); return Results.NoContent(); }).AllowAnonymous().RequireRateLimiting("IntegrationPairing");
        var consent = group.MapGroup("").RequireRateLimiting("IntegrationPairing").RequireAuthorization(IntegrationCredentialEndpoints.CredentialManagementPolicy);
        consent.MapPost("/review", async (IntegrationPairingReviewRequest request, ClaimsPrincipal principal, HttpContext http,
            IntegrationCredentialPairingService service, CancellationToken ct) =>
        {
            var review = await service.ReviewAsync(request.HumanCode, principal, Remote(http), ct);
            return review is null ? Results.NotFound() : Results.Ok(review);
        });
        consent.MapPost("/approve", async (IntegrationPairingApprovalRequest request, ClaimsPrincipal principal, HttpContext http,
            IntegrationCredentialPairingService service, CancellationToken ct) =>
            await service.ApproveAsync(request, principal, Remote(http), ct) ? Results.NoContent() : Results.Conflict());
        consent.MapPost("/deny", async (IntegrationPairingReviewRequest request, ClaimsPrincipal principal, HttpContext http,
            IntegrationCredentialPairingService service, CancellationToken ct) =>
            await service.DenyAsync(request.HumanCode, principal, Remote(http), ct) ? Results.NoContent() : Results.Conflict());
    }
    private static string Remote(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
