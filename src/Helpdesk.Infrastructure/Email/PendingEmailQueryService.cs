using System.Text.Json;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Email;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

/// <summary>All predicates, counts and ordering run in the database before the bounded page is enriched.</summary>
public sealed class PendingEmailQueryService(HelpdeskDbContext db)
{
    public const int MaximumPageSize = 100;

    public async Task<PendingEmailPage> SearchAsync(PendingEmailQuery input, CancellationToken ct)
    {
        if (input.Page < 0 || input.Page > 1000000 || input.PageSize is not (25 or 50 or 100))
            throw new ArgumentException("Page must be non-negative; page size must be 25, 50 or 100.");
        if (input.From > input.To) throw new ArgumentException("Created From must be before or equal to To.");
        if (input.Status is not null && !Enum.IsDefined(input.Status.Value)) throw new ArgumentException("Invalid status.");
        if (new[] { input.Search, input.OrganizationId, input.Sender, input.Recipient, input.Reason }.Any(x => x?.Length > 200))
            throw new ArgumentException("Filters must be at most 200 characters.");
        var query = from delivery in db.TicketTimelineEvents.AsNoTracking()
                    join ticket in db.Tickets.AsNoTracking() on delivery.TicketId equals ticket.Id
                    join effect in db.Set<MailboxOutboxEffect>().AsNoTracking() on delivery.Id equals effect.DeliveryEventId into effects
                    from effect in effects.DefaultIfEmpty()
                    join organization in db.Organizations.AsNoTracking() on ticket.OrganizationId equals organization.Id into organizations
                    from organization in organizations.DefaultIfEmpty()
                    join mailbox in db.EmailInboxSettings.AsNoTracking() on effect.DispatchGroup equals "mailbox:" + mailbox.Id.ToString().Replace("-", "") into mailboxes
                    from mailbox in mailboxes.DefaultIfEmpty()
                    where delivery.EventType == TimelineEventType.EmailDelivery &&
                        delivery.EmailStatus != EmailDeliveryStatus.Removed &&
                        (effect == null || effect.State != MailboxEffectState.Removed)
                    select new { Delivery = delivery, Ticket = ticket, Effect = effect, Organization = organization, Mailbox = mailbox };
        if (input.Status is { } status) query = query.Where(x => x.Delivery.EmailStatus == status);
        else query = query.Where(x => x.Delivery.EmailStatus == EmailDeliveryStatus.Pending || x.Delivery.EmailStatus == EmailDeliveryStatus.Failed);
        if (input.Search?.Trim().ToLowerInvariant() is { Length: > 0 } search)
            query = query.Where(x => x.Ticket.TrackingId.ToLower().Contains(search) || x.Ticket.Title.ToLower().Contains(search) ||
                x.Delivery.EmailRecipient != null && x.Delivery.EmailRecipient.ToLower().Contains(search));
        if (!string.IsNullOrWhiteSpace(input.OrganizationId)) query = query.Where(x => x.Ticket.OrganizationId == input.OrganizationId);
        if (input.Sender?.Trim().ToLowerInvariant() is { Length: > 0 } sender)
            query = query.Where(x => x.Mailbox != null && x.Mailbox.MailboxAddress.ToLower().Contains(sender));
        if (input.Recipient?.Trim().ToLowerInvariant() is { Length: > 0 } recipient)
            query = query.Where(x => x.Delivery.EmailRecipient != null && x.Delivery.EmailRecipient.ToLower().Contains(recipient));
        if (input.Reason?.Trim().ToLowerInvariant() is { Length: > 0 } reason)
            query = query.Where(x => x.Effect != null && x.Effect.LastErrorCode != null && x.Effect.LastErrorCode.ToLower().Contains(reason) ||
                x.Delivery.RetryError != null && x.Delivery.RetryError.ToLower().Contains(reason));
        var sqlite = db.Database.IsSqlite();
        if (input.From is { } from)
            query = sqlite ? query.Where(x => EF.Property<long>(x.Delivery, "CreatedUtcSortTicks") >= from.UtcTicks)
                : query.Where(x => x.Delivery.CreatedUtc >= from.ToUniversalTime());
        if (input.To is { } to)
            query = sqlite ? query.Where(x => EF.Property<long>(x.Delivery, "CreatedUtcSortTicks") <= to.UtcTicks)
                : query.Where(x => x.Delivery.CreatedUtc <= to.ToUniversalTime());
        var total = await query.CountAsync(ct);
        var page = total == 0 ? 0 : Math.Min(input.Page, (total - 1) / input.PageSize);
        var ordered = input.Sort switch
        {
            "ticket" => input.Descending ? query.OrderByDescending(x => x.Ticket.TrackingId) : query.OrderBy(x => x.Ticket.TrackingId),
            "recipient" => input.Descending ? query.OrderByDescending(x => x.Delivery.EmailRecipient) : query.OrderBy(x => x.Delivery.EmailRecipient),
            "status" => input.Descending ? query.OrderByDescending(x => x.Delivery.EmailStatus) : query.OrderBy(x => x.Delivery.EmailStatus),
            "created" when sqlite => input.Descending ? query.OrderByDescending(x => EF.Property<long>(x.Delivery, "CreatedUtcSortTicks"))
                : query.OrderBy(x => EF.Property<long>(x.Delivery, "CreatedUtcSortTicks")),
            "created" => input.Descending ? query.OrderByDescending(x => x.Delivery.CreatedUtc) : query.OrderBy(x => x.Delivery.CreatedUtc),
            _ => throw new ArgumentException("Sort must be created, ticket, recipient or status.")
        };
        var rows = await ordered.ThenBy(x => x.Delivery.Id).Skip(page * input.PageSize).Take(input.PageSize).ToListAsync(ct);
        var supportByDelivery = new Dictionary<Guid, string>();
        var items = rows.Select(x =>
        {
            var row = new PendingEmailRow
            {
                Id = x.Delivery.Id, TicketId = x.Ticket.Id, TrackingId = x.Ticket.TrackingId, Subject = x.Ticket.Title,
                TicketType = x.Ticket is Incident ? "incidents" : x.Ticket is Request ? "requests" : "changes",
                OrganizationId = x.Ticket.OrganizationId, OrganizationName = x.Organization?.Name,
                CreatedUtc = x.Delivery.CreatedUtc, EmailStatus = x.Delivery.EmailStatus, EmailRecipient = x.Delivery.EmailRecipient,
                RetryCount = x.Delivery.RetryCount, MessageText = x.Delivery.MessageText,
                DeliveryErrorCode = x.Effect?.LastErrorCode ?? x.Delivery.RetryError,
                Sender = x.Mailbox?.MailboxAddress, QueueState = x.Effect?.State.ToString() ?? "Legacy",
                IsRetryable = x.Delivery.IsRetryable && (x.Effect is null || x.Effect.State is MailboxEffectState.Exhausted or MailboxEffectState.NeedsReview),
                CanCancel = PendingEmailActions.CanCancel(x.Effect, x.Delivery.EmailStatus, x.Delivery.RetryError) && x.Delivery.EmailStatus != EmailDeliveryStatus.Cancelled,
                CanRemove = x.Effect is null ? x.Delivery.EmailStatus != EmailDeliveryStatus.Pending :
                    x.Effect.State != MailboxEffectState.Submitting && (x.Effect.State != MailboxEffectState.InFlight ||
                    x.Effect.Owner?.StartsWith("pre-submit:", StringComparison.Ordinal) == true)
            };
            if (x.Effect is not null)
            {
                try
                {
                    var email = JsonSerializer.Deserialize<IngressEmailEffect>(x.Effect.Payload)
                        ?? throw new JsonException();
                    if (email.SupportDeliveryId is { } supportId) supportByDelivery[row.Id] = supportId;
                }
                catch (JsonException) { row.IsRetryable = false; row.DeliveryReviewReason = "Delivery payload requires review"; }
            }
            if (x.Effect?.RecipientOutcomeJson is { } json)
            {
                try
                {
                    var outcome = JsonSerializer.Deserialize<MailboxRecipientOutcome>(json);
                    row.AcceptedRecipients = outcome?.AcceptedRecipients;
                    row.RejectedRecipients = outcome?.RejectedRecipients;
                    row.RecipientOutcomeUnavailable = outcome?.AcceptedRecipients is null || outcome.RejectedRecipients is null;
                    if (row.RecipientOutcomeUnavailable || row.AcceptedRecipients?.Length > 0) row.IsRetryable = false;
                }
                catch (JsonException) { row.RecipientOutcomeUnavailable = true; row.IsRetryable = false; }
            }
            return row;
        }).ToArray();
        var supportIds = supportByDelivery.Values.Distinct().ToArray();
        if (supportIds.Length > 0)
        {
            var states = await db.SupportNotificationDeliveries.AsNoTracking().Where(x => supportIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Status }).ToDictionaryAsync(x => x.Id, x => x.Status, ct);
            foreach (var item in items)
            {
                if (!supportByDelivery.TryGetValue(item.Id, out var supportId)) continue;
                if (states.TryGetValue(supportId, out var state) && state == SupportNotificationDeliveryStatus.Failed) continue;
                item.IsRetryable = false;
                item.DeliveryReviewReason = state == SupportNotificationDeliveryStatus.Sent
                    ? "Support notification already sent" : "Support notification is no longer failed";
            }
        }
        return new(items, total, page, input.PageSize);
    }
}
