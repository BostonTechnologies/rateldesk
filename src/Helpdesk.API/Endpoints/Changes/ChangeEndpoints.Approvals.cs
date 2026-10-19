using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.API.Endpoints.Changes;

public static partial class ChangeEndpoints
{
    public static void MapChangeApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var signed = app.MapGroup("/api/v1/changes/public").WithTags("Changes");
        signed.MapGet("/approval", GetPublicChangeApproval).AllowAnonymous().WithName("GetPublicChangeApproval");
        signed.MapPost("/approval/review", ReviewPublicChangeApproval).AllowAnonymous().WithName("ReviewPublicChangeApproval");

        var authenticated = app.MapGroup("/api/v1/changes/{changeId}/approval").WithTags("Changes").RequireAuthorization();
        authenticated.MapGet("/", async (string changeId, ClaimsPrincipal user, [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db, CancellationToken ct) =>
        {
            var selection = await LoadActorChangeApprovalAsync(changeId, user, accessService, db, ct);
            if (selection.Failure is not null) return selection.Failure;
            var change = selection.Change!;
            var approval = selection.Approval!;
            return Results.Ok(new
            {
                ChangeId = change.Id, change.TrackingId, change.Title,
                LifecycleState = EffectiveLifecycleState(change), Approval = ToApprovalDto(approval),
                CanReview = approval.Status == ChangeApprovalStatus.Pending && EffectiveLifecycleState(change) == ChangeLifecycleState.PendingApproval
            });
        }).WithName("GetAuthenticatedChangeApproval");
        authenticated.MapPost("/review", (string changeId, ClaimsPrincipal user, [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db, [FromServices] ITicketNotificationService notifications, [FromServices] ILoggerFactory loggerFactory, CancellationToken ct) =>
            ReviewChangeApprovalAsync(() => LoadActorChangeApprovalAsync(changeId, user, accessService, db, ct),
                user.FindFirstValue(ClaimTypes.NameIdentifier), db, notifications, loggerFactory, ct))
            .WithName("ReviewAuthenticatedChangeApproval");
    }

    private static async Task<IResult> ReviewPublicChangeApproval([FromBody] ReviewChangeApprovalRequest request,
        [FromServices] IPublicTicketLinkSigner signer, [FromServices] HelpdeskDbContext db, [FromServices] ITicketNotificationService notificationService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TrackingId) || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Token))
            return Results.BadRequest("Tracking ID, email, and token are required.");
        return await ReviewChangeApprovalAsync(async () =>
        {
            var change = await db.Changes.Include(item => item.Approvals).SingleOrDefaultAsync(item => item.TrackingId == request.TrackingId, ct);
            if (change is null) return new(null, null, Results.NotFound());
            if (!signer.ValidateToken(request.Token, change.TrackingId, request.Email)) return new(null, null, Results.Unauthorized());
            var approvals = change.Approvals.Where(item => string.Equals(item.ApproverEmail, request.Email, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return approvals.Length == 1 ? new(change, approvals[0], null) : new(null, null, Results.Unauthorized());
        }, null, db, notificationService, loggerFactory, ct);
    }

    private static async Task<ChangeApprovalSelection> LoadActorChangeApprovalAsync(string changeId, ClaimsPrincipal user,
        ICurrentUserAccessService accessService, HelpdeskDbContext db, CancellationToken ct)
    {
        var actorId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var access = await accessService.ResolveAsync(user, ct);
        if (!access.IsAuthenticated || string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(access.Email))
            return new(null, null, Results.NotFound());
        var change = await db.Changes.IgnoreQueryFilters().Include(item => item.Approvals).SingleOrDefaultAsync(item => item.Id == changeId, ct);
        if (change is null || (!access.CanApproveChange(change.OrganizationId) &&
            !access.CanViewChange(change.OrganizationId, change.CustomerId, change.RequesterEmail)))
            return new(null, null, Results.NotFound());
        var matches = change.Approvals.Where(item => string.Equals(item.ApproverEmail, access.Email, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(item.ApproverId, actorId, StringComparison.Ordinal) ||
             !string.IsNullOrWhiteSpace(access.CustomerId) && string.Equals(item.ApproverId, access.CustomerId, StringComparison.Ordinal))).Take(2).ToArray();
        return matches.Length == 1 ? new(change, matches[0], null) : new(null, null, Results.NotFound());
    }

    private static async Task<IResult> ReviewChangeApprovalAsync(Func<Task<ChangeApprovalSelection>> load, string? actorId,
        HelpdeskDbContext db, ITicketNotificationService notifications, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var selected = await load();
            if (selected.Failure is not null) return selected.Failure;
            var change = selected.Change!;
            var approval = selected.Approval!;
            if (!await FenceChangeDecisionAsync(db, change, ct))
                return ChangeApprovalConflict();
            if (approval.Status == ChangeApprovalStatus.Reviewed)
                return Results.Ok(new { approval.Status, approval.ReviewedAtUtc, LifecycleState = EffectiveLifecycleState(change) });
            if (EffectiveLifecycleState(change) != ChangeLifecycleState.PendingApproval)
                return Results.Conflict(new { code = "change_not_pending_approval", message = "The change is no longer awaiting this approval." });

            approval.Status = ChangeApprovalStatus.Reviewed;
            approval.ReviewedAtUtc = DateTimeOffset.UtcNow;
            approval.ViewedAtUtc ??= approval.ReviewedAtUtc;
            change.UpdatedAt = DateTime.UtcNow;
            QueueChangeApprovalTimeline(db, change.Id, $"{approval.ApproverName} reviewed/approved the change.");
            if (actorId is not null)
                db.ActivityLogs.Add(new ActivityLog { UserId = actorId, TicketId = change.Id, RelatedEntityId = approval.Id,
                    Message = $"Authenticated designated approver reviewed change '{change.Id}'." });
            if (change.Approvals.All(item => item.Status == ChangeApprovalStatus.Reviewed))
            {
                change.LifecycleState = ChangeLifecycleState.ApprovedForImplementation;
                QueueChangeApprovalTimeline(db, change.Id, "All approvals received; change approved for implementation.");
                var participants = await BuildParticipantLookupAsync(db, [change], ct);
                var organizationName = await db.Organizations.AsNoTracking().Where(item => item.Id == change.OrganizationId)
                    .Select(item => item.Name).SingleOrDefaultAsync(ct) ?? string.Empty;
                await QueueChangeApprovedNotificationsAsync(notifications, change, organizationName, participants, ct);
            }
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Results.Ok(new { approval.Status, approval.ReviewedAtUtc, LifecycleState = EffectiveLifecycleState(change) });
        }
        catch (Exception exception) when (IsChangeApprovalConflict(exception))
        {
            db.ChangeTracker.Clear();
            return ChangeApprovalConflict();
        }
    }

    private static async Task<bool> FenceChangeDecisionAsync(HelpdeskDbContext db, Change change, CancellationToken ct)
    {
        // Both approval transports and lifecycle transitions must acquire this row
        // fence inside a serializable transaction before changing approval state.
        return !db.Database.IsRelational() || await db.Changes.IgnoreQueryFilters()
            .Where(item => item.Id == change.Id && item.LifecycleState == change.LifecycleState)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LifecycleState, item => item.LifecycleState), ct) == 1;
    }

    private static void QueueChangeApprovalTimeline(HelpdeskDbContext db, string changeId, string message,
        string createdByUserName = "Change Approval")
    {
        var timeline = new TicketTimelineEvent { TicketId = changeId, CreatedUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = "system", CreatedByUserName = createdByUserName, EventType = TimelineEventType.SystemNotification, MessageText = message };
        db.TicketTimelineEvents.Add(timeline);
        db.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect
        {
            EffectKey = $"change-approval:{timeline.Id:N}", DispatchGroup = $"change-approval:{changeId}",
            Kind = MailboxEffectKind.Timeline, AvailableUnixMilliseconds = timeline.CreatedUtc.ToUnixTimeMilliseconds(),
            Payload = JsonSerializer.Serialize(new TicketTimelineEventDto { Id = timeline.Id, TicketId = changeId,
                CreatedUtc = timeline.CreatedUtc, CreatedByUserId = timeline.CreatedByUserId, CreatedByUserName = timeline.CreatedByUserName,
                EventType = timeline.EventType, MessageText = message })
        });
    }

    private static IResult ChangeApprovalConflict() => Results.Conflict(new { code = "change_approval_changed",
        message = "The change approval changed concurrently. Read the current decision before another action." });
    private static bool IsChangeApprovalConflict(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => true,
        DbUpdateException { InnerException: { } inner } => IsChangeApprovalConflict(inner),
        SqliteException { SqliteErrorCode: 5 or 6 } => true,
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => true,
        _ => false
    };
    private sealed record ChangeApprovalSelection(Change? Change, ChangeApproval? Approval, IResult? Failure);
}
