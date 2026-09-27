using Helpdesk.Application.Events;
using Helpdesk.Application.Workflow;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.RequestTasks;

public sealed class RequestTaskRetryProcessor(
    IRepository<RequestTask> requestTasks,
    IRequestTaskLifecycleService lifecycleService,
    IWorkflowEngine workflowEngine,
    IDomainEventPublisher domainEvents,
    ICorrelationContext correlationContext,
    ILogger<RequestTaskRetryProcessor> logger) : IRequestTaskRetryProcessor
{
    private readonly IRepository<RequestTask> _requestTasks = requestTasks;
    private readonly IRequestTaskLifecycleService _lifecycleService = lifecycleService;
    private readonly IWorkflowEngine _workflowEngine = workflowEngine;
    private readonly IDomainEventPublisher _domainEvents = domainEvents;
    private readonly ICorrelationContext _correlationContext = correlationContext;
    private readonly ILogger<RequestTaskRetryProcessor> _logger = logger;

    public async Task ProcessAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var correlationId = GetCorrelationId();

        var dueRetries = (await _requestTasks.GetAllAsync())
            .Where(task => task.Type == RequestTaskType.Automation
                && task.Status == RequestTaskStatus.Failed
                && !AutomationTaskStatuses.RequiresOperatorAction(task.LastAutomationStatus)
                && task.NextRetryAt.HasValue
                && task.NextRetryAt <= now)
            .ToList();

        if (dueRetries.Count == 0)
        {
            return;
        }

        var retriedCount = 0;

        foreach (var task in dueRetries)
        {
            if (task.MaxRetries.HasValue && task.RetryCount > task.MaxRetries.Value)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.RequestId))
            {
                continue;
            }

            try
            {
                await _lifecycleService.RetryAsync(task.Id, ct);

                await _domainEvents.PublishAsync(
                    new RequestTaskRetryTriggeredEvent(
                        task.Id,
                        task.RequestId,
                        task.Name,
                        task.IsCritical,
                        ResolveFailurePolicy(task.FailurePolicy, task.IsCritical),
                        task.RetryCount,
                        null,
                        task.OrganizationId,
                        DateTimeOffset.UtcNow,
                        correlationId),
                    ct);

                await _lifecycleService.StartAsync(task.Id, ct);
                await _workflowEngine.RunAsync(task.RequestId, WorkflowRunReason.TaskRetried, ct);
                retriedCount++;
            }
            catch (AutomationBindingNotReadyException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Retry start blocked by automation binding readiness. TaskId={TaskId} RequestId={RequestId}",
                    task.Id,
                    task.RequestId);

                try
                {
                    await _lifecycleService.FailAsync(task.Id, ex.Message, ct);
                    await _workflowEngine.RunAsync(task.RequestId, WorkflowRunReason.TaskFailed, ct);
                }
                catch (Exception failEx) when (failEx is InvalidOperationException or KeyNotFoundException)
                {
                    _logger.LogDebug(
                        failEx,
                        "Skipped retry failure persistence for task. TaskId={TaskId} RequestId={RequestId}",
                        task.Id,
                        task.RequestId);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                _logger.LogDebug(
                    ex,
                    "Skipped retry trigger for task. TaskId={TaskId} RequestId={RequestId}",
                    task.Id,
                    task.RequestId);
            }
        }

        if (retriedCount > 0)
        {
            _logger.LogInformation(
                "Triggered retries for automation tasks. Count={Count}",
                retriedCount);
        }
    }

    private static string ResolveFailurePolicy(string? explicitPolicy, bool isCritical)
    {
        if (!string.IsNullOrWhiteSpace(explicitPolicy))
        {
            var normalized = explicitPolicy.Trim();
            if (string.Equals(normalized, "FailRequest", StringComparison.OrdinalIgnoreCase))
            {
                return "FailRequest";
            }

            if (string.Equals(normalized, "BlockRequest", StringComparison.OrdinalIgnoreCase))
            {
                return "BlockRequest";
            }

            if (string.Equals(normalized, "Continue", StringComparison.OrdinalIgnoreCase))
            {
                return "Continue";
            }
        }

        return isCritical ? "FailRequest" : "BlockRequest";
    }

    private string GetCorrelationId()
    {
        return _correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }
}
