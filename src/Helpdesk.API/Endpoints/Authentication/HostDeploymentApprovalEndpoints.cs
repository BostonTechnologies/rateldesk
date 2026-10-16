using System.Security.Claims;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Shared.DTOs.Auth;

namespace Helpdesk.API.Endpoints.Authentication;

public static class HostDeploymentApprovalEndpoints
{
    public static void MapHostDeploymentApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integration-credentials/host-proposals").WithTags("MCP deployment approval")
            .RequireRateLimiting("IntegrationPairing")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                try { return await next(context); }
                catch (CredentialGrantException error) { return error.Errors is null ? Results.Forbid() : Results.ValidationProblem(error.Errors); }
                catch (PairingRateLimitException) { context.HttpContext.Response.Headers.RetryAfter = "60"; return Results.StatusCode(429); }
                catch (PairingUnavailableException) { return Results.StatusCode(503); }
            });
        group.MapPost("/start", async (HostDeploymentProposalRequest request, HttpContext http, HostDeploymentApprovalService service, CancellationToken ct) =>
            Results.Ok(await service.StartAsync(request, http.Connection.RemoteIpAddress?.ToString() ?? "unknown", ct))).AllowAnonymous();
        group.MapPost("/poll", async (IntegrationPairingDeviceRequest request, HostDeploymentApprovalService service, CancellationToken ct) =>
            Results.Ok(await service.PollAsync(request.DeviceCode, ct))).AllowAnonymous();
        group.MapPost("/cancel", async (IntegrationPairingDeviceRequest request, HostDeploymentApprovalService service, CancellationToken ct) =>
        { await service.CancelAsync(request.DeviceCode, ct); return Results.NoContent(); }).AllowAnonymous();
        var consent = group.MapGroup("").RequireAuthorization(IntegrationCredentialEndpoints.CredentialManagementPolicy);
        consent.MapPost("/review", async (IntegrationPairingReviewRequest request, ClaimsPrincipal principal, HttpContext http, HostDeploymentApprovalService service, CancellationToken ct) =>
        {
            var review = await service.ReviewAsync(request.HumanCode, principal, http.Connection.RemoteIpAddress?.ToString() ?? "unknown", ct);
            return review is null ? Results.NotFound() : Results.Ok(review);
        });
        consent.MapPost("/approve", async (HostDeploymentApprovalRequest request, ClaimsPrincipal principal, HostDeploymentApprovalService service, CancellationToken ct) =>
            await service.ApproveAsync(request, principal, ct) ? Results.NoContent() : Results.Conflict());
        consent.MapPost("/deny", async (IntegrationPairingReviewRequest request, ClaimsPrincipal principal, HostDeploymentApprovalService service, CancellationToken ct) =>
            await service.DenyAsync(request.HumanCode, principal, ct) ? Results.NoContent() : Results.Conflict());
    }
}
