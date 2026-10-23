using Helpdesk.API.Endpoints.Authentication;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.API.Configuration;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Observability;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.Notifications;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications")
            .WithTags("Notifications")
            .RequireAuthorization("HelpdeskAdmin")
            .ExcludeFromDescription();

        static async Task<IResult> GetTimelineAsync(
            HttpContext httpContext,
            INotificationService notifications,
            string? reference,
            string? correlationId,
            string? tenantId,
            int take,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(reference) && string.IsNullOrWhiteSpace(correlationId))
                return Results.BadRequest(new { error = "Either reference or correlationId is required." });

            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var result = await notifications.GetDomainEventTimelineAsync(
                userId,
                reference,
                correlationId,
                tenantId,
                take,
                ct);

            return Results.Ok(result);
        }

        group.MapGet("/", async (
            HttpContext httpContext,
            [FromServices] INotificationService notifications,
            int page = 1,
            int pageSize = 10,
            string? search = null,
            NotificationSeverity? severity = null,
            string? source = null,
            string? category = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var result = await notifications.GetNotificationsForUserAsync(
                userId,
                page,
                pageSize,
                search,
                severity,
                source,
                category,
                from,
                to,
                null,
                null,
                ct);
            return Results.Ok(result);
        })
        .WithName("GetNotifications")
        .WithSummary("Get notifications")
        .WithDescription("Gets paginated notifications for the current authenticated user.");

        app.MapGet("/api/v1/notifications", async (
            HttpContext httpContext,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            int page = 1,
            int pageSize = 10,
            string? search = null,
            NotificationSeverity? severity = null,
            string? source = null,
            string? category = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            string? sortBy = null,
            string? sortDir = null,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var access = await accessService.ResolveAsync(httpContext.User, ct);
            var result = await GetScopedNotificationsPageAsync(
                db,
                access,
                userId,
                page,
                pageSize,
                search,
                severity,
                source,
                category,
                from,
                to,
                sortBy,
                sortDir,
                ct);
            return Results.Ok(result);
        })
        .WithTags("Notifications")
        .RequireAuthorization("NotificationAccess")
        .WithName("GetNotificationsV1")
        .WithSummary("Get notifications paged")
        .WithDescription("Gets paginated notifications with total count for the current authenticated user.");

        app.MapGet("/api/v1/notifications/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var access = await accessService.ResolveAsync(httpContext.User, ct);
            var result = await GetScopedNotificationByIdAsync(db, access, userId, id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .WithTags("Notifications")
        .RequireAuthorization("NotificationAccess")
        .WithName("GetNotificationByIdV1")
        .WithSummary("Get notification by id")
        .WithDescription("Gets a single notification for the current authenticated user.");

        app.MapGet("/api/v1/notifications/unread-errors", async (
            HttpContext httpContext,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            int take = 20,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var access = await accessService.ResolveAsync(httpContext.User, ct);
            var result = await GetScopedUnreadNotificationsQuery(db, access, userId)
                .Where(n => n.Severity == NotificationSeverity.Error || n.Severity == NotificationSeverity.Critical)
                .OrderByDescending(n => n.CreatedUtc)
                .Take(Math.Clamp(take, 1, 100))
                .SelectScopedDto(db, userId)
                .ToListAsync(ct);
            return Results.Ok(result);
        })
        .WithTags("Notifications")
        .RequireAuthorization("NotificationAccess")
        .WithName("GetUnreadErrorNotificationsV1")
        .WithSummary("Get unread error notifications")
        .WithDescription("Returns unread Error/Critical notifications only, newest first.");

        app.MapGet("/api/v1/notifications/error-summary", async (
            HttpContext httpContext,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var access = await accessService.ResolveAsync(httpContext.User, ct);
            var unreadErrors = await GetScopedUnreadNotificationsQuery(db, access, userId)
                .CountAsync(n => n.Severity == NotificationSeverity.Error || n.Severity == NotificationSeverity.Critical, ct);
            var summary = new NotificationSummaryDto { UnreadErrorCount = unreadErrors };
            return Results.Ok(summary);
        })
        .WithTags("Notifications")
        .RequireAuthorization("NotificationAccess")
        .WithName("GetNotificationErrorSummaryV1")
        .WithSummary("Get error notification summary")
        .WithDescription("Gets unread Error/Critical notification count for the current authenticated user.");

        app.MapPost("/api/v1/notifications/mark-read", async (
            HttpContext httpContext,
            [FromBody] BulkMarkNotificationReadRequest body,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var access = await accessService.ResolveAsync(httpContext.User, ct);
            var updated = await MarkScopedNotificationsReadAsync(db, access, body?.Ids ?? new List<Guid>(), userId, ct);
            return Results.Ok(new BulkMarkReadResult { Updated = updated });
        })
        .WithTags("Notifications")
        .RequireAuthorization("NotificationAccess")
        .WithName("BulkMarkNotificationReadV1")
        .WithSummary("Mark notifications read in bulk")
        .WithDescription("Marks unread notifications as read for the current authenticated user and returns number of changed rows.");

        app.MapDelete("/api/v1/notifications/purge", async (
            HttpContext httpContext,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IOptions<NotificationFeatureOptions> notificationOptions,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            if (!notificationOptions.Value.EnablePurge)
                return Results.NotFound();

            var readCount = await db.NotificationReads.CountAsync(ct);
            var notificationCount = await db.Notifications.CountAsync(ct);

            if (notificationCount > 0)
            {
                var notifications = await db.Notifications.ToListAsync(ct);
                db.Notifications.RemoveRange(notifications);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new { deleted = readCount + notificationCount, notificationsDeleted = notificationCount, readsDeleted = readCount });
        })
        .WithTags("Notifications")
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("PurgeNotificationsV1")
        .WithSummary("Purge all notifications in development")
        .WithDescription("Deletes all notification and notification-read rows. Development only.");

        group.MapGet("/unread", async (
            HttpContext httpContext,
            [FromServices] INotificationService notifications,
            int page = 1,
            int pageSize = 10,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var result = await notifications.GetUnreadNotificationsAsync(userId, page, pageSize, ct);
            return Results.Ok(result);
        })
        .WithName("GetUnreadNotifications")
        .WithSummary("Get unread notifications")
        .WithDescription("Returns unread notifications only for the current user.");

        group.MapGet("/summary", async (
            HttpContext httpContext,
            [FromServices] INotificationService notifications,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var summary = await notifications.GetNotificationSummaryAsync(userId, ct);
            return Results.Ok(summary);
        })
        .WithName("GetNotificationSummary")
        .WithSummary("Get notification summary")
        .WithDescription("Gets notification counts for the current authenticated user.");

        group.MapGet("/timeline", async (
            HttpContext httpContext,
            [FromServices] INotificationService notifications,
            string? reference = null,
            string? correlationId = null,
            string? tenantId = null,
            int take = 200,
            CancellationToken ct = default) =>
        {
            return await GetTimelineAsync(httpContext, notifications, reference, correlationId, tenantId, take, ct);
        })
        .WithName("GetNotificationTimeline")
        .WithSummary("Get correlated notification timeline")
        .WithDescription("Returns ordered DomainEvent notifications by reference and/or correlation ID.");

        app.MapGet("/api/v1/notifications/timeline", async (
            HttpContext httpContext,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            string? reference = null,
            string? correlationId = null,
            string? tenantId = null,
            int take = 200,
            CancellationToken ct = default) =>
        {
            return await GetScopedTimelineAsync(httpContext, db, accessService, reference, correlationId, tenantId, take, ct);
        })
        .WithTags("Notifications")
        .RequireAuthorization("NotificationAccess")
        .WithName("GetNotificationTimelineV1")
        .WithSummary("Get correlated notification timeline")
        .WithDescription("Returns ordered DomainEvent notifications by reference and/or correlation ID.");

        app.MapGet("/api/v1/notifications/unread", async (
            HttpContext httpContext,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            int page = 1,
            int pageSize = 10,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var safePage = page < 1 ? 1 : page;
            var safePageSize = pageSize < 1 ? 10 : Math.Min(pageSize, 100);
            var access = await accessService.ResolveAsync(httpContext.User, ct);
            var result = await GetScopedUnreadNotificationsQuery(db, access, userId)
                .OrderByDescending(n => n.CreatedUtc)
                .Skip((safePage - 1) * safePageSize)
                .Take(safePageSize)
                .SelectScopedDto(db, userId)
                .ToListAsync(ct);
            return Results.Ok(result);
        })
        .WithTags("Notifications")
        .RequireAuthorization("NotificationAccess")
        .WithName("GetUnreadNotificationsV1")
        .WithSummary("Get unread notifications")
        .WithDescription("Returns unread notifications only for the current user.");

        app.MapGet("/api/v1/notifications/summary", async (
            HttpContext httpContext,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var access = await accessService.ResolveAsync(httpContext.User, ct);
            var summary = await GetScopedNotificationSummaryAsync(db, access, userId, ct);
            return Results.Ok(summary);
        })
        .WithTags("Notifications")
        .RequireAuthorization("NotificationAccess")
        .WithName("GetNotificationSummaryV1")
        .WithSummary("Get notification summary")
        .WithDescription("Gets notification counts for the current authenticated user.");

        app.MapGet("/api/v1/notifications/stream", StreamNotifications)
            .WithTags("Notifications")
            .RequireAuthorization("NotificationAccess")
            .WithName("StreamNotifications")
            .WithSummary("Streams notifications in real time")
            .WithDescription("Pushes notifications over server-sent events (SSE).");

        group.MapPost("/", async (
            [FromBody] CreateNotificationRequest request,
            [FromServices] INotificationService notifications,
            CancellationToken ct = default) =>
        {
            await notifications.CreateNotificationAsync(request, ct);
            return Results.Accepted();
        })
        .WithName("CreateNotification")
        .WithSummary("Create notification")
        .WithDescription("Creates a user-specific or global notification.");

        app.MapPost("/api/v1/notifications", async (
            [FromBody] CreateNotificationRequest request,
            [FromServices] INotificationService notifications,
            CancellationToken ct = default) =>
        {
            await notifications.CreateNotificationAsync(request, ct);
            return Results.Accepted();
        })
        .WithTags("Notifications")
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("CreateNotificationV1")
        .WithSummary("Create notification")
        .WithDescription("Creates a user-specific or global notification.");

        group.MapPost("/{id:guid}/read", async (
            [FromRoute] Guid id,
            HttpContext httpContext,
            [FromBody] MarkNotificationReadRequest? body,
            [FromServices] INotificationService notifications,
            CancellationToken ct = default) =>
        {
            if (body is not null && body.NotificationId != Guid.Empty && body.NotificationId != id)
                return Results.BadRequest(new { error = "Notification ID mismatch." });

            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            try
            {
                await notifications.MarkNotificationReadAsync(id, userId, ct);
                return Results.Ok();
            }
            catch (InvalidOperationException)
            {
                return Results.Problem("Notification not found or not accessible.", statusCode: 404);
            }
        })
        .WithName("MarkNotificationRead")
        .WithSummary("Mark notification read")
        .WithDescription("Marks a notification as read for the current authenticated user.");

        app.MapPost("/api/v1/notifications/{id:guid}/read", async (
            [FromRoute] Guid id,
            HttpContext httpContext,
            [FromBody] MarkNotificationReadRequest? body,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            CancellationToken ct = default) =>
        {
            if (body is not null && body.NotificationId != Guid.Empty && body.NotificationId != id)
                return Results.BadRequest(new { error = "Notification ID mismatch." });

            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var access = await accessService.ResolveAsync(httpContext.User, ct);
            var updated = await MarkScopedNotificationsReadAsync(db, access, [id], userId, ct);
            return updated == 0
                ? Results.Problem("Notification not found or not accessible.", statusCode: 404)
                : Results.Ok();
        })
        .WithTags("Notifications")
        .RequireAuthorization("NotificationAccess")
        .WithName("MarkNotificationReadV1")
        .WithSummary("Mark notification read")
        .WithDescription("Marks a notification as read for the current authenticated user.");
    }

    private static string? ResolveUserId(HttpContext context)
    {
        var principal = context.User;

        var claimUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue("preferred_username");

        if (!string.IsNullOrWhiteSpace(claimUserId) &&
            !principal.IsInRole("system.blazor-web"))
        {
            return claimUserId;
        }

        // In system-token scenarios, NewWeb supplies the authenticated user context from server-side auth.
        if (context.Request.Headers.TryGetValue("X-Helpdesk-UserId", out var userIdHeader))
            return userIdHeader.ToString();

        return claimUserId;
    }

    private static async Task<IResult> GetScopedTimelineAsync(
        HttpContext httpContext,
        HelpdeskDbContext db,
        ICurrentUserAccessService accessService,
        string? reference,
        string? correlationId,
        string? tenantId,
        int take,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference) && string.IsNullOrWhiteSpace(correlationId))
            return Results.BadRequest(new { error = "Either reference or correlationId is required." });

        var userId = ResolveUserId(httpContext);
        if (string.IsNullOrWhiteSpace(userId))
            return Results.Unauthorized();

        var access = await accessService.ResolveAsync(httpContext.User, ct);
        if (!access.IsHelpdeskAdmin &&
            !string.IsNullOrWhiteSpace(tenantId) &&
            !NotificationOrganizationIds(access).Contains(tenantId))
        {
            return Results.Forbid();
        }

        var safeTake = Math.Clamp(take, 1, 500);
        var query = BuildScopedNotificationsQuery(db, access, userId)
            .Where(n => n.Category == "DomainEvent");

        if (!string.IsNullOrWhiteSpace(reference))
            query = query.Where(n => n.Reference == reference);

        if (!string.IsNullOrWhiteSpace(correlationId))
            query = query.Where(n => n.CorrelationId == correlationId);

        if (!string.IsNullOrWhiteSpace(tenantId))
            query = query.Where(n => n.TenantId == tenantId);

        var items = await query
            .OrderBy(n => n.CreatedUtc)
            .Take(safeTake)
            .SelectScopedDto(db, userId)
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    private static async Task<PagedResponse<NotificationDto>> GetScopedNotificationsPageAsync(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        string userId,
        int page,
        int pageSize,
        string? searchTerm,
        NotificationSeverity? severity,
        string? source,
        string? category,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? sortBy,
        string? sortDir,
        CancellationToken ct)
    {
        var safePage = page < 1 ? 1 : page;
        var safePageSize = pageSize < 1 ? 10 : Math.Min(pageSize, 100);
        var query = ApplyNotificationFilters(
            BuildScopedNotificationsQuery(db, access, userId),
            searchTerm,
            severity,
            source,
            category,
            from,
            to);

        var total = await query.CountAsync(ct);
        var items = await ApplyNotificationSorting(query, sortBy, sortDir)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .SelectScopedDto(db, userId)
            .ToListAsync(ct);

        return new PagedResponse<NotificationDto>
        {
            Page = safePage,
            PageSize = safePageSize,
            TotalCount = total,
            Items = items
        };
    }

    private static async Task<NotificationDto?> GetScopedNotificationByIdAsync(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        string userId,
        Guid id,
        CancellationToken ct)
    {
        return await BuildScopedNotificationsQuery(db, access, userId)
            .Where(n => n.Id == id)
            .SelectScopedDto(db, userId)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<NotificationSummaryDto> GetScopedNotificationSummaryAsync(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        string userId,
        CancellationToken ct)
    {
        var reads = db.NotificationReads.AsNoTracking().Where(read => read.UserId == userId);
        // Aggregate one authorized population in one database statement so all
        // counters share a snapshot and repeated scope checks need no extra round trips.
        return await BuildScopedNotificationsQuery(db, access, userId)
            .Select(notification => new
            {
                notification.Severity,
                IsUnread = notification.IsGlobal
                    ? !reads.Any(read => read.NotificationId == notification.Id)
                    : notification.ReadUtc == null
            })
            .GroupBy(_ => 1)
            .Select(group => new NotificationSummaryDto
            {
                TotalCount = group.Count(),
                UnreadCount = group.Count(notification => notification.IsUnread),
                UnreadErrorCount = group.Count(notification => notification.IsUnread &&
                    (notification.Severity == NotificationSeverity.Error || notification.Severity == NotificationSeverity.Critical))
            })
            .SingleOrDefaultAsync(ct) ?? new NotificationSummaryDto();
    }

    private static IQueryable<NotificationEntity> BuildScopedNotificationsQuery(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        string userId)
    {
        var query = db.Notifications.AsNoTracking();
        if (access.IsHelpdeskAdmin)
        {
            return query;
        }

        var incidentScopes = access.OrganizationIdsForAny(HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentManager).ToArray();
        var requestScopes = access.OrganizationIdsForAny(HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager).ToArray();
        var changeScopes = access.OrganizationIdsForAny(HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeManager).ToArray();
        var generalScopes = incidentScopes.Intersect(requestScopes, StringComparer.OrdinalIgnoreCase)
            .Intersect(changeScopes, StringComparer.OrdinalIgnoreCase).ToArray();
        var incidentOwnScopes = access.OrganizationIdsFor(HelpdeskPermissions.IncidentUser).ToArray();
        var requestOwnScopes = access.OrganizationIdsFor(HelpdeskPermissions.RequestUser).ToArray();
        var changeOwnScopes = access.OrganizationIdsFor(HelpdeskPermissions.ChangeUser).ToArray();
        var currentScopes = access.ScopedPermissionGrants.Count > 0
            ? access.ScopedPermissionGrants.Select(grant => grant.OrganizationId).Distinct().ToArray()
            : access.Permissions.Count > 0 ? access.AllowedOrganizationIds.ToArray() : [];
        var customerId = access.CustomerId;
        // Personal ticket notices also require current resource access: delivery does not create a permanent grant.
        // Correlate broadcasts with their actual ticket before granting module-specific readers access.
        // Unclassified operational notices require visibility across all ticket modules in that tenant.
        return query.Where(n =>
            (n.UserId == userId &&
             (((n.Reference == null || n.Reference == "") && (n.TenantId == null || currentScopes.Contains(n.TenantId))) ||
              db.Incidents.IgnoreQueryFilters().Any(ticket => (n.TenantId == null || ticket.OrganizationId == n.TenantId) &&
                  (ticket.Id == n.Reference || ticket.TrackingId == n.Reference) &&
                  (incidentScopes.Contains(ticket.OrganizationId!) || (customerId != null && ticket.CustomerId == customerId && incidentOwnScopes.Contains(ticket.OrganizationId!)))) ||
              db.Requests.IgnoreQueryFilters().Any(ticket => (n.TenantId == null || ticket.OrganizationId == n.TenantId) &&
                  (ticket.Id == n.Reference || ticket.TrackingId == n.Reference) &&
                  (requestScopes.Contains(ticket.OrganizationId!) || (customerId != null && ticket.CustomerId == customerId && requestOwnScopes.Contains(ticket.OrganizationId!)))) ||
              db.Changes.IgnoreQueryFilters().Any(ticket => (n.TenantId == null || ticket.OrganizationId == n.TenantId) &&
                  (ticket.Id == n.Reference || ticket.TrackingId == n.Reference) &&
                  (changeScopes.Contains(ticket.OrganizationId!) || (customerId != null && ticket.CustomerId == customerId && changeOwnScopes.Contains(ticket.OrganizationId!)))))) ||
            (n.IsGlobal && n.UserId == null && n.TenantId != null &&
             (generalScopes.Contains(n.TenantId) ||
              db.Incidents.IgnoreQueryFilters().Any(ticket => ticket.OrganizationId == n.TenantId &&
                  incidentScopes.Contains(ticket.OrganizationId!) && (ticket.Id == n.Reference || ticket.TrackingId == n.Reference)) ||
              db.Requests.IgnoreQueryFilters().Any(ticket => ticket.OrganizationId == n.TenantId &&
                  requestScopes.Contains(ticket.OrganizationId!) && (ticket.Id == n.Reference || ticket.TrackingId == n.Reference)) ||
              db.Changes.IgnoreQueryFilters().Any(ticket => ticket.OrganizationId == n.TenantId &&
                  changeScopes.Contains(ticket.OrganizationId!) && (ticket.Id == n.Reference || ticket.TrackingId == n.Reference)))));

    }

    private static IQueryable<NotificationEntity> GetScopedUnreadNotificationsQuery(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        string userId)
    {
        var readsForUser = db.NotificationReads.AsNoTracking().Where(x => x.UserId == userId);

        return BuildScopedNotificationsQuery(db, access, userId)
            .Where(n =>
                (n.IsGlobal && !readsForUser.Any(r => r.NotificationId == n.Id)) ||
                (!n.IsGlobal && n.ReadUtc == null));
    }

    private static async Task<int> MarkScopedNotificationsReadAsync(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        IReadOnlyCollection<Guid> notificationIds,
        string userId,
        CancellationToken ct)
    {
        if (notificationIds.Count == 0)
            return 0;

        var requestedIds = notificationIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (requestedIds.Length == 0)
            return 0;

        var visibleIds = await BuildScopedNotificationsQuery(db, access, userId)
            .Where(n => requestedIds.Contains(n.Id))
            .Select(n => n.Id)
            .ToListAsync(ct);

        if (visibleIds.Count == 0)
            return 0;

        var visibleIdSet = visibleIds.ToHashSet();
        var notifications = await db.Notifications
            .Where(n => visibleIdSet.Contains(n.Id))
            .ToListAsync(ct);

        var existingReads = await db.NotificationReads
            .Where(r => r.UserId == userId && visibleIdSet.Contains(r.NotificationId))
            .Select(r => r.NotificationId)
            .ToListAsync(ct);

        var existingReadSet = existingReads.ToHashSet();
        var updated = 0;
        foreach (var notification in notifications)
        {
            if (!notification.IsGlobal)
            {
                if (!string.Equals(notification.UserId, userId, StringComparison.OrdinalIgnoreCase) ||
                    notification.ReadUtc.HasValue)
                {
                    continue;
                }

                notification.ReadUtc = DateTime.UtcNow;
                updated++;
                continue;
            }

            if (existingReadSet.Contains(notification.Id))
                continue;

            db.NotificationReads.Add(new NotificationReadEntity
            {
                NotificationId = notification.Id,
                UserId = userId,
                ReadUtc = DateTime.UtcNow
            });
            updated++;
        }

        if (updated > 0)
            await db.SaveChangesAsync(ct);

        return updated;
    }

    private static IQueryable<NotificationDto> SelectScopedDto(
        this IQueryable<NotificationEntity> notifications,
        HelpdeskDbContext db,
        string userId)
    {
        var readsForUser = db.NotificationReads.AsNoTracking().Where(x => x.UserId == userId);

        return from n in notifications
               join r in readsForUser on n.Id equals r.NotificationId into readGroup
               from r in readGroup.DefaultIfEmpty()
               select new NotificationDto
               {
                   Id = n.Id,
                   UserId = n.UserId,
                   Title = n.Title,
                   Message = n.Message,
                   Severity = n.Severity,
                   CreatedUtc = n.CreatedUtc,
                   ReadUtc = n.IsGlobal ? (r != null ? r.ReadUtc : null) : n.ReadUtc,
                   IsRead = n.IsGlobal ? r != null : n.ReadUtc.HasValue,
                   Source = n.Source,
                   Category = n.Category,
                   TenantId = n.TenantId,
                   Reference = n.Reference,
                   CorrelationId = n.CorrelationId,
                   Link = n.Link,
                   IsGlobal = n.IsGlobal
               };
    }

    private static IQueryable<NotificationEntity> ApplyNotificationFilters(
        IQueryable<NotificationEntity> query,
        string? searchTerm,
        NotificationSeverity? severity,
        string? source,
        string? category,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(n =>
                EF.Functions.Like(n.Title, term) ||
                EF.Functions.Like(n.Message, term) ||
                (n.Reference != null && EF.Functions.Like(n.Reference, term)) ||
                (n.CorrelationId != null && EF.Functions.Like(n.CorrelationId, term)));
        }

        if (severity.HasValue)
            query = query.Where(n => n.Severity == severity.Value);

        if (!string.IsNullOrWhiteSpace(source))
            query = query.Where(n => n.Source == source);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(n => n.Category == category);

        if (from.HasValue)
        {
            var fromUtc = from.Value.UtcDateTime;
            query = query.Where(n => n.CreatedUtc >= fromUtc);
        }

        if (to.HasValue)
        {
            var toUtc = to.Value.UtcDateTime;
            query = query.Where(n => n.CreatedUtc <= toUtc);
        }

        return query;
    }

    private static IQueryable<NotificationEntity> ApplyNotificationSorting(
        IQueryable<NotificationEntity> query,
        string? sortBy,
        string? sortDir)
    {
        var isAsc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy?.ToLowerInvariant() switch
        {
            "created" => isAsc
                ? query.OrderBy(n => n.CreatedUtc)
                : query.OrderByDescending(n => n.CreatedUtc),
            "severity" => isAsc
                ? query.OrderBy(n => n.Severity)
                : query.OrderByDescending(n => n.Severity),
            "source" => isAsc
                ? query.OrderBy(n => n.Source)
                : query.OrderByDescending(n => n.Source),
            "category" => isAsc
                ? query.OrderBy(n => n.Category)
                : query.OrderByDescending(n => n.Category),
            _ => query.OrderByDescending(n => n.CreatedUtc)
        };
    }

    private static async Task StreamNotifications(
        HttpContext context,
        [FromServices] HelpdeskDbContext db,
        [FromServices] INotificationEventBus eventBus,
        [FromServices] ICurrentUserAccessService accessService,
        [FromServices] ILoggerFactory loggerFactory,
        [FromQuery] string? tenantId = null,
        [FromQuery] string? category = "DomainEvent",
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger("NotificationEndpoints");
        var userId = ResolveUserId(context);
        if (string.IsNullOrWhiteSpace(userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var access = await accessService.ResolveAsync(context.User, ct);
        if (!access.IsHelpdeskAdmin &&
            !string.IsNullOrWhiteSpace(tenantId) &&
            !NotificationOrganizationIds(access).Contains(tenantId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var reader = eventBus.Subscribe(tenantId);

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Append("Connection", "keep-alive");
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Response.ContentType = "text/event-stream";

        try
        {
            HelpdeskTelemetry.RecordNotificationStreamConnected(tenantId, category);
            logger.LogInformation(
                "Notification stream connected. User={UserId} Tenant={TenantId} Category={Category}",
                userId,
                tenantId,
                category);

            await context.Response.StartAsync(ct);
            await context.Response.WriteAsync(": connected\n\n", ct);
            await context.Response.Body.FlushAsync(ct);

            var keepAliveInterval = TimeSpan.FromSeconds(15);
            while (!ct.IsCancellationRequested)
            {
                var waitForDataTask = reader.WaitToReadAsync(ct).AsTask();
                var keepAliveTask = Task.Delay(keepAliveInterval, ct);
                var completedTask = await Task.WhenAny(waitForDataTask, keepAliveTask);

                access = await accessService.ResolveAsync(context.User, ct);
                if (!await LocalSessionValidator.IsValidAsync(context, ct) ||
                    !CanAccessNotifications(access) ||
                    (!access.IsHelpdeskAdmin &&
                     !string.IsNullOrWhiteSpace(tenantId) &&
                     !NotificationOrganizationIds(access).Contains(tenantId)))
                {
                    logger.LogInformation(
                        "Notification stream authorization revoked. User={UserId} Tenant={TenantId}",
                        userId,
                        tenantId);
                    break;
                }

                if (completedTask == waitForDataTask)
                {
                    if (!await waitForDataTask)
                        break;

                    while (reader.TryRead(out var notification))
                    {
                        if (!MatchesStreamFilter(notification, tenantId, category) ||
                            !await BuildScopedNotificationsQuery(db, access, userId).AnyAsync(row => row.Id == notification.Id, ct))
                            continue;

                        var json = JsonSerializer.Serialize(notification);
                        await context.Response.WriteAsync("event: notification\n", ct);
                        await context.Response.WriteAsync($"data: {json}\n\n", ct);
                        HelpdeskTelemetry.RecordNotificationStreamEventSent(tenantId, category);
                    }

                    await context.Response.Body.FlushAsync(ct);
                }
                else
                {
                    await context.Response.WriteAsync(": keepalive\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // connection terminated by client disconnect/cancellation
        }
        catch (Exception ex)
        {
            HelpdeskTelemetry.RecordNotificationStreamError(tenantId, category);
            logger.LogError(ex, "Notification stream error. User={UserId} Tenant={TenantId}", userId, tenantId);
        }
        finally
        {
            HelpdeskTelemetry.RecordNotificationStreamDisconnected(tenantId, category);
            logger.LogInformation("Notification stream disconnected. User={UserId} Tenant={TenantId}", userId, tenantId);
            eventBus.Unsubscribe(tenantId, reader);
        }
    }

    private static bool MatchesStreamFilter(NotificationDto notification, string? tenantId, string? category) =>
        (string.IsNullOrWhiteSpace(category) || string.Equals(notification.Category, category, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(tenantId) || string.Equals(notification.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlySet<string> NotificationOrganizationIds(CurrentUserAccessProfile access) =>
        access.OrganizationIdsForAny(
            HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentManager,
            HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager,
            HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeManager);

    private static bool CanAccessNotifications(CurrentUserAccessProfile access) =>
        access.IsHelpdeskAdmin ||
        access.HasPermission(HelpdeskPermissions.IncidentRead) ||
        access.HasPermission(HelpdeskPermissions.RequestRead) ||
        access.HasPermission(HelpdeskPermissions.ChangeRead) ||
        access.HasPermission(HelpdeskPermissions.IncidentWrite) ||
        access.HasPermission(HelpdeskPermissions.RequestWrite) ||
        access.HasPermission(HelpdeskPermissions.ChangeWrite) ||
        access.HasPermission(HelpdeskPermissions.IncidentManager) ||
        access.HasPermission(HelpdeskPermissions.RequestManager) ||
        access.HasPermission(HelpdeskPermissions.ChangeManager);
}
