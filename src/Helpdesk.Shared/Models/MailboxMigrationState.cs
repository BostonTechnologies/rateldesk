namespace Helpdesk.Shared.Models;

public sealed class MailboxMigrationState
{
    public int Id { get; set; } = 1;
    public bool Completed { get; set; }
    public string OutboundMailboxAddress { get; set; } = string.Empty;
}
