using System.Data;
using System.Security.Claims;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Data.Sqlite;

namespace Helpdesk.API.Endpoints.Ticketing;

public static class TicketRelationEndpoints
{
    public static void MapTicketRelationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tickets/{id}/relations").WithTags("Ticket Relations").RequireAuthorization();
        group.MapGet("/", async (string id, int? page, int? pageSize, HttpContext http,
            ICurrentUserAccessService accessService, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(http.User, ct);
            var relations = new TicketRelationService(db);
            var ticket = await relations.ResolveVisibleAsync(id, access, ct);
            return ticket is null ? Results.NotFound() : Results.Ok(await relations.ListAsync(ticket, access,
                Math.Clamp(page ?? 1, 1, 100_000), PageSize(pageSize), ct));
        }).WithMetadata(new QueueAwareEndpoint());
        group.MapGet("/choices", async (string id, string? search, string? type, int? page, int? pageSize, HttpContext http,
            ICurrentUserAccessService accessService, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(http.User, ct);
            var relations = new TicketRelationService(db);
            var source = await relations.ResolveVisibleAsync(id, access, ct);
            if (source is null) return Results.NotFound();
            var writable = relations.Writable(access, source.OrganizationId!);
            if (!await writable.AnyAsync(ticket => ticket.Id == source.Id, ct)) return Results.Forbid();
            if ((search?.Length ?? 0) > 128 || type is not (null or "" or "incidents" or "requests" or "changes" or "request-tasks")) return Results.BadRequest();
            var q = (search ?? "").Trim().ToUpperInvariant();
            var query = writable.Where(x => x.Id != source.Id);
            if (type == "incidents") query = query.Where(x => x is Incident);
            if (type == "requests") query = query.Where(x => x is Request);
            if (type == "changes") query = query.Where(x => x is Change);
            if (type == "request-tasks") query = query.Where(x => x is RequestTask);
            if (q.Length > 0) query = query.Where(x => x.TrackingId.ToUpper().Contains(q) || x.Title.ToUpper().Contains(q));
            var number = Math.Clamp(page ?? 1, 1, 100_000); var size = PageSize(pageSize);
            var count = await query.CountAsync(ct);
            var items = await query.OrderBy(x => x.TrackingId).ThenBy(x => x.Id).Skip((number - 1) * size).Take(size).ToArrayAsync(ct);
            return Results.Ok(new TicketLinkChoicePage(items.Select(TicketRelationService.Choice).ToArray(), count, number, size));
        }).WithMetadata(new QueueAwareEndpoint());
        group.MapPost("/", async (string id, LinkTicketRequest input, HttpContext http,
            ICurrentUserAccessService accessService, HelpdeskDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.TargetTicketId) || input.TargetTicketId.Length > 128 || (input.Note?.Length ?? 0) > 1000) return Results.BadRequest();
            var access = await accessService.ResolveAsync(http.User, ct);
            var relations = new TicketRelationService(db);
            var source = await relations.ResolveVisibleAsync(id, access, ct);
            var target = await relations.ResolveVisibleAsync(input.TargetTicketId, access, ct);
            if (source is null || target is null || source.OrganizationId != target.OrganizationId) return Results.NotFound();
            if (source.Id == target.Id) return Results.BadRequest("A ticket cannot link to itself.");
            var writable = relations.Writable(access, source.OrganizationId!);
            if (await writable.CountAsync(ticket => ticket.Id == source.Id || ticket.Id == target.Id, ct) != 2) return Results.Forbid();
            try
            {
                var relation = await relations.LinkAsync(source, target, http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                    http.User.Identity?.Name ?? "Staff", input.Note, ct);
                return Results.Ok(TicketRelationService.Map(relation, relation.SourceTicketId == source.Id ? source : target,
                    relation.TargetTicketId == target.Id ? target : source, source.Id));
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "23505" } or SqliteException { SqliteExtendedErrorCode: 2067 })
            {
                db.ChangeTracker.Clear();
                var relation = await db.TicketRelations.AsNoTracking().SingleOrDefaultAsync(x => x.RelationType == TicketRelationType.RelatedTo &&
                    (x.SourceTicketId == source.Id && x.TargetTicketId == target.Id || x.SourceTicketId == target.Id && x.TargetTicketId == source.Id), ct);
                return relation is null ? Results.Conflict() : Results.Ok(TicketRelationService.Map(relation,
                    relation.SourceTicketId == source.Id ? source : target, relation.TargetTicketId == target.Id ? target : source, source.Id));
            }
        }).AddEndpointFilter(FenceScopedWriteAsync).WithMetadata(new QueueAwareEndpoint());
        group.MapDelete("/{relationId:guid}", async (string id, Guid relationId, HttpContext http,
            ICurrentUserAccessService accessService, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(http.User, ct);
            var relations = new TicketRelationService(db);
            var source = await relations.ResolveVisibleAsync(id, access, ct);
            if (source is null) return Results.NotFound();
            var relation = await db.TicketRelations.SingleOrDefaultAsync(x => x.Id == relationId &&
                (x.SourceTicketId == source.Id || x.TargetTicketId == source.Id), ct);
            if (relation is null) return Results.NotFound();
            var target = await relations.ResolveVisibleAsync(relation.SourceTicketId == source.Id ? relation.TargetTicketId : relation.SourceTicketId, access, ct);
            if (target is null || source.OrganizationId != target.OrganizationId) return Results.NotFound();
            var writable = relations.Writable(access, source.OrganizationId!);
            if (await writable.CountAsync(ticket => ticket.Id == source.Id || ticket.Id == target.Id, ct) != 2) return Results.Forbid();
            if (relation.RelationType != TicketRelationType.RelatedTo) return Results.Conflict("Use the operation that owns this relation.");
            db.TicketRelations.Remove(relation);
            db.TicketTimelineEvents.Add(new() { TicketId = source.Id, EventType = TimelineEventType.InternalNote,
                CreatedByUserId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", CreatedByUserName = http.User.Identity?.Name ?? "Staff",
                MessageText = "Removed neutral relation " + relation.Id.ToString("D") + "." });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).AddEndpointFilter(FenceScopedWriteAsync).WithMetadata(new QueueAwareEndpoint());
    }

    private static async ValueTask<object?> FenceScopedWriteAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var http = invocation.HttpContext;
        var ct = http.RequestAborted;
        var accessService = http.RequestServices.GetRequiredService<ICurrentUserAccessService>();
        var db = http.RequestServices.GetRequiredService<HelpdeskDbContext>();
        var access = await accessService.ResolveAsync(http.User, ct);
        if (!access.HasRestrictedWorkforceAccess || !db.Database.IsRelational()) return await next(invocation);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await next(invocation);
            if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 }) await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (TicketQueueEndpoints.IsConcurrentWrite(error) || TicketQueueEndpoints.IsConcurrentWrite(error.InnerException))
        {
            return Results.Conflict(new { message = "The relation or ticket authorization changed. Refresh and try again." });
        }
    }

    private static int PageSize(int? value) => value is 50 or 100 ? value.Value : 25;
}
