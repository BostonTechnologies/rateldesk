using System.Diagnostics;
using System.Reflection;
using Helpdesk.Application.Events;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Workflow;

public sealed class WorkflowEngine(
    IWorkflowDependencyEvaluator dependencyEvaluator,
    IWorkflowConditionEvaluator conditionEvaluator,
    IRequestTaskApprovalService approvalService,
    IRepository<Request> requests,
    IRepository<RequestTask> requestTasks,
    IRequestTaskLifecycleService lifecycleService,
    IRequestTaskStateService requestStateEvaluator,
    IDomainEventPublisher domainEvents,
    ICorrelationContext correlationContext,
    ILogger<WorkflowEngine> logger) : IWorkflowEngine
{
    private readonly IWorkflowDependencyEvaluator _dependencyEvaluator = dependencyEvaluator;
    private readonly IWorkflowConditionEvaluator _conditionEvaluator = conditionEvaluator;
    private readonly IRequestTaskApprovalService _approvalService = approvalService;
    private readonly IRepository<Request> _requests = requests;
    private readonly IRepository<RequestTask> _requestTasks = requestTasks;
    private readonly IRequestTaskLifecycleService _lifecycleService = lifecycleService;
    private readonly IRequestTaskStateService _requestStateEvaluator = requestStateEvaluator;
    private readonly IDomainEventPublisher _domainEvents = domainEvents;
    private readonly ICorrelationContext _correlationContext = correlationContext;
    private readonly ILogger<WorkflowEngine> _logger = logger;

    public async Task<WorkflowRunResult> RunAsync(string requestId, WorkflowRunReason reason, CancellationToken ct)
    {
        var requestIdText = string.IsNullOrWhiteSpace(requestId) ? string.Empty : requestId.Trim();
        var startedAt = Stopwatch.StartNew();
        LogRepositoryContextHash();

        if (string.IsNullOrWhiteSpace(requestIdText))
        {
            _logger.LogError("Workflow run failed. Request id was empty.");
            return WorkflowRunResult.Failed(string.Empty);
        }

        var request = await _requests.GetAsync(requestIdText);
        if (request is null)
        {
            _logger.LogError("Workflow run failed. Request {RequestId} not found.", requestIdText);
            return WorkflowRunResult.Failed(requestIdText);
        }
        var correlationId = GetCorrelationId();
        var isRequestProgressionBlocked = string.Equals(request.WorkflowStatus, "Blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.WorkflowStatus, "Failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.WorkflowStatus, "Cancelled", StringComparison.OrdinalIgnoreCase);

        ct.ThrowIfCancellationRequested();
        _ = (await _requestTasks.GetAllAsync())
            .Where(x => x.RequestId == requestIdText)
            .OrderBy(x => x.Order)
            .ToList();

        var dependencyResult = await _dependencyEvaluator.EvaluateAsync(requestIdText, ct);
        var unblockedCount = dependencyResult.UnblockedCount;

        ct.ThrowIfCancellationRequested();
        var tasksAfterDependencies = (await _requestTasks.GetAllAsync())
            .Where(x => x.RequestId == requestIdText)
            .OrderBy(x => x.Order)
            .ToList();
        var skippedCount = 0;
        var skipTimestamp = DateTimeOffset.UtcNow;

        foreach (var task in tasksAfterDependencies)
        {
            if (task.Status != RequestTaskStatus.Pending
                || task.IsBlocked
                || string.IsNullOrWhiteSpace(task.ConditionExpression))
            {
                continue;
            }

            if (_conditionEvaluator.Evaluate(request, task))
            {
                continue;
            }

            task.Status = RequestTaskStatus.Skipped;
            task.State = TicketState.Resolved;
            task.CompletedAt ??= skipTimestamp;
            task.UpdatedAt = DateTime.UtcNow;
            task.ResultJson = $"Skipped: condition '{task.ConditionExpression}' evaluated false.";
            await _requestTasks.UpdateAsync(task);

            await _domainEvents.PublishAsync(
                new RequestTaskSkippedEvent(
                    task.Id,
                    requestIdText,
                    task.ConditionExpression,
                    request.OrganizationId,
                    skipTimestamp,
                    correlationId),
                ct);

            skippedCount++;
        }

        if (skippedCount > 0)
        {
            var secondDependencyResult = await _dependencyEvaluator.EvaluateAsync(requestIdText, ct);
            unblockedCount += secondDependencyResult.UnblockedCount;

            tasksAfterDependencies = (await _requestTasks.GetAllAsync())
                .Where(x => x.RequestId == requestIdText)
                .OrderBy(x => x.Order)
                .ToList();
        }

        var autoStartedCount = 0;
        if (!isRequestProgressionBlocked)
        {
            var pendingApprovalTasks = tasksAfterDependencies
                .Where(x => x.Type == RequestTaskType.Approval && x.Status == RequestTaskStatus.PendingApproval)
                .ToList();
            if (pendingApprovalTasks.Count > 0)
            {
                foreach (var approvalTask in pendingApprovalTasks)
                {
                    try
                    {
                        await _approvalService.StartApprovalAsync(approvalTask, ct);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Workflow approval pause hardening failed. RequestId={RequestId} TaskId={TaskId}",
                            requestIdText,
                            approvalTask.Id);
                    }
                    catch (KeyNotFoundException ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Workflow approval pause hardening could not load request approval context. RequestId={RequestId} TaskId={TaskId}",
                            requestIdText,
                            approvalTask.Id);
                    }
                }

                _logger.LogInformation("Workflow progression paused by pending approval. RequestId={RequestId}", requestIdText);
            }
            else
            {
                foreach (var task in tasksAfterDependencies)
                {
                    if ((task.Type != RequestTaskType.Automation && task.Type != RequestTaskType.Approval)
                        || task.Status != RequestTaskStatus.Pending
                        || (task.Type == RequestTaskType.Automation && task.AutoStart == false)
                        || task.IsBlocked)
                    {
                        continue;
                    }

                    try
                    {
                        if (task.Type == RequestTaskType.Approval)
                        {
                            await _approvalService.StartApprovalAsync(task, ct);
                            autoStartedCount++;
                            break;
                        }

                        if (RequestTaskApprovalGate.IsBlockedByEarlierApproval(task, tasksAfterDependencies))
                        {
                            _logger.LogInformation(
                                "Workflow auto-start skipped by earlier approval gate. RequestId={RequestId} TaskId={TaskId}",
                                requestIdText,
                                task.Id);
                            continue;
                        }

                        await _lifecycleService.StartAsync(task.Id, ct);
                        autoStartedCount++;
                    }
                    catch (AutomationBindingNotReadyException ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Workflow auto-start blocked by automation binding readiness. RequestId={RequestId} TaskId={TaskId}",
                            requestIdText,
                            task.Id);

                        try
                        {
                            await _lifecycleService.FailAsync(task.Id, ex.Message, ct);
                        }
                        catch (Exception failEx) when (failEx is InvalidOperationException or KeyNotFoundException)
                        {
                            _logger.LogDebug(
                                failEx,
                                "Failed to persist workflow auto-start binding failure. RequestId={RequestId} TaskId={TaskId}",
                                requestIdText,
                                task.Id);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Another run may have started/transitioned the task first.
                    }
                    catch (KeyNotFoundException)
                    {
                        // Task no longer exists.
                    }
                }
            }
        }

        var parentStateChanged = isRequestProgressionBlocked
            ? false
            : await _requestStateEvaluator.EvaluateParentRequestState(requestIdText, ct);
        var timestamp = DateTimeOffset.UtcNow;

        var result = new WorkflowRunResult
        {
            RequestId = requestIdText,
            UnblockedCount = unblockedCount,
            AutoStartedCount = autoStartedCount,
            ParentStateChanged = parentStateChanged
        };

        // An accepted approval can be replayed to resume a failed continuation.
        // Re-evaluating an already settled workflow must not create another
        // user-visible Workflow.Evaluated notification for the same decision.
        if (reason != WorkflowRunReason.ApprovalReceived ||
            result.UnblockedCount > 0 || result.AutoStartedCount > 0 || result.ParentStateChanged || skippedCount > 0)
        {
            await _domainEvents.PublishAsync(
                new WorkflowEvaluatedEvent(
                    requestIdText,
                    reason,
                    result.UnblockedCount,
                    result.AutoStartedCount,
                    result.ParentStateChanged,
                    request.OrganizationId,
                    timestamp,
                    correlationId),
                ct);
        }

        if (result.UnblockedCount > 0 || result.AutoStartedCount > 0 || result.ParentStateChanged || skippedCount > 0)
        {
            await _domainEvents.PublishAsync(
                new WorkflowProgressedEvent(
                    requestIdText,
                    reason,
                    result.UnblockedCount,
                    result.AutoStartedCount,
                    result.ParentStateChanged,
                    request.OrganizationId,
                    timestamp,
                    correlationId),
                ct);
        }

        startedAt.Stop();
        _logger.LogInformation(
            "Workflow run completed. RequestId={RequestId} Reason={Reason} UnblockedCount={UnblockedCount} SkippedCount={SkippedCount} AutoStartedCount={AutoStartedCount} ParentStateChanged={ParentStateChanged} DurationMs={DurationMs}",
            requestIdText,
            reason,
            result.UnblockedCount,
            skippedCount,
            result.AutoStartedCount,
            result.ParentStateChanged,
            startedAt.ElapsedMilliseconds);

        return result;
    }

    private string GetCorrelationId()
    {
        return _correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private void LogRepositoryContextHash()
    {
        // Temporary diagnostics for DbContext scope reuse without taking an infra-layer dependency.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var contextField = _requests.GetType().GetField("_context", flags);
        if (contextField?.GetValue(_requests) is { } dbContext)
        {
            _logger.LogInformation("WorkflowEngine DbContext instance hash: {Hash}", dbContext.GetHashCode());
        }
    }
}
