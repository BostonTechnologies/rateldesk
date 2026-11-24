using System.Data;
using System.Security.Claims;
using Helpdesk.Application.Changes;
using Helpdesk.Application.Events;
using Helpdesk.Application.Services.Changes;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Sla;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Changes;

public static partial class ChangeEndpoints
{
    private static string? LifecycleActor(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");

    private static bool MatchesLifecycleSelection(Change change, ChangeLifecycleState? expected, DateTime? updatedAt) =>
        (!expected.HasValue || expected.Value == EffectiveLifecycleState(change)) &&
        (!updatedAt.HasValue || updatedAt.Value.Ticks / 10 == change.UpdatedAt?.Ticks / 10);

    private static IResult LifecycleConflict() => Results.Conflict(new
    {
        code = "change_lifecycle_changed",
        message = "The change was updated. Reload its current state before trying again."
    });

    private static IResult? ValidateLifecycleComment(string? comment) =>
        comment?.Length > ChangeLifecycleRequest.MaxCommentLength
            ? Results.BadRequest($"The explanation must be {ChangeLifecycleRequest.MaxCommentLength} characters or fewer.")
            : null;

    private static bool HasChangeClosureEdits(Change change, UpdateChangeDto update) =>
        update.Priority is { } priority && priority != change.Priority ||
        update.OrganizationId is not null || update.RequestedForUserId is not null ||
        update.ImplementorUserId is not null || update.ApproverUserIds is not null ||
        update.ImplementationStartAt is not null || update.ImplementationEndAt is not null ||
        update.CategoryIds is not null || update.ChangeType is not null || update.ChangeTemplate is not null;

    private static ChangeDto ReadLifecycleResult(Change change, IChangeTemplateService templates, bool changed)
    {
        var lifecycle = EffectiveLifecycleState(change);
        var template = lifecycle == ChangeLifecycleState.Cancelled
            ? null : templates.DeserializeTemplate(change.ChangeTemplateJson);
        var result = ToQuickLifecycleDto(change, lifecycle, template,
            lifecycle == ChangeLifecycleState.Cancelled ? null : templates.ValidateTemplate(change.ChangeType, template));
        result.LifecycleChanged = changed;
        return result;
    }

    private static void RecordLifecycleAudit(HelpdeskDbContext db, Change change, ChangeLifecycleState previous,
        ChangeLifecycleState destination, string actorId, string? actorName, string? comment, DateTimeOffset now)
    {
        var message = $"Change lifecycle moved from {ChangeLifecycle.Label(previous)} to {ChangeLifecycle.Label(destination)}.";
        if (!string.IsNullOrEmpty(comment))
            message += $" Explanation: {comment}";
        var entry = new TicketTimelineEvent
        {
            TicketId = change.Id,
            CreatedUtc = now,
            CreatedByUserId = actorId,
            CreatedByUserName = string.IsNullOrWhiteSpace(actorName) ? actorId : actorName,
            EventType = string.IsNullOrEmpty(comment) ? TimelineEventType.StatusChange : TimelineEventType.InternalNote,
            MessageText = message
        };
        QueueChangeTimeline(db, entry);
        db.ActivityLogs.Add(new ActivityLog
        {
            TicketId = change.Id, UserId = actorId, Timestamp = now.UtcDateTime, Message = message
        });
    }

    // Callers hold the change decision fence inside the same transaction.
    private static async Task<IResult?> ApplyChangeClosureAsync(
        HelpdeskDbContext db, Change change, ChangeLifecycleState destination, string actorId, string? actorName,
        string? comment, IChangeTemplateService templates, ITicketSlaCompletionService sla,
        ITicketNotificationService notifications, ILoggerFactory loggerFactory, CancellationToken token)
    {
        var previous = EffectiveLifecycleState(change);
        var transitionError = ChangeLifecycle.ValidateTransition(previous, destination);
        if (transitionError is not null) return Results.BadRequest(transitionError);
        if (ChangeLifecycle.RequiresComment(previous, destination) && string.IsNullOrEmpty(comment))
            return Results.BadRequest("An explanation is required for this lifecycle action.");
        if (ChangeLifecycle.IsImplemented(destination))
        {
            var validation = templates.ValidateTemplate(change.ChangeType, templates.DeserializeTemplate(change.ChangeTemplateJson));
            if (!validation.IsComplete)
                return Results.BadRequest(new { message = "Change template must be complete before implementing the change.", errors = validation.Errors });
        }

        var now = DateTimeOffset.UtcNow;
        change.LifecycleState = destination;
        change.State = TicketState.Resolved;
        change.UpdatedAt = now.UtcDateTime;
        change.ClosedAt = now;
        change.AdministrativeClosureReason = string.IsNullOrEmpty(comment) ? null : comment;
        RecordLifecycleAudit(db, change, previous, destination, actorId, actorName, comment, now);
        await db.SaveChangesAsync(token);
        await sla.HandleTicketClosedAsync(change.Id, actorId, now);

        if (destination != ChangeLifecycleState.Cancelled || previous != ChangeLifecycleState.Draft)
        {
            var kind = destination == ChangeLifecycleState.Cancelled ? "cancelled" : "implemented";
            if (!await SendChangeLifecycleNotificationsAsync(notifications, change, kind, loggerFactory, db, token))
                throw new InvalidOperationException("The change lifecycle notifications could not be queued.");
        }
        await db.SaveChangesAsync(token);
        return null;
    }

    private static async Task PublishChangeClosureAsync(Change change, TicketState previousState,
        IDomainEventPublisher domainEvents, ICorrelationContext correlationContext, CancellationToken token)
    {
        var at = change.ClosedAt!.Value;
        var correlation = GetCorrelationId(correlationContext);
        await domainEvents.PublishAsync(new ChangeUpdatedDomainEvent(change.Id, change.TrackingId, change.Title,
            change.OrganizationId, change.ChangeType, change.State, TemplateChanged: false, at, correlation), token);
        if (previousState != change.State)
            await domainEvents.PublishAsync(new ChangeStateChangedDomainEvent(change.Id, change.TrackingId,
                change.OrganizationId, previousState, change.State, at, correlation), token);
    }

    private static async Task<IResult> BulkCancelChangesAsync(
        BulkChangeCancellationRequest request, ClaimsPrincipal user, HelpdeskDbContext db,
        ICurrentUserAccessService accessService, IChangeTemplateService templates, ITicketSlaCompletionService sla,
        ITicketNotificationService notifications, IDomainEventPublisher domainEvents,
        ICorrelationContext correlationContext, ILoggerFactory loggerFactory, CancellationToken token)
    {
        if (request.Selection is not { Count: > 0 and <= BulkChangeCancellationRequest.MaxSelectionSize } ||
            request.Selection.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 256 ||
                item.ExpectedLifecycleState is not { } expected || !Enum.IsDefined(expected)))
            return Results.BadRequest("Select 1 to 100 change IDs with their expected lifecycle states.");

        if (ValidateLifecycleComment(request.Comment) is { } invalidComment) return invalidComment;
        var comment = request.Comment?.Trim();
        var selection = request.Selection.DistinctBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var ids = selection.Select(item => item.Id).ToArray();
        var access = await accessService.ResolveAsync(user, token);
        var actorId = LifecycleActor(user);
        if (string.IsNullOrWhiteSpace(actorId)) return Results.Forbid();

        var committed = false;
        try
        {
            await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            var changes = await db.Changes.Where(change => ids.Contains(change.Id)).ToDictionaryAsync(change => change.Id, token);
            var permitted = changes.Values.Where(change => access.CanManageChange(change.OrganizationId)).ToArray();
            if (!await FenceChangeDecisionsAsync(db, permitted, token)) return LifecycleConflict();
            var candidates = selection.Select(item =>
            {
                var change = changes.GetValueOrDefault(item.Id);
                return (Selection: item, Change: change, Outcome: CancellationOutcome(change, item, access));
            }).ToArray();
            if (string.IsNullOrEmpty(comment) && candidates.Any(item => item.Outcome is null &&
                item.Selection.ExpectedLifecycleState != ChangeLifecycleState.Draft))
                return Results.BadRequest("An explanation is required when cancelling changes beyond Draft.");

            var results = new List<BulkChangeCancellationResult>(selection.Length);
            var closed = new List<(Change Change, TicketState PreviousState)>();
            foreach (var (item, change, initialOutcome) in candidates)
            {
                var outcome = initialOutcome;
                if (outcome is null && change is not null)
                {
                    var previousState = change.State;
                    var failure = await ApplyChangeClosureAsync(db, change, ChangeLifecycleState.Cancelled, actorId,
                        access.Name ?? user.Identity?.Name, comment, templates, sla, notifications, loggerFactory, token);
                    if (failure is not null) return failure;
                    outcome = "cancelled";
                    closed.Add((change, previousState));
                }
                results.Add(new(item.Id, outcome!));
            }
            if (transaction is not null) await transaction.CommitAsync(token);
            committed = true;
            foreach (var item in closed)
                await PublishChangeClosureAsync(item.Change, item.PreviousState, domainEvents, correlationContext, token);
            return Results.Ok(results);
        }
        catch (Exception exception) when (!committed && IsChangeApprovalConflict(exception))
        {
            db.ChangeTracker.Clear();
            return LifecycleConflict();
        }
    }

    private static string? CancellationOutcome(Change? change, ChangeCancellationSelection item, CurrentUserAccessProfile access)
    {
        if (change is null) return "not-found";
        if (!access.CanManageChange(change.OrganizationId)) return "not-permitted";
        if (EffectiveLifecycleState(change) == ChangeLifecycleState.Cancelled) return "already-cancelled";
        if (!MatchesLifecycleSelection(change, item.ExpectedLifecycleState, item.ExpectedUpdatedAt)) return "conflict";
        return ChangeLifecycle.CanCancel(EffectiveLifecycleState(change)) ? null : "ineligible";
    }
}
