using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Tickets;

public static class PublicTicketEndpoints
{
    public static void MapPublicTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tickets/public")
            .WithTags("Tickets");

        group.MapGet("/view", GetPublicTicketView)
            .AllowAnonymous()
            .WithName("GetPublicTicketView")
            .WithSummary("Gets a ticket view for customers")
            .WithDescription("Returns limited ticket information for signed public access.");

        group.MapGet("/timeline", GetPublicTicketTimeline)
            .AllowAnonymous()
            .WithName("GetPublicTicketTimeline")
            .WithSummary("Gets public ticket timeline")
            .WithDescription("Returns customer-safe timeline entries with rendered HTML.");
    }

    private static async Task<IResult> GetPublicTicketView(
        [FromQuery] string? trackingId,
        [FromQuery] string? ticketId,
        [FromQuery] string email,
        [FromQuery] string token,
        [FromServices] IPublicTicketLinkSigner signer,
        [FromServices] HelpdeskDbContext db)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            return Results.BadRequest("Email and token are required.");

        var ticket = !string.IsNullOrWhiteSpace(trackingId)
            ? await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.TrackingId == trackingId)
            : !string.IsNullOrWhiteSpace(ticketId)
                ? await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId || t.TrackingId == ticketId)
                : null;

        if (ticket is null)
            return Results.Problem("Ticket not found", statusCode: 404);

        if (string.IsNullOrWhiteSpace(ticket.TrackingId) ||
            !signer.ValidateToken(token, ticket.TrackingId, email))
        {
            return Results.Unauthorized();
        }

        var lastNote = await db.WorkLogs.AsNoTracking()
            .Where(l => l.TicketId == ticket.Id)
            .OrderByDescending(l => l.LoggedAt)
            .Select(l => l.NotesText)
            .FirstOrDefaultAsync();

        var dto = new PublicTicketViewDto
        {
            Id = ticket.Id,
            TrackingId = ticket.TrackingId,
            Title = ticket.Title,
            State = ticket.State,
            LastUpdatedAt = ticket.UpdatedAt ?? ticket.CreatedAt,
            LastPublicNote = lastNote
        };

        return Results.Ok(dto);
    }

    private static async Task<IResult> GetPublicTicketTimeline(
        [FromQuery] string trackingId,
        [FromQuery] string email,
        [FromQuery] string token,
        [FromServices] IPublicTicketLinkSigner signer,
        [FromServices] HelpdeskDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackingId) ||
            string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(token))
        {
            return Results.BadRequest("Tracking ID, email, and token are required.");
        }

        var ticket = await db.Tickets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TrackingId == trackingId, ct);

        if (ticket is null)
            return Results.Problem("Ticket not found", statusCode: 404);

        if (!signer.ValidateToken(token, ticket.TrackingId, email))
            return Results.Unauthorized();

        // Load timeline reply events (customer + technician replies only)
        var timelineEventsRaw = await db.TicketTimelineEvents
            .AsNoTracking()
            .Where(t => t.TicketId == ticket.Id)
            .Where(t =>
                t.EventType == TimelineEventType.CustomerReply ||
                t.EventType == TimelineEventType.TechnicianReply)
            .ToListAsync(ct);


        // Load worklogs WITH technician name via join
        var worklogsRaw = await (
            from w in db.WorkLogs.AsNoTracking()
            join u in db.Users.AsNoTracking()
                on w.TechnicianId equals u.Id into gj
            from user in gj.DefaultIfEmpty()
            where w.TicketId == ticket.Id && !w.IsInternalNote
            orderby w.LoggedAt
            select new
            {
                WorkLog = w,
                TechnicianName = user != null ? user.Name : null
            }
        ).ToListAsync(ct);


        // Convert timeline events to timeline DTO
        var timelineEvents = timelineEventsRaw.Select(t => new TicketTimelineEventDto
        {
            Id = t.Id,
            TicketId = t.TicketId,
            CreatedUtc = t.CreatedUtc,
            CreatedByUserId = t.EventType == TimelineEventType.CustomerReply ? "customer" : "helpdesk",
            CreatedByUserName = t.EventType == TimelineEventType.CustomerReply ? "Customer" : "Helpdesk",
            EventType = t.EventType,
            MessageHtml = t.MessageHtml,
            MessageText = t.MessageText,
            Hours = t.Hours ?? 0,
            RecordedHours = t.Hours,
            WorkLogId = t.WorkLogId
        });


        // Convert worklogs to timeline DTO
        var worklogs = worklogsRaw.Select(x => new TicketTimelineEventDto
        {
            Id = Guid.TryParse(x.WorkLog.Id, out var parsedWorklogId)
                ? parsedWorklogId
                : CreateDeterministicGuid(x.WorkLog.Id),
            TicketId = x.WorkLog.TicketId,
            CreatedUtc = new DateTimeOffset(DateTime.SpecifyKind(x.WorkLog.LoggedAt, DateTimeKind.Utc)),
            CreatedByUserId = x.WorkLog.TechnicianId ?? "helpdesk",
            CreatedByUserName = x.TechnicianName ?? "Helpdesk",
            EventType = TimelineEventType.Worklog,
            MessageHtml = x.WorkLog.NotesHtml,
            MessageText = x.WorkLog.NotesText,
            Hours = x.WorkLog.Hours,
            RecordedHours = x.WorkLog.Hours,
            WorkLogId = x.WorkLog.Id,
            TechnicianName = x.TechnicianName
        });


        // Merge both sources
        var combined = timelineEvents
            .Concat(worklogs)
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();

        return Results.Ok(combined);

    }

    private static Guid CreateDeterministicGuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Guid.Empty;

        var bytes = global::System.Security.Cryptography.MD5.HashData(global::System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes);
    }
}
