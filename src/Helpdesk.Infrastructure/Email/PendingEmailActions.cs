using System.Text.Json;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Email;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Queue controls retain the original timeline content and durable payload.</summary>
public sealed class PendingEmailActions(HelpdeskDbContext db, MailboxOutboxStore store, TimeProvider clock)
{
    public async Task<PendingEmailActionResult> ApplyAsync(Guid id, string action, string actor,
        string reason, CancellationToken ct)
    {
        if (action == "retry")
        {
            try
            {
                var owner = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleOrDefaultAsync(x => x.DeliveryEventId == id, ct);
                if (owner is null) return new(id, "ReviewRequired", "Legacy delivery requires individual sender review.");
                var email = JsonSerializer.Deserialize<IngressEmailEffect>(owner.Payload);
                if (email?.MailboxId is null || email.MailboxConfigurationVersion is null || email.OutgoingConfigurationVersion is null)
                    return new(id, "ReviewRequired", "Sender binding requires individual review.");
                var sender = await new MailboxSenderResolver(db).ResolveAsync(email.TicketId, email.OrganizationId, ct);
                if (sender.Status != "Ready" || sender.Mailbox?.Id != email.MailboxId ||
                    sender.Mailbox.Version != email.MailboxConfigurationVersion || sender.Outgoing?.Version != email.OutgoingConfigurationVersion)
                    return new(id, "ReviewRequired", "Sender route or revision changed. Review the current sender before retrying.");
                return await store.RetryForTimelineAsync(id, ct, actor, reason)
                    ? new(id, "Queued", "Delivery queued using its existing sender binding.")
                    : new(id, "ReviewRequired", "Legacy delivery requires individual sender review.");
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException)
            {
                return new(id, "ReviewRequired", "State changed or sender, recipient or outcome review is required. Open delivery details.");
            }
        }
        if (action is not ("cancel" or "remove"))
            return new(id, "InvalidAction", "Unsupported queue action.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var timeline = await db.TicketTimelineEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.EventType == TimelineEventType.EmailDelivery, ct);
        if (timeline is null) return new(id, "NotFound", "Delivery unavailable.");
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeliveryEventId == id, ct);
        if (row?.State == MailboxEffectState.Removed || timeline.EmailStatus == EmailDeliveryStatus.Removed)
            return new(id, "AlreadyRemoved", "Queue entry was already removed; no delivery recall is implied.");
        if (action == "cancel" && (row?.State == MailboxEffectState.Cancelled || timeline.EmailStatus == EmailDeliveryStatus.Cancelled))
            return new(id, "AlreadyCancelled", "Future dispatch is already cancelled.");
        if (row is not null && (row.Kind != MailboxEffectKind.Email || row.State == MailboxEffectState.Submitting ||
            row.State == MailboxEffectState.InFlight && row.Owner?.StartsWith("pre-submit:", StringComparison.Ordinal) != true))
            return new(id, "Conflict", "Submission may have started. This delivery cannot be recalled or safely changed.");
        if (action == "cancel" && !CanCancel(row, timeline.EmailStatus, timeline.RetryError))
            return new(id, "ReviewRequired", "Delivery may have been accepted or its outcome is unknown. Cancellation cannot be guaranteed.");
        // A legacy Pending row can be between a retry CAS and durable outbox creation.
        if (row is null && timeline.EmailStatus == EmailDeliveryStatus.Pending)
            return new(id, "Conflict", "Legacy delivery may be in flight. Refresh and verify its outcome.");

        if (row is not null)
        {
            var next = action == "cancel" ? MailboxEffectState.Cancelled : MailboxEffectState.Removed;
            var changed = await db.Set<MailboxOutboxEffect>()
                .Where(x => x.Id == row.Id && x.State == row.State && x.Fence == row.Fence &&
                    x.Payload == row.Payload && x.LastErrorCode == row.LastErrorCode &&
                    x.RecipientOutcomeJson == row.RecipientOutcomeJson)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, next)
                    .SetProperty(x => x.Fence, x => x.Fence + 1).SetProperty(x => x.Owner, (string?)null)
                    .SetProperty(x => x.LeaseExpiresUnixMilliseconds, 0L), ct);
            if (changed != 1) return new(id, "Conflict", "Delivery changed. Refresh before taking action.");
        }
        // Removed delivery status is used only for ownerless legacy rows. Owned rows retain their
        // delivery outcome, recipients and retry state; the outbox tombstone controls visibility.
        if (action == "cancel" || row is null)
        {
            var changed = await db.TicketTimelineEvents.Where(x => x.Id == id && x.EmailStatus == timeline.EmailStatus)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.EmailStatus,
                    action == "cancel" ? EmailDeliveryStatus.Cancelled : EmailDeliveryStatus.Removed), ct);
            if (changed != 1) return new(id, "Conflict", "Delivery changed. Refresh before taking action.");
        }
        db.ActivityLogs.Add(new ActivityLog
        {
            TicketId = timeline.TicketId, RelatedEntityId = id.ToString("D"), UserId = actor,
            Timestamp = clock.GetUtcNow().UtcDateTime,
            Message = $"Email queue {action}. PreviousStatus={timeline.EmailStatus}; PreviousQueueState={row?.State}; Fence={row?.Fence}; Reason={reason}. " +
                (action == "remove" ? "Queue visibility removed and future dispatch blocked; prior delivery is not recalled." : "Future delivery cancelled before submission.")
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return action == "cancel" ? new(id, "Cancelled", "Future delivery cancelled before submission.")
            : new(id, "Removed", "Queue entry removed. Ticket history is retained; prior delivery is not recalled.");
    }

    internal static bool CanCancel(MailboxOutboxEffect? row, EmailDeliveryStatus? status, string? legacyError = null)
    {
        if (status is EmailDeliveryStatus.Sent or EmailDeliveryStatus.Delivered or EmailDeliveryStatus.Removed)
            return false;
        if (row is null) return status == EmailDeliveryStatus.Failed && legacyError is not
            ("DispatchOutcomeUnknown" or "SubmissionOutcomeUnknown" or "SmtpPartialRecipientAcceptance");
        if (row.State is MailboxEffectState.Submitting or MailboxEffectState.Completed or MailboxEffectState.Removed)
            return false;
        if (row.State == MailboxEffectState.InFlight && row.Owner?.StartsWith("pre-submit:", StringComparison.Ordinal) != true)
            return false;
        if (row.LastErrorCode is "DispatchOutcomeUnknown" or "SubmissionOutcomeUnknown" or "SmtpPartialRecipientAcceptance")
            return false;
        if (row.RecipientOutcomeJson is null) return true;
        try
        {
            var outcome = JsonSerializer.Deserialize<MailboxRecipientOutcome>(row.RecipientOutcomeJson);
            return outcome?.AcceptedRecipients?.Length == 0 && outcome.RejectedRecipients is not null;
        }
        catch (JsonException) { return false; }
    }
}
