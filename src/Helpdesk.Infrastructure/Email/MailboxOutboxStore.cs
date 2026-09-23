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
        return await db.Set<MailboxOutboxEffect>().AsNoTracking()
            .Where(x => (x.State == MailboxEffectState.Pending && x.AvailableUnixMilliseconds <= now)
                || (x.State == MailboxEffectState.InFlight && x.LeaseExpiresUnixMilliseconds <= now))
            .OrderBy(x => x.AvailableUnixMilliseconds).ThenBy(x => x.EffectKey)
            .Take(Math.Clamp(count, 1, 64)).Select(x => x.Id).ToListAsync(ct);
    }

    public async Task<MailboxOutboxEffect?> TryClaimAsync(Guid id, string owner, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null || row.State is MailboxEffectState.Completed or MailboxEffectState.Exhausted or MailboxEffectState.NeedsReview)
            return null;
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
        bool requiresReview)
    {
        var now = timeProvider.GetUtcNow();
        var milliseconds = now.ToUnixTimeMilliseconds();
        var state = succeeded ? MailboxEffectState.Completed
            : requiresReview ? MailboxEffectState.NeedsReview
            : claim.Attempts >= MaximumAttempts ? MailboxEffectState.Exhausted : MailboxEffectState.Pending;
        var nextAttempt = milliseconds + (long)TimeSpan.FromSeconds(30 * Math.Pow(2, claim.Attempts - 1)).TotalMilliseconds;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == claim.Id && x.Owner == claim.Owner && x.Fence == claim.Fence
                && x.State == MailboxEffectState.InFlight && x.LeaseExpiresUnixMilliseconds > milliseconds)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, state)
                .SetProperty(x => x.Owner, (string?)null).SetProperty(x => x.LeaseExpiresUnixMilliseconds, 0L)
                .SetProperty(x => x.AvailableUnixMilliseconds, nextAttempt)
                .SetProperty(x => x.LastErrorCode, errorCode), ct);
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
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Set<MailboxOutboxEffect>()
            .Where(x => x.Id == row.Id && (x.State == MailboxEffectState.Exhausted || x.State == MailboxEffectState.NeedsReview) && x.Fence == row.Fence)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, MailboxEffectState.Pending)
                .SetProperty(x => x.Attempts, 0).SetProperty(x => x.Fence, x => x.Fence + 1)
                .SetProperty(x => x.Owner, (string?)null).SetProperty(x => x.LeaseExpiresUnixMilliseconds, 0L)
                .SetProperty(x => x.AvailableUnixMilliseconds, now).SetProperty(x => x.LastErrorCode, (string?)null), ct);
        if (changed == 1)
        {
            await db.TicketTimelineEvents.Where(x => x.Id == timelineId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.EmailStatus, EmailDeliveryStatus.Pending)
                    .SetProperty(x => x.RetryError, (string?)null), ct);
        }
        await transaction.CommitAsync(ct);
        // A matching outbox always owns delivery, including when another retry already claimed it.
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
                    : state == MailboxEffectState.NeedsReview ? "Email delivery needs sender review."
                    : state == MailboxEffectState.Exhausted
                    ? "Email delivery requires retry." : "Email queued for another delivery attempt.";
                AddTimelineEffect(row.ReceiptId, $"{row.EffectKey}:result:{row.Fence}", delivery, now.ToUnixTimeMilliseconds());
            }
        }
    }

    private void AddTimelineEffect(Guid receiptId, string key, TicketTimelineEvent delivery, long now) =>
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
}
