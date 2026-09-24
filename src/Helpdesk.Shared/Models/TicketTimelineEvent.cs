using Helpdesk.Shared.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace Helpdesk.Shared.Models;

public class TicketTimelineEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string TicketId { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string CreatedByUserId { get; set; } = string.Empty;

    public string CreatedByUserName { get; set; } = string.Empty;

    public TimelineEventType EventType { get; set; }

    public string? MessageHtml { get; set; }

    public string? MessageText { get; set; }

    [NotMapped]
    public string? Message
    {
        get => MessageText;
        set => MessageText = value;
    }

    public EmailDeliveryStatus? EmailStatus { get; set; }

    public string? EmailRecipient { get; set; }

    public int RetryCount { get; set; }

    public DateTimeOffset? LastRetryUtc { get; set; }

    public string? RetryError { get; set; }

    public bool IsRetryable =>
        EventType == TimelineEventType.EmailDelivery &&
        EmailStatus == EmailDeliveryStatus.Failed &&
        RetryError is not ("SenderRouteChanged" or "DispatchOutcomeUnknown" or "SubmissionOutcomeUnknown" or
            "SmtpPartialRecipientAcceptance");
}
