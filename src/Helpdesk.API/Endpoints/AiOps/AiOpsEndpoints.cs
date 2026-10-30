using System.Security.Claims;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Infrastructure.AiOps;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.DTOs.AiOps;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.AiOps;

public static class AiOpsEndpoints
{
    public static void MapAiOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ai/ops").RequireAuthorization("HelpdeskStaff").WithTags("AI Operations");
        group.AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "The saved revision changed. Reload before editing." }); }
            catch (DbUpdateException) { return Results.Conflict(new { message = "The saved state changed concurrently. Reload before editing." }); }
        });
        group.MapGet("/organizations", async (HelpdeskDbContext db, ICurrentUserAccessService accessService, HttpContext ctx, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(ctx.User, ct);
            var orgs = access.OrganizationIdsForAny(
                HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentManager,
                HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager,
                HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeManager).ToArray();
            var organizations = await db.Organizations.AsNoTracking()
                .Where(x => x.State == Helpdesk.Shared.Models.EntityState.Enabled && (access.IsHelpdeskAdmin || orgs.Contains(x.Id)))
                .OrderBy(x => x.Name).ThenBy(x => x.Id)
                .Select(x => new AiOpsOrganizationDto(x.Id, x.Name)).ToListAsync(ct);
            return Results.Ok(organizations.Where(x => CanUse(access, x.Id)));
        });
        group.MapGet("/tickets", async (string organizationId, string? q, HelpdeskDbContext db, ICurrentUserAccessService accessService, HttpContext ctx, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(ctx.User, ct);
            var term = (q ?? "").Trim().ToLowerInvariant();
            if (term.Length > 200) return Results.BadRequest();
            var rows = await db.Tickets.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && (term == "" || x.Title.ToLower().Contains(term) || x.TrackingId.ToLower().Contains(term)))
                .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Where(x => AiOpsContext.CanManage(access, x)).Take(50)
                .Select(x => new AiContextTicketDto(x.Id, AiOpsContext.TicketType(x), x.TrackingId, x.Title)));
        });
        group.MapGet("/prompts", async (string organizationId, HelpdeskDbContext db, ICurrentUserAccessService accessService, HttpContext ctx, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(ctx.User, ct);
            if (!CanUse(access, organizationId)) return Results.Forbid();
            return Results.Ok(await db.Set<AiGuidancePrompt>().AsNoTracking().Where(x => x.OrganizationId == organizationId)
                .OrderBy(x => x.Name).Select(x => new AiPromptDto(x.Id, x.OrganizationId, x.Name, x.Description, x.Content, x.Revision, x.IsDefault, x.Archived)).ToListAsync(ct));
        });
        group.MapPut("/prompts/{id:guid}", async (Guid id, SaveAiPromptDto request, HelpdeskDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 || request.Description is null || request.Description.Length > 500 || request.Content is null || request.Content.Length > 12000)
                return Results.BadRequest(new { message = "Name (up to 120 characters), description (500) and guidance (12000) are required within their limits." });
            if (!await db.Organizations.AnyAsync(x => x.Id == request.OrganizationId && x.IsEnabled, ct)) return Results.NotFound();
            var row = await db.Set<AiGuidancePrompt>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is not null && row.OrganizationId != request.OrganizationId) return Results.NotFound();
            if ((row?.Revision ?? 0) != request.ExpectedRevision) return Results.Conflict(new { message = "The prompt revision changed. Reload before saving." });
            if (request.IsDefault && !request.Archived && await db.Set<AiGuidancePrompt>().AnyAsync(x => x.OrganizationId == request.OrganizationId && x.Id != id && x.IsDefault && !x.Archived, ct))
                return Results.Conflict(new { message = "Unset the current default before selecting another." });
            if (row is null) { row = new() { Id = id, OrganizationId = request.OrganizationId, Revision = 0 }; db.Add(row); }
            row.Name = request.Name.Trim(); row.Description = request.Description; row.Content = request.Content;
            row.IsDefault = request.IsDefault; row.Archived = request.Archived; row.Revision++;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new AiPromptDto(row.Id, row.OrganizationId, row.Name, row.Description, row.Content, row.Revision, row.IsDefault, row.Archived));
        }).RequireAuthorization("HelpdeskAdmin");
        group.MapPost("/investigations", CreateAsync);
        group.MapGet("/investigations", async (string? q, string? organizationId, HelpdeskDbContext db, ICurrentUserAccessService accessService, HttpContext ctx, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(ctx.User, ct);
            var term = (q ?? "").Trim().ToLowerInvariant();
            if (term.Length > 200) return Results.BadRequest();
            var orgs = access.AllowedOrganizationIds.ToArray();
            var candidates = await db.Set<AiOpsInvestigation>().AsNoTracking()
                .Where(x => (access.IsHelpdeskAdmin || orgs.Contains(x.OrganizationId)) && (organizationId == null || x.OrganizationId == organizationId) && (term == "" || x.Title.ToLower().Contains(term)))
                .OrderByDescending(x => x.CreatedUtc).Take(100).ToListAsync(ct);
            var candidateIds = candidates.Select(x => x.Id).ToArray();
            var states = await db.Set<AiAssistantChatConversation>().AsNoTracking().Where(x => candidateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.State, ct);
            var ticketIds = candidates.SelectMany(x => JsonSerializer.Deserialize<string[]>(x.TicketIdsJson) ?? []).Distinct().ToArray();
            var tickets = await db.Tickets.IgnoreQueryFilters().AsNoTracking().Where(x => ticketIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var result = new List<AiInvestigationDto>();
            foreach (var row in candidates)
            {
                var ids = JsonSerializer.Deserialize<string[]>(row.TicketIdsJson) ?? [];
                if (ids.Length > 0 && ids.All(id => tickets.TryGetValue(id, out var ticket) && ticket.OrganizationId == row.OrganizationId && AiOpsContext.CanManage(access, ticket)))
                    result.Add(ToDto(row) with { ContextSnapshot = "", State = states[row.Id] });
            }
            return Results.Ok(result);
        });
        group.MapGet("/investigations/{id:guid}", async (Guid id, HelpdeskDbContext db, ICurrentUserAccessService accessService, HttpContext ctx, CancellationToken ct) =>
        {
            var row = await db.Set<AiOpsInvestigation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || !await AiOpsContext.CanAccessAsync(db, await accessService.ResolveAsync(ctx.User, ct), row, ct)) return Results.NotFound();
            var state = await db.Set<AiAssistantChatConversation>().Where(x => x.Id == id).Select(x => x.State).SingleAsync(ct);
            return Results.Ok(ToDto(row) with { State = state });
        });
        group.MapPost("/investigations/{id:guid}/archive", async (Guid id, HelpdeskDbContext db, ICurrentUserAccessService accessService, HttpContext ctx, IAiAssistantChatTransport transport, CancellationToken ct) =>
        {
            if (!db.Database.IsNpgsql()) return Results.Conflict();
            var row = await db.Set<AiOpsInvestigation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || !await AiOpsContext.CanAccessAsync(db, await accessService.ResolveAsync(ctx.User, ct), row, ct)) return Results.NotFound();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var conversation = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
            if (conversation.State == ChatState.Archived) return Results.NoContent();
            if (conversation.State != ChatState.Idle) return Results.Conflict(new { message = "Reconcile the active or uncertain turn before archiving." });
            conversation.State = ChatState.Archived;
            AiAssistantChatStore.Append(db, conversation, "archived", "Investigation archived.", ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub"));
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            await transport.RetireAsync(id, ct);
            return Results.NoContent();
        });
    }

    private static bool CanUse(CurrentUserAccessProfile access, string org) => access.CanManageIncident(org) || access.CanManageRequest(org) || access.CanManageChange(org);
    private static AiInvestigationDto ToDto(AiOpsInvestigation x) => new(x.Id, x.OrganizationId, x.Title, x.TicketId, x.TicketType, x.ContextSnapshot, DateTime.SpecifyKind(x.CreatedUtc, DateTimeKind.Utc));
    private static async Task<IResult> CreateAsync(CreateAiInvestigationDto request, HelpdeskDbContext db, ICurrentUserAccessService accessService, HttpContext ctx, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return Results.Problem("Native investigations require PostgreSQL.", statusCode: 409);
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200 || request.TicketIds is not { Length: > 0 and <= 12 } || request.PromptIds is not { Length: <= 8 }) return Results.BadRequest();
        var actor = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub") ?? throw new UnauthorizedAccessException();
        var ids = request.TicketIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var promptIds = request.PromptIds.Distinct().Order().ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.OrganizationId, request.Title, ids, promptIds, actor }))));
        var access = await accessService.ResolveAsync(ctx.User, ct);
        var previous = await db.Set<AiOpsInvestigation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.Id, ct);
        if (previous is not null)
            return previous.RequestFingerprint == fingerprint && await AiOpsContext.CanAccessAsync(db, access, previous, ct) ? Results.Ok(ToDto(previous)) : Results.Conflict();
        var tickets = await db.Tickets.IgnoreQueryFilters().AsNoTracking().Where(x => ids.Contains(x.Id) && x.OrganizationId == request.OrganizationId).OrderBy(x => x.Id).ToListAsync(ct);
        if (tickets.Count != ids.Length || tickets.Any(x => !AiOpsContext.CanManage(access, x))) return Results.NotFound();
        var prompts = await db.Set<AiGuidancePrompt>().AsNoTracking().Where(x => x.OrganizationId == request.OrganizationId && !x.Archived && (x.IsDefault || promptIds.Contains(x.Id))).OrderByDescending(x => x.IsDefault).ThenBy(x => x.Id).ToListAsync(ct);
        if (promptIds.Any(id => prompts.All(x => x.Id != id))) return Results.NotFound();
        var snapshot = JsonSerializer.Serialize(new
        {
            observedAtUtc = DateTime.UtcNow,
            guidance = prompts.Select(x => new { x.Id, x.Name, x.Revision, x.IsDefault, x.Content }),
            tickets = tickets.Select(x => new { x.Id, type = AiOpsContext.TicketType(x), x.TrackingId, x.Title, x.Description, x.OrganizationId, state = x.State.ToString() })
        });
        AiOpsContext.ValidatePayload(AiOpsContext.Outbound(request.Id, Guid.Empty, snapshot, "", actor));
        var anchor = tickets[0];
        var row = new AiOpsInvestigation { Id = request.Id, OrganizationId = request.OrganizationId, Title = request.Title.Trim(), TicketId = anchor.Id, TicketType = AiOpsContext.TicketType(anchor), TicketIdsJson = JsonSerializer.Serialize(ids), ContextSnapshot = snapshot, RequestFingerprint = fingerprint, CreatedBy = actor };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({request.Id.ToString()}, 0))", ct);
        if (await db.Set<AiOpsInvestigation>().AnyAsync(x => x.Id == request.Id, ct)) return Results.Conflict();
        db.Add(new AiAssistantChatConversation { Id = request.Id, OrganizationId = row.OrganizationId, TicketId = row.TicketId, TicketType = row.TicketType, CreatedByUserId = actor, IsInvestigation = true });
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(ToDto(row));
    }
}
