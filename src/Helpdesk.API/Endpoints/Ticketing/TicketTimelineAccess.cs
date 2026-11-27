using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;

namespace Helpdesk.API.Endpoints.Ticketing;

internal static class TicketTimelineAccess
{
    private static readonly TimelineEventType[] WorkforceEvents =
        [TimelineEventType.Worklog, TimelineEventType.InternalNote, TimelineEventType.CustomerReply, TimelineEventType.TechnicianReply];

    public static IQueryable<TicketTimelineEvent> Filter(
        IQueryable<TicketTimelineEvent> events, bool staff, bool restricted)
    {
        if (!staff) events = events.Where(item => item.EventType != TimelineEventType.InternalNote);
        if (restricted) events = events.Where(item => WorkforceEvents.Contains(item.EventType));
        return events;
    }

    public static bool Includes(TimelineEventType type, bool staff, bool restricted) =>
        (staff || type != TimelineEventType.InternalNote) && (!restricted || WorkforceEvents.Contains(type));

    public static TicketTimelineEventDto Project(TicketTimelineEventDto item, bool restricted) => restricted
        ? new TicketTimelineEventDto
        {
            Id = item.Id,
            TicketId = item.TicketId,
            CreatedUtc = item.CreatedUtc,
            CreatedByUserId = item.CreatedByUserId,
            CreatedByUserName = item.CreatedByUserName,
            EventType = item.EventType,
            MessageText = item.MessageText,
            Hours = item.Hours,
            RecordedHours = item.RecordedHours,
            WorkLogId = item.WorkLogId,
            TechnicianName = item.TechnicianName
        }
        : item;
}
