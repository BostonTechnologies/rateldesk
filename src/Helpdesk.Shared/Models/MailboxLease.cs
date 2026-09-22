namespace Helpdesk.Shared.Models;

/// <summary>Durable ownership of one mailbox. Provisioned together with its configuration.</summary>
public sealed class MailboxLease
{
    public Guid MailboxId { get; set; }
    public string? Owner { get; set; }
    public long Fence { get; set; }
    public long ExpiresUnixMilliseconds { get; set; }
}
