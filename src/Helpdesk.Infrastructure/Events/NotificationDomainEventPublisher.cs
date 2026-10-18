using System.Text.Json;
using Helpdesk.Application.Events;
using Helpdesk.Application.Notifications;
using Helpdesk.Shared.DTOs.Notification;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Events;

public class NotificationDomainEventPublisher(
    INotificationService notifications,
    ILogger<NotificationDomainEventPublisher> logger) : IDomainEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly INotificationService _notifications = notifications;
    private readonly ILogger<NotificationDomainEventPublisher> _logger = logger;

    public async Task PublishAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions);
        var category = ResolveCategory(domainEvent);
        var correlationId = string.IsNullOrWhiteSpace(domainEvent.CorrelationId) ? null : domainEvent.CorrelationId;
        var reference = !string.IsNullOrWhiteSpace(domainEvent.Reference)
            ? domainEvent.Reference
            : domainEvent.EntityId;

        try
        {
            _logger.LogInformation(
                "Publishing domain event notification. EventType={EventType} Source={Source} TenantId={TenantId} EntityId={EntityId}",
                domainEvent.EventType,
                domainEvent.Source,
                domainEvent.TenantId,
                domainEvent.EntityId);

            await _notifications.CreateNotificationAsync(new CreateNotificationRequest
            {
                Title = domainEvent.EventType,
                Message = payload,
                Severity = MapSeverity(domainEvent),
                Category = category,
                Source = domainEvent.Source,
                Reference = reference,
                TenantId = domainEvent.TenantId,
                CorrelationId = correlationId
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish domain event notification. EventType={EventType} Source={Source} TenantId={TenantId} EntityId={EntityId}",
                domainEvent.EventType,
                domainEvent.Source,
                domainEvent.TenantId,
                domainEvent.EntityId);
        }
    }

    private static NotificationSeverity MapSeverity(DomainEvent domainEvent)
    {
        if (domainEvent.EventType is "DomainEvent.Workflow.RequestFailedByCriticalTask"
            or "DomainEvent.RequestTask.Failed"
            or "DomainEvent.RequestTask.AutomationFailed"
            or "DomainEvent.Orchestration.External orchestration.TestFailed")
        {
            return NotificationSeverity.Error;
        }

        if (domainEvent.EventType is "DomainEvent.RequestTask.Escalated"
            or "DomainEvent.RequestTask.SlaBreached"
            or "DomainEvent.RequestTask.RetryScheduled"
            or "DomainEvent.RequestTask.Blocked"
            or "DomainEvent.Workflow.RequestBlockedByTaskFailure"
            or "DomainEvent.Orchestration.External orchestration.CallbackRejected")
        {
            return NotificationSeverity.Warning;
        }

        return domainEvent switch
        {
            EmailQueuedEvent => NotificationSeverity.Info,
            EmailSentEvent { Success: false } => NotificationSeverity.Error,
            _ => NotificationSeverity.Success
        };
    }

    private static string ResolveCategory(DomainEvent domainEvent)
    {
        return domainEvent.Source switch
        {
            "SLA" => "DomainEvent.SLA",
            "Hangfire" => "DomainEvent.Hangfire",
            "RequestTask" => "DomainEvent.RequestTask",
            "RequestForm" => "DomainEvent.RequestForm",
            "SelfService" => "DomainEvent.SelfService",
            "TaskDashboard" => "DomainEvent.TaskDashboard",
            "Orchestration" => "DomainEvent.Orchestration",
            "Workflow" => "DomainEvent.Workflow",
            "Change" => "DomainEvent.Change",
            _ => "DomainEvent"
        };
    }
}
