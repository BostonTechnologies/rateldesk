using System.Data;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Helpdesk.Infrastructure.RequestTasks;

public sealed partial class RequestTaskApprovalService
{
    private async Task<RequestTaskApprovalReviewResult> ReviewDecisionAsync(string taskId,
        Func<Task<(Request Request, RequestTask Task, RequestTaskApproval Approval)?>> load,
        bool approve, string? reason, string? actorId, CancellationToken ct)
    {
        try
        {
            await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var loaded = await load();
            if (loaded is null)
                return RequestTaskApprovalReviewResult.Failed(actorId is null
                    ? "Approval link is invalid or expired."
                    : "The authenticated caller is not the designated approver with authority in this organization.", "approval_not_found");

            var (request, task, approval) = loaded.Value;
            // Both public signed links and authenticated decisions acquire the same
            // task row before changing any approval, task, audit or delivery effect.
            // The predicate also rejects stale tracked task state from an earlier read.
            if (!await FenceTaskAsync(task, ct))
                return DecisionChanged();

            var desired = approve ? RequestTaskApprovalStatus.Approved : RequestTaskApprovalStatus.Rejected;
            if (approval.Status == desired)
                return RequestTaskApprovalReviewResult.Succeeded(request.Id, task.Id, desired);
            if (approval.Status != RequestTaskApprovalStatus.Pending || task.Status != RequestTaskStatus.PendingApproval ||
                request.WorkflowStatus == "Cancelled" || task.DueAt is { } due && due <= DateTimeOffset.UtcNow)
                return RequestTaskApprovalReviewResult.Failed("Approval is no longer pending or its deadline has expired.", "approval_not_pending");

            var result = approve
                ? await ApproveLoadedAsync(request, task, approval, ct)
                : await RejectLoadedAsync(request, task, approval, reason, ct);
            if (!result.Success) return result;
            if (actorId is not null)
            {
                db.ActivityLogs.Add(new ActivityLog
                {
                    UserId = actorId, RelatedEntityId = taskId,
                    Message = $"Authenticated designated approver {(approve ? "approved" : "rejected")} request task '{taskId}'."
                });
                await db.SaveChangesAsync(ct);
            }
            if (transaction is not null) await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (IsDecisionConflict(error))
        {
            db.ChangeTracker.Clear();
            return DecisionChanged();
        }
    }

    private async Task<bool> ExpireTaskAsync(string taskId, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var task = await db.RequestTasks.SingleOrDefaultAsync(candidate => candidate.Id == taskId, ct);
            if (task is null || task.Type != RequestTaskType.Approval || task.Status != RequestTaskStatus.PendingApproval ||
                task.DueAt is null || task.DueAt > now || !await FenceTaskAsync(task, ct))
                return false;
            var request = await db.Requests.SingleOrDefaultAsync(candidate => candidate.Id == task.RequestId, ct);
            if (request is null || request.WorkflowStatus == "Cancelled") return false;
            var approvals = await db.RequestTaskApprovals
                .Where(candidate => candidate.RequestTaskId == taskId && candidate.Status == RequestTaskApprovalStatus.Pending)
                .ToListAsync(ct);
            foreach (var approval in approvals)
            {
                approval.Status = RequestTaskApprovalStatus.Expired;
                approval.ReviewedAtUtc = now;
            }
            await CancelRequestAsync(request, task, $"Approval timed out after waiting until {task.DueAt:yyyy-MM-dd HH:mm} UTC.", now, ct);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return true;
        }
        catch (Exception error) when (IsDecisionConflict(error))
        {
            // A concurrent decision owns the outcome. The next scheduled sweep
            // revisits anything still pending instead of overwriting that decision.
            db.ChangeTracker.Clear();
            logger.LogInformation("Approval timeout deferred after a concurrent decision for task {TaskId}.", taskId);
            return false;
        }
    }

    private async Task<bool> FenceTaskAsync(RequestTask task, CancellationToken ct) =>
        !db.Database.IsRelational() || await db.RequestTasks.IgnoreQueryFilters()
            .Where(candidate => candidate.Id == task.Id && candidate.RequestId == task.RequestId && candidate.Status == task.Status)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.Status, candidate => candidate.Status), ct) == 1;

    private static RequestTaskApprovalReviewResult DecisionChanged() =>
        RequestTaskApprovalReviewResult.Failed("The approval changed concurrently. Read its current outcome before another action.", "approval_changed");

    private static bool IsDecisionConflict(Exception error) => error switch
    {
        DbUpdateConcurrencyException => true,
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => true,
        SqliteException { SqliteErrorCode: 5 or 6 } => true,
        DbUpdateException { InnerException: { } inner } => IsDecisionConflict(inner),
        // Unwrap Npgsql's execution-strategy wrapper without treating arbitrary
        // InvalidOperationException failures as retryable approval conflicts.
        InvalidOperationException { InnerException: { } inner } => IsDecisionConflict(inner),
        _ => false
    };
}
