using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.Workflow;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    ILogger<RequestTaskLifecycleService> logger,
    IRequestTaskReadStore? requestTaskReadStore = null,
    IRequestTaskStartFence? startFence = null) : IRequestTaskLifecycleService
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

    public async Task<RequestTask> StartAsync(string taskId, CancellationToken ct, RequestTaskQueueAuthority? queueAuthority = null)
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

        var requestTasks = await RequestTaskQueries.ForRequestAsync(_requestTasks, requestTaskReadStore, task.RequestId, ct);
        if (requestTasks.Any(sibling => AutomationTaskStatuses.RequiresManualReconciliation(sibling.LastAutomationStatus)))
            throw new InvalidOperationException("Reconcile the unresolved automation attempt before starting more work on this request.");
        if (RequestTaskApprovalGate.IsBlockedByEarlierApproval(task, requestTasks))
        {
            throw new InvalidOperationException("Task is blocked by an earlier approval step.");
        }

        if (task.Type == RequestTaskType.Automation)
        {
            automationBindingSyncState = await EnsureAutomationBindingReadyAsync(task, ct);
        }

        var correlationId = task.Type == RequestTaskType.Automation
            ? task.AutomationAttemptCorrelationId ?? GetCorrelationId()
            : GetCorrelationId();

        var startIntent = new RequestTaskStartIntent(DateTimeOffset.UtcNow, correlationId);
        if (startFence is not null)
            await startFence.PersistAsync(task, startIntent, ct, queueAuthority);
        else
        {
            if (queueAuthority is not null) throw new InvalidOperationException("Task queue authorization is unavailable.");
            startIntent.Apply(task);
            await _requestTasks.UpdateAsync(task);
        }

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

        var settings = await _orchestrationConnectivityService.GetResolvedOrchestrationSettingsAsync(task.OrganizationId, Guid.TryParse(task.OrchestrationLinkId, out var selectedConnection) ? selectedConnection : null, ct);
        if (!settings.Enabled)
        {
            return await HandleAutomationSubmissionFailureAsync(task, "External orchestration disabled", correlationId, ct);
        }
        if (settings.Pairing is { } linked)
        {
            if (task.OrganizationId != linked.LocalTenantId)
                return await HandleAutomationSubmissionFailureAsync(task, "The system connection does not authorize this task organization", correlationId, ct);
            task.OrchestrationLinkId = linked.MappingId;
            task.OrchestrationPeerInstanceId = linked.PeerInstanceId;
            task.OrchestrationLinkRevision = linked.MappingRevision;
            await _requestTasks.UpdateAsync(task);
        }

        var payloadResult = await _payloadBuilder.BuildAsync(task, correlationId, ct);
        if (!payloadResult.Success)
        {
            return await HandleAutomationSubmissionFailureAsync(
                task,
                payloadResult.Error ?? "Payload build failed.",
                correlationId,
                ct,
                payloadResult.PreDispatchFailure);
        }

        OrchestrationIngestResult? received = null;
        try
        {
            var dispatchStarted = Stopwatch.GetTimestamp();
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
                OriginContext = payloadResult.OriginContext,
                CallbackUrl = null,
                ExpectedRuntimeSeconds = task.ExpectedRuntimeSeconds,
                GraceSeconds = task.GraceSeconds,
                HardTimeoutSeconds = task.HardTimeoutSeconds
            }, ct);
            _logger.LogInformation("NetRatel dispatch acknowledged. TaskId={TaskId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                task.Id, correlationId, Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds);

            if (ingestResult.Disposition == OrchestrationSubmissionDisposition.Unknown)
            {
                OrchestrationIngestClassifier.Apply(
                    ingestResult,
                    hasStatus: !string.IsNullOrWhiteSpace(ingestResult.Status));
            }
            received = ingestResult;
            if (startFence != null && await startFence.TryRecordClosedAcknowledgementAsync(task, ingestResult, ct) is { } closed)
                return closed;
            ApplyRemoteAcknowledgement(task, payloadResult, ingestResult);

            if (ingestResult.Disposition == OrchestrationSubmissionDisposition.Unknown)
            {
                return await HandleUncertainAutomationSubmissionAsync(
                    task,
                    "The NetRatel acknowledgement could not be classified.",
                    correlationId,
                    ingestResult,
                    payloadResult,
                    ct);
            }

            if (ingestResult.Disposition == OrchestrationSubmissionDisposition.Rejected)
            {
                return await HandleRejectedAutomationSubmissionAsync(
                    task,
                    Truncate(FirstNonEmpty(ingestResult.Message, "NetRatel rejected the submitted operation.")!, 500),
                    correlationId,
                    ct, ingestResult.Diagnostic);
            }

            if (ingestResult.Disposition == OrchestrationSubmissionDisposition.Existing)
            {
                if (ingestResult.Outcome == OrchestrationExecutionOutcome.Completed)
                    return await CompleteKnownAutomationOutcomeAsync(task, correlationId, ct);

                if (ingestResult.Outcome is OrchestrationExecutionOutcome.Failed or OrchestrationExecutionOutcome.Cancelled)
                {
                    var reason = FirstNonEmpty(
                        ingestResult.Message,
                        ingestResult.Outcome == OrchestrationExecutionOutcome.Cancelled
                            ? "The existing NetRatel execution was cancelled."
                            : "The existing NetRatel execution failed.")!;
                    return await FailKnownAutomationOutcomeAsync(task, reason, correlationId, ct);
                }
            }

            task.LastAutomationStatus = FirstNonEmpty(ingestResult.Status) ?? "submitted";
            task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
            task.ResultJson = FirstNonEmpty(ingestResult.Message, ingestResult.Status);
            task.UpdatedAt = DateTime.UtcNow;
            await _requestTasks.UpdateAsync(task);

            _logger.LogInformation(
                "External orchestration automation acknowledgement accepted. RequestId={RequestId} TaskId={TaskId} Disposition={Disposition} Outcome={Outcome} OrchestrationRequestId={OrchestrationRequestId} OrchestrationRunId={OrchestrationRunId} CorrelationId={CorrelationId}",
                task.RequestId,
                task.Id,
                ingestResult.Disposition,
                ingestResult.Outcome,
                task.OrchestrationExternalRequestId,
                task.OrchestrationExternalRunId,
                correlationId);

            if (ingestResult.Disposition == OrchestrationSubmissionDisposition.Admitted)
            {
                await _domainEvents.PublishAsync(
                    new RequestTaskAutomationSubmittedEvent(
                        task.Id,
                        task.RequestId,
                        task.OrchestratorExecutionId!,
                        task.OrganizationId,
                        DateTimeOffset.UtcNow,
                        correlationId),
                    ct);
            }

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

            return await HandleUncertainAutomationSubmissionAsync(
                task,
                Truncate(ex.Message, 500),
                correlationId,
                ex.Acknowledgement,
                payloadResult,
                DurablePersistenceToken(ct));
        }
        catch (OrchestrationSubmissionRejectedException ex)
        {
            return await HandleRejectedAutomationSubmissionAsync(task, Truncate(ex.Message, 500), correlationId,
                DurablePersistenceToken(ct), ex.Diagnostic);
        }
        catch (Exception ex)
        {
            if (received != null && startFence != null &&
                await startFence.TryRecordClosedAcknowledgementAsync(task, received, DurablePersistenceToken(ct)) is { } closed)
                return closed;
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

    public async Task<RequestTask> CompleteAsync(string taskId, string? notes, CancellationToken ct, RequestTaskQueueAuthority? queueAuthority = null)
    {
        var task = await GetRequiredTaskAsync(taskId);

        if (task.Status != RequestTaskStatus.InProgress)
        {
            throw new InvalidOperationException("Task must be in InProgress status to complete.");
        }

        task.Status = RequestTaskStatus.Completed;
        task.State = TicketState.Resolved;
        task.CompletedAt = DateTimeOffset.UtcNow;
        if (task.Type == RequestTaskType.Automation)
        {
            task.AutomationNextCheckUtcTicks = null;
            task.AutomationContinuationPending = true;
        }
        task.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(notes))
        {
            task.ResultJson = notes;
        }

        await PersistTransitionAsync(task, queueAuthority, () => _requestTasks.UpdateAsync(task), ct);

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

    public async Task<RequestTask> FailAsync(string taskId, string reason, CancellationToken ct, RequestTaskQueueAuthority? queueAuthority = null) =>
        await FailAsync(await GetRequiredTaskAsync(taskId), reason, ct, queueAuthority: queueAuthority);

    private async Task<RequestTask> FailAsync(RequestTask task, string reason, CancellationToken ct,
        AutomationPreDispatchFailure? preDispatchFailure = null, RequestTaskQueueAuthority? queueAuthority = null)
    {
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
        if (task.Type == RequestTaskType.Automation)
        {
            task.AutomationNextCheckUtcTicks = null;
            task.AutomationContinuationPending = true;
        }
        task.FailureReason = string.IsNullOrWhiteSpace(reason) ? task.FailureReason : reason;
        task.ResultJson = preDispatchFailure is null
            ? string.IsNullOrWhiteSpace(reason) ? task.ResultJson : reason
            : JsonSerializer.Serialize(new { PreDispatchFailure = preDispatchFailure }, JsonSerializerOptions.Web);
        task.UpdatedAt = DateTime.UtcNow;

        await PersistTransitionAsync(task, queueAuthority, () => _requestTasks.UpdateAsync(task), ct);

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
            await _failurePolicyEngine.OnTaskFailedAsync(task.RequestId, task.Id, ct, preDispatchFailure);
        }

        return task;
    }

    public async Task<RequestTask> RetryAsync(string taskId, CancellationToken ct, RequestTaskQueueAuthority? queueAuthority = null)
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

        if (AutomationTaskStatuses.RequiresManualReconciliation(task.LastAutomationStatus))
            throw new InvalidOperationException("The previous automation outcome is uncertain. Reconcile its provider execution before retrying.");
        var parent = await _requests.GetAsync(task.RequestId);
        if (string.Equals(parent?.WorkflowStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A cancelled request cannot be resumed by retrying a task.");

        JsonArray history;
        try
        {
            history = string.IsNullOrWhiteSpace(task.AutomationAttemptHistoryJson)
                ? new JsonArray()
                : JsonNode.Parse(task.AutomationAttemptHistoryJson) as JsonArray
                    ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Automation attempt history is invalid. Reconcile it before retrying.");
        }
        history.Add(JsonSerializer.SerializeToNode(new
        {
            task.AutomationAttemptCorrelationId, task.AutomationBindingId,
            task.OrchestrationRequestDefinitionId, task.OrchestrationJobDefinitionId,
            task.OrchestrationExternalRequestId, task.OrchestrationExternalRunId,
            task.OrchestratorExecutionId, task.LastAutomationStatus,
            task.OrchestrationLinkId, task.OrchestrationPeerInstanceId, task.OrchestrationLinkRevision,
            task.AutomationDeadlineUtcTicks, task.AutomationTimedOutAt, task.AutomationTimedOutAttemptId,
            task.ExpectedRuntimeSeconds, task.GraceSeconds, task.HardTimeoutSeconds, task.LastAutomationUpdatedAt,
            task.StartedAt, task.CompletedAt, task.FailureReason, task.ResultJson, task.ProviderDiagnostic,
            task.AutomationOriginJson, task.AutomationDispatchPayloadJson,
            RetriedAtUtc = DateTimeOffset.UtcNow
        }));
        task.AutomationAttemptHistoryJson = history.ToJsonString();
        // A new explicit attempt gets a durable identity before another provider call.
        // Transport reconciliation must reuse the previous identity, never manufacture a retry.
        task.AutomationAttemptCorrelationId = $"automation-attempt-{Guid.NewGuid():N}";
        task.AutomationOriginJson = null;
        task.AutomationDispatchPayloadJson = null;

        task.Status = RequestTaskStatus.Pending;
        task.State = TicketState.New;
        task.StartedAt = null;
        task.AutomationDeadlineUtcTicks = null;
        task.AutomationNextCheckUtcTicks = null;
        task.AutomationTimedOutAt = null;
        task.AutomationTimedOutAttemptId = null;
        task.AutomationReconciliationLeaseId = null;
        task.AutomationReconciliationLeaseUntilUtcTicks = null;
        task.AutomationContinuationPending = false;
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
        task.ProviderDiagnostic = null;
        task.UpdatedAt = DateTime.UtcNow;

        await PersistTransitionAsync(task, queueAuthority, async () =>
        {
            await _requestTasks.UpdateAsync(task);
            if (parent is null || parent.WorkflowStatus is not ("Blocked" or "Failed")) return;

            var siblings = await RequestTaskQueries.ForRequestAsync(_requestTasks, requestTaskReadStore, task.RequestId, ct);
            var taskFailureBlock = siblings.Any(x => parent.WorkflowBlockReason == $"Task '{x.Name}' failed."
                || parent.WorkflowBlockReason == $"Critical task '{x.Name}' failed.");
            if (taskFailureBlock && !siblings.Any(x => x.Status is RequestTaskStatus.Failed or RequestTaskStatus.Cancelled))
            {
                parent.WorkflowStatus = "InProgress";
                parent.WorkflowBlockReason = null;
                parent.WorkflowUpdatedAt = DateTimeOffset.UtcNow;
                parent.UpdatedAt = DateTime.UtcNow;
                await _requests.UpdateAsync(parent);
            }
        }, ct);

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

    private Task PersistTransitionAsync(RequestTask task, RequestTaskQueueAuthority? queueAuthority, Func<Task> save, CancellationToken ct)
    {
        if (queueAuthority is null) return save();
        if (startFence is null) throw new InvalidOperationException("Task queue authorization is unavailable.");
        return startFence.PersistAuthorizedTransitionAsync(task, queueAuthority, save, ct);
    }

    private async Task<RequestTask> HandleAutomationSubmissionFailureAsync(
        RequestTask task,
        string reason,
        string correlationId,
        CancellationToken ct,
        AutomationPreDispatchFailure? preDispatchFailure = null)
    {
        if (preDispatchFailure is { } failure)
        {
            _logger.LogWarning(
                "Automation pre-dispatch check failed. RequestId={RequestId} TaskId={TaskId} Phase={Phase} Layer={Layer} Code={Code} UpstreamStatus={UpstreamStatus} ConnectionId={ConnectionId} AttemptCorrelationId={AttemptCorrelationId} ProviderCorrelationId={ProviderCorrelationId} TraceId={TraceId}",
                task.RequestId, task.Id, failure.Phase, failure.Layer, failure.Code, failure.UpstreamStatus,
                failure.ConnectionId, failure.AttemptCorrelationId, failure.ProviderCorrelationId, failure.TraceId);
        }
        await _domainEvents.PublishAsync(
            new RequestTaskAutomationSubmitFailedEvent(
                task.Id,
                task.RequestId,
                reason,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId) { PreDispatchFailure = preDispatchFailure },
            ct);

        task.LastAutomationStatus = AutomationTaskStatuses.SubmitFailedManualRetry;
        task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
        task.NextRetryAt = null;
        task.ResultJson = reason;
        task.FailureReason = reason;

        return await FailAsync(task, reason, ct, preDispatchFailure);
    }

    private async Task<RequestTask> HandleUncertainAutomationSubmissionAsync(
        RequestTask task,
        string reason,
        string correlationId,
        OrchestrationIngestResult? acknowledgement,
        RequestTaskPayloadBuildResult? payload,
        CancellationToken ct)
    {
        if (acknowledgement is not null)
            ApplyRemoteAcknowledgement(task, payload, acknowledgement);

        task.ProviderDiagnostic = ProviderExecutionDiagnostics.Outcome(task, new(), "unknown", correlationId);
        await _domainEvents.PublishAsync(
            new RequestTaskAutomationSubmitUncertainEvent(
                task.Id,
                task.RequestId,
                reason,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId, task.ProviderDiagnostic),
            ct);

        task.LastAutomationStatus = AutomationTaskStatuses.SubmitUncertainManualReconcile;
        task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
        task.NextRetryAt = null;
        task.ResultJson = reason;
        task.FailureReason = reason;

        return await FailUncertainSubmissionAsync(task, reason, ct);
    }

    private async Task<RequestTask> CompleteKnownAutomationOutcomeAsync(
        RequestTask task,
        string correlationId,
        CancellationToken ct)
    {
        task.Status = RequestTaskStatus.Completed;
        task.State = TicketState.Resolved;
        task.CompletedAt = DateTimeOffset.UtcNow;
        task.FailureReason = null;
        task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
        task.AutomationNextCheckUtcTicks = null;
        task.AutomationContinuationPending = true;
        task.UpdatedAt = DateTime.UtcNow;
        await _requestTasks.UpdateAsync(task);

        await _domainEvents.PublishAsync(
            new RequestTaskCompletedEvent(
                task.Id,
                task.RequestId,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId),
            ct);
        await _domainEvents.PublishAsync(
            new RequestTaskAutomationCompletedEvent(
                task.Id,
                task.RequestId,
                task.OrchestratorExecutionId,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId),
            ct);
        return task;
    }

    private async Task<RequestTask> FailKnownAutomationOutcomeAsync(
        RequestTask task,
        string reason,
        string correlationId,
        CancellationToken ct)
    {
        task.Status = RequestTaskStatus.Failed;
        task.State = TicketState.OnHold;
        task.FailureReason = reason;
        task.ResultJson = reason;
        task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
        task.AutomationNextCheckUtcTicks = null;
        task.AutomationContinuationPending = true;
        task.UpdatedAt = DateTime.UtcNow;
        await _requestTasks.UpdateAsync(task);

        await _domainEvents.PublishAsync(
            new RequestTaskFailedEvent(
                task.Id,
                task.RequestId,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId),
            ct);
        await _domainEvents.PublishAsync(
            new RequestTaskAutomationFailedEvent(
                task.Id,
                task.RequestId,
                task.OrchestratorExecutionId,
                reason,
                task.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId),
            ct);
        await _failurePolicyEngine.OnTaskFailedAsync(task.RequestId, task.Id, ct);
        return task;
    }

    private async Task<RequestTask> HandleRejectedAutomationSubmissionAsync(
        RequestTask task,
        string reason,
        string correlationId,
        CancellationToken ct,
        ProviderExecutionDiagnostic? diagnostic)
    {
        diagnostic ??= ProviderExecutionDiagnostics.Rejection(null, null, new()
        {
            CorrelationId = correlationId, OrchestrationJobDefinitionId = task.OrchestrationJobDefinitionId
        }, task.OrchestrationLinkId);
        task.ProviderDiagnostic = diagnostic;
        task.LastAutomationStatus = AutomationTaskStatuses.SubmitRejectedManualRetry;
        task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
        task.NextRetryAt = null;
        task.ResultJson = reason;
        task.FailureReason = reason;

        var failed = await FailAsync(task, reason, ct);
        _logger.LogInformation("NetRatel admission rejected. RequestId={RequestId} TaskId={TaskId} ConnectionId={ConnectionId} JobDefinitionId={JobDefinitionId} CorrelationId={CorrelationId} HttpStatus={HttpStatus} ReasonCode={ReasonCode} RetryDisposition={RetryDisposition}",
            task.RequestId, task.Id, diagnostic.ConnectionId, diagnostic.JobDefinitionId, correlationId,
            diagnostic.HttpStatus, diagnostic.Code, diagnostic.RetryDisposition);
        await _domainEvents.PublishAsync(new RequestTaskAutomationSubmitRejectedEvent(task.Id, task.RequestId,
            diagnostic.Message, task.OrganizationId, DateTimeOffset.UtcNow, correlationId, diagnostic), ct);
        return failed;
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

    private static void ApplyRemoteAcknowledgement(
        RequestTask task,
        RequestTaskPayloadBuildResult? payload,
        Helpdesk.Shared.DTOs.Orchestration.OrchestrationIngestResult result)
    {
        if (payload is not null)
        {
            task.AutomationBindingId = payload.AutomationBindingId;
            task.OrchestrationRequestDefinitionId = payload.OrchestrationRequestDefinitionId;
            task.OrchestrationJobDefinitionId = payload.OrchestrationJobDefinitionId;
        }

        task.OrchestrationExternalRequestId = FirstNonEmpty(result.RequestId, task.OrchestrationExternalRequestId);
        task.OrchestrationExternalRunId = FirstNonEmpty(result.RunId, task.OrchestrationExternalRunId);
        task.OrchestratorExecutionId = FirstNonEmpty(result.ExecutionId, task.OrchestratorExecutionId);
        task.LastAutomationStatus = FirstNonEmpty(result.Status, task.LastAutomationStatus) ?? "submitted";
        task.LastAutomationUpdatedAt = DateTimeOffset.UtcNow;
        task.ResultJson = FirstNonEmpty(result.Message, result.Status, task.ResultJson);
        task.UpdatedAt = DateTime.UtcNow;
    }

    private static CancellationToken DurablePersistenceToken(CancellationToken ct)
        => ct.IsCancellationRequested ? CancellationToken.None : ct;

    private async Task<RequestTask> GetRequiredTaskAsync(string taskId)
    {
        var normalizedTaskId = string.IsNullOrWhiteSpace(taskId)
            ? throw new KeyNotFoundException("Task not found")
            : taskId.Trim();
        var task = await _requestTasks.GetAsync(normalizedTaskId);
        if (task?.AdministrativeClosureReason is not null)
            throw new InvalidOperationException("This task was closed by an operator.");
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

        if (binding.SystemConnectionId is { } connection) task.OrchestrationLinkId = connection.ToString("D");
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
            $"Automation binding for task '{task.Name}' cannot start because '{targetName}' is {stateLabel}. Resolve the binding in Service Catalog before starting the task.");
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
