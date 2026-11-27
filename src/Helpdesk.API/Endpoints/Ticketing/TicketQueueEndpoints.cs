using System.Data;
using System.Security.Claims;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Ticketing;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.TicketQueue;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.API.Endpoints.Ticketing;

public static class TicketQueueEndpoints
{
    public static void MapTicketQueueEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ticket-queues/{module}")
            .WithTags("Ticket queues")
            .RequireAuthorization("TicketReadAccess")
            .WithMetadata(new QueueAwareEndpoint());
        group.MapGet("/", GetOptionsAsync).WithName("SearchTicketQueues");
        group.MapPost("/assignment", AssignAsync).WithName("AssignTicketQueue");
        group.MapPost("/{ticketId}/claim", ClaimAsync).WithName("ClaimTicketInQueue");
    }

    private static async Task<IResult> ClaimAsync(
        string module, string ticketId, long? expectedVersion, ClaimsPrincipal principal,
        ICurrentUserAccessService accessService, HelpdeskDbContext db, ISupportNotificationService notifications, CancellationToken ct)
    {
        if (!TryModule(module, out var ticketType)) return Results.NotFound();
        if (expectedVersion is not >= 0) return Results.BadRequest("Provide the current assignment version.");
        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                : null;
            var access = await accessService.ResolveAsync(principal, ct);
            var caller = access.DomainUserId;
            var ticket = await VisibleTickets(db, access, ticketType).SingleOrDefaultAsync(row => row.Id == ticketId, ct);
            if (ticket == null) return Results.NotFound();
            if (caller == null || ticket.OrganizationId == null || !TicketAccessPolicy.CanWrite(access, ticket) ||
                !await AssigneeQueries.CanAssignToQueueAsync(db, accessService, ticketType, ticket.OrganizationId, ticket.AssignedSupportGroupId, caller, ct))
                return Results.Forbid();
            if (ticket.AssignedToId == caller) return Results.NoContent();
            if (ticket.QueueAssignmentVersion != expectedVersion || ticket.AssignedToId != null) return Conflict();
            TicketQueueAssignment.Stage(db, ticket, ticket.AssignedSupportGroupId, caller, caller, null, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            await notifications.NotifyTicketAssignedAsync(ticket, null, caller, ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict();
        }
        catch (Exception error) when (IsConcurrentWrite(error) || IsConcurrentWrite(error.InnerException))
        {
            return Conflict();
        }
    }

    private static async Task<IResult> GetOptionsAsync(
        string module, string? organizationId, string? q, string? id, int? take, bool? transfer, bool? picker,
        HelpdeskDbContext db, ClaimsPrincipal principal, ICurrentUserAccessService accessService, CancellationToken ct)
    {
        if (!TryModule(module, out var ticketType)) return Results.NotFound();
        if (transfer == true && string.IsNullOrWhiteSpace(organizationId))
            return Results.BadRequest("Choose a ticket organization to look up transfer destinations.");
        if (q?.Length > 200 || id?.Length > 64) return Results.BadRequest("The queue lookup parameters are too long.");

        var access = await accessService.ResolveAsync(principal, ct);
        var broadOrganizations = TicketAccessPolicy.StaffOrganizations(access, ticketType).ToArray();
        var queueIds = TicketAccessPolicy.StaffQueueGrants(access, ticketType)
            .Where(grant => organizationId == null || grant.OrganizationId == organizationId)
            .Select(grant => grant.SupportGroupId).Distinct().ToArray();
        var main = access.IsHelpdeskAdmin || (organizationId is null ? broadOrganizations.Length > 0 : broadOrganizations.Contains(organizationId));
        IQueryable<SupportGroup> routes;
        if (organizationId is not null)
        {
            routes = TicketQueueAssignment.Destinations(db, organizationId);
            if (!main) routes = routes.Where(queue => queueIds.Contains(queue.Id));
        }
        else
        {
            routes = db.SupportGroups.Where(queue => queue.IsEnabled &&
                (access.IsHelpdeskAdmin || queueIds.Contains(queue.Id) ||
                 db.Organizations.Any(customer => broadOrganizations.Contains(customer.Id) && customer.IsEnabled &&
                     (customer.Id == queue.OwningOrganizationId ||
                      (customer.ItSupportOrganizationId == queue.OwningOrganizationId &&
                       db.OrganizationSupportCoverages.Any(coverage => coverage.IsEnabled && coverage.CustomerOrganizationId == customer.Id &&
                           coverage.ProviderOrganizationId == queue.OwningOrganizationId && coverage.SupportGroupId == queue.Id))))));
        }
        var eligible = routes.Select(queue => queue.Id);

        if (transfer == true && organizationId is not null)
        {
            if (!TicketAccessPolicy.CanTransfer(access, ticketType, organizationId, null))
            {
                main = false;
                var writable = queueIds.Where(queueId => TicketAccessPolicy.CanTransfer(access, ticketType, organizationId, queueId)).ToArray();
                eligible = eligible.Where(queueId => writable.Contains(queueId));
            }
        }
        else if (main)
        {
            var previous = VisibleTickets(db, access, ticketType)
                .Where(ticket => ticket.AssignedSupportGroupId != null && (organizationId == null || ticket.OrganizationId == organizationId))
                .Select(ticket => ticket.AssignedSupportGroupId!);
            eligible = eligible.Union(previous);
        }

        var search = db.SupportGroups.AsNoTracking().Where(queue => eligible.Contains(queue.Id));
        if (id != null) search = search.Where(queue => queue.Id == id);
        var totalEligible = picker == true ? await search.CountAsync(ct) + (main && (id == null || id == "main") ? 1 : 0) : 0;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            search = db.Database.IsNpgsql()
                ? search.Where(queue => EF.Functions.ILike(queue.Name, pattern, "\\"))
                : search.Where(queue => EF.Functions.Like(queue.Name, pattern, "\\"));
        }
        var limit = Math.Clamp(take ?? 25, 1, 100);
        var includeMain = main && (id == null || id == "main") && (string.IsNullOrWhiteSpace(q) || "Main Queue".Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
        var maximum = limit - (includeMain ? 1 : 0);
        var results = await search.OrderBy(queue => queue.Name).ThenBy(queue => queue.Id).Take(maximum + 1)
            .Select(queue => new TicketQueueOptionDto(queue.Id, queue.Name)).ToListAsync(ct);
        var hasMore = results.Count > maximum;
        if (hasMore) results.RemoveAt(maximum);
        if (includeMain) results.Insert(0, new TicketQueueOptionDto(null, "Main Queue"));
        return picker == true ? Results.Ok(new CreationLookupResult<TicketQueueOptionDto>(results, totalEligible, hasMore)) : Results.Ok(results);
    }

    private static async Task<IResult> AssignAsync(
        string module, AssignTicketQueueDto request, ClaimsPrincipal principal, HelpdeskDbContext db,
        ICurrentUserAccessService accessService, CancellationToken ct)
    {
        if (!TryModule(module, out var ticketType)) return Results.NotFound();
        if (request.Tickets is not { Count: > 0 and <= 100 } ||
            request.Tickets.Any(selection => selection == null || string.IsNullOrWhiteSpace(selection.Id) ||
                selection.Id.Length > 64 || selection.ExpectedVersion is not >= 0) ||
            request.Tickets.Select(selection => selection.Id).Distinct().Count() != request.Tickets.Count ||
            (request.SupportGroupId != null && (string.IsNullOrWhiteSpace(request.SupportGroupId) || request.SupportGroupId.Length > 64)) ||
            request.Reason?.Length > 1024 || !Enum.IsDefined(request.AssigneeAction) ||
            (request.AssigneeAction == QueueAssigneeAction.Replace
                ? string.IsNullOrWhiteSpace(request.AssigneeId) || request.AssigneeId.Length > 128
                : request.AssigneeId != null))
            return Results.BadRequest("Provide valid distinct ticket IDs and versions, a destination queue and an assignee action.");

        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                : null;
            var access = await accessService.ResolveAsync(principal, ct);
            var actorId = access.DomainUserId ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(actorId)) return Results.Forbid();

            var expected = request.Tickets.ToDictionary(selection => selection.Id, selection => selection.ExpectedVersion);
            var ids = expected.Keys.ToArray();
            var tickets = await VisibleTickets(db, access, ticketType).Where(ticket => ids.Contains(ticket.Id)).ToListAsync(ct);
            if (tickets.Count != ids.Length) return Results.NotFound();
            if (tickets.Any(ticket => ticket.OrganizationId == null ||
                !TicketAccessPolicy.CanTransfer(access, ticketType, ticket.OrganizationId, ticket.AssignedSupportGroupId)))
                return Results.Forbid();
            if (tickets.Any(ticket => ticket.QueueAssignmentVersion != expected[ticket.Id])) return Conflict();

            var target = request.SupportGroupId?.Trim();
            foreach (var organizationId in tickets.Select(ticket => ticket.OrganizationId!).Distinct())
            {
                if (!await IsAuthorizedDestinationAsync(db, access, ticketType, organizationId, target, ct))
                    return Results.BadRequest("The queue is unavailable for the selected tickets.");
            }

            var assignments = tickets.Select(ticket => new
            {
                Ticket = ticket,
                AssigneeId = request.AssigneeAction switch
                {
                    QueueAssigneeAction.Clear => null,
                    QueueAssigneeAction.Replace => request.AssigneeId?.Trim(),
                    _ => ticket is Change change ? change.ImplementorUserId ?? change.AssignedToId : ticket.AssignedToId
                }
            }).ToArray();
            foreach (var destination in assignments.Where(item => item.AssigneeId != null)
                         .Select(item => (OrganizationId: item.Ticket.OrganizationId!, AssigneeId: item.AssigneeId!)).Distinct())
            {
                if (!await AssigneeQueries.CanAssignToQueueAsync(db, accessService, ticketType, destination.OrganizationId, target, destination.AssigneeId, ct))
                    return Results.Conflict(new { message = "An individual assignee is ineligible for this queue. Clear or replace the assignee to proceed." });
            }

            var occurredUtc = DateTime.UtcNow;
            foreach (var assignment in assignments)
                TicketQueueAssignment.Stage(db, assignment.Ticket, target, assignment.AssigneeId, actorId, request.Reason?.Trim(), occurredUtc);
            await db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict();
        }
        catch (Exception error) when (IsConcurrentWrite(error) || IsConcurrentWrite(error.InnerException))
        {
            return Conflict();
        }
    }

    internal static async Task<bool> IsAuthorizedDestinationAsync(
        HelpdeskDbContext db, CurrentUserAccessProfile access, TicketType module, string organizationId,
        string? groupId, CancellationToken ct) =>
        TicketAccessPolicy.CanTransfer(access, module, organizationId, groupId) &&
        (groupId is null
            ? await db.Organizations.AnyAsync(organization => organization.IsEnabled && organization.Id == organizationId, ct)
            : await TicketQueueAssignment.Destinations(db, organizationId).AnyAsync(queue => queue.Id == groupId, ct));

    internal static async Task<IResult?> ValidateCreationAsync(
        HelpdeskDbContext db, ICurrentUserAccessService accessService, CurrentUserAccessProfile access, TicketType module,
        string? organizationId, string groupId, string? assigneeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(organizationId) || groupId.Length is 0 or > 64 ||
            !await IsAuthorizedDestinationAsync(db, access, module, organizationId, groupId, ct))
            return Results.BadRequest("The queue is unavailable for this ticket.");
        if (!string.IsNullOrWhiteSpace(assigneeId) &&
            !await AssigneeQueries.CanAssignToQueueAsync(db, accessService, module, organizationId, groupId, assigneeId.Trim(), ct))
            return Results.BadRequest("The selected assignee is unavailable for this queue.");
        return null;
    }

    internal static string? ActorId(ClaimsPrincipal principal, CurrentUserAccessProfile access) =>
        access.DomainUserId ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");

    private static IQueryable<Ticket> VisibleTickets(HelpdeskDbContext db, CurrentUserAccessProfile access, TicketType module) => module switch
    {
        TicketType.Incident => TicketAccessPolicy.Readable(db.Incidents, access, module),
        TicketType.Request => TicketAccessPolicy.Readable(db.Requests, access, module),
        TicketType.Change => TicketAccessPolicy.Readable(db.Changes, access, module),
        _ => throw new ArgumentOutOfRangeException(nameof(module))
    };

    private static bool TryModule(string module, out TicketType ticketType) =>
        Enum.TryParse(module, ignoreCase: true, out ticketType) && Enum.IsDefined(ticketType) && !char.IsDigit(module[0]);

    private static bool IsConcurrentWrite(Exception? error) =>
        error is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } ||
        error is SqliteException { SqliteErrorCode: 5 or 6 };

    private static IResult Conflict() => Results.Conflict(new
    {
        code = "ticket_queue_changed",
        message = "A ticket assignment changed. Refresh the selection and try again."
    });
}
