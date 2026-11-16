using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Persistence;

/// <summary>Neutral relations only. No ticket state, SLA, listener, notification or automation writes.</summary>
public sealed class TicketRelationService(HelpdeskDbContext db)
{
    public IQueryable<Ticket> Visible(CurrentUserAccessProfile access, string organization)
    {
        var incidentRead = access.HasPermission(HelpdeskPermissions.IncidentRead, organization) || access.CanManageIncident(organization);
        var requestRead = access.HasPermission(HelpdeskPermissions.RequestRead, organization) || access.CanManageRequest(organization);
        var changeRead = access.HasPermission(HelpdeskPermissions.ChangeRead, organization) || access.CanManageChange(organization);
        var incidentOwn = access.HasPermission(HelpdeskPermissions.IncidentUser, organization);
        var requestOwn = access.HasPermission(HelpdeskPermissions.RequestUser, organization);
        var changeOwn = access.HasPermission(HelpdeskPermissions.ChangeUser, organization);
        var incidents = db.Incidents.Where(x => x.OrganizationId == organization &&
            (incidentRead || incidentOwn && access.CustomerId != null && x.CustomerId == access.CustomerId)).Select(x => x.Id);
        var requests = db.Requests.Where(x => x.OrganizationId == organization &&
            (requestRead || requestOwn && access.CustomerId != null && x.CustomerId == access.CustomerId)).Select(x => x.Id);
        var changes = db.Changes.Where(x => x.OrganizationId == organization &&
            (changeRead || changeOwn && access.CustomerId != null && x.CustomerId == access.CustomerId)).Select(x => x.Id);
        var tasks = db.RequestTasks.Where(x => x.OrganizationId == organization && requests.Contains(x.RequestId)).Select(x => x.Id);
        var ids = incidents.Concat(requests).Concat(changes).Concat(tasks);
        return db.Tickets.AsNoTracking().Where(x => x.OrganizationId == organization && ids.Contains(x.Id));
    }

    public static bool CanManage(CurrentUserAccessProfile access, Ticket ticket) => ticket switch
    {
        Incident => access.CanManageIncident(ticket.OrganizationId),
        Request or RequestTask => access.CanManageRequest(ticket.OrganizationId),
        Change => access.CanManageChange(ticket.OrganizationId),
        _ => false
    };
    public static string Type(Ticket ticket) => ticket switch { Incident => "incidents", Request => "requests", Change => "changes", RequestTask => "request-tasks", _ => "unknown" };
    public static string State(Ticket ticket) => ticket switch
    {
        Change change => change.LifecycleState?.ToString() ?? change.State.ToString(),
        RequestTask task => task.Status.ToString(),
        _ => ticket.State.ToString()
    };
    public static string Url(Ticket ticket) => ticket is RequestTask task
        ? "/requests/" + Uri.EscapeDataString(task.RequestId)
        : "/" + Type(ticket) + "/" + Uri.EscapeDataString(ticket.Id);
    public static TicketLinkChoice Choice(Ticket ticket) => new(ticket.Id, Type(ticket), ticket.TrackingId, ticket.Title, State(ticket), Url(ticket));

    public async Task<Ticket?> ResolveVisibleAsync(string id, CurrentUserAccessProfile access, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (ticket?.OrganizationId is not { Length: > 0 } org) return null;
        return await Visible(access, org).SingleOrDefaultAsync(x => x.Id == ticket.Id, ct);
    }

    public async Task<TicketRelationPage> ListAsync(Ticket source, CurrentUserAccessProfile access, int page, int size, CancellationToken ct)
    {
        var visible = Visible(access, source.OrganizationId!).Select(x => x.Id);
        var query = db.TicketRelations.AsNoTracking().Where(x =>
            (x.SourceTicketId == source.Id || x.TargetTicketId == source.Id) &&
            visible.Contains(x.SourceTicketId) && visible.Contains(x.TargetTicketId));
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByUtc(db, x => x.CreatedUtc, descending: true).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        var ids = rows.SelectMany(x => new[] { x.SourceTicketId, x.TargetTicketId }).Distinct().ToArray();
        var tickets = await Visible(access, source.OrganizationId!).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return new(rows.Where(x => tickets.ContainsKey(x.SourceTicketId) && tickets.ContainsKey(x.TargetTicketId))
            .Select(x => Map(x, tickets[x.SourceTicketId], tickets[x.TargetTicketId], source.Id)).ToArray(), count, page, size);
    }

    public static TicketRelation Map(Ticket source, Ticket target, TicketRelationType type, string actor, string name, string? note = null)
    {
        if (source.Id == target.Id || source.OrganizationId is null || source.OrganizationId != target.OrganizationId)
            throw new ArgumentException("Tickets must be distinct and in the same organization.");
        if (type == TicketRelationType.RelatedTo && string.CompareOrdinal(source.Id, target.Id) > 0) (source, target) = (target, source);
        if (type == TicketRelationType.AutomationFailureOf && (source is not Incident || target is not Request))
            throw new ArgumentException("Automation origin requires an incident and its source request.");
        return new() { SourceTicketId = source.Id, TargetTicketId = target.Id, RelationType = type,
            CreatedByUserId = actor, CreatedByUserName = name, Note = note };
    }
    public async Task<TicketRelation> LinkAsync(Ticket source, Ticket target, string actor, string name, string? note, CancellationToken ct)
    {
        var relation = Map(source, target, TicketRelationType.RelatedTo, actor, name, note);
        var prior = await db.TicketRelations.OrderBy(x => x.Id).FirstOrDefaultAsync(x => x.RelationType == TicketRelationType.RelatedTo &&
            (x.SourceTicketId == source.Id && x.TargetTicketId == target.Id || x.SourceTicketId == target.Id && x.TargetTicketId == source.Id), ct);
        if (prior is not null) return prior;
        db.TicketRelations.Add(relation);
        // Canonical symmetric ordering plus the existing unique database index fences concurrent/reverse creates.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is Npgsql.PostgresException { SqlState: "23505" } or
            Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            db.Entry(relation).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            return await db.TicketRelations.OrderBy(x => x.Id).FirstAsync(x => x.RelationType == TicketRelationType.RelatedTo &&
                (x.SourceTicketId == source.Id && x.TargetTicketId == target.Id || x.SourceTicketId == target.Id && x.TargetTicketId == source.Id), ct);
        }
        return relation;
    }
    public static TicketRelationDto Map(TicketRelation relation, Ticket source, Ticket target, string viewed) => new()
    {
        Id = relation.Id, SourceTicketId = source.Id, TargetTicketId = target.Id, SourceTrackingId = source.TrackingId,
        TargetTrackingId = target.TrackingId, SourceSubject = source.Title, TargetSubject = target.Title,
        SourceState = source.State, TargetState = target.State, SourceType = Type(source), TargetType = Type(target),
        SourceLifecycleState = State(source), TargetLifecycleState = State(target), SourceUrl = Url(source), TargetUrl = Url(target),
        RelationType = relation.RelationType, CreatedUtc = relation.CreatedUtc, CreatedByUserName = relation.CreatedByUserName,
        Note = relation.Note, IsOutgoing = relation.SourceTicketId == viewed
    };
}
