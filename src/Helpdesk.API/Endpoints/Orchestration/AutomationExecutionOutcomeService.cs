using System.Text.Json;
using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Orchestration;

/// <summary>One committed outcome path for authenticated callbacks and exact-attempt reconciliation.</summary>
public sealed class AutomationExecutionOutcomeService(
    HelpdeskDbContext db,
    IRequestTaskStateService requestTaskState,
    IDomainEventPublisher domainEvents,
    IRequestSender sender,
    IFailurePolicyEngine failurePolicy,
    IWorkflowEngine workflow,
    IIngressEffectContext effects,
    MailboxOutboxStore outbox,
    ITimelineEventBus timelineBus,
    INotificationEventBus notificationBus,
    TimeProvider clock,
    ILogger<AutomationExecutionOutcomeService> logger)
{
    public async Task<bool> ApplyAsync(RequestTask task, OrchestrationCallbackDto dto,
        string incomingStatus, string correlationId, string callerClientId, CancellationToken ct,
        bool deadlineExpired = false)
    {
        var reconcile = task.Status == RequestTaskStatus.Failed
            && AutomationTaskStatuses.RequiresManualReconciliation(task.LastAutomationStatus);
        var terminal = incomingStatus is "succeeded" or "failed" or "cancelled";
        var recordUnconfirmedDeadline = reconcile && incomingStatus == "unknown" && deadlineExpired
            && task.AutomationTimedOutAt is null;
        if (task.Status != RequestTaskStatus.InProgress && !(reconcile && terminal) && !recordUnconfirmedDeadline)
            return true;
        if (dto.CorrelationId is { Length: > 0 } && dto.CorrelationId != task.AutomationAttemptCorrelationId)
            return false;
        dto.Status = incomingStatus;

        var now = clock.GetUtcNow();
        var attempt = task.AutomationAttemptCorrelationId;
        IReadOnlyList<IngressCapturedEffect> publications;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            using var capture = effects.Begin(Guid.NewGuid());
            effects.OrganizationId = task.OrganizationId;
            try
            {
                var previousStatus = task.LastAutomationStatus;
                var previousResult = task.ResultJson;
                task.LastAutomationUpdatedAt = deadlineExpired ? task.LastAutomationUpdatedAt : now;
                task.LastAutomationStatus = incomingStatus;
                task.OrchestrationExternalRequestId ??= dto.OrchestrationRequestId ?? dto.RequestId;
                task.OrchestrationExternalRunId ??= dto.OrchestrationRunId ?? dto.ExecutionId;
                task.OrchestratorExecutionId ??= dto.ExecutionId ?? dto.OrchestrationRunId;
                task.UpdatedAt = now.UtcDateTime;
                task.AutomationReconciliationLeaseId = null;
                task.AutomationReconciliationLeaseUntilUtcTicks = null;
                task.ResultJson = JsonSerializer.Serialize(new
                {
                    dto.RequestTaskId, dto.OrchestrationRequestId, dto.OrchestrationRunId,
                    dto.Status, dto.Message, dto.ResultJson, dto.ErrorJson, dto.WorklogSummary,
                    dto.StartedAtUtc, dto.CompletedAtUtc, dto.ExpectedRuntimeExceeded,
                    dto.ExpectedRuntimeSeconds, dto.GraceSeconds, dto.HardTimeoutSeconds,
                    previousResultJson = incomingStatus == "unknown" || reconcile ? task.ResultJson : null
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

                switch (incomingStatus)
                {
                    case "running":
                        task.AutomationNextCheckUtcTicks = Math.Min(
                            AutomationExecutionDeadlinePolicy.ResolveDeadlineUtcTicks(task, now),
                            now.Add(AutomationExecutionDeadlinePolicy.ReconciliationInterval).UtcTicks);
                        break;
                    case "succeeded":
                        task.Status = RequestTaskStatus.Completed;
                        task.State = TicketState.Resolved;
                        task.CompletedAt = dto.CompletedAtUtc ?? now;
                        task.FailureReason = null;
                        task.AutomationNextCheckUtcTicks = null;
                        task.AutomationContinuationPending = true;
                        break;
                    case "failed":
                    case "cancelled":
                        task.Status = RequestTaskStatus.Failed;
                        task.State = TicketState.OnHold;
                        task.CompletedAt = dto.CompletedAtUtc ?? now;
                        task.FailureReason = BuildFailureReason(dto);
                        task.AutomationNextCheckUtcTicks = null;
                        task.AutomationContinuationPending = true;
                        break;
                    case "unknown":
                        task.Status = RequestTaskStatus.Failed;
                        task.State = TicketState.OnHold;
                        task.LastAutomationStatus = AutomationTaskStatuses.ExecutionUnknownManualReconcile;
                        task.NextRetryAt = null;
                        task.FailureReason = deadlineExpired
                            ? "The execution deadline expired. The provider outcome is unknown; reconcile this recorded attempt before retrying."
                            : "Automation execution outcome is unknown. Reconcile the recorded provider execution before retrying.";
                        task.AutomationNextCheckUtcTicks = now.Add(AutomationExecutionDeadlinePolicy.UnknownOutcomeReconciliationInterval).UtcTicks;
                        if (deadlineExpired)
                        {
                            task.AutomationTimedOutAt ??= now;
                            task.AutomationTimedOutAttemptId = attempt;
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported automation execution outcome.");
                }

                // The optimistic version check makes callback/watchdog/replica races
                // lose before any audit, policy, mail or stream effect can commit.
                await db.SaveChangesAsync(ct);
                if (incomingStatus == "running" && previousStatus == "running" && previousResult == task.ResultJson)
                {
                    await transaction.CommitAsync(ct);
                    return true;
                }
                await domainEvents.PublishAsync(new OrchestrationCallbackReceivedEvent(
                    task.RequestId, task.Id, task.OrchestratorExecutionId ?? task.OrchestrationExternalRunId ?? task.Id,
                    incomingStatus, callerClientId, task.OrganizationId, now, correlationId), ct);

                if (incomingStatus != "running" || dto.ExpectedRuntimeExceeded == true)
                    await AppendAutomationWorklogAsync(task, dto, incomingStatus, callerClientId, ct);

                if (incomingStatus == "succeeded")
                {
                    await domainEvents.PublishAsync(new RequestTaskCompletedEvent(task.Id, task.RequestId,
                        task.OrganizationId, now, correlationId), ct);
                    await domainEvents.PublishAsync(new RequestTaskAutomationCompletedEvent(task.Id, task.RequestId,
                        task.OrchestratorExecutionId, task.OrganizationId, now, correlationId, callerClientId), ct);
                }
                else if (incomingStatus is "failed" or "cancelled")
                {
                    await domainEvents.PublishAsync(new RequestTaskFailedEvent(task.Id, task.RequestId,
                        task.OrganizationId, now, correlationId), ct);
                    await domainEvents.PublishAsync(new RequestTaskAutomationFailedEvent(task.Id, task.RequestId,
                        task.OrchestratorExecutionId, task.FailureReason!, task.OrganizationId, now, correlationId, callerClientId), ct);
                    await failurePolicy.OnTaskFailedAsync(task.RequestId, task.Id, ct);
                }
                else if (incomingStatus == "unknown")
                {
                    await domainEvents.PublishAsync(new RequestTaskFailedEvent(task.Id, task.RequestId,
                        task.OrganizationId, now, correlationId), ct);
                }
                else
                {
                    await domainEvents.PublishAsync(new RequestTaskAutomationRunningEvent(task.Id, task.RequestId,
                        task.OrchestratorExecutionId, task.OrganizationId, now, correlationId, callerClientId), ct);
                }

                await requestTaskState.EvaluateParentRequestState(task.RequestId, ct);
                await outbox.FlushAsync(ct);
                publications = effects.Effects.ToArray();
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                logger.LogInformation("Automation outcome raced another committed transition. TaskId={TaskId}", task.Id);
                return false;
            }
        }

        // Publish the committed IDs immediately for the healthy live path. The
        // existing fenced outbox replays those same IDs after interruption.
        foreach (var publication in publications)
        {
            if (publication.Kind == MailboxEffectKind.Timeline)
                await timelineBus.PublishAsync(JsonSerializer.Deserialize<TicketTimelineEventDto>(publication.Payload)!);
            else if (publication.Kind == MailboxEffectKind.Notification)
                notificationBus.Publish(JsonSerializer.Deserialize<NotificationDto>(publication.Payload)!);
        }
        if (terminal)
            await ContinueAsync(task.Id, attempt, ct);
        return true;
    }

    public async Task ContinueAsync(string taskId, string? attempt, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var lease = Guid.NewGuid().ToString("N");
        var claimed = await db.RequestTasks.Where(task => task.Id == taskId
            && task.AutomationAttemptCorrelationId == attempt && task.AutomationContinuationPending
            && (task.AutomationReconciliationLeaseUntilUtcTicks == null || task.AutomationReconciliationLeaseUntilUtcTicks <= now.UtcTicks))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(task => task.AutomationReconciliationLeaseId, lease)
                .SetProperty(task => task.AutomationReconciliationLeaseUntilUtcTicks, now.AddMinutes(5).UtcTicks)
                .SetProperty(task => task.AutomationStateVersion, task => task.AutomationStateVersion + 1), ct);
        if (claimed != 1) return;
        try
        {
            var task = await db.RequestTasks.SingleAsync(task => task.Id == taskId, ct);
            await db.Entry(task).ReloadAsync(ct);
            await workflow.RunAsync(task.RequestId, WorkflowRunReason.AutomationCallback, ct);
            await requestTaskState.EvaluateParentRequestState(task.RequestId, ct);
            await db.RequestTasks.Where(task => task.Id == taskId && task.AutomationAttemptCorrelationId == attempt
                && task.AutomationReconciliationLeaseId == lease)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(task => task.AutomationContinuationPending, false)
                    .SetProperty(task => task.AutomationReconciliationLeaseId, (string?)null)
                    .SetProperty(task => task.AutomationReconciliationLeaseUntilUtcTicks, (long?)null)
                    .SetProperty(task => task.AutomationStateVersion, task => task.AutomationStateVersion + 1), ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The persisted continuation and bounded lease make restart/retry safe;
            // never roll back the provider's confirmed outcome or resubmit this task.
            logger.LogWarning("Automation workflow continuation remains pending. TaskId={TaskId} ErrorCode={ErrorCode}", taskId, error.GetType().Name);
        }
    }

    private Task AppendAutomationWorklogAsync(RequestTask task, OrchestrationCallbackDto dto,
        string status, string callerClientId, CancellationToken ct)
        => sender.Send(new CreateWorkLogCommand(task.RequestId, 0,
            BuildWorklogMessage(task, dto, status, callerClientId), null, callerClientId,
            NotifyCustomer: false, EventType: TimelineEventType.SystemNotification), ct);

    private static string BuildWorklogMessage(RequestTask task, OrchestrationCallbackDto dto, string status, string callerClientId)
    {
        var lines = new List<string> { $"Automation task '{task.Name}' reported '{status}'.", $"Source: {callerClientId}" };
        if (status == "unknown")
            lines.Add("Reconciliation required. The remote outcome is unconfirmed; automatic and manual retries are held.");
        if (task.AutomationTimedOutAt is { } timedOutAt)
            lines.Add($"Local execution deadline expired at {timedOutAt:O}. This does not confirm remote termination.");
        if (task.AutomationBindingId is { Length: > 0 } binding) lines.Add($"Binding: {binding}");
        if (task.OrchestrationExternalRequestId is { Length: > 0 } request) lines.Add($"External orchestration Request: {request}");
        if (task.OrchestrationExternalRunId is { Length: > 0 } run) lines.Add($"External orchestration Run: {run}");
        if (!string.IsNullOrWhiteSpace(dto.WorklogSummary)) lines.Add(dto.WorklogSummary.Trim());
        else if (status is "failed" or "cancelled") lines.Add(BuildFailureReason(dto));
        else if (dto.ExpectedRuntimeExceeded == true) lines.Add("Expected runtime exceeded; the configured execution deadline remains in effect.");
        if (dto.ExpectedRuntimeSeconds is { } expected) lines.Add($"Expected runtime: {expected / 60} min");
        if (dto.GraceSeconds is { } grace) lines.Add($"Grace runtime: {grace / 60} min");
        if (dto.HardTimeoutSeconds is { } hard) lines.Add($"Hard timeout: {hard / 60} min");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildFailureReason(OrchestrationCallbackDto dto)
    {
        var reason = string.IsNullOrWhiteSpace(dto.Message)
            ? ExtractErrorMessage(dto.ErrorJson) ?? (dto.Status == "cancelled" ? "The provider confirmed that the execution was cancelled." : "Automation callback failed.")
            : dto.Message.Trim();
        if (dto.ExpectedRuntimeExceeded == true || reason.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            reason += $" Expected runtime: {FormatSeconds(dto.ExpectedRuntimeSeconds)}. Grace: {FormatSeconds(dto.GraceSeconds)}. Hard timeout: {FormatSeconds(dto.HardTimeoutSeconds)}.";
        return reason.Length > 2000 ? reason[..2000] : reason;
    }

    private static string FormatSeconds(int? seconds) => seconds is { } value ? $"{value / 60} min" : "not supplied";

    private static string? ExtractErrorMessage(string? errorJson)
    {
        if (string.IsNullOrWhiteSpace(errorJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(errorJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "message", "error" })
                    if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString();
        }
        catch (JsonException)
        {
            // The callback contract historically also accepts plain-text errors.
            return errorJson;
        }
        return errorJson;
    }
}
