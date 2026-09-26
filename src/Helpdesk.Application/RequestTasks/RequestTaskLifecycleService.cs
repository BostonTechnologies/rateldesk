using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.Workflow;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.RequestTasks;

public sealed class RequestTaskLifecycleService(
    IRepository<RequestTask> requestTasks,
    IRepository<Request> requests,
    IOrchestrationConnectivityService orchestrationConnectivityService,
    IAutomationBindingService automationBindingService,
    IRequestTaskPayloadBuilder payloadBuilder,
    IOrchestrationInternalClient orchestrationClient,
    IFailurePolicyEngine failurePolicyEngine,
    IDomainEventPublisher domainEvents,
    ICorrelationContext correlationContext,
    ILogger<RequestTaskLifecycleService> logger) : IRequestTaskLifecycleService
{
    private readonly IRepository<RequestTask> _requestTasks = requestTasks;
    private readonly IRepository<Request> _requests = requests;
    private readonly IOrchestrationConnectivityService _orchestrationConnectivityService = orchestrationConnectivityService;
    private readonly IAutomationBindingService _automationBindingService = automationBindingService;
    private readonly IRequestTaskPayloadBuilder _payloadBuilder = payloadBuilder;
    private readonly IOrchestrationInternalClient _stoClient = orchestrationClient;
    private readonly IFailurePolicyEngine _failurePolicyEngine = failurePolicyEngine;
    private readonly IDomainEventPublisher _domainEvents = domainEvents;
    private readonly ICorrelationContext _correlationContext = correlationContext;
    private readonly ILogger<RequestTaskLifecycleService> _logger = logger;

    public async Task<RequestTask> StartAsync(string taskId, CancellationToken ct)
    {
        var task = await GetRequiredTaskAsync(taskId);

        if (task.Status != RequestTaskStatus.Pending)
        {
            throw new InvalidOperationException("Task must be in Pending status to start.");
        }

        if (task.IsBlocked)
        {
            throw new InvalidOperationException("Task is blocked by dependencies.");
        }

        AutomationBindingSyncState? automationBindingSyncState = null;
        if (task.Type == RequestTaskType.Approval)
        {
            throw new InvalidOperationException("Approval tasks are started by the approval workflow.");
        }

        var requestTasks = (await _requestTasks.GetAllAsync())
            .Where(x => x.RequestId == task.RequestId)
            .ToList();
        if (RequestTaskApprovalGate.IsBlockedByEarlierApproval(task, requestTasks))
        {
            throw new InvalidOperationException("Task is blocked by an earlier approval step.");
        }

        if (task.Type == RequestTaskType.Automation)
        {
            automationBindingSyncState = await EnsureAutomationBindingReadyAsync(task, ct);
        }

        var correlationId = GetCorrelationId();

        task.Status = RequestTaskStatus.InProgress;
        task.State = TicketState.InProgress;
        task.StartedAt = DateTimeOffset.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;

        if (task.TaskSlaMinutes is > 0)
        {
            task.SlaStartedAt = task.StartedAt;
            task.DueAt = task.StartedAt.Value.AddMinutes(task.TaskSlaMinutes.Value);
            task.Escalated = false;
            task.EscalatedAt = null;
            task.SlaBreached = false;
        }

        await _requestTasks.UpdateAsync(task);

        await _domainEvents.PublishAsync(
            new RequestTaskStartedEvent(
                task.Id,
                task.RequestId,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId),
            ct);

        if (task.DueAt.HasValue)
        {
            await _domainEvents.PublishAsync(
                new RequestTaskSlaStartedEvent(
                    task.Id,
                    task.RequestId,
                    task.DueAt.Value,
                    task.OrganizationId,
                    DateTimeOffset.UtcNow,
                    correlationId),
                ct);
        }

        if (task.Type != RequestTaskType.Automation)
        {
            return task;
        }

        var settings = await _orchestrationConnectivityService.GetResolvedOrchestrationSettingsAsync(ct);
        if (!settings.Enabled)
        {
            return await HandleAutomationSubmissionFailureAsync(task, "External orchestration disabled", correlationId, ct);
        }

        var payloadResult = await _payloadBuilder.BuildAsync(task, correlationId, ct);
        if (!payloadResult.Success)
        {
            return await HandleAutomationSubmissionFailureAsync(
                task,
                payloadResult.Error ?? "Payload build failed.",
                correlationId,
                ct);
        }

        try
        {
            _logger.LogInformation(
                "External orchestration automation submit started. RequestId={RequestId} TaskId={TaskId} AutomationBindingId={AutomationBindingId} OrchestrationRequestDefinitionId={OrchestrationRequestDefinitionId} OrchestrationJobDefinitionId={OrchestrationJobDefinitionId} AutomationBindingSyncState={AutomationBindingSyncState} InputKeyCount={InputKeyCount} CorrelationId={CorrelationId}",
                task.RequestId,
                task.Id,
                payloadResult.AutomationBindingId,
                payloadResult.OrchestrationRequestDefinitionId,
                payloadResult.OrchestrationJobDefinitionId,
                automationBindingSyncState,
                CountInputKeys(payloadResult.PayloadJson),
                correlationId);

            var ingestResult = await _stoClient.IngestAsync(settings, new Helpdesk.Shared.DTOs.Orchestration.OrchestrationIngestRequest
            {
                RequestId = task.RequestId,
                RequestTaskId = task.Id,
                CorrelationId = correlationId,
                AutomationBindingId = payloadResult.AutomationBindingId,
                OrchestrationRequestDefinitionId = payloadResult.OrchestrationRequestDefinitionId,
                OrchestrationJobDefinitionId = payloadResult.OrchestrationJobDefinitionId,
                JobName = payloadResult.JobName,
                PayloadJson = payloadResult.PayloadJson,
                CallbackUrl = null,
                ExpectedRuntimeSeconds = task.ExpectedRuntimeSeconds,
                GraceSeconds = task.GraceSeconds,
                HardTimeoutSeconds = task.HardTimeoutSeconds
            }, ct);

            task.AutomationBindingId = payloadResult.AutomationBindingId;
            task.OrchestrationRequestDefinitionId = payloadResult.OrchestrationRequestDefinitionId;
            task.OrchestrationJobDefinitionId = payloadResult.OrchestrationJobDefinitionId;
            task.OrchestrationExternalRequestId = FirstNonEmpty(ingestResult.RequestId);
            task.OrchestrationExternalRunId = FirstNonEmpty(ingestResult.RunId, ingestResult.ExecutionId);
            task.OrchestratorExecutionId = FirstNonEmpty(ingestResult.ExecutionId)
                ?? throw new OrchestrationAcknowledgementException("The NetRatel acknowledgement did not contain an execution identifier.");
            task.LastAutomationStatus = FirstNonEmpty(ingestResult.Status) ?? "submitted";
            task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
            task.ResultJson = FirstNonEmpty(ingestResult.Message, ingestResult.Status);
            task.UpdatedAt = DateTime.UtcNow;
            await _requestTasks.UpdateAsync(task);

            _logger.LogInformation(
                "External orchestration automation submit succeeded. RequestId={RequestId} TaskId={TaskId} OrchestrationRequestId={OrchestrationRequestId} OrchestrationRunId={OrchestrationRunId} CorrelationId={CorrelationId}",
                task.RequestId,
                task.Id,
                task.OrchestrationExternalRequestId,
                task.OrchestrationExternalRunId,
                correlationId);

            await _domainEvents.PublishAsync(
                new RequestTaskAutomationSubmittedEvent(
                    task.Id,
                    task.RequestId,
                    task.OrchestratorExecutionId,
                    task.OrganizationId,
                    DateTimeOffset.UtcNow,
                    correlationId),
                ct);

            await _domainEvents.PublishAsync(
                new RequestTaskAutomationRunningEvent(
                    task.Id,
                    task.RequestId,
                    task.OrchestratorExecutionId,
                    task.OrganizationId,
                    DateTimeOffset.UtcNow,
                    correlationId),
                ct);

            return task;
        }
        catch (OrchestrationSubmissionUncertainException ex)
        {
            _logger.LogWarning(
                "External orchestration automation submit outcome is uncertain and requires reconciliation. RequestId={RequestId} TaskId={TaskId} CorrelationId={CorrelationId}",
                task.RequestId,
                task.Id,
                correlationId);

            return await HandleUncertainAutomationSubmissionAsync(task, Truncate(ex.Message, 500), correlationId, ct);
        }
        catch (OrchestrationSubmissionRejectedException ex)
        {
            _logger.LogWarning(
                "External orchestration automation submit was explicitly rejected before admission. RequestId={RequestId} TaskId={TaskId} CorrelationId={CorrelationId}",
                task.RequestId,
                task.Id,
                correlationId);

            return await HandleRejectedAutomationSubmissionAsync(task, Truncate(ex.Message, 500), correlationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "External orchestration automation submit failed and requires manual retry. RequestId={RequestId} TaskId={TaskId} CorrelationId={CorrelationId} ExceptionType={ExceptionType}",
                task.RequestId,
                task.Id,
                correlationId,
                ex.GetType().Name);

            return await HandleAutomationSubmissionFailureAsync(
                task,
                "External orchestration submission failed. Check provider status before retrying.",
                correlationId,
                ct);
        }
    }

    public async Task<RequestTask> CompleteAsync(string taskId, string? notes, CancellationToken ct)
    {
        var task = await GetRequiredTaskAsync(taskId);

        if (task.Status != RequestTaskStatus.InProgress)
        {
            throw new InvalidOperationException("Task must be in InProgress status to complete.");
        }

        task.Status = RequestTaskStatus.Completed;
        task.State = TicketState.Resolved;
        task.CompletedAt = DateTimeOffset.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(notes))
        {
            task.ResultJson = notes;
        }

        await _requestTasks.UpdateAsync(task);

        await _domainEvents.PublishAsync(
            new RequestTaskCompletedEvent(
                task.Id,
                task.RequestId,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                GetCorrelationId()),
            ct);

        if (task.Type == RequestTaskType.Automation)
        {
            await _domainEvents.PublishAsync(
                new RequestTaskAutomationCompletedEvent(
                    task.Id,
                    task.RequestId,
                    task.OrchestratorExecutionId,
                    task.OrganizationId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId()),
                ct);
        }

        return task;
    }

    public async Task<RequestTask> FailAsync(string taskId, string reason, CancellationToken ct)
    {
        var task = await GetRequiredTaskAsync(taskId);

        if (task.Status is RequestTaskStatus.Completed or RequestTaskStatus.Skipped)
        {
            throw new InvalidOperationException("Completed or skipped tasks cannot be failed.");
        }

        if (task.Status == RequestTaskStatus.Failed)
        {
            throw new InvalidOperationException("Task is already failed.");
        }

        task.Status = RequestTaskStatus.Failed;
        task.State = TicketState.OnHold;
        task.FailureReason = string.IsNullOrWhiteSpace(reason) ? task.FailureReason : reason;
        task.ResultJson = string.IsNullOrWhiteSpace(reason) ? task.ResultJson : reason;
        task.UpdatedAt = DateTime.UtcNow;

        await _requestTasks.UpdateAsync(task);

        await _domainEvents.PublishAsync(
            new RequestTaskFailedEvent(
                task.Id,
                task.RequestId,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                GetCorrelationId()),
            ct);

        if (task.Type == RequestTaskType.Automation)
        {
            await _domainEvents.PublishAsync(
                new RequestTaskAutomationFailedEvent(
                    task.Id,
                    task.RequestId,
                    task.OrchestratorExecutionId,
                    reason,
                    task.OrganizationId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId()),
                ct);
        }

        if (!string.IsNullOrWhiteSpace(task.RequestId)
            && !string.IsNullOrWhiteSpace(task.Id))
        {
            await _failurePolicyEngine.OnTaskFailedAsync(task.RequestId, task.Id, ct);
        }

        return task;
    }

    public async Task<RequestTask> RetryAsync(string taskId, CancellationToken ct)
    {
        var task = await GetRequiredTaskAsync(taskId);

        if (task.Type != RequestTaskType.Automation)
        {
            throw new InvalidOperationException("Only automation tasks can be retried.");
        }

        if (task.Status != RequestTaskStatus.Failed)
        {
            throw new InvalidOperationException("Only failed tasks can be retried.");
        }

        task.Status = RequestTaskStatus.Pending;
        task.State = TicketState.New;
        task.StartedAt = null;
        task.CompletedAt = null;
        task.SlaStartedAt = null;
        task.DueAt = null;
        task.Escalated = false;
        task.EscalatedAt = null;
        task.SlaBreached = false;
        task.NextRetryAt = null;
        task.FailureReason = null;
        task.AutomationBindingId = null;
        task.OrchestrationRequestDefinitionId = null;
        task.OrchestrationJobDefinitionId = null;
        task.OrchestrationExternalRequestId = null;
        task.OrchestrationExternalRunId = null;
        task.LastAutomationStatus = null;
        task.LastAutomationUpdatedAt = null;
        task.OrchestratorExecutionId = null;
        task.ResultJson = null;
        task.UpdatedAt = DateTime.UtcNow;

        await _requestTasks.UpdateAsync(task);

        _logger.LogInformation(
            "Manual automation retry requested. RequestId={RequestId} TaskId={TaskId} CorrelationId={CorrelationId}",
            task.RequestId,
            task.Id,
            GetCorrelationId());

        await _domainEvents.PublishAsync(
            new RequestTaskRetryRequestedEvent(
                task.Id,
                task.RequestId,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                GetCorrelationId()),
            ct);

        return task;
    }

    private async Task<RequestTask> HandleAutomationSubmissionFailureAsync(
        RequestTask task,
        string reason,
        string correlationId,
        CancellationToken ct)
    {
        await _domainEvents.PublishAsync(
            new RequestTaskAutomationSubmitFailedEvent(
                task.Id,
                task.RequestId,
                reason,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId),
            ct);

        task.LastAutomationStatus = AutomationTaskStatuses.SubmitFailedManualRetry;
        task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
        task.NextRetryAt = null;
        task.ResultJson = reason;
        task.FailureReason = reason;

        return await FailAsync(task.Id, reason, ct);
    }

    private async Task<RequestTask> HandleUncertainAutomationSubmissionAsync(
        RequestTask task,
        string reason,
        string correlationId,
        CancellationToken ct)
    {
        await _domainEvents.PublishAsync(
            new RequestTaskAutomationSubmitUncertainEvent(
                task.Id,
                task.RequestId,
                reason,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId),
            ct);

        task.LastAutomationStatus = AutomationTaskStatuses.SubmitUncertainManualReconcile;
        task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
        task.NextRetryAt = null;
        task.ResultJson = reason;
        task.FailureReason = reason;

        return await FailUncertainSubmissionAsync(task, reason, ct);
    }

    private async Task<RequestTask> HandleRejectedAutomationSubmissionAsync(
        RequestTask task,
        string reason,
        string correlationId,
        CancellationToken ct)
    {
        await _domainEvents.PublishAsync(
            new RequestTaskAutomationSubmitRejectedEvent(
                task.Id,
                task.RequestId,
                reason,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId),
            ct);

        task.LastAutomationStatus = AutomationTaskStatuses.SubmitRejectedManualRetry;
        task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
        task.NextRetryAt = null;
        task.ResultJson = reason;
        task.FailureReason = reason;

        return await FailAsync(task.Id, reason, ct);
    }

    private async Task<RequestTask> FailUncertainSubmissionAsync(
        RequestTask task,
        string reason,
        CancellationToken ct)
    {
        task.Status = RequestTaskStatus.Failed;
        task.State = TicketState.OnHold;
        task.FailureReason = reason;
        task.ResultJson = reason;
        task.UpdatedAt = DateTime.UtcNow;
        await _requestTasks.UpdateAsync(task);

        await _domainEvents.PublishAsync(
            new RequestTaskFailedEvent(
                task.Id,
                task.RequestId,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                GetCorrelationId()),
            ct);

        return task;
    }

    private async Task<RequestTask> GetRequiredTaskAsync(string taskId)
    {
        var normalizedTaskId = string.IsNullOrWhiteSpace(taskId)
            ? throw new KeyNotFoundException("Task not found")
            : taskId.Trim();
        var task = await _requestTasks.GetAsync(normalizedTaskId);
        return task ?? throw new KeyNotFoundException("Task not found");
    }

    private async Task<AutomationBindingSyncState?> EnsureAutomationBindingReadyAsync(RequestTask task, CancellationToken ct)
    {
        if (!Guid.TryParse(task.TemplateId, out var taskTemplateId))
        {
            return null;
        }

        var request = await _requests.GetAsync(task.RequestId);
        if (request is null || string.IsNullOrWhiteSpace(request.RequestFormId))
        {
            return null;
        }

        var binding = await _automationBindingService.GetByTaskTemplateAsync(request.RequestFormId, taskTemplateId, ct);
        if (binding is null || !binding.Enabled)
        {
            return null;
        }

        if (binding.SyncState != AutomationBindingSyncState.Broken)
        {
            return binding.SyncState;
        }

        var targetName = FirstNonEmpty(
            binding.OrchestrationRequestDefinitionName,
            binding.OrchestrationJobDefinitionName,
            binding.OrchestrationRequestDefinitionId,
            binding.OrchestrationJobDefinitionId) ?? "the bound External orchestration target";

        var stateLabel = binding.SyncState switch
        {
            AutomationBindingSyncState.Drifted => "drifted",
            AutomationBindingSyncState.ImportPending => "awaiting import review",
            AutomationBindingSyncState.Broken => "broken",
            _ => "not ready"
        };

        throw new AutomationBindingNotReadyException(
            $"Automation binding for task '{task.Name}' cannot start because '{targetName}' is {stateLabel}. Resolve the binding in Service Explorer before starting the task.");
    }

    private string GetCorrelationId()
    {
        return _correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static int CountInputKeys(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return 0;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("input", out var inputNode)
                || inputNode.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return 0;
            }

            return inputNode.EnumerateObject().Count();
        }
        catch
        {
            return 0;
        }
    }

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
}
