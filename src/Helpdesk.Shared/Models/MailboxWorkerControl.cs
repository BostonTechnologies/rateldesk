namespace Helpdesk.Shared.Models;

/// <summary>Instance-wide operator decision shared by every API replica.</summary>
public sealed class MailboxWorkerControl
{
    public int Id { get; set; } = 1;
    public bool Running { get; set; }
    public long Version { get; set; } = 1;
    public long UpdatedUnixMilliseconds { get; set; }
    public long? LastHeartbeatUnixMilliseconds { get; set; }
}
