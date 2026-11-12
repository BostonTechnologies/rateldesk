using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Background;

/// <summary>Durable, bounded current-attempt reconciliation; never submits provider work.</summary>
public sealed class AutomationExecutionDeadlineProcessor(
    HelpdeskDbContext db,
    IOrchestrationConnectivityService connectivity,
    IOrchestrationInternalClient provider,
    AutomationExecutionOutcomeService outcomes,
    TimeProvider clock,
    ILogger<AutomationExecutionDeadlineProcessor> logger)
{
    public const int BatchSize = 50;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // This is a system worker, not a user directory query. Every claimed row
        // is subsequently bound to its persisted organization before business work.
        var candidates = await db.RequestTasks.IgnoreQueryFilters().AsNoTracking()
            .Where(task => (task.Type == RequestTaskType.Automation || task.Type == RequestTaskType.Approval || task.Type == RequestTaskType.Manual)
                && (task.AutomationContinuationPending
                    || (task.Type == RequestTaskType.Automation && (task.Status == RequestTaskStatus.InProgress
                        || (task.Status == RequestTaskStatus.Failed
                            && (task.LastAutomationStatus == AutomationTaskStatuses.ExecutionUnknownManualReconcile
                                || task.LastAutomationStatus == AutomationTaskStatuses.SubmitUncertainManualReconcile)))
                        && (task.AutomationNextCheckUtcTicks == null || task.AutomationNextCheckUtcTicks <= now.UtcTicks)))
                && (task.AutomationReconciliationLeaseUntilUtcTicks == null || task.AutomationReconciliationLeaseUntilUtcTicks <= now.UtcTicks))
            .OrderBy(task => task.AutomationNextCheckUtcTicks).ThenBy(task => task.Id)
            .Select(task => new Candidate(task.Id, task.OrganizationId, task.AutomationStateVersion,
                task.AutomationAttemptCorrelationId, task.AutomationContinuationPending))
            .Take(BatchSize).ToListAsync(ct);
        var processed = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            db.BeginIngressRoutingScope();
            if (!string.IsNullOrWhiteSpace(candidate.OrganizationId))
                db.RestrictIngressToOrganization(candidate.OrganizationId);
            if (candidate.ContinuationPending)
            {
                await outcomes.ContinueAsync(candidate.Id, candidate.Attempt, ct);
                processed++;
                continue;
            }

            var lease = Guid.NewGuid().ToString("N");
            var claimed = await db.RequestTasks.Where(task => task.Id == candidate.Id
                && task.AutomationStateVersion == candidate.Version
                && task.AutomationAttemptCorrelationId == candidate.Attempt
                && (task.AutomationReconciliationLeaseUntilUtcTicks == null || task.AutomationReconciliationLeaseUntilUtcTicks <= now.UtcTicks))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(task => task.AutomationReconciliationLeaseId, lease)
                    .SetProperty(task => task.AutomationReconciliationLeaseUntilUtcTicks, now.Add(LeaseDuration).UtcTicks)
                    .SetProperty(task => task.AutomationStateVersion, task => task.AutomationStateVersion + 1), ct);
            if (claimed != 1) continue;
            var task = await db.RequestTasks.SingleAsync(task => task.Id == candidate.Id, ct);
            try
            {
                await ReconcileAsync(task, now, ct);
                processed++;
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Execution reconciliation raced a committed callback. TaskId={TaskId}", task.Id);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A failed row retains a bounded lease; its later retry cannot
                // manufacture a new attempt or alter the saved connection.
                logger.LogWarning("Execution reconciliation will retry after its lease. TaskId={TaskId} ErrorCode={ErrorCode}", task.Id, error.GetType().Name);
            }
        }
        return processed;
    }

    private async Task ReconcileAsync(RequestTask task, DateTimeOffset now, CancellationToken ct)
    {
        task.AutomationDeadlineUtcTicks ??= AutomationExecutionDeadlinePolicy.ResolveDeadlineUtcTicks(task, now);
        var expired = now.UtcTicks >= task.AutomationDeadlineUtcTicks.Value;
        OrchestrationCallbackDto? result = null;
        try
        {
            if (Guid.TryParse(task.OrchestrationLinkId, out var connection)
                && !string.IsNullOrWhiteSpace(task.OrganizationId)
                && !string.IsNullOrWhiteSpace(task.AutomationAttemptCorrelationId)
                && !string.IsNullOrWhiteSpace(task.OrchestrationExternalRequestId)
                && !string.IsNullOrWhiteSpace(task.OrchestrationExternalRunId))
            {
                var settings = await connectivity.GetResolvedOrchestrationSettingsAsync(task.OrganizationId, connection, ct);
                if (settings.Enabled && settings.Pairing is { } binding
                    && binding.LocalTenantId == task.OrganizationId && binding.MappingId == task.OrchestrationLinkId
                    && binding.PeerInstanceId == task.OrchestrationPeerInstanceId && binding.MappingRevision == task.OrchestrationLinkRevision)
                {
                    result = await provider.ReadExecutionAsync(settings, new OrchestrationExecutionQuery(
                        task.RequestId, task.Id, task.AutomationAttemptCorrelationId,
                        task.OrchestrationExternalRequestId, task.OrchestrationExternalRunId), ct);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("The recorded provider execution could not be confirmed. TaskId={TaskId} ErrorCode={ErrorCode}", task.Id, error.GetType().Name);
        }
        if (result is not null && (result.RequestTaskId != task.Id || result.CorrelationId != task.AutomationAttemptCorrelationId
            || result.OrchestrationRequestId != task.OrchestrationExternalRequestId || result.OrchestrationRunId != task.OrchestrationExternalRunId))
        {
            logger.LogWarning("Provider execution read did not match the recorded attempt. TaskId={TaskId}", task.Id);
            result = null;
        }
        if (result?.Status is "succeeded" or "failed" or "cancelled")
        {
            await outcomes.ApplyAsync(task, result, result.Status, task.AutomationAttemptCorrelationId!, "execution-reconciler", ct);
            return;
        }
        if ((task.Status == RequestTaskStatus.InProgress && (expired || result?.Status == "unknown"))
            || (expired && task.AutomationTimedOutAt is null
                && AutomationTaskStatuses.RequiresManualReconciliation(task.LastAutomationStatus)))
        {
            var unknown = new OrchestrationCallbackDto
            {
                RequestTaskId = task.Id, CorrelationId = task.AutomationAttemptCorrelationId,
                OrchestrationRequestId = task.OrchestrationExternalRequestId, OrchestrationRunId = task.OrchestrationExternalRunId,
                Status = "unknown", Message = expired ? "The local deadline expired without confirmed remote termination." : "The provider could not establish the execution outcome."
            };
            await outcomes.ApplyAsync(task, unknown, "unknown", task.AutomationAttemptCorrelationId ?? $"legacy:{task.Id}",
                "execution-reconciler", ct, deadlineExpired: expired);
            return;
        }
        // A provider still reporting Running cannot extend the snapshotted hard
        // deadline. Already uncertain attempts remain held while read-only checks back off.
        var interval = task.Status == RequestTaskStatus.InProgress
            ? AutomationExecutionDeadlinePolicy.ReconciliationInterval
            : AutomationExecutionDeadlinePolicy.UnknownOutcomeReconciliationInterval;
        task.AutomationNextCheckUtcTicks = task.Status == RequestTaskStatus.InProgress
            ? Math.Min(task.AutomationDeadlineUtcTicks.Value, now.Add(interval).UtcTicks) : now.Add(interval).UtcTicks;
        if (result is not null)
        {
            task.LastAutomationUpdatedAt = now;
            if (task.Status == RequestTaskStatus.InProgress) task.LastAutomationStatus = result.Status;
        }
        task.AutomationReconciliationLeaseId = null;
        task.AutomationReconciliationLeaseUntilUtcTicks = null;
        await db.SaveChangesAsync(ct);
    }

    private sealed record Candidate(string Id, string? OrganizationId, long Version, string? Attempt, bool ContinuationPending);
}
