using Helpdesk.Shared.Enums;

namespace Helpdesk.Shared.DTOs.Worklog;

public class TicketTimelineEventDto
{
    public Guid Id { get; set; }
    public string TicketId { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public string CreatedByUserName { get; set; } = string.Empty;
    public TimelineEventType EventType { get; set; }
    public string? MessageHtml { get; set; }
    public string? MessageText { get; set; }
    public string? Message
    {
        get => MessageText;
        set => MessageText = value;
    }
    public double Hours { get; set; }
    public string? TechnicianName { get; set; }
    public EmailDeliveryStatus? EmailStatus { get; set; }
    public string? EmailRecipient { get; set; }
    public string[]? Recipients { get; set; }
    public string? PrimaryRecipient { get; set; }
    public string[]? CcRecipients { get; set; }
    public int RetryCount { get; set; }
    public bool IsRetryable { get; set; }
    public string? DeliveryErrorCode { get; set; }
    public string[]? AcceptedRecipients { get; set; }
    public string[]? RejectedRecipients { get; set; }
    public bool RecipientOutcomeUnavailable { get; set; }
    public string? DeliveryReviewReason { get; set; }
}
