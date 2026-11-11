using Helpdesk.Application.Events;
using Helpdesk.Application.Notifications;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.RequestTasks;

public sealed class TaskEscalationProcessor(
    IRepository<RequestTask> requestTasks,
    IRepository<Request> requests,
    IRepository<User> users,
    INotificationService notifications,
    IDomainEventPublisher domainEvents,
    ICorrelationContext correlationContext,
    ILogger<TaskEscalationProcessor> logger) : ITaskEscalationProcessor
{
    private readonly IRepository<RequestTask> _requestTasks = requestTasks;
    private readonly IRepository<Request> _requests = requests;
    private readonly IRepository<User> _users = users;
    private readonly INotificationService _notifications = notifications;
    private readonly IDomainEventPublisher _domainEvents = domainEvents;
    private readonly ICorrelationContext _correlationContext = correlationContext;
    private readonly ILogger<TaskEscalationProcessor> _logger = logger;

    public async Task ProcessAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var correlationId = GetCorrelationId();

        var inProgressTasks = (await _requestTasks.GetAllAsync())
            .Where(task => task.Status == RequestTaskStatus.InProgress)
            .ToList();

        if (inProgressTasks.Count == 0)
        {
            return;
        }

        var requestIds = inProgressTasks
            .Select(task => task.RequestId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requestMap = (await _requests.GetAllAsync())
            .Where(request => requestIds.Contains(request.Id))
            .ToDictionary(request => request.Id, StringComparer.OrdinalIgnoreCase);

        var allUsers = (await _users.GetAllAsync()).Where(user => !user.IsDeleted).ToList();

        var escalatedCount = 0;
        var breachedCount = 0;

        foreach (var task in inProgressTasks)
        {
            var changed = false;

            if (!task.SlaBreached && task.DueAt.HasValue && now >= task.DueAt.Value)
            {
                task.SlaBreached = true;
                task.UpdatedAt = DateTime.UtcNow;
                changed = true;
                breachedCount++;

                await _domainEvents.PublishAsync(
                    new RequestTaskSlaBreachedEvent(
                        task.Id,
                        task.RequestId,
                        task.DueAt,
                        task.OrganizationId,
                        now,
                        correlationId),
                    ct);
            }

            if (!task.Escalated
                && task.SlaStartedAt.HasValue
                && task.EscalateAfterMinutes is > 0
                && now >= task.SlaStartedAt.Value.AddMinutes(task.EscalateAfterMinutes.Value))
            {
                task.Escalated = true;
                task.EscalatedAt = now;
                task.UpdatedAt = DateTime.UtcNow;
                changed = true;
                escalatedCount++;

                await _domainEvents.PublishAsync(
                    new RequestTaskEscalatedEvent(
                        task.Id,
                        task.RequestId,
                        task.DueAt,
                        task.OrganizationId,
                        now,
                        correlationId),
                    ct);

                await SendEscalationNotificationsAsync(task, requestMap, allUsers, correlationId, ct);
            }

            if (!changed)
            {
                continue;
            }

            await _requestTasks.UpdateAsync(task);
        }

        if (escalatedCount > 0 || breachedCount > 0)
        {
            _logger.LogInformation(
                "Task SLA processor updated tasks. EscalatedCount={EscalatedCount} BreachedCount={BreachedCount}",
                escalatedCount,
                breachedCount);
        }
    }

    private async Task SendEscalationNotificationsAsync(
        RequestTask task,
        IReadOnlyDictionary<string, Request> requestMap,
        IReadOnlyCollection<User> allUsers,
        string correlationId,
        CancellationToken ct)
    {
        var recipients = ResolveRecipients(task, allUsers);
        if (recipients.Count == 0)
        {
            return;
        }

        var trackingId = requestMap.TryGetValue(task.RequestId, out var request)
            ? request.TrackingId
            : task.RequestId;

        var message = $"Task '{task.Title}' in Request {trackingId} has exceeded its SLA escalation threshold.";

        foreach (var recipientId in recipients)
        {
            await _notifications.CreateNotificationAsync(new CreateNotificationRequest
            {
                UserId = recipientId,
                Title = "Task SLA Escalation",
                Message = message,
                Severity = NotificationSeverity.Warning,
                Source = "RequestTaskSla",
                Category = "TaskSlaEscalation",
                TenantId = task.OrganizationId,
                Reference = task.RequestId,
                CorrelationId = correlationId
            }, ct);
        }
    }

    private static List<string> ResolveRecipients(RequestTask task, IReadOnlyCollection<User> allUsers)
    {
        if (!string.IsNullOrWhiteSpace(task.EscalationUserId))
        {
            return [task.EscalationUserId];
        }

        if (string.IsNullOrWhiteSpace(task.EscalationRole))
        {
            return new List<string>();
        }

        return allUsers
            .Where(user => string.Equals(user.Role, task.EscalationRole, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(task.OrganizationId)
                    || string.Equals(user.OrganizationId, task.OrganizationId, StringComparison.OrdinalIgnoreCase)))
            .Select(user => user.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetCorrelationId()
    {
        return _correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }
}
