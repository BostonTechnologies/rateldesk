using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using System.Text.Json;
using Helpdesk.Application.AiAssistant;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.AiAssistant;

public static class AiAssistantAiAssistantEndpoints
{
    public static void MapAiAssistantAiAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin/ai-assistant-webhooks").WithTags("AiAssistant Webhooks").RequireAuthorization("HelpdeskAdmin");
        admin.MapGet("/", async (string organizationId, IAiAssistantAiAssistantService service, CancellationToken ct) => Results.Ok(await service.ListConfigurationsAsync(organizationId, ct)));
        admin.MapPost("/", async (UpsertAiAssistantWebhookConfigurationDto dto, HttpContext ctx, IAiAssistantAiAssistantService service, CancellationToken ct) => await UpsertAsync(null, dto, ctx, service, ct, true));
        admin.MapPut("/{id:guid}", async (Guid id, UpsertAiAssistantWebhookConfigurationDto dto, HttpContext ctx, IAiAssistantAiAssistantService service, CancellationToken ct) => await UpsertAsync(id, dto, ctx, service, ct, false));
        admin.MapPost("/{id:guid}/state", async (Guid id, AiAssistantWebhookStateDto dto, HttpContext ctx, IAiAssistantAiAssistantService service, CancellationToken ct) => { try { await service.SetConfigurationStateAsync(id, dto.OrganizationId, dto.Enabled, dto.Archived, Actor(ctx), ct); return Results.NoContent(); } catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["organizationId"] = [ex.Message] }); } });

        var tickets = app.MapGroup("/api/v1/{ticketType}/{ticketId}/ai-assistant").WithTags("AI Assistant");
        tickets.MapGet("/eligible-configurations", async (string ticketType, string ticketId, HttpContext context, HelpdeskDbContext db, ICurrentUserAccessService accessService, IAiAssistantAiAssistantService service, CancellationToken ct) =>
        {
            var failure = await AuthorizeTicketManagementAsync(ticketType, ticketId, context.User, db, accessService, ct);
            return failure ?? Results.Ok(await service.GetEligibleAsync(ticketId, ticketType, ct));
        }).RequireAuthorization("HelpdeskStaff");
        tickets.MapPost("/investigations", async (string ticketType, string ticketId, DispatchAiInvestigationDto dto, HttpContext ctx, HelpdeskDbContext db, ICurrentUserAccessService accessService, IAiAssistantAiAssistantService service, CancellationToken ct) =>
        {
            var failure = await AuthorizeTicketManagementAsync(ticketType, ticketId, ctx.User, db, accessService, ct);
            return failure ?? await DispatchAsync(ticketType, ticketId, dto, ctx, service, ct);
        }).RequireAuthorization("HelpdeskStaff");
        tickets.MapGet("/worklog", async (string ticketType, string ticketId, HttpContext context, HelpdeskDbContext db, ICurrentUserAccessService accessService, IAiAssistantAiAssistantService service, CancellationToken ct) =>
        {
            var failure = await AuthorizeTicketManagementAsync(ticketType, ticketId, context.User, db, accessService, ct);
            return failure ?? Results.Ok(await service.GetWorklogAsync(ticketId, ticketType, ct));
        }).RequireAuthorization("HelpdeskStaff");
        tickets.MapGet("/worklog/stream", StreamAsync).RequireAuthorization("HelpdeskStaff");

        var mcp = app.MapGroup("/api/v1/ai-assistant/investigations").WithTags("AI Assistant MCP").RequireAuthorization(AiAgentOrOwnerMcpAuthenticationHandler.PolicyName);
        mcp.MapPost("/{invocationId:guid}/worklog", async (Guid invocationId, AppendAiInvestigationWorklogDto dto, IAiAssistantAiAssistantService service, CancellationToken ct) => await service.AppendMcpWorklogAsync(invocationId, dto, ct) ? Results.NoContent() : Results.BadRequest());
    }

    private static async Task StreamAsync(string ticketType, string ticketId, HttpContext context, HelpdeskDbContext db, ICurrentUserAccessService accessService, IAiInvestigationEventBus bus, CancellationToken ct)
    {
        var failure = await AuthorizeTicketManagementAsync(ticketType, ticketId, context.User, db, accessService, ct);
        if (failure is not null)
        {
            await failure.ExecuteAsync(context);
            return;
        }

        context.Response.Headers.ContentType = "text/event-stream"; context.Response.Headers.CacheControl = "no-cache"; context.Response.Headers.Connection = "keep-alive";
        var reader = bus.Subscribe(ticketId);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await LocalSessionValidator.IsValidAsync(context, ct) ||
                    await AuthorizeTicketManagementAsync(ticketType, ticketId, context.User, db, accessService, ct) is not null)
                    return;

                while (reader.TryRead(out var item))
                {
                    if (!await LocalSessionValidator.IsValidAsync(context, ct) ||
                        await AuthorizeTicketManagementAsync(ticketType, ticketId, context.User, db, accessService, ct) is not null)
                        return;
                    await context.Response.WriteAsync($"event: ai-worklog\ndata: {JsonSerializer.Serialize(item)}\n\n", ct);
                }
                await context.Response.WriteAsync(": keepalive\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wake.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await reader.WaitToReadAsync(wake.Token)) return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }
        finally { bus.Unsubscribe(ticketId, reader); }
    }
    private static async Task<IResult> UpsertAsync(Guid? id, UpsertAiAssistantWebhookConfigurationDto dto, HttpContext context, IAiAssistantAiAssistantService service, CancellationToken ct, bool isCreate)
    {
        try
        {
            var configuration = await service.UpsertConfigurationAsync(id, dto, Actor(context), ct);
            return isCreate ? Results.Created($"/api/v1/admin/ai-assistant-webhooks/{configuration.Id}", configuration) : Results.Ok(configuration);
        }
        catch (ArgumentException ex)
        {
            var field = ex.Message.Contains("tenant", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("organization", StringComparison.OrdinalIgnoreCase)
                ? "organizationId"
                : "endpoint";
            return Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [ex.Message] });
        }
    }
    private static async Task<IResult> DispatchAsync(string ticketType, string ticketId, DispatchAiInvestigationDto dto, HttpContext context, IAiAssistantAiAssistantService service, CancellationToken ct)
    {
        try
        {
            var invocation = await service.DispatchAsync(ticketId, ticketType, dto, Actor(context), ct);
            return Results.Accepted($"/api/v1/{ticketType}/{ticketId}/ai-assistant/investigations", invocation);
        }
        catch (AiInvestigationDispatchConflictException ex)
        {
            return Results.Conflict(new { code = "ai_investigation_changed", message = ex.Message });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["operatorAssistanceRequest"] = [ex.Message] });
        }
    }

    private static async Task<IResult?> AuthorizeTicketManagementAsync(
        string ticketType,
        string ticketId,
        ClaimsPrincipal user,
        HelpdeskDbContext db,
        ICurrentUserAccessService accessService,
        CancellationToken ct)
    {
        var ticket = await db.Tickets.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == ticketId, ct);
        if (ticket is null || !MatchesTicketType(ticket, ticketType))
        {
            return Results.NotFound();
        }

        var access = await accessService.ResolveAsync(user, ct);
        var canManage = ticket switch
        {
            Incident => access.CanManageIncident(ticket.OrganizationId),
            Request => access.CanManageRequest(ticket.OrganizationId),
            Change => access.CanManageChange(ticket.OrganizationId),
            _ => false
        };
        return canManage ? null : Results.Forbid();
    }

    private static bool MatchesTicketType(Ticket ticket, string ticketType) =>
        ticketType.ToLowerInvariant() switch
        {
            "incidents" => ticket is Incident,
            "requests" => ticket is Request,
            "changes" => ticket is Change,
            _ => false
        };
    private static string Actor(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub") ?? "unknown";
}
