namespace Helpdesk.Infrastructure.Email;

public sealed class PendingEmailOperationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ActorId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public long CreatedUnixMilliseconds { get; set; }
}

public sealed class PendingEmailOperationTarget
{
    public Guid OperationId { get; set; }
    public Guid DeliveryId { get; set; }
    public int Sequence { get; set; }
    public string Snapshot { get; set; } = string.Empty;
    public bool Eligible { get; set; }
    public string? Outcome { get; set; }
    public string? Message { get; set; }
}
