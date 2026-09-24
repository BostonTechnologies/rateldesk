using System.Text.Json;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

public sealed class MailboxOutboxStore(HelpdeskDbContext db, IIngressEffectContext context, TimeProvider timeProvider)
{
    public const int MaximumAttempts = 5;
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(5);

    public async Task<MailboxOutboxEffect> QueueDirectAsync(IngressEmailEffect email, CancellationToken ct)
    {
        if (context.IsActive) throw new InvalidOperationException("Ingress mail must use its receipt transaction.");
        var now = timeProvider.GetUtcNow();
        var row = new MailboxOutboxEffect
        {
            Kind = MailboxEffectKind.Email, EffectKey = $"direct:{Guid.NewGuid():N}",
            AvailableUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            DeliveryEventId = email.TimelineDeliveryId
        };
        row.DispatchGroup = EmailDispatchGroup(email.MailboxId, row.Id);
        if (!email.SuppressTimeline && !string.IsNullOrWhiteSpace(email.TicketId))
        {
            var delivery = new TicketTimelineEvent
            {
                TicketId = email.TicketId, EventType = TimelineEventType.EmailDelivery,
                CreatedUtc = now, CreatedByUserId = "system", CreatedByUserName = "System",
                EmailStatus = EmailDeliveryStatus.Pending, EmailRecipient = string.Join(",", email.Recipients),
                MessageText = "Email queued for delivery."
            };
            db.TicketTimelineEvents.Add(delivery);
            row.DeliveryEventId = delivery.Id;
            email = email with { TimelineDeliveryId = delivery.Id, SuppressTimeline = true };
        }
        row.Payload = JsonSerializer.Serialize(email);
        if (row.Payload.Length > 24 * 1024 * 1024)
            throw new InvalidOperationException("Email delivery payload exceeds the durable queue limit.");
        db.Set<MailboxOutboxEffect>().Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        context.Validate();
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Ingress effects must be flushed in the receipt business transaction.");
        var now = timeProvider.GetUtcNow();
        foreach (var effect in context.Effects)
        {
            var row = new MailboxOutboxEffect
            {
                ReceiptId = context.ReceiptId, EffectKey = effect.Key, Kind = effect.Kind,
                Payload = effect.Payload, AvailableUnixMilliseconds = now.ToUnixTimeMilliseconds()
            };
            if (effect.Kind == MailboxEffectKind.Email)
            {
                var email = Deserialize<IngressEmailEffect>(effect.Payload);
                row.DispatchGroup = EmailDispatchGroup(email.MailboxId, row.Id);
                if (email.TimelineDeliveryId is null && !email.SuppressTimeline && !string.IsNullOrWhiteSpace(email.TicketId))
                {
                    var delivery = new TicketTimelineEvent
                    {
                        TicketId = email.TicketId, EventType = TimelineEventType.EmailDelivery,
                        CreatedUtc = now, CreatedByUserId = "system", CreatedByUserName = "System",
                        EmailStatus = EmailDeliveryStatus.Pending, EmailRecipient = string.Join(",", email.Recipients),
                        MessageText = "Email queued for delivery."
                    };
                    db.TicketTimelineEvents.Add(delivery);
                    email = email with { TimelineDeliveryId = delivery.Id, SuppressTimeline = true };
                    AddTimelineEffect(row.ReceiptId, row.EffectKey + ":pending", delivery, now.ToUnixTimeMilliseconds());
                }
                row.DeliveryEventId = email.TimelineDeliveryId;
                row.Payload = JsonSerializer.Serialize(email);
            }
            db.Set<MailboxOutboxEffect>().Add(row);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetCandidatesAsync(int count, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var limit = Math.Clamp(count, 1, 64);
        var due = db.Set<MailboxOutboxEffect>().AsNoTracking()
            .Where(x => (x.State == MailboxEffectState.Pending && x.AvailableUnixMilliseconds <= now)
                || (x.State == MailboxEffectState.InFlight && x.LeaseExpiresUnixMilliseconds <= now));
        var other = await due.Where(x => x.Kind != MailboxEffectKind.Email)
            .OrderBy(x => x.AvailableUnixMilliseconds).ThenBy(x => x.EffectKey)
            .Take(Math.Min(4, limit)).Select(x => x.Id).ToListAsync(ct);
        var capacity = limit - other.Count;
        var emailDue = due.Where(x => x.Kind == MailboxEffectKind.Email);
        var emailHeads = capacity == 0 ? [] : await emailDue
            .Where(x => x.Id == emailDue.Where(candidate => candidate.DispatchGroup == x.DispatchGroup)
                .OrderBy(candidate => candidate.AvailableUnixMilliseconds)
                .ThenBy(candidate => candidate.EffectKey).Select(candidate => candidate.Id).FirstOrDefault())
            .OrderBy(x => x.AvailableUnixMilliseconds).ThenBy(x => x.EffectKey)
            .Take(capacity).Select(x => x.Id).ToListAsync(ct);
        var selected = other.Concat(emailHeads).ToList();
        if (selected.Count < limit)
        {
            var additional = await due.Where(x => !selected.Contains(x.Id))
                .OrderBy(x => x.AvailableUnixMilliseconds).ThenBy(x => x.EffectKey)
                .Take(limit - selected.Count).Select(x => x.Id).ToListAsync(ct);
            selected.AddRange(additional);
        }
        return selected;
    }

    public async Task<MailboxOutboxEffect?> TryClaimAsync(Guid id, string owner, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null || row.State is MailboxEffectState.Completed or MailboxEffectState.Exhausted or MailboxEffectState.NeedsReview)
            return null;
        if (row.Kind == MailboxEffectKind.Email && row.State == MailboxEffectState.InFlight &&
            row.LeaseExpiresUnixMilliseconds <= now)
        {
            await HoldExpiredEmailAsync(row, now, ct);
            return null;
        }
        if (row.Attempts >= MaximumAttempts)
        {
            await ExhaustExpiredAsync(row, now, ct);
            return null;
        }
        var nextFence = checked(row.Fence + 1);
        var expires = checked(now + (long)ClaimDuration.TotalMilliseconds);
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == id && x.Fence == row.Fence && x.Attempts < MaximumAttempts
                && ((x.State == MailboxEffectState.Pending && x.AvailableUnixMilliseconds <= now)
                    || (x.State == MailboxEffectState.InFlight && x.LeaseExpiresUnixMilliseconds <= now)))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Owner, owner)
                .SetProperty(x => x.Fence, nextFence).SetProperty(x => x.State, MailboxEffectState.InFlight)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                .SetProperty(x => x.LeaseExpiresUnixMilliseconds, expires), ct);
        if (changed != 1)
            return null;
        row.Owner = owner;
        row.Fence = nextFence;
        row.State = MailboxEffectState.InFlight;
        row.Attempts++;
        row.LeaseExpiresUnixMilliseconds = expires;
        return row;
    }

    public Task<bool> CompleteAsync(MailboxOutboxEffect claim, bool succeeded, string? errorCode, CancellationToken ct) =>
        CompleteAsync(claim, succeeded, errorCode, ct, false);

    public async Task<bool> CompleteAsync(MailboxOutboxEffect claim, bool succeeded, string? errorCode, CancellationToken ct,
        bool requiresReview, MailboxSubmissionResult? submission = null)
    {
        var now = timeProvider.GetUtcNow();
        var milliseconds = now.ToUnixTimeMilliseconds();
        var state = succeeded ? MailboxEffectState.Completed
            : requiresReview ? MailboxEffectState.NeedsReview
            : claim.Attempts >= MaximumAttempts ? MailboxEffectState.Exhausted : MailboxEffectState.Pending;
        var nextAttempt = milliseconds + (long)TimeSpan.FromSeconds(30 * Math.Pow(2, claim.Attempts - 1)).TotalMilliseconds;
        var recipientOutcomeJson = submission is { AcceptedRecipients: not null } or { RejectedRecipients: not null }
            ? JsonSerializer.Serialize(new MailboxRecipientOutcome(
                submission.AcceptedRecipients?.ToArray() ?? [], submission.RejectedRecipients?.ToArray() ?? []))
            : null;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == claim.Id && x.Owner == claim.Owner && x.Fence == claim.Fence
                && x.State == MailboxEffectState.InFlight && x.LeaseExpiresUnixMilliseconds > milliseconds)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, state)
                .SetProperty(x => x.Owner, (string?)null).SetProperty(x => x.LeaseExpiresUnixMilliseconds, 0L)
                .SetProperty(x => x.AvailableUnixMilliseconds, nextAttempt)
                .SetProperty(x => x.LastErrorCode, errorCode)
                .SetProperty(x => x.RecipientOutcomeJson, recipientOutcomeJson), ct);
        if (changed != 1)
            return false;
        await UpdateDeliveryAsync(claim, succeeded, state, errorCode, now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RetryForTimelineAsync(Guid timelineId, CancellationToken ct)
    {
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleOrDefaultAsync(x => x.DeliveryEventId == timelineId, ct);
        if (row is null)
            return false;
        if (row.Kind != MailboxEffectKind.Email)
            throw new InvalidOperationException("This delivery has no email outbox owner.");
        if (row.State is not (MailboxEffectState.Exhausted or MailboxEffectState.NeedsReview))
            throw new InvalidOperationException("This delivery is no longer available for retry.");
        if (row.RecipientOutcomeJson is not null)
        {
            MailboxRecipientOutcome recipients;
            try
            {
                recipients = JsonSerializer.Deserialize<MailboxRecipientOutcome>(row.RecipientOutcomeJson)
                    ?? throw new JsonException("Missing recipient outcome.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Recipient outcomes require review before retry.");
            }
            if (recipients.AcceptedRecipients is null || recipients.RejectedRecipients is null)
                throw new InvalidOperationException("Recipient outcomes require review before retry.");
            if (recipients.AcceptedRecipients.Length > 0)
                throw new InvalidOperationException("Accepted recipients require individual review before retry.");
        }
        if (row.State == MailboxEffectState.NeedsReview &&
            row.LastErrorCode is not ("OutgoingNotConfigured" or "OutgoingDisabled" or
                "SmtpCredentialMissing" or "GraphCredentialMissing" or "ReplyToMismatch"))
            throw new InvalidOperationException("This delivery requires sender or recipient review before retry.");
        if (row.LastErrorCode is "DispatchOutcomeUnknown" or "SubmissionOutcomeUnknown" or
            "SmtpPartialRecipientAcceptance")
            throw new InvalidOperationException("The previous delivery outcome is uncertain; check the recipient before retrying.");
        IngressEmailEffect email;
        try { email = Deserialize<IngressEmailEffect>(row.Payload); }
        catch (JsonException) { throw new InvalidOperationException("The delivery payload requires review before retry."); }
        if (email.SupportDeliveryId is { } supportId &&
            !await db.SupportNotificationDeliveries.AsNoTracking().AnyAsync(x => x.Id == supportId &&
                x.Status == SupportNotificationDeliveryStatus.Failed, ct))
            throw new InvalidOperationException("The support notification is no longer failed.");
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == row.Id && x.DeliveryEventId == timelineId && x.Kind == MailboxEffectKind.Email &&
                x.State == row.State && x.Fence == row.Fence && x.Payload == row.Payload &&
                x.LastErrorCode == row.LastErrorCode && x.RecipientOutcomeJson == row.RecipientOutcomeJson)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, MailboxEffectState.Pending)
                .SetProperty(x => x.Attempts, 0).SetProperty(x => x.Fence, x => x.Fence + 1)
                .SetProperty(x => x.Owner, (string?)null).SetProperty(x => x.LeaseExpiresUnixMilliseconds, 0L)
                .SetProperty(x => x.AvailableUnixMilliseconds, now).SetProperty(x => x.LastErrorCode, (string?)null)
                .SetProperty(x => x.RecipientOutcomeJson, (string?)null), ct);
        if (changed != 1)
            throw new InvalidOperationException("Delivery changed during retry; refresh before trying again.");
        var timelineChanged = await db.TicketTimelineEvents.Where(x => x.Id == timelineId &&
                x.EventType == TimelineEventType.EmailDelivery && x.EmailStatus == EmailDeliveryStatus.Failed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.EmailStatus, EmailDeliveryStatus.Pending)
                .SetProperty(x => x.RetryError, (string?)null), ct);
        if (timelineChanged != 1)
            throw new InvalidOperationException("Delivery timeline changed during retry; refresh before trying again.");
        if (email.SupportDeliveryId is { } deliveryId)
        {
            var supportChanged = await db.SupportNotificationDeliveries
                .Where(x => x.Id == deliveryId && x.Status == SupportNotificationDeliveryStatus.Failed)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, SupportNotificationDeliveryStatus.Pending)
                    .SetProperty(x => x.FailureReason, (string?)null)
                    .SetProperty(x => x.FailedUtc, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedUtc, timeProvider.GetUtcNow()), ct);
            if (supportChanged != 1)
                throw new InvalidOperationException("Support notification changed during retry; refresh before trying again.");
        }
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task ExhaustExpiredAsync(MailboxOutboxEffect row, long now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == row.Id && x.Fence == row.Fence && x.Attempts >= MaximumAttempts
                && ((x.State == MailboxEffectState.InFlight && x.LeaseExpiresUnixMilliseconds <= now)
                    || x.State == MailboxEffectState.Pending))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, MailboxEffectState.Exhausted)
                .SetProperty(x => x.Owner, (string?)null).SetProperty(x => x.LastErrorCode, "DispatchOutcomeUnknown"), ct);
        if (changed == 1)
        {
            await UpdateDeliveryAsync(row, false, MailboxEffectState.Exhausted, "DispatchOutcomeUnknown",
                DateTimeOffset.FromUnixTimeMilliseconds(now), ct);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task HoldExpiredEmailAsync(MailboxOutboxEffect row, long now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == row.Id && x.Kind == MailboxEffectKind.Email &&
                x.State == MailboxEffectState.InFlight && x.Fence == row.Fence && x.Owner == row.Owner &&
                x.LeaseExpiresUnixMilliseconds <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, MailboxEffectState.NeedsReview)
                .SetProperty(x => x.Owner, (string?)null)
                .SetProperty(x => x.LeaseExpiresUnixMilliseconds, 0L)
                .SetProperty(x => x.LastErrorCode, "DispatchOutcomeUnknown"), ct);
        if (changed == 1)
        {
            await UpdateDeliveryAsync(row, false, MailboxEffectState.NeedsReview, "DispatchOutcomeUnknown",
                DateTimeOffset.FromUnixTimeMilliseconds(now), ct);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task UpdateDeliveryAsync(MailboxOutboxEffect row, bool succeeded, MailboxEffectState state,
        string? errorCode, DateTimeOffset now, CancellationToken ct)
    {
        if (row.Kind != MailboxEffectKind.Email)
            return;
        IngressEmailEffect? email;
        try
        {
            email = JsonSerializer.Deserialize<IngressEmailEffect>(row.Payload);
        }
        catch (JsonException)
        {
            // A corrupt payload must still reach Exhausted; its durable timeline ID
            // remains usable even when optional support metadata cannot be decoded.
            email = null;
        }
        if (email?.SupportDeliveryId is { } supportId)
        {
            var delivery = await db.SupportNotificationDeliveries.SingleOrDefaultAsync(x => x.Id == supportId, ct);
            if (delivery is not null)
            {
                delivery.Status = errorCode == "Suppressed" ? SupportNotificationDeliveryStatus.Skipped
                    : succeeded ? SupportNotificationDeliveryStatus.Sent
                    : state is MailboxEffectState.Exhausted or MailboxEffectState.NeedsReview
                        ? SupportNotificationDeliveryStatus.Failed : SupportNotificationDeliveryStatus.Pending;
                delivery.AttemptedUtc = now;
                delivery.UpdatedUtc = now;
                delivery.SentUtc = succeeded ? now : null;
                delivery.FailedUtc = state is MailboxEffectState.Exhausted or MailboxEffectState.NeedsReview ? now : null;
                delivery.FailureReason = errorCode;
            }
        }
        if (row.DeliveryEventId is { } timelineId)
        {
            var delivery = await db.TicketTimelineEvents.SingleOrDefaultAsync(x => x.Id == timelineId, ct);
            if (delivery is not null)
            {
                delivery.EmailStatus = errorCode == "Suppressed" ? EmailDeliveryStatus.Suppressed
                    : succeeded ? EmailDeliveryStatus.Delivered
                    : state is MailboxEffectState.Exhausted or MailboxEffectState.NeedsReview
                        ? EmailDeliveryStatus.Failed : EmailDeliveryStatus.Pending;
                delivery.RetryCount = Math.Max(0, row.Attempts - 1);
                delivery.LastRetryUtc = now;
                delivery.RetryError = errorCode;
                delivery.MessageText = errorCode == "Suppressed" ? "Automatic email suppressed to prevent a mailbox loop."
                    : succeeded ? "Email accepted by provider."
                    : errorCode is "DispatchOutcomeUnknown" or "SubmissionOutcomeUnknown"
                        ? "Email submission outcome is unknown; verify with recipients before taking action."
                    : state == MailboxEffectState.NeedsReview ? "Email delivery needs sender review."
                    : state == MailboxEffectState.Exhausted
                    ? "Email delivery requires retry." : "Email queued for another delivery attempt.";
                AddTimelineEffect(row.ReceiptId, $"{row.EffectKey}:result:{row.Fence}", delivery, now.ToUnixTimeMilliseconds());
            }
        }
    }

    private void AddTimelineEffect(Guid? receiptId, string key, TicketTimelineEvent delivery, long now) =>
        db.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect
        {
            ReceiptId = receiptId, EffectKey = key, Kind = MailboxEffectKind.Timeline,
            AvailableUnixMilliseconds = now, Payload = JsonSerializer.Serialize(ToDto(delivery))
        });

    internal static TicketTimelineEventDto ToDto(TicketTimelineEvent delivery) => new()
    {
        Id = delivery.Id, TicketId = delivery.TicketId, CreatedUtc = delivery.CreatedUtc,
        CreatedByUserId = delivery.CreatedByUserId, CreatedByUserName = delivery.CreatedByUserName,
        EventType = delivery.EventType, MessageHtml = delivery.MessageHtml, MessageText = delivery.MessageText,
        EmailStatus = delivery.EmailStatus, EmailRecipient = delivery.EmailRecipient,
        RetryCount = delivery.RetryCount, IsRetryable = delivery.IsRetryable
    };

    internal static T Deserialize<T>(string payload) => JsonSerializer.Deserialize<T>(payload)
        ?? throw new InvalidOperationException("Invalid durable ingress effect payload.");

    private static string EmailDispatchGroup(Guid? mailboxId, Guid effectId) =>
        mailboxId is { } id ? $"mailbox:{id:N}" : $"legacy:{effectId:N}";
}
