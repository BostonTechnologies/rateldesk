namespace Helpdesk.Application.Events;

public interface IDomainEventPublisher
{
    Task PublishAsync(DomainEvent domainEvent, CancellationToken ct);
    Task PublishRequiredAsync(DomainEvent domainEvent, CancellationToken ct) => PublishAsync(domainEvent, ct);
}
