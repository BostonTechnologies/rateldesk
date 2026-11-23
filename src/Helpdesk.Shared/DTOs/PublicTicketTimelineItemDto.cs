using Helpdesk.Shared.Enums;

namespace Helpdesk.Shared.DTOs;

public sealed record PublicTicketTimelineItemDto(DateTimeOffset CreatedUtc, bool IsCustomer,
    TimelineEventType EventType, string? MessageHtml, string? MessageText, double? Hours);
