using Dodo.Primitives;
using Helpdesk.Shared.Enums;

namespace Helpdesk.Shared.Models;

public class SupportNotificationDelivery
{
    public string Id { get; set; } = Uuid.CreateVersion7().ToString();
    public string DeduplicationKey { get; set; } = string.Empty;
    public string TicketId { get; set; } = string.Empty;
    public string? TicketTrackingId { get; set; }
    public string? QueueTransitionId { get; set; }
    public SupportNotificationEventType EventType { get; set; }
    public SupportNotificationChannel Channel { get; set; } = SupportNotificationChannel.Email;
    public string? RecipientUserId { get; set; }
    public string RecipientEmail { get; set; } = string.Empty;
    public SupportNotificationDeliveryStatus Status { get; set; } = SupportNotificationDeliveryStatus.Pending;
    public DateTimeOffset? AttemptedUtc { get; set; }
    public DateTimeOffset? SentUtc { get; set; }
    public DateTimeOffset? FailedUtc { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedUtc { get; set; }
}
