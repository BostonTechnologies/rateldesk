using Helpdesk.Infrastructure.Ticketing;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Persistence;

/// <summary>Neutral relations only. No ticket state, SLA, listener, notification or automation writes.</summary>
public sealed class TicketRelationService(HelpdeskDbContext db)
{
    public IQueryable<Ticket> Visible(CurrentUserAccessProfile access, string? organization = null) => Authorized(
        TicketAccessPolicy.Readable(db.Incidents, access, TicketType.Incident),
        TicketAccessPolicy.Readable(db.Requests, access, TicketType.Request),
        TicketAccessPolicy.Readable(db.Changes, access, TicketType.Change), organization);

    public IQueryable<Ticket> Writable(CurrentUserAccessProfile access, string organization) => Authorized(
        TicketAccessPolicy.Writable(db.Incidents, access, TicketType.Incident),
        TicketAccessPolicy.Writable(db.Requests, access, TicketType.Request),
        TicketAccessPolicy.Writable(db.Changes, access, TicketType.Change), organization);

    private IQueryable<Ticket> Authorized(IQueryable<Incident> incidents, IQueryable<Request> requests, IQueryable<Change> changes, string? organization)
    {
        var tasks = db.RequestTasks.Where(task => requests.Any(parent => parent.Id == task.RequestId && parent.OrganizationId == task.OrganizationId));
        var ids = incidents.Select(ticket => ticket.Id).Concat(requests.Select(ticket => ticket.Id))
            .Concat(changes.Select(ticket => ticket.Id)).Concat(tasks.Select(task => task.Id));
        return db.Tickets.AsNoTracking().Where(ticket => ticket.OrganizationId != null && ticket.OrganizationId != "" &&
            (organization == null || ticket.OrganizationId == organization) && ids.Contains(ticket.Id));
    }
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

    public Task<Ticket?> ResolveVisibleAsync(string id, CurrentUserAccessProfile access, CancellationToken ct) =>
        Visible(access).SingleOrDefaultAsync(ticket => ticket.Id == id, ct);

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
            var concurrentRelation = await db.TicketRelations.OrderBy(x => x.Id).FirstOrDefaultAsync(x => x.RelationType == TicketRelationType.RelatedTo &&
                (x.SourceTicketId == source.Id && x.TargetTicketId == target.Id || x.SourceTicketId == target.Id && x.TargetTicketId == source.Id), ct);
            if (concurrentRelation is null) throw;
            return concurrentRelation;
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
