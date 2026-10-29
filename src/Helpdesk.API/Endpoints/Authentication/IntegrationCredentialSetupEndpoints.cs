using System.Data.Common;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Authentication;

public static class IntegrationCredentialSetupEndpoints
{
    public static void Map(RouteGroupBuilder credentials)
    {
        var group = credentials.MapGroup("").RequireAuthorization(IntegrationCredentialEndpoints.CredentialManagementPolicy);
        group.MapGet("/setup-options", () => Results.Ok(new { McpPolicyDefinitionService.Operations }));
        group.MapPost("/setup", async ([FromBody] IntegrationCredentialSetupRequest? request, HttpContext context,
            IIntegrationCredentialOwnerResolver owners, ICurrentUserAccessService access, HelpdeskDbContext db,
            RatelDeskIdentityDbContext identity, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var service = new IntegrationCredentialSetupService(owners, access, db, identity, protection,
                context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
            return await ExecuteAsync(context, async () => Results.Ok(await service.CreateAsync(request, context.User, ct)));
        }).WithSummary("Create a reviewed HTTP MCP integration and its base policy atomically");
        group.MapPost("/setup/{requestId:guid}/acknowledge", async (Guid requestId, HttpContext context,
            IIntegrationCredentialOwnerResolver owners, ICurrentUserAccessService access, HelpdeskDbContext db,
            RatelDeskIdentityDbContext identity, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var service = new IntegrationCredentialSetupService(owners, access, db, identity, protection,
                context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
            return await ExecuteAsync(context, async () => { await service.AcknowledgeAsync(requestId, context.User, ct); return Results.NoContent(); });
        }).WithSummary("Acknowledge saved integration credentials and erase protected delivery");
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (CredentialGrantException error)
        { return error.Errors is null ? Results.Forbid() : Results.ValidationProblem(error.Errors); }
        catch (IntegrationSetupException error)
        { return Results.Json(new { code = error.Code, message = error.Message, stage = error.Stage, correlationId = context.TraceIdentifier }, statusCode: error.StatusCode); }
        catch (DbUpdateException)
        { return Results.Json(new { code = "setup_in_progress", message = "Setup may have completed concurrently. Retry this same approved request.", stage = "persistence", correlationId = context.TraceIdentifier }, statusCode: 409); }
        catch (DbException)
        { return Results.Json(new { code = "setup_storage_unavailable", message = "Setup could not be completed. Retry this same approved request after the database is available.", stage = "persistence", correlationId = context.TraceIdentifier }, statusCode: 503); }
    }
}
