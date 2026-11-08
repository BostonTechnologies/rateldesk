using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Email;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Durable, actor-bound selection. Each target and its outcome commit atomically.</summary>
public sealed class PendingEmailOperationService(HelpdeskDbContext db, PendingEmailActions actions, TimeProvider clock)
{
    public const int BatchSize = 50;

    public async Task<PendingEmailOperation> ResolveAsync(PendingEmailSelectionRequest request,
        string actor, CurrentUserAccessProfile profile, CancellationToken ct = default)
    {
        if (request.Action is not ("cancel" or "remove") || request.IncludedIds is null || request.ExcludedIds is null ||
            request.IncludedIds.Length > 10000 || request.ExcludedIds.Length > 10000 ||
            !request.AllMatching && request.IncludedIds.Length == 0)
            throw new ArgumentException("Select deliveries and choose cancel or remove. Retry requires individual review.");
        var excluded = request.ExcludedIds.ToHashSet();
        var included = request.IncludedIds.ToHashSet();
        HashSet<Guid> unresolved = request.AllMatching ? [] : included.Except(excluded).ToHashSet();
        var operation = new PendingEmailOperationRecord
        {
            ActorId = actor, Action = request.Action, Scope = JsonSerializer.Serialize(request.Query),
            CreatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds()
        };
        // Repeatable read freezes both membership and revisions while bounded pages are captured.
        await using var transaction = await db.Database.BeginTransactionAsync(
            db.Database.IsSqlite() ? IsolationLevel.Serializable : IsolationLevel.RepeatableRead, ct);
        db.Add(operation);
        await db.SaveChangesAsync(ct);
        var sequence = 0;
        var browse = new PendingEmailQueryService(db);
        for (var page = 0; ; page++)
        {
            var result = await browse.SearchAsync(request.Query with { Page = page, PageSize = 100 }, ct,
                request.AllMatching ? null : request.IncludedIds);
            foreach (var row in result.Items)
            {
                if (excluded.Contains(row.Id) || !request.AllMatching && !included.Contains(row.Id)) continue;
                var snapshot = await CaptureAsync(row.Id, profile, ct);
                if (snapshot is null) continue;
                unresolved.Remove(row.Id);
                db.Add(new PendingEmailOperationTarget
                {
                    OperationId = operation.Id, DeliveryId = row.Id, Sequence = sequence++, Snapshot = snapshot,
                    Eligible = operation.Action == "cancel" ? row.CanCancel : row.CanRemove
                });
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            if ((page + 1) * 100 >= result.Total) break;
        }
        // Explicitly requested IDs that disappeared, left the filter or lost authority
        // retain a non-actionable receipt. They must not silently vanish from the review.
        foreach (var batch in unresolved.Order().Chunk(100))
        {
            foreach (var id in batch)
                db.Add(new PendingEmailOperationTarget
                {
                    OperationId = operation.Id, DeliveryId = id, Sequence = sequence++, Snapshot = "", Eligible = false
                });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        await transaction.CommitAsync(ct);
        return (await GetAsync(operation.Id, actor, ct))!;
    }

    public async Task<PendingEmailOperation?> GetAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var op = await db.Set<PendingEmailOperationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.ActorId == actor, ct);
        if (op is null) return null;
        var targets = db.Set<PendingEmailOperationTarget>().AsNoTracking().Where(x => x.OperationId == id);
        var groups = await targets.GroupBy(x => new { x.Outcome, x.Eligible })
            .Select(x => new { x.Key.Outcome, x.Key.Eligible, Count = x.Count() }).ToListAsync(ct);
        var results = await targets.Where(x => x.Outcome != null).OrderByDescending(x => x.Sequence).Take(100)
            .Select(x => new PendingEmailActionResult(x.DeliveryId, x.Outcome!, x.Message ?? "")).ToListAsync(ct);
        return new(id, op.Action, op.Scope, groups.Sum(x => x.Count), groups.Where(x => x.Eligible).Sum(x => x.Count),
            groups.Where(x => x.Outcome != null).Sum(x => x.Count),
            groups.Where(x => x.Outcome == "Cancelled" || x.Outcome == "Removed" ||
                x.Outcome == "AlreadyCancelled" || x.Outcome == "AlreadyRemoved").Sum(x => x.Count),
            groups.Where(x => x.Outcome == "ReviewRequired" || x.Outcome == "Unavailable" || x.Outcome == "NotFound").Sum(x => x.Count),
            groups.Where(x => x.Outcome == "Conflict").Sum(x => x.Count),
            groups.Where(x => x.Outcome == "Failed").Sum(x => x.Count), op.Reason is not null, results);
    }

