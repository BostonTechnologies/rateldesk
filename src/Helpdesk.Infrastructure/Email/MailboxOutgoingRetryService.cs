using System.Text.Json;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Explicitly adopts a repaired outgoing revision for one failed delivery.</summary>
public sealed class MailboxOutgoingRetryService(HelpdeskDbContext db, MailboxSenderResolver resolver,
    TimeProvider clock)
{
    public async Task<MailboxOutgoingRetryPreview> PreviewAsync(Guid timelineId, CancellationToken ct)
    {
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeliveryEventId == timelineId, ct);
        return await PreviewAsync(timelineId, row, ct);
    }

    public async Task<MailboxOutgoingRetryPreview> RetryAsync(Guid timelineId,
        long expectedOutgoingVersion, string userId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeliveryEventId == timelineId, ct);
        var preview = await PreviewAsync(timelineId, row, ct);
        if (!preview.CanRetry || row is null || preview.CurrentOutgoingVersion != expectedOutgoingVersion)
            return preview with { CanRetry = false, Status = preview.CanRetry ? "OutgoingRevisionChanged" : preview.Status };

        var email = MailboxOutboxStore.Deserialize<IngressEmailEffect>(row.Payload);
        var rebound = email with
        {
            OutgoingConfigurationVersion = expectedOutgoingVersion,
            SenderBindingError = null
        };
        var payload = JsonSerializer.Serialize(rebound);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == row.Id && x.Fence == row.Fence && x.State == row.State &&
                x.Payload == row.Payload && x.LastErrorCode == row.LastErrorCode)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Payload, payload)
                .SetProperty(x => x.State, MailboxEffectState.Pending)
                .SetProperty(x => x.Attempts, 0)
                .SetProperty(x => x.Fence, x => x.Fence + 1)
                .SetProperty(x => x.Owner, (string?)null)
                .SetProperty(x => x.LeaseExpiresUnixMilliseconds, 0L)
                .SetProperty(x => x.AvailableUnixMilliseconds, now)
                .SetProperty(x => x.LastErrorCode, (string?)null)
                .SetProperty(x => x.RecipientOutcomeJson, (string?)null), ct);
        if (changed != 1)
            return preview with { CanRetry = false, Status = "DeliveryChanged" };

        await db.TicketTimelineEvents.Where(x => x.Id == timelineId &&
                x.EventType == TimelineEventType.EmailDelivery && x.EmailStatus == EmailDeliveryStatus.Failed)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.EmailStatus, EmailDeliveryStatus.Pending)
                .SetProperty(x => x.RetryError, (string?)null)
                .SetProperty(x => x.MessageText, "Email queued with the confirmed outgoing revision."), ct);
        db.ActivityLogs.Add(new ActivityLog
        {
            UserId = userId,
            RelatedEntityId = preview.MailboxId?.ToString("D"),
            Message = $"Mailbox outgoing retry confirmed. TimelineId={timelineId:D}; OriginalOutgoingVersion={preview.OriginalOutgoingVersion?.ToString() ?? "none"}; CurrentOutgoingVersion={expectedOutgoingVersion}."
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return preview with { Status = "Queued" };
    }

    private async Task<MailboxOutgoingRetryPreview> PreviewAsync(Guid timelineId,
        MailboxOutboxEffect? row, CancellationToken ct)
    {
        if (row is null || row.Kind != MailboxEffectKind.Email ||
            row.State is not (MailboxEffectState.Exhausted or MailboxEffectState.NeedsReview) ||
            !await db.TicketTimelineEvents.AsNoTracking().AnyAsync(x => x.Id == timelineId &&
                x.EventType == TimelineEventType.EmailDelivery && x.EmailStatus == EmailDeliveryStatus.Failed, ct))
            return Blocked(timelineId, "DeliveryNotReviewable");

        // Outcomes that may already have reached the server need recipient-level review.
        // Exhausted connection/authentication errors occurred before submission.
        var safeFailure = row.State == MailboxEffectState.NeedsReview
            ? row.LastErrorCode is "OutgoingNotConfigured" or "OutgoingDisabled" or
                "SmtpCredentialMissing" or "GraphCredentialMissing" or "SenderConfigurationChanged" or
                "SmtpAuthenticationFailed" or "SmtpTlsFailed" or "SmtpSenderRejected" or "SmtpCommandRejected"
            : row.LastErrorCode is "SmtpAuthenticationFailed" or "SmtpTlsFailed" or
                "SmtpTimedOut" or "SocketException" or "SmtpCommandRejected";
        if (!safeFailure) return Blocked(timelineId, "DeliveryOutcomeRequiresReview");

        IngressEmailEffect email;
        try { email = MailboxOutboxStore.Deserialize<IngressEmailEffect>(row.Payload); }
        catch (JsonException) { return Blocked(timelineId, "InvalidDeliveryPayload"); }
        if (email.MailboxId is null || email.MailboxConfigurationVersion is null ||
            string.IsNullOrWhiteSpace(email.TicketId) || string.IsNullOrWhiteSpace(email.OrganizationId))
            return Blocked(timelineId, "SenderBindingMissing");

        var selected = await resolver.ResolveAsync(email.TicketId, email.OrganizationId, ct);
        if (selected.Mailbox?.Id != email.MailboxId)
            return Blocked(timelineId, "SenderRouteChanged");
        if (selected.Mailbox.Version != email.MailboxConfigurationVersion)
            return Blocked(timelineId, "MailboxConfigurationChanged");
        if (selected.Status != "Ready" || selected.Outgoing is null)
            return Blocked(timelineId, selected.ErrorCode ?? "SenderUnavailable");
        if (selected.Outgoing.Version == email.OutgoingConfigurationVersion)
            return Blocked(timelineId, "OutgoingRevisionUnchanged");

        return new(timelineId, true, "Ready", selected.Mailbox.Id, selected.Mailbox.MailboxAddress,
            selected.Outgoing.Transport, email.OutgoingConfigurationVersion, selected.Outgoing.Version);
    }

    private static MailboxOutgoingRetryPreview Blocked(Guid timelineId, string status) =>
        new(timelineId, false, status, null, null, null, null, null);
}
