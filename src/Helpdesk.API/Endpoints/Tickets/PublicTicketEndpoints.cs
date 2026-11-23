using Helpdesk.API.Endpoints.PublicIngress;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.PublicAccess;
using Helpdesk.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Tickets;

public static class PublicTicketEndpoints
{
    public static void MapPublicTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(PublicAccessProtocol.ApiPrefix + "/{accessId:guid}").WithTags("Public Access").WithMetadata(new PublicGrantRequiredMetadata()).AllowAnonymous();
        group.MapGet("/ticket", GetPublicTicketView).Produces<PublicTicketViewDto>()
            .WithName("GetPublicTicketView").WithSummary("Gets a ticket for a scoped public grant.");
        group.MapGet("/timeline", GetPublicTicketTimeline).Produces<PublicTicketTimelineItemDto[]>()
            .WithName("GetPublicTicketTimeline").WithSummary("Gets customer-visible events for a scoped public grant.");
        group.MapGet("/asset/{reference}", PublicTicketAssets.GetAsync).WithName("GetPublicTicketRaster");
    }

    private static async Task<IResult> GetPublicTicketView(Guid accessId, HttpContext http, PublicLinkService links,
        HelpdeskDbContext db, CancellationToken ct)
    {
        if (await PublicLinkEndpoints.AuthorizeAsync(http, links, accessId, ct) is not { Purpose: PublicLinkPurpose.Ticket } grant)
            return Results.Unauthorized();

        var ticket = await db.Tickets.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.Id == grant.TicketId && row.OrganizationId == grant.OrganizationId)
            .Select(row => new PublicTicketViewDto
            {
                TrackingId = row.TrackingId, Title = row.Title,
                State = row.State, LastUpdatedAt = row.UpdatedAt ?? row.CreatedAt
            }).SingleOrDefaultAsync(ct);
        if (ticket is null)
            return Results.NotFound();

        ticket.LastPublicNote = await db.WorkLogs.AsNoTracking()
            .Where(log => log.TicketId == grant.TicketId && !log.IsInternalNote)
            .OrderByDescending(log => log.LoggedAt).Select(log => log.NotesText).FirstOrDefaultAsync(ct);
        return Results.Ok(ticket);
    }

    private static async Task<IResult> GetPublicTicketTimeline(Guid accessId, HttpContext http, PublicLinkService links,
        HelpdeskDbContext db, PublicTicketAssets assets, CancellationToken ct)
    {
        if (await PublicLinkEndpoints.AuthorizeAsync(http, links, accessId, ct) is not { Purpose: PublicLinkPurpose.Ticket } grant)
            return Results.Unauthorized();

        var replies = await db.TicketTimelineEvents.AsNoTracking()
            .Where(row => row.TicketId == grant.TicketId &&
                (row.EventType == TimelineEventType.CustomerReply || row.EventType == TimelineEventType.TechnicianReply) &&
                (row.WorkLogId == null || db.WorkLogs.Any(log => log.Id == row.WorkLogId && log.TicketId == grant.TicketId && !log.IsInternalNote)))
            .OrderBy(row => row.Id)
            .Select(row => new { row.WorkLogId, row.CreatedUtc, row.EventType, row.MessageHtml, row.MessageText, row.Hours })
            .ToListAsync(ct);
        var logs = await db.WorkLogs.AsNoTracking().Where(row => row.TicketId == grant.TicketId && !row.IsInternalNote)
            .OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.LoggedAt, row.NotesHtml, row.NotesText, row.Hours })
            .ToListAsync(ct);

        var timeline = replies.Select(row => new PublicTicketTimelineItemDto(row.CreatedUtc,
                row.EventType == TimelineEventType.CustomerReply, row.EventType,
                assets.Rewrite(row.MessageHtml, grant, row.WorkLogId), row.MessageText, row.Hours))
            .Concat(logs.Select(row => new PublicTicketTimelineItemDto(
                new DateTimeOffset(DateTime.SpecifyKind(row.LoggedAt, DateTimeKind.Utc)), false, TimelineEventType.Worklog,
                assets.Rewrite(row.NotesHtml, grant, row.Id), row.NotesText, row.Hours)))
            .OrderByDescending(item => item.CreatedUtc).ToArray();
        return Results.Ok(timeline);
    }
}