    public async Task<bool> ConfirmAsync(Guid id, string actor, PendingEmailOperationConfirmation request,
        CancellationToken ct = default)
    {
        if (!request.Confirmed || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 200 || request.Reason.Any(char.IsControl))
            throw new ArgumentException("Confirm this fixed selection with a reason of 1–200 characters.");
        var reason = request.Reason.Trim();
        var changed = await db.Set<PendingEmailOperationRecord>().Where(x => x.Id == id && x.ActorId == actor && x.Reason == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Reason, reason), ct);
        return changed == 1 || await db.Set<PendingEmailOperationRecord>()
            .AnyAsync(x => x.Id == id && x.ActorId == actor && x.Reason == reason, ct);
    }

    public async Task<PendingEmailOperation?> ProcessAsync(Guid id, string actor, CurrentUserAccessProfile profile,
        CancellationToken ct = default)
    {
        var op = await db.Set<PendingEmailOperationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.ActorId == actor, ct);
        if (op is null) return null;
        if (op.Reason is null) throw new ArgumentException("Review and confirm the selection before processing.");
        var ids = await db.Set<PendingEmailOperationTarget>().AsNoTracking()
            .Where(x => x.OperationId == id && x.Outcome == null).OrderBy(x => x.Sequence)
            .Take(BatchSize).Select(x => x.DeliveryId).ToListAsync(ct);
        foreach (var deliveryId in ids)
        {
            await ProcessTargetAsync(op, deliveryId, profile, ct);
            db.ChangeTracker.Clear();
        }
        return await GetAsync(id, actor, ct);
    }

    private async Task ProcessTargetAsync(PendingEmailOperationRecord op, Guid deliveryId,
        CurrentUserAccessProfile profile, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The conditional write locks the target until BOTH queue change and receipt commit.
        var claimed = await db.Set<PendingEmailOperationTarget>()
            .Where(x => x.OperationId == op.Id && x.DeliveryId == deliveryId && x.Outcome == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Outcome, "Processing"), ct);
        if (claimed != 1) return;
        // Hold the same queue/timeline rows through snapshot validation and the action CAS.
        // Without these locks a dispatch could advance between validation and ApplyAsync.
        await db.Set<MailboxOutboxEffect>().Where(x => x.DeliveryEventId == deliveryId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Fence, x => x.Fence), ct);
        await db.TicketTimelineEvents.Where(x => x.Id == deliveryId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.EmailStatus, x => x.EmailStatus), ct);
        var target = await db.Set<PendingEmailOperationTarget>().AsNoTracking()
            .SingleAsync(x => x.OperationId == op.Id && x.DeliveryId == deliveryId, ct);
        var current = await CaptureAsync(deliveryId, profile, ct);
        await transaction.CreateSavepointAsync("queue_action", ct);
        var result = current is null ? new PendingEmailActionResult(deliveryId, "Unavailable", "Delivery unavailable in your current authorized scope.") :
            current != target.Snapshot ? new(deliveryId, "Conflict", "Delivery changed or left the matching scope. Review it in a new selection.") :
            !target.Eligible ? new(deliveryId, "ReviewRequired", "This delivery was not eligible when the selection was reviewed.") :
            await actions.ApplyAsync(deliveryId, op.Action, op.ActorId, op.Reason!, ct);
        if (result.Outcome is not ("Cancelled" or "Removed" or "AlreadyCancelled" or "AlreadyRemoved"))
            await transaction.RollbackToSavepointAsync("queue_action", ct);
        await db.Set<PendingEmailOperationTarget>().Where(x => x.OperationId == op.Id && x.DeliveryId == deliveryId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Outcome, result.Outcome).SetProperty(x => x.Message, result.Message), ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<string?> CaptureAsync(Guid id, CurrentUserAccessProfile profile, CancellationToken ct)
    {
        var timeline = await db.TicketTimelineEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.EventType == TimelineEventType.EmailDelivery, ct);
        if (timeline is null) return null;
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == timeline.TicketId, ct);
        var allowed = ticket switch
        {
            Incident => profile.CanManageIncident(ticket.OrganizationId),
            Request => profile.CanManageRequest(ticket.OrganizationId),
            Change => profile.CanManageChange(ticket.OrganizationId),
            _ => false
        };
        if (!allowed) return null;
        var effect = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleOrDefaultAsync(x => x.DeliveryEventId == id, ct);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            timeline.TicketId, ticket!.OrganizationId, timeline.EmailStatus, timeline.RetryCount, timeline.RetryError,
            timeline.EmailRecipient, EffectId = effect?.Id, effect?.State, effect?.Fence, effect?.Payload,
            effect?.LastErrorCode, effect?.RecipientOutcomeJson, effect?.Owner
        }));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
