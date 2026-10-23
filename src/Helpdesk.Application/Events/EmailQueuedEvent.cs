namespace Helpdesk.Application.Events;

/// <summary>Durable acceptance only; the provider has not yet confirmed submission.</summary>
public sealed record EmailQueuedEvent(
    string Recipient,
    string TemplateName,
    string? TenantId,
    string? Reference,
    string CorrelationId)
    : DomainEvent(
        EventType: "EmailQueued",
        Source: "Infrastructure",
        TenantId: TenantId,
        EntityId: null,
        CorrelationId: CorrelationId,
        Reference: Reference,
        OccurredUtc: DateTime.UtcNow);
