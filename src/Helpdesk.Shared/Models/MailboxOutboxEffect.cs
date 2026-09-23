namespace Helpdesk.Shared.Models;

public enum MailboxEffectKind { Email, Timeline, Notification }
public enum MailboxEffectState { Pending, InFlight, Completed, Exhausted, NeedsReview }

public sealed class MailboxOutboxEffect
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReceiptId { get; set; }
    public string EffectKey { get; set; } = string.Empty;
    public MailboxEffectKind Kind { get; set; }
    public string Payload { get; set; } = string.Empty;
    public MailboxEffectState State { get; set; }
    public int Attempts { get; set; }
    public long Fence { get; set; }
    public string? Owner { get; set; }
    public long AvailableUnixMilliseconds { get; set; }
    public long LeaseExpiresUnixMilliseconds { get; set; }
    public string? LastErrorCode { get; set; }
    public Guid? DeliveryEventId { get; set; }
}
