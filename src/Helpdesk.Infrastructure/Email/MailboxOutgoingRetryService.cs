using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly Regex EvidenceReferenceFormat = new(
        @"\A[A-Za-z0-9][A-Za-z0-9._:/-]{7,127}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            .Where(x => x.Id == row.Id && x.DeliveryEventId == timelineId && x.Kind == MailboxEffectKind.Email &&
                x.Fence == row.Fence && x.State == row.State && x.Payload == row.Payload &&
                x.LastErrorCode == row.LastErrorCode && x.RecipientOutcomeJson == row.RecipientOutcomeJson)
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

        var timelineChanged = await db.TicketTimelineEvents.Where(x => x.Id == timelineId &&
                x.EventType == TimelineEventType.EmailDelivery && x.EmailStatus == EmailDeliveryStatus.Failed)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.EmailStatus, EmailDeliveryStatus.Pending)
                .SetProperty(x => x.RetryError, (string?)null)
                .SetProperty(x => x.MessageText, "Email queued with the confirmed outgoing revision."), ct);
        if (timelineChanged != 1)
            return preview with { CanRetry = false, Status = "DeliveryChanged" };
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

    public async Task<MailboxUncertainRetryPreview> PreviewUncertainAsync(Guid timelineId, CancellationToken ct)
    {
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeliveryEventId == timelineId, ct);
        return await PreviewUncertainAsync(timelineId, row, ct);
    }

    public async Task<MailboxUncertainRetryPreview> RetryConfirmedUndeliveredAsync(Guid timelineId,
        ConfirmMailboxUndeliveredRetryRequest request, string userId, CancellationToken ct)
    {
        if (!request.ConfirmedNoDelivery || string.IsNullOrWhiteSpace(request.EvidenceReference) ||
            !EvidenceReferenceFormat.IsMatch(request.EvidenceReference))
            return BlockedUncertain(timelineId, "EvidenceReferenceRequired");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeliveryEventId == timelineId, ct);
        var preview = await PreviewUncertainAsync(timelineId, row, ct);
        if (!preview.CanRetry || row is null)
            return preview;
        if (row.Fence != request.ExpectedFence ||
            preview.CurrentOutgoingVersion != request.ExpectedOutgoingVersion)
            return preview with { CanRetry = false, Status = "DeliveryChanged" };

        var original = MailboxOutboxStore.Deserialize<IngressEmailEffect>(row.Payload);
        var payload = JsonSerializer.Serialize(original with
        {
            OutgoingConfigurationVersion = request.ExpectedOutgoingVersion,
            SenderBindingError = null
        });
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == row.Id && x.DeliveryEventId == timelineId && x.Kind == MailboxEffectKind.Email &&
                x.Fence == request.ExpectedFence && x.State == row.State && x.Payload == row.Payload &&
                x.LastErrorCode == row.LastErrorCode && x.RecipientOutcomeJson == row.RecipientOutcomeJson)
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
        var timelineChanged = await db.TicketTimelineEvents.Where(x => x.Id == timelineId &&
                x.EventType == TimelineEventType.EmailDelivery && x.EmailStatus == EmailDeliveryStatus.Failed)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.EmailStatus, EmailDeliveryStatus.Pending)
                .SetProperty(x => x.RetryError, (string?)null)
                .SetProperty(x => x.MessageText, "Email queued after administrator confirmed non-delivery."), ct);
        if (timelineChanged != 1)
            return preview with { CanRetry = false, Status = "DeliveryChanged" };
        db.ActivityLogs.Add(new ActivityLog
        {
            UserId = userId,
            RelatedEntityId = preview.MailboxId?.ToString("D"),
            Message = $"Uncertain mailbox delivery retried after confirmed non-delivery. TimelineId={timelineId:D}; EvidenceReference={request.EvidenceReference}; OriginalOutgoingVersion={preview.OriginalOutgoingVersion}; CurrentOutgoingVersion={request.ExpectedOutgoingVersion}."
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return preview with { Status = "Queued" };
    }

    private async Task<MailboxUncertainRetryPreview> PreviewUncertainAsync(Guid timelineId,
        MailboxOutboxEffect? row, CancellationToken ct)
    {
        if (row is null || row.Kind != MailboxEffectKind.Email ||
            row.State is not (MailboxEffectState.NeedsReview or MailboxEffectState.Exhausted) ||
            row.LastErrorCode is not ("DispatchOutcomeUnknown" or "SubmissionOutcomeUnknown") ||
            !await db.TicketTimelineEvents.AsNoTracking().AnyAsync(x => x.Id == timelineId &&
                x.EventType == TimelineEventType.EmailDelivery && x.EmailStatus == EmailDeliveryStatus.Failed, ct))
            return BlockedUncertain(timelineId, "DeliveryNotUncertain");
        if (row.RecipientOutcomeJson is not null)
        {
            MailboxRecipientOutcome? outcome;
            try { outcome = JsonSerializer.Deserialize<MailboxRecipientOutcome>(row.RecipientOutcomeJson); }
            catch (JsonException) { return BlockedUncertain(timelineId, "RecipientOutcomeUnavailable"); }
            if (outcome?.AcceptedRecipients is null || outcome.RejectedRecipients is null)
                return BlockedUncertain(timelineId, "RecipientOutcomeUnavailable");
            if (outcome.AcceptedRecipients.Length > 0)
                return BlockedUncertain(timelineId, "AcceptedRecipientsRequireReview");
        }

        IngressEmailEffect email;
        try { email = MailboxOutboxStore.Deserialize<IngressEmailEffect>(row.Payload); }
        catch (JsonException) { return BlockedUncertain(timelineId, "InvalidDeliveryPayload"); }
        if (email.MailboxId is null || email.MailboxConfigurationVersion is null ||
            string.IsNullOrWhiteSpace(email.TicketId) || string.IsNullOrWhiteSpace(email.OrganizationId) ||
            email.Recipients is null || email.Cc is null || email.Bcc is null || email.Attachments is null)
            return BlockedUncertain(timelineId, "SenderBindingMissing");
        var recipients = email.Recipients.Concat(email.Cc).Concat(email.Bcc)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (recipients.Length == 0)
            return BlockedUncertain(timelineId, "RecipientsMissing");

        var selected = await resolver.ResolveAsync(email.TicketId, email.OrganizationId, ct);
        if (selected.Mailbox?.Id != email.MailboxId)
            return BlockedUncertain(timelineId, "SenderRouteChanged");
        if (selected.Mailbox.Version != email.MailboxConfigurationVersion)
            return BlockedUncertain(timelineId, "MailboxConfigurationChanged");
        if (selected.Status != "Ready" || selected.Outgoing is null)
            return BlockedUncertain(timelineId, selected.ErrorCode ?? "SenderUnavailable");
        return new(timelineId, true, "RequiresNonDeliveryConfirmation", selected.Mailbox.Id,
            selected.Mailbox.MailboxAddress, selected.Outgoing.Transport, recipients, row.Fence,
            email.OutgoingConfigurationVersion, selected.Outgoing.Version);
    }

    private static MailboxUncertainRetryPreview BlockedUncertain(Guid timelineId, string status) =>
        new(timelineId, false, status, null, null, null, [], null, null, null);

    public async Task<MailboxRouteRetryPreview> PreviewChangedRouteAsync(Guid timelineId, CancellationToken ct)
    {
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeliveryEventId == timelineId, ct);
        return await PreviewChangedRouteAsync(timelineId, row, ct);
    }

    public async Task<MailboxRouteRetryPreview> RetryWithCurrentRouteAsync(Guid timelineId,
        ConfirmMailboxRouteRetryRequest request, string userId, CancellationToken ct)
    {
        if (!request.ConfirmedNewSender)
            return BlockedRoute(timelineId, "SenderConfirmationRequired");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeliveryEventId == timelineId, ct);
        var preview = await PreviewChangedRouteAsync(timelineId, row, ct);
        if (!preview.CanRetry || row is null)
            return preview;
        if (preview.Fence != request.ExpectedFence || preview.OriginalMailboxId != request.ExpectedOriginalMailboxId ||
            preview.CurrentMailboxId != request.ExpectedCurrentMailboxId ||
            preview.CurrentMailboxVersion != request.ExpectedMailboxVersion ||
            preview.CurrentOutgoingVersion != request.ExpectedOutgoingVersion)
            return preview with { CanRetry = false, Status = "SenderRouteChanged" };

        var original = MailboxOutboxStore.Deserialize<IngressEmailEffect>(row.Payload);
        var rebound = original with
        {
            MailboxId = request.ExpectedCurrentMailboxId,
            MailboxConfigurationVersion = request.ExpectedMailboxVersion,
            OutgoingConfigurationVersion = request.ExpectedOutgoingVersion,
            ReplyTo = original.ReplyTo is null ? null : preview.CurrentMailboxAddress,
            SenderBindingError = null
        };
        var payload = JsonSerializer.Serialize(rebound);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == row.Id && x.DeliveryEventId == timelineId && x.Kind == MailboxEffectKind.Email &&
                x.State == row.State && x.Fence == row.Fence && x.Payload == row.Payload &&
                x.LastErrorCode == row.LastErrorCode && x.RecipientOutcomeJson == row.RecipientOutcomeJson)
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
        var timelineChanged = await db.TicketTimelineEvents.Where(x => x.Id == timelineId &&
                x.EventType == TimelineEventType.EmailDelivery && x.EmailStatus == EmailDeliveryStatus.Failed)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.EmailStatus, EmailDeliveryStatus.Pending)
                .SetProperty(x => x.RetryError, (string?)null)
                .SetProperty(x => x.MessageText, "Email queued with the confirmed current mailbox route."), ct);
        if (timelineChanged != 1)
            return preview with { CanRetry = false, Status = "DeliveryChanged" };
        db.ActivityLogs.Add(new ActivityLog
        {
            UserId = userId,
            RelatedEntityId = preview.CurrentMailboxId?.ToString("D"),
            Message = $"Mailbox route retry confirmed. TimelineId={timelineId:D}; OriginalMailboxId={preview.OriginalMailboxId:D}; CurrentMailboxId={preview.CurrentMailboxId:D}; CurrentMailboxVersion={request.ExpectedMailboxVersion}; CurrentOutgoingVersion={request.ExpectedOutgoingVersion}."
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return preview with { Status = "Queued" };
    }

    private async Task<MailboxRouteRetryPreview> PreviewChangedRouteAsync(Guid timelineId,
        MailboxOutboxEffect? row, CancellationToken ct)
    {
        if (row is null || row.Kind != MailboxEffectKind.Email || row.State != MailboxEffectState.NeedsReview ||
            row.LastErrorCode != "SenderRouteChanged" ||
            !await db.TicketTimelineEvents.AsNoTracking().AnyAsync(x => x.Id == timelineId &&
                x.EventType == TimelineEventType.EmailDelivery && x.EmailStatus == EmailDeliveryStatus.Failed, ct))
            return BlockedRoute(timelineId, "DeliveryNotRouteChanged");
        if (row.RecipientOutcomeJson is not null)
        {
            MailboxRecipientOutcome? outcome;
            try { outcome = JsonSerializer.Deserialize<MailboxRecipientOutcome>(row.RecipientOutcomeJson); }
            catch (JsonException) { return BlockedRoute(timelineId, "RecipientOutcomeUnavailable"); }
            if (outcome?.AcceptedRecipients is null || outcome.RejectedRecipients is null)
                return BlockedRoute(timelineId, "RecipientOutcomeUnavailable");
            if (outcome.AcceptedRecipients.Length > 0)
                return BlockedRoute(timelineId, "AcceptedRecipientsRequireReview");
        }

        IngressEmailEffect email;
        try { email = MailboxOutboxStore.Deserialize<IngressEmailEffect>(row.Payload); }
        catch (JsonException) { return BlockedRoute(timelineId, "InvalidDeliveryPayload"); }
        if (email.MailboxId is null || email.MailboxConfigurationVersion is null ||
            string.IsNullOrWhiteSpace(email.TicketId) || string.IsNullOrWhiteSpace(email.OrganizationId) ||
            email.Recipients is null || email.Cc is null || email.Bcc is null || email.Attachments is null)
            return BlockedRoute(timelineId, "SenderBindingMissing");
        var originalMailbox = await db.EmailInboxSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == email.MailboxId, ct);
        if (originalMailbox is null)
            return BlockedRoute(timelineId, "OriginalMailboxMissing");
        if (email.ReplyTo is not null && !EmailAddressGuard.IsSameAddress(email.ReplyTo, originalMailbox.MailboxAddress))
            return BlockedRoute(timelineId, "ReplyToMismatch");
        var recipients = email.Recipients.Concat(email.Cc).Concat(email.Bcc)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (recipients.Length == 0)
            return BlockedRoute(timelineId, "RecipientsMissing");

        var current = await resolver.ResolveAsync(email.TicketId, email.OrganizationId, ct);
        if (current.Status != "Ready" || current.Mailbox is null || current.Outgoing is null)
            return BlockedRoute(timelineId, current.ErrorCode ?? "SenderUnavailable");
        if (current.Mailbox.Id == originalMailbox.Id)
            return BlockedRoute(timelineId, "SenderRouteUnchanged");
        return new(timelineId, true, "RequiresSenderConfirmation", originalMailbox.Id,
            originalMailbox.MailboxAddress, current.Mailbox.Id, current.Mailbox.MailboxAddress,
            current.Outgoing.Transport, recipients, row.Fence, current.Mailbox.Version, current.Outgoing.Version);
    }

    private static MailboxRouteRetryPreview BlockedRoute(Guid timelineId, string status) =>
        new(timelineId, false, status, null, null, null, null, null, [], null, null, null);

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
        if (row.RecipientOutcomeJson is not null)
        {
            MailboxRecipientOutcome? recipients;
            try { recipients = JsonSerializer.Deserialize<MailboxRecipientOutcome>(row.RecipientOutcomeJson); }
            catch (JsonException) { return Blocked(timelineId, "RecipientOutcomeUnavailable"); }
            if (recipients?.AcceptedRecipients is null || recipients.RejectedRecipients is null)
                return Blocked(timelineId, "RecipientOutcomeUnavailable");
            if (recipients.AcceptedRecipients.Length > 0)
                return Blocked(timelineId, "AcceptedRecipientsRequireReview");
        }

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
