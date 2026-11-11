namespace Helpdesk.Application.Events;

/// <summary>Invalidates authorized request snapshots after an operator update commits.</summary>
public sealed record RequestSnapshotChangedEvent(string RequestId, string? OrganizationId,
    DateTimeOffset Timestamp, string CorrelationId)
    : DomainEvent("DomainEvent.Request.Updated", "Request", OrganizationId, RequestId,
        CorrelationId, RequestId, Timestamp.UtcDateTime);
