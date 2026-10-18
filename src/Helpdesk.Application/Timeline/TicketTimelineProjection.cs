using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Timeline;

/// <summary>Projects persisted event facts identically for live publication and refreshed histories.</summary>
public static class TicketTimelineProjection
{
    public static TicketTimelineEventDto ToDto(TicketTimelineEvent timelineEvent) => new()
    {
        Id = timelineEvent.Id,
        TicketId = timelineEvent.TicketId,
        CreatedUtc = timelineEvent.CreatedUtc,
        CreatedByUserId = timelineEvent.CreatedByUserId,
        CreatedByUserName = timelineEvent.CreatedByUserName,
        EventType = timelineEvent.EventType,
        MessageHtml = timelineEvent.MessageHtml,
        MessageText = timelineEvent.MessageText,
        Hours = timelineEvent.Hours ?? 0,
        RecordedHours = timelineEvent.Hours,
        WorkLogId = timelineEvent.WorkLogId,
        EmailStatus = timelineEvent.EmailStatus,
        EmailRecipient = timelineEvent.EmailRecipient,
        RetryCount = timelineEvent.RetryCount,
        IsRetryable = timelineEvent.IsRetryable
    };
}
