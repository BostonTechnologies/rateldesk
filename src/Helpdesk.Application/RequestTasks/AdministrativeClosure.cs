using Helpdesk.Shared.Models;

namespace Helpdesk.Application.RequestTasks;

public static class AdministrativeClosure
{
    public const int MaximumReasonLength = 1000;

    public static bool RequiresDisposition(RequestTask task) =>
        task.AdministrativeClosureReason is null &&
        task.Status is not (RequestTaskStatus.Completed or RequestTaskStatus.Skipped);

    public static void Close(Ticket ticket, string reason, string actor, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (reason.Trim().Length > MaximumReasonLength)
            throw new ArgumentOutOfRangeException(nameof(reason));
        if (ticket is not (Request or RequestTask))
            throw new ArgumentException("Only requests and request tasks support administrative closure.", nameof(ticket));
        if (ticket.AdministrativeClosureReason != null)
            return;
        ticket.AdministrativeClosureReason = reason.Trim();
        ticket.AdministrativelyClosedBy = actor;
        ticket.ClosedAt = now;
        ticket.UpdatedAt = now.UtcDateTime;
        ticket.State = TicketState.Resolved;
        if (ticket is RequestTask task)
        {
            task.NextRetryAt = null;
            task.AutomationNextCheckUtcTicks = null;
            task.AutomationContinuationPending = false;
            task.AutomationReconciliationLeaseId = null;
            task.AutomationReconciliationLeaseUntilUtcTicks = null;
        }
    }
}
