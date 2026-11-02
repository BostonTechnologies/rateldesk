using System.Security.Claims;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.AiOps;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.AiAssistant.Chat;

public static partial class AiAssistantChatEndpoints
{
    private static bool CanReadOrganization(CurrentUserAccessProfile access, string org) => CanWriteOrganization(access, org)
        || access.HasPermission(HelpdeskPermissions.IncidentRead, org) || access.HasPermission(HelpdeskPermissions.RequestRead, org)
        || access.HasPermission(HelpdeskPermissions.ChangeRead, org);
    private static bool CanWriteOrganization(CurrentUserAccessProfile access, string org) =>
        access.CanManageIncident(org) || access.CanManageRequest(org) || access.CanManageChange(org);

    private static async Task<IResult?> AuthorizeOrganizationAsync(string organization, bool write, ClaimsPrincipal user,
        HelpdeskDbContext db, ICurrentUserAccessService accessService, CancellationToken ct)
    {
        var access = await accessService.ResolveAsync(user, ct);
        if (!access.IsAuthenticated || !(write ? CanWriteOrganization(access, organization) : CanReadOrganization(access, organization))) return Results.Forbid();
        return await db.Organizations.IgnoreQueryFilters().AsNoTracking().AnyAsync(x => x.Id == organization && x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct)
            ? null : Results.NotFound();
    }

