using Helpdesk.Application.Events;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Workflow;

public sealed class FailurePolicyEngine(
    IRepository<Request> requests,
    IRepository<RequestTask> requestTasks,
    IDomainEventPublisher domainEvents,
    ICorrelationContext correlationContext,
    ILogger<FailurePolicyEngine> logger,
    IRepository<TicketTimelineEvent>? timelineEvents = null,
    IRequestSender? requestSender = null,
    IRepository<Incident>? incidents = null,
    ITicketNotificationService? ticketNotificationService = null) : IFailurePolicyEngine
{
    private const string DecisionRetryScheduled = "RetryScheduled";
    private const string DecisionFailRequest = "FailRequest";
    private const string DecisionBlockRequest = "BlockRequest";
    private const string DecisionContinue = "Continue";

    private readonly IRepository<Request> _requests = requests;
    private readonly IRepository<RequestTask> _requestTasks = requestTasks;
    private readonly IDomainEventPublisher _domainEvents = domainEvents;
    private readonly ICorrelationContext _correlationContext = correlationContext;
    private readonly ILogger<FailurePolicyEngine> _logger = logger;
    private readonly IRepository<TicketTimelineEvent>? _timelineEvents = timelineEvents;
    private readonly IRequestSender? _requestSender = requestSender;
    private readonly IRepository<Incident>? _incidents = incidents;
    private readonly ITicketNotificationService? _ticketNotificationService = ticketNotificationService;

    public async Task<FailurePolicyResult> OnTaskFailedAsync(string requestId, string taskId, CancellationToken ct)
    {
        var requestIdText = string.IsNullOrWhiteSpace(requestId) ? string.Empty : requestId.Trim();
        var taskIdText = string.IsNullOrWhiteSpace(taskId) ? string.Empty : taskId.Trim();

        var request = await _requests.GetAsync(requestIdText)
            ?? throw new KeyNotFoundException("Request not found");
        var task = await _requestTasks.GetAsync(taskIdText)
            ?? throw new KeyNotFoundException("Task not found");

        var now = DateTimeOffset.UtcNow;
        var correlationId = GetCorrelationId();
        var policy = ResolveFailurePolicy(task.FailurePolicy, task.IsCritical);

        if (task.Status == RequestTaskStatus.Failed
            && task.NextRetryAt.HasValue
            && task.MaxRetries.HasValue
            && task.RetryCount > 0
            && task.RetryCount <= task.MaxRetries.Value)
        {
            return new FailurePolicyResult
            {
                Decision = DecisionRetryScheduled,
                RetryScheduled = true,
                NextRetryAt = task.NextRetryAt,
                RequestStateChanged = false
            };
        }

        if (ShouldScheduleRetry(task))
        {
            task.RetryCount += 1;
            var retryDelay = task.RetryDelayMinutes ?? 0;
            task.NextRetryAt = now.AddMinutes(retryDelay);
            task.UpdatedAt = DateTime.UtcNow;
            await _requestTasks.UpdateAsync(task);

            await _domainEvents.PublishAsync(
                new RequestTaskRetryScheduledEvent(
                    task.Id,
                    requestIdText,
                    task.Name,
                    task.IsCritical,
                    policy,
                    task.RetryCount,
                    task.NextRetryAt,
                    request.OrganizationId,
                    now,
                    correlationId),
                ct);

            _logger.LogInformation(
                "Retry scheduled for task failure. RequestId={RequestId} TaskId={TaskId} RetryCount={RetryCount} NextRetryAt={NextRetryAt}",
                requestIdText,
                taskIdText,
                task.RetryCount,
                task.NextRetryAt);

            return new FailurePolicyResult
            {
                Decision = DecisionRetryScheduled,
                RetryScheduled = true,
                NextRetryAt = task.NextRetryAt,
                RequestStateChanged = false
            };
        }

        var requestStateChanged = false;
        switch (policy)
        {
            case DecisionFailRequest:
                requestStateChanged = await MarkRequestStateAsync(
                    request,
                    workflowStatus: "Failed",
                    blockReason: $"Critical task '{task.Name}' failed.",
                    ct);

                await _domainEvents.PublishAsync(
                    new WorkflowRequestFailedByCriticalTaskEvent(
                        request.Id,
                        task.Id,
                        task.Name,
                        task.IsCritical,
                        policy,
                        task.RetryCount,
                        task.NextRetryAt,
                        request.OrganizationId,
                        now,
                        correlationId),
                    ct);

                await CreateSelfServiceFailureIncidentAsync(request, task, now, ct);
                await CreateFailureTimelineNotificationAsync(
                    request,
                    task,
                    "Failed",
                    $"Critical task '{task.Name}' failed.",
                    now,
                    ct);
                break;

            case DecisionBlockRequest:
                requestStateChanged = await MarkRequestStateAsync(
                    request,
                    workflowStatus: "Blocked",
                    blockReason: $"Task '{task.Name}' failed.",
                    ct);

                await _domainEvents.PublishAsync(
                    new WorkflowRequestBlockedByTaskFailureEvent(
                        request.Id,
                        task.Id,
                        task.Name,
                        task.IsCritical,
                        policy,
                        task.RetryCount,
                        task.NextRetryAt,
                        request.OrganizationId,
                        now,
                        correlationId),
                    ct);

                await CreateSelfServiceFailureIncidentAsync(request, task, now, ct);
                await CreateFailureTimelineNotificationAsync(
                    request,
                    task,
                    "Blocked",
                    $"Task '{task.Name}' failed.",
                    now,
                    ct);
                break;

            default:
                await _domainEvents.PublishAsync(
                    new WorkflowTaskFailureIgnoredEvent(
                        request.Id,
                        task.Id,
                        task.Name,
                        task.IsCritical,
                        policy,
                        task.RetryCount,
                        task.NextRetryAt,
                        request.OrganizationId,
                        now,
                        correlationId),
                    ct);
                break;
        }

        _logger.LogInformation(
            "Failure policy applied. RequestId={RequestId} TaskId={TaskId} Policy={Policy} RequestStateChanged={RequestStateChanged}",
            requestIdText,
            taskIdText,
            policy,
            requestStateChanged);

        return new FailurePolicyResult
        {
            Decision = policy,
            RetryScheduled = false,
            NextRetryAt = null,
            RequestStateChanged = requestStateChanged
        };
    }

    private static bool ShouldScheduleRetry(RequestTask task)
    {
        if (task.Type != RequestTaskType.Automation)
        {
            return false;
        }

        if (AutomationTaskStatuses.RequiresOperatorAction(task.LastAutomationStatus))
        {
            return false;
        }

        if (!task.MaxRetries.HasValue || task.MaxRetries.Value <= 0)
        {
            return false;
        }

        return task.RetryCount < task.MaxRetries.Value;
    }

    private static string ResolveFailurePolicy(string? explicitPolicy, bool isCritical)
    {
        if (!string.IsNullOrWhiteSpace(explicitPolicy))
        {
            var normalized = explicitPolicy.Trim();
            if (string.Equals(normalized, DecisionFailRequest, StringComparison.OrdinalIgnoreCase))
            {
                return DecisionFailRequest;
            }

            if (string.Equals(normalized, DecisionBlockRequest, StringComparison.OrdinalIgnoreCase))
            {
                return DecisionBlockRequest;
            }

            if (string.Equals(normalized, DecisionContinue, StringComparison.OrdinalIgnoreCase))
            {
                return DecisionContinue;
            }
        }

        return isCritical ? DecisionFailRequest : DecisionBlockRequest;
    }

    private async Task<bool> MarkRequestStateAsync(
        Request request,
        string workflowStatus,
        string blockReason,
        CancellationToken ct)
    {
        var changed = request.WorkflowStatus != workflowStatus
            || request.WorkflowBlockReason != blockReason
            || request.State != TicketState.OnHold;

        request.WorkflowStatus = workflowStatus;
        request.WorkflowBlockReason = blockReason;
        request.WorkflowUpdatedAt = DateTimeOffset.UtcNow;
        request.State = TicketState.OnHold;
        request.UpdatedAt = DateTime.UtcNow;

        await _requests.UpdateAsync(request);
        return changed;
    }

    private async Task CreateFailureTimelineNotificationAsync(
        Request request,
        RequestTask task,
        string workflowStatus,
        string failureLine,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        if (_requestSender is null)
        {
            return;
        }

        if (await HasExistingFailureTimelineNotificationAsync(request, task))
        {
            return;
        }

        var message = string.Join(
            Environment.NewLine,
            $"Workflow: {workflowStatus}",
            failureLine,
            $"Updated {updatedAt.LocalDateTime:g}");

        await _requestSender.Send(
            new CreateWorkLogCommand(
                request.Id,
                0,
                message,
                "system",
                "System",
                NotifyCustomer: true,
                EventType: TimelineEventType.SystemNotification,
                EventCreatedByUserId: "system",
                EventCreatedByUserName: "System"),
            ct);
    }

    private async Task CreateSelfServiceFailureIncidentAsync(
        Request request,
        RequestTask task,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RequestFormId) || _requestSender is null)
        {
            return;
        }

        if (await HasExistingFailureTimelineNotificationAsync(request, task) ||
            await HasExistingFailureIncidentAsync(request))
        {
            return;
        }

        var marker = BuildFailureIncidentMarker(request);
        var failureReason = FirstNonEmpty(
            task.FailureReason,
            request.WorkflowBlockReason,
            $"Task '{task.Name}' failed.")!;
        var description = string.Join(
            Environment.NewLine,
            $"Self-service automation failed for request {request.TrackingId}.",
            $"Request: {request.Title}",
            $"Task: {task.Name}",
            $"Failure: {failureReason}",
            $"Updated: {updatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC",
            marker);

        var incident = await _requestSender.Send(
            new CreateIncidentCommand(
                $"Self-service automation failed for {request.TrackingId}",
                description,
                request.Priority,
                request.CustomerId,
                request.OrganizationId,
                request.LinkedAssetIds,
                null,
                null,
                "Self-service automation failure",
                request.RequesterEmail,
                request.CcRecipients,
                null),
            ct);

        if (IsRuntimeTimeoutFailure(task.FailureReason))
        {
            task.TimeoutIncidentId = incident.Id;
            await _requestTasks.UpdateAsync(task);
        }

        if (_ticketNotificationService is not null && !string.IsNullOrWhiteSpace(request.RequesterEmail))
        {
            await _ticketNotificationService.SendSelfServiceRequestFailedAsync(
                request,
                incident,
                failureReason,
                request.RequesterEmail,
                request.RequesterEmail,
                request.CcRecipients,
                ct);
        }
    }

    private async Task<bool> HasExistingFailureTimelineNotificationAsync(Request request, RequestTask task)
    {
        if (_timelineEvents is null)
        {
            return false;
        }

        return (await _timelineEvents.GetAllAsync())
            .Any(x =>
                x.TicketId == request.Id &&
                x.EventType == TimelineEventType.SystemNotification &&
                x.MessageText != null &&
                x.MessageText.Contains("Workflow:", StringComparison.OrdinalIgnoreCase) &&
                x.MessageText.Contains($"Task '{task.Name}' failed.", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> HasExistingFailureIncidentAsync(Request request)
    {
        if (_incidents is null)
        {
            return false;
        }

        var marker = BuildFailureIncidentMarker(request);
        return (await _incidents.GetAllAsync())
            .Any(x => x.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildFailureIncidentMarker(Request request) =>
        $"Source self-service request: {request.Id}";

    private static bool IsRuntimeTimeoutFailure(string? failureReason)
        => !string.IsNullOrWhiteSpace(failureReason)
           && failureReason.Contains("timeout", StringComparison.OrdinalIgnoreCase)
           && failureReason.Contains("Expected runtime", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private string GetCorrelationId()
    {
        return _correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }
}
