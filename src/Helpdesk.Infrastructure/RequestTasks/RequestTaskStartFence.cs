using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.RequestTasks;

/// <summary>Linearizes a start intent with every committed parent/uncertainty hold.</summary>
public sealed class RequestTaskStartFence(HelpdeskDbContext db) : IRequestTaskStartFence
{
    public Task PersistAsync(RequestTask task, RequestTaskStartIntent intent, CancellationToken ct)
        => PersistAsync(task, intent.StartedAt, _ => intent.Apply(task), approval: false, ct);

    public Task PersistApprovalAsync(RequestTask task, RequestTaskApprovalStartIntent intent, CancellationToken ct)
        => PersistAsync(task, intent.StartedAt, parent => intent.Apply(task, parent), approval: true, ct);

    private async Task PersistAsync(RequestTask task, DateTimeOffset startedAt, Action<Request> apply, bool approval, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Complete the current workflow transaction before starting execution.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var parent = await db.Requests.AsNoTracking().SingleOrDefaultAsync(request => request.Id == task.RequestId, ct)
                ?? throw new KeyNotFoundException("Request not found");
            var ownsExistingApprovalHold = approval && parent.WorkflowStatus == "PendingApproval"
                && parent.WorkflowBlockReason == $"Waiting for approval for task '{task.Name}'.";
            if (RequestHoldPolicy.IsOperatorHeld(parent)
                || RequestHoldPolicy.BlocksStart(parent) && !ownsExistingApprovalHold)
                throw new InvalidOperationException("This request is on hold. Resolve its outstanding holds before starting more work.");
            if (await db.RequestTasks.AsNoTracking().AnyAsync(sibling => sibling.RequestId == task.RequestId
                && sibling.Type == RequestTaskType.Automation && sibling.LastAutomationStatus != null
                && (sibling.LastAutomationStatus.ToLower() == "executionunknownmanualreconcile"
                    || sibling.LastAutomationStatus.ToLower() == "submituncertainmanualreconcile"), ct))
                throw new InvalidOperationException("Reconcile the unresolved automation attempt before starting more work on this request.");
            var current = await db.RequestTasks.AsNoTracking().Where(row => row.Id == task.Id)
                .Select(row => new { row.Status, row.IsBlocked, row.AutomationStateVersion }).SingleAsync(ct);
            if ((current.Status != RequestTaskStatus.Pending && !(approval && current.Status == RequestTaskStatus.PendingApproval))
                || current.IsBlocked
                || current.AutomationStateVersion != task.AutomationStateVersion)
                throw new DbUpdateConcurrencyException("The task changed before its start intent could be recorded.");

            var trackedParent = db.Requests.Local.SingleOrDefault(request => request.Id == parent.Id);
            if (trackedParent is null)
                db.Requests.Attach(parent);
            else
            {
                if (db.Entry(trackedParent).State != Microsoft.EntityFrameworkCore.EntityState.Unchanged)
                    throw new InvalidOperationException("Persist pending request changes before starting execution.");
                db.Entry(trackedParent).CurrentValues.SetValues(parent);
                db.Entry(trackedParent).OriginalValues.SetValues(parent);
                parent = trackedParent;
            }
            // SaveChanges increments the parent's optimistic version. A hold that
            // wins after the fresh reads rolls back both this task and this fence.
            parent.WorkflowUpdatedAt = startedAt;
            parent.UpdatedAt = startedAt.UtcDateTime;
            db.Entry(parent).Property(request => request.WorkflowStateVersion).IsModified = true;
            apply(parent);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            // A caller may catch a rejected start and continue evaluating siblings.
            // Never let its next repository save persist a rejected tracked intent.
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
