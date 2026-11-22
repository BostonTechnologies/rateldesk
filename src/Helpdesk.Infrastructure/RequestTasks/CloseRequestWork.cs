using System.Data;
using Helpdesk.Application.Events;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.RequestTasks;

public sealed class CloseRequestWork(HelpdeskDbContext db, IIngressEffectContext effects, MailboxOutboxStore outbox,
    ITicketSlaCompletionService sla, TimeProvider clock, IDomainEventPublisher domainEvents)
{
    public async Task<CloseWorkResult> ExecuteAsync(CloseWorkSelection selection, bool closeParent, string reason,
        string actorId, CurrentUserAccessProfile access, CancellationToken ct)
    {
        if (!access.IsAuthenticated || string.IsNullOrWhiteSpace(actorId))
            return new(selection.Id, "unavailable");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            using var capture = effects.Begin(Guid.NewGuid());
            var task = closeParent ? null : await db.RequestTasks.SingleOrDefaultAsync(x => x.Id == selection.Id, ct);
            var parentId = closeParent ? selection.Id : task?.RequestId;
            var parent = await db.Requests.SingleOrDefaultAsync(x => x.Id == parentId, ct);
            if (parent is null || !access.CanManageRequest(parent.OrganizationId) ||
                task != null && task.OrganizationId != parent.OrganizationId)
                return new(selection.Id, "unavailable");

            var selected = (Ticket?)task ?? parent;
            if (selected.AdministrativeClosureReason != null)
                return new(selection.Id, "already_closed");
            if (selection.StateVersion != (task?.AutomationStateVersion ?? parent.WorkflowStateVersion))
                return new(selection.Id, "changed");

            // Validate the whole parent/child relation, including malformed legacy
            // links hidden by a tenant filter, before settling any member.
            var children = await db.RequestTasks.IgnoreQueryFilters().Where(x => x.RequestId == parent.Id).ToListAsync(ct);
            if (children.Any(x => x.OrganizationId != parent.OrganizationId))
                return new(selection.Id, "unavailable");
            var unfinished = children.Where(AdministrativeClosure.RequiresDisposition).ToList();
            if (task != null && !unfinished.Contains(task) || closeParent && parent.State == TicketState.Resolved)
                return new(selection.Id, "already_finished");

            var affected = task is null ? unfinished : [task];
            var remaining = unfinished.Count - affected.Count;
            var now = clock.GetUtcNow();
            effects.OrganizationId = parent.OrganizationId;
            foreach (var child in affected)
                AdministrativeClosure.Close(child, reason, actorId, now);
            // An individual disposition that settles all outstanding work also
            // settles its parent. Siblings never start as a side effect.
            var settleParent = closeParent || remaining == 0;
            if (settleParent)
                AdministrativeClosure.Close(parent, reason, actorId, now);
            parent.WorkflowUpdatedAt = now;
            db.Entry(parent).Property(x => x.WorkflowStateVersion).IsModified = true;
            var ids = affected.Select(x => x.Id).ToArray();
            var approvals = await db.RequestTaskApprovals.Where(x => ids.Contains(x.RequestTaskId) &&
                x.Status == RequestTaskApprovalStatus.Pending).ToListAsync(ct);
            foreach (var approval in approvals)
            {
                approval.Status = RequestTaskApprovalStatus.Superseded;
                approval.ReviewedAtUtc = now;
            }

            var message = closeParent
                ? $"Request closed by operator. {affected.Count} unfinished tasks closed. {reason}"
                : $"Task '{task!.Title}' closed by operator. {remaining} unfinished tasks remain. {reason}";
            var entry = new TicketTimelineEvent
            {
                TicketId = parent.Id, EventType = TimelineEventType.SystemNotification,
                CreatedUtc = now, CreatedByUserId = actorId, CreatedByUserName = access.Name ?? actorId,
                MessageText = message
            };
            db.TicketTimelineEvents.Add(entry);
            await RequestSnapshotJournal.StageAsync(db,
                [new RequestSnapshotChangedEvent(parent.Id, parent.OrganizationId, now, effects.ReceiptId.ToString("N"))], ct);
            db.ActivityLogs.Add(new ActivityLog { TicketId = parent.Id, RelatedEntityId = selected.Id, UserId = actorId, Message = message });
            await db.SaveChangesAsync(ct);
            foreach (var id in ids)
                await sla.HandleTicketClosedAsync(id, actorId, now);
            if (settleParent)
                await sla.HandleTicketClosedAsync(parent.Id, actorId, now);
            await domainEvents.PublishAsync(new RequestWorkClosedEvent(parent.Id, task?.Id, task?.Title, actorId,
                settleParent, affected.Count, remaining, parent.OrganizationId, now, effects.ReceiptId.ToString("N")), ct);
            effects.Capture(MailboxEffectKind.Timeline, TicketTimelineProjection.ToDto(entry));
            await outbox.FlushAsync(ct);
            await tx.CommitAsync(ct);
            return new(selection.Id, "closed", affected.Count, remaining);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            return new(selection.Id, "changed");
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static bool IsConcurrencyConflict(Exception error) => error switch
    {
        DbUpdateConcurrencyException => true,
        Npgsql.PostgresException { SqlState: "40001" or "40P01" } => true,
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 } => true,
        { InnerException: { } inner } => IsConcurrencyConflict(inner),
        _ => false
    };
}