    private static void MapOrganizationChatEndpoints(IEndpointRouteBuilder app)
    {
        // Read-only staff admission is checked against current organization grants below.
        // Existing ticket HelpdeskStaff authorization is unchanged.
        var root = app.MapGroup("/api/v1/ai/chat").RequireAuthorization().WithTags("AiAssistant Chat");
        root.MapGet("/organizations", async (HttpContext ctx, ICurrentUserAccessService accessService, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(ctx.User, ct);
            var orgs = access.OrganizationIdsForAny(HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentManager,
                HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager,
                HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeManager).ToArray();
            var rows = await db.Organizations.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.State == Helpdesk.Shared.Models.EntityState.Enabled && (access.IsHelpdeskAdmin || orgs.Contains(x.Id)))
                .OrderBy(x => x.Name).Select(x => new AiOpsOrganizationDto(x.Id, x.Name)).ToListAsync(ct);
            return Results.Ok(rows.Where(x => CanReadOrganization(access, x.Id)));
        });
        var group = root.MapGroup("/organizations/{organizationId}");
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var services = ctx.HttpContext.RequestServices;
            var db = services.GetRequiredService<HelpdeskDbContext>();
            var org = ctx.HttpContext.Request.RouteValues["organizationId"]!.ToString()!;
            var failure = await AuthorizeOrganizationAsync(org, !HttpMethods.IsGet(ctx.HttpContext.Request.Method), ctx.HttpContext.User,
                db, services.GetRequiredService<ICurrentUserAccessService>(), ctx.HttpContext.RequestAborted);
            if (failure is not null) return failure;
            if (!ctx.HttpContext.Request.Path.Value!.EndsWith("/capabilities", StringComparison.Ordinal)
                && (!db.Database.IsNpgsql() || !services.GetRequiredService<IAiAssistantChatRuntimeState>().Current.Enabled))
                return Results.Problem("Organization chat requires PostgreSQL and an enabled Netclaw connection.", statusCode: 503);
            try { return await next(ctx); }
            catch (ChatConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        group.MapGet("/capabilities", async (string organizationId, HttpContext ctx, HelpdeskDbContext db, ICurrentUserAccessService accessService, IAiAssistantChatRuntimeState runtime, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(ctx.User, ct);
            return new ChatCapabilities(db.Database.IsNpgsql() && runtime.Current.Enabled,
                !db.Database.IsNpgsql() ? "Organization chat requires PostgreSQL. Ticket webhook assistance remains available on SQLite."
                : !runtime.Current.Enabled ? "Connect and enable Netclaw to use organization chat." : null,
                CanWriteOrganization(access, organizationId), access.IsHelpdeskAdmin ? "/admin/automation/integration/netclaw" : null);
        });
        group.MapGet("/history", (string organizationId, int? skip, string? q, bool? archived, IAiAssistantChatStore store, CancellationToken ct) =>
            store.HistoryAsync(ChatScope.Organization(organizationId), skip ?? 0, q, archived ?? true, ct));
        group.MapGet("/conversations/{id:guid}", (string organizationId, Guid id, IAiAssistantChatStore store, CancellationToken ct) => store.GetOrganizationAsync(organizationId, id, ct));
        group.MapGet("", (string organizationId, Guid conversationId, long? cursor, HttpContext ctx, IAiAssistantChatStore store, CancellationToken ct) =>
            store.LoadAsync(ChatScope.Organization(organizationId), conversationId, cursor ?? 0, Actor(ctx), ct));
        group.MapPost("/new", (string organizationId, ChatCreateRequest request, HttpContext ctx, IAiAssistantChatStore store, CancellationToken ct) =>
            store.CreateOrganizationAsync(organizationId, request, Actor(ctx), ct));
        group.MapPost("/archive", async (string organizationId, ChatNewRequest request, HttpContext ctx, IAiAssistantChatStore store, IAiAssistantChatTransport transport, CancellationToken ct) =>
        {
            await store.ArchiveAsync(ChatScope.Organization(organizationId), request.ConversationId, Actor(ctx), ct);
            await transport.RetireAsync(request.ConversationId, ct);
            return Results.NoContent();
        });
        group.MapPost("/messages", async (string organizationId, ChatMessageRequest request, HttpContext ctx, IAiAssistantChatStore store, IAiAssistantChatTransport transport, CancellationToken ct) =>
        {
            if (await store.AcceptMessageAsync(ChatScope.Organization(organizationId), request, Actor(ctx), ct))
                await transport.SendAsync(request.ConversationId, request.ClientMessageId, request.Text, ct);
            return Results.Accepted();
        });
        group.MapPost("/reconcile", async (string organizationId, ChatNewRequest request, HttpContext ctx, IAiAssistantChatStore store, IAiAssistantChatTransport transport, CancellationToken ct) =>
        {
            await store.RequestRecoveryAsync(ChatScope.Organization(organizationId), request.ConversationId, Actor(ctx), ct);
            await transport.ReconcileAsync(request.ConversationId, ct);
            return Results.Accepted();
        });
        group.MapPost("/stop-waiting", async (string organizationId, ChatStopWaitingRequest request, HttpContext ctx, IAiAssistantChatStore store, IAiAssistantChatTransport transport, CancellationToken ct) =>
        {
            await store.StopWaitingAsync(ChatScope.Organization(organizationId), request, Actor(ctx), ct);
            await transport.RetireAsync(request.ConversationId, ct);
            return Results.Accepted();
        });
        group.MapPost("/abandon", async (string organizationId, ChatAbandonRequest request, HttpContext ctx, IAiAssistantChatStore store, IAiAssistantChatTransport transport, CancellationToken ct) =>
        {
            await store.AbandonAsync(ChatScope.Organization(organizationId), request, Actor(ctx), ct);
            await transport.RetireAsync(request.ConversationId, ct);
            return Results.Accepted();
        });
        group.MapPost("/interactions/{callId}", async (string organizationId, string callId, ChatApprovalRequest request, HttpContext ctx, IAiAssistantChatStore store, IAiAssistantChatTransport transport, CancellationToken ct) =>
        {
            await store.AcceptApprovalAsync(ChatScope.Organization(organizationId), callId, request, Actor(ctx), ct);
            await transport.RespondAsync(request.ConversationId, callId, request.SelectedKey, ct);
            return Results.Accepted();
        });
        group.MapGet("/stream", (string organizationId, Guid conversationId, long? cursor, HttpContext ctx, IChatLiveFeed feed, IServiceScopeFactory scopes, CancellationToken ct) =>
            StreamCoreAsync(ChatScope.Organization(organizationId), conversationId, cursor, ctx, feed, scopes, ct));
    }
}
