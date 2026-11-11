using Helpdesk.Application.Events;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Requests;

public static partial class SelfServiceMyRequestsEndpoints
{
    public static void MapSelfServiceMyRequestsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/self-service/requests")
            .WithTags("Self Service")
            .RequireAuthorization("SelfService.User");

        group.MapGet("/", GetMyRequests)
            .WithName("GetMySelfServiceRequests")
            .WithSummary("List requests submitted by current user");

        group.MapGet("/{id}", GetMyRequestDetail)
            .WithName("GetMySelfServiceRequestDetail")
            .WithSummary("Get one self-service request submitted by current user");

        group.MapGet("/{id}/tasks", GetMyRequestTasks)
            .WithName("GetMySelfServiceRequestTasks")
            .WithSummary("Get tasks for one self-service request submitted by current user");
    }

    private static async Task<IResult> GetMyRequests(
        ClaimsPrincipal user,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ISelfServiceAudienceService selfServiceAudienceService,
        [FromServices] ITenantContext tenant,
        [FromServices] ICurrentUserAccessService accessService,
        [FromServices] IDomainEventPublisher domainEvents,
        [FromServices] ICorrelationContext correlationContext,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? q,
        [FromQuery] TicketState? state,
        [FromQuery] RequestFormReleaseStatus? releaseStage,
        [FromQuery] DateTime? createdFromUtc,
        [FromQuery] DateTime? createdBeforeUtc,
        [FromQuery] DateTime? updatedFromUtc,
        [FromQuery] DateTime? updatedBeforeUtc,
        [FromQuery] string? sortBy,
        [FromQuery] bool? descending,
        CancellationToken token)
    {
        var resolvedPage = Math.Max(page ?? 1, 1);
        var resolvedPageSize = Math.Clamp(pageSize ?? 25, 1, 100);
        createdFromUtc = NormalizeUtcQueryDate(createdFromUtc);
        createdBeforeUtc = NormalizeUtcQueryDate(createdBeforeUtc);
        updatedFromUtc = NormalizeUtcQueryDate(updatedFromUtc);
        updatedBeforeUtc = NormalizeUtcQueryDate(updatedBeforeUtc);
        var sort = (sortBy ?? "created").Trim().ToLowerInvariant();
        if (sort is not ("created" or "updated" or "trackingid" or "title" or "state") ||
            (state.HasValue && !Enum.IsDefined(state.Value)) ||
            (releaseStage.HasValue && !Enum.IsDefined(releaseStage.Value)) ||
            (createdFromUtc.HasValue && createdBeforeUtc.HasValue && createdFromUtc >= createdBeforeUtc) ||
            (updatedFromUtc.HasValue && updatedBeforeUtc.HasValue && updatedFromUtc >= updatedBeforeUtc))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request filters",
                detail: "Choose a supported sort, status and release stage, with a start date before the end date.");
        }

        var access = await accessService.ResolveAsync(user, token);
        var isAdmin = access.IsHelpdeskAdmin;
        var isTestUser = await selfServiceAudienceService.IsTestUserAsync(token);
        var currentCustomer = isAdmin ? null : await ResolveCurrentCustomerAsync(db, access.CustomerId, token);
        var currentCustomerId = currentCustomer?.Id;
        if (!isAdmin && (string.IsNullOrWhiteSpace(currentCustomerId) ||
                         !IsOrganizationAllowed(access, currentCustomer?.OrganizationId)))
        {
            return Results.Ok(new PagedResponse<MyRequestListItemDto>
            {
                Page = 1,
                PageSize = resolvedPageSize,
                TotalCount = 0,
                Items = []
            });
        }

        var effectiveOrganizationId = currentCustomer?.OrganizationId;

        var accessibleForms = BuildAccessibleRequestFormsQuery(
            db,
            selfServiceAudienceService,
            isAdmin,
            isTestUser,
            effectiveOrganizationId);
        var accessibleFormIds = accessibleForms.Select(f => f.Id);

        var query = db.Requests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => (isAdmin || r.CustomerId == currentCustomerId)
                        && (effectiveOrganizationId == null || r.OrganizationId == effectiveOrganizationId)
                        && (isAdmin
                            || string.IsNullOrWhiteSpace(r.RequestFormId)
                            || accessibleFormIds.Contains(r.RequestFormId!)))
            .Select(r => new
            {
                Request = r,
                ReleaseStatus = string.IsNullOrWhiteSpace(r.RequestFormId)
                    ? (RequestFormReleaseStatus?)null
                    : accessibleForms
                        .Where(f => f.Id == r.RequestFormId)
                        .Select(f => (RequestFormReleaseStatus?)f.ReleaseStatus)
                        .FirstOrDefault()
            });

        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(r =>
                EF.Functions.Like(r.Request.TrackingId.ToLower(), like, "\\") ||
                EF.Functions.Like(r.Request.Title.ToLower(), like, "\\"));
        }

        if (state.HasValue) query = query.Where(r => r.Request.State == state.Value);
        if (releaseStage.HasValue) query = query.Where(r => r.ReleaseStatus == releaseStage.Value);
        if (createdFromUtc.HasValue) query = query.Where(r => r.Request.CreatedAt >= createdFromUtc.Value);
        if (createdBeforeUtc.HasValue) query = query.Where(r => r.Request.CreatedAt < createdBeforeUtc.Value);
        if (updatedFromUtc.HasValue) query = query.Where(r => (r.Request.UpdatedAt ?? r.Request.CreatedAt) >= updatedFromUtc.Value);
        if (updatedBeforeUtc.HasValue) query = query.Where(r => (r.Request.UpdatedAt ?? r.Request.CreatedAt) < updatedBeforeUtc.Value);

        var totalCount = await query.CountAsync(token);
        resolvedPage = Math.Min(resolvedPage, Math.Max(1, (int)Math.Ceiling(totalCount / (double)resolvedPageSize)));
        var reverse = descending ?? true;
        var ordered = (sort, reverse) switch
        {
            ("updated", true) => query.OrderByDescending(r => r.Request.UpdatedAt ?? r.Request.CreatedAt),
            ("updated", false) => query.OrderBy(r => r.Request.UpdatedAt ?? r.Request.CreatedAt),
            ("trackingid", true) => query.OrderByDescending(r => r.Request.TrackingId),
            ("trackingid", false) => query.OrderBy(r => r.Request.TrackingId),
            ("title", true) => query.OrderByDescending(r => r.Request.Title),
            ("title", false) => query.OrderBy(r => r.Request.Title),
            ("state", true) => query.OrderByDescending(r => r.Request.State),
            ("state", false) => query.OrderBy(r => r.Request.State),
            (_, false) => query.OrderBy(r => r.Request.CreatedAt),
            _ => query.OrderByDescending(r => r.Request.CreatedAt)
        };
        var items = await ordered.ThenBy(r => r.Request.Id)
            .Skip((resolvedPage - 1) * resolvedPageSize)
            .Take(resolvedPageSize)
            .Select(r => new MyRequestListItemDto
            {
                Id = r.Request.Id,
                TrackingId = r.Request.TrackingId,
                Title = r.Request.Title,
                State = r.Request.State,
                ReleaseStatus = r.ReleaseStatus,
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(r.Request.CreatedAt, DateTimeKind.Utc)),
                UpdatedAt = r.Request.UpdatedAt.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(r.Request.UpdatedAt.Value, DateTimeKind.Utc))
                    : null
            })
            .ToListAsync(token);

        await domainEvents.PublishAsync(
            new MyRequestsViewedEvent(
                currentCustomerId ?? tenant.UserId,
                resolvedPage,
                resolvedPageSize,
                string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
                items.Count,
                effectiveOrganizationId,
                DateTimeOffset.UtcNow,
                GetCorrelationId(correlationContext)),
            token);

        return Results.Ok(new PagedResponse<MyRequestListItemDto>
        {
            Page = resolvedPage,
            PageSize = resolvedPageSize,
            TotalCount = totalCount,
            Items = items
        });
    }

    private static DateTime? NormalizeUtcQueryDate(DateTime? value) => value is null ? null
        : value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime()
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static async Task<IResult> GetMyRequestDetail(
        [FromRoute] string id,
        ClaimsPrincipal user,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ISelfServiceAudienceService selfServiceAudienceService,
        [FromServices] ITenantContext tenant,
        [FromServices] ICurrentUserAccessService accessService,
        [FromServices] IDomainEventPublisher domainEvents,
        [FromServices] ICorrelationContext correlationContext,
        CancellationToken token)
    {
        var request = await GetOwnedRequestAsync(user, db, selfServiceAudienceService, accessService, id, token);
        if (request is null)
        {
            return Results.NotFound();
        }

        await domainEvents.PublishAsync(
            new MyRequestOpenedEvent(
                tenant.UserId,
                request.Id,
                request.TrackingId,
                tenant.TenantId,
                DateTimeOffset.UtcNow,
                GetCorrelationId(correlationContext)),
            token);

        return Results.Ok(new MyRequestDetailDto
        {
            Id = request.Id,
            TrackingId = request.TrackingId,
            Title = request.Title,
            Description = request.Description,
            State = request.State,
            ReleaseStatus = request.RequestFormReleaseStatus,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(request.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt = request.UpdatedAt.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(request.UpdatedAt.Value, DateTimeKind.Utc))
                : null,
            PayloadJson = string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson
        });
    }

    private static async Task<IResult> GetMyRequestTasks(
        ClaimsPrincipal user,
        [FromRoute] string id,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ISelfServiceAudienceService selfServiceAudienceService,
        [FromServices] ICurrentUserAccessService accessService,
        CancellationToken token)
    {
        var request = await GetOwnedRequestAsync(user, db, selfServiceAudienceService, accessService, id, token);
        if (request is null)
        {
            return Results.NotFound();
        }

        var tasks = await db.RequestTasks.AsNoTracking()
            .Where(t => t.RequestId == request.Id)
            .OrderBy(t => t.Order)
            .ThenBy(t => t.CreatedAt)
            .Select(t => new MyRequestTaskDto
            {
                Id = t.Id,
                Name = t.Name,
                Type = t.Type,
                Status = t.Status,
                Order = t.Order,
                StartedAt = t.StartedAt,
                CompletedAt = t.CompletedAt,
                NextRetryAt = t.NextRetryAt,
                DueAt = t.DueAt,
                RetryCount = t.RetryCount,
                FailureReason = t.FailureReason
            })
            .ToListAsync(token);

        var taskIds = tasks.Select(x => x.Id).ToList();
        var pendingApprovals = taskIds.Count == 0
            ? new List<MyRequestPendingApprovalDto>()
            : await db.RequestTaskApprovals.AsNoTracking()
                .Where(x => taskIds.Contains(x.RequestTaskId)
                    && x.Status == RequestTaskApprovalStatus.Pending)
                .Join(
                    db.RequestTasks.AsNoTracking(),
                    approval => approval.RequestTaskId,
                    task => task.Id,
                    (approval, task) => new MyRequestPendingApprovalDto
                    {
                        Id = approval.Id,
                        RequestTaskId = approval.RequestTaskId,
                        TaskName = task.Name,
                        ApproverName = approval.ApproverName,
                        ApproverEmail = approval.ApproverEmail,
                        OrganizationName = approval.OrganizationName,
                        Status = approval.Status,
                        DueAt = task.DueAt,
                        ReviewedAtUtc = approval.ReviewedAtUtc
                    })
                .ToListAsync(token);
        var approvalsByTask = pendingApprovals
            .GroupBy(x => x.RequestTaskId)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (approvalsByTask.TryGetValue(task.Id, out var approvals))
            {
                task.PendingApprovals = approvals;
            }
        }

        return Results.Ok(tasks);
    }

    private static async Task<RequestWithReleaseStatus?> GetOwnedRequestAsync(
        ClaimsPrincipal user,
        HelpdeskDbContext db,
        ISelfServiceAudienceService selfServiceAudienceService,
        ICurrentUserAccessService accessService,
        string requestId,
        CancellationToken token)
    {
        var access = await accessService.ResolveAsync(user, token);
        var isAdmin = access.IsHelpdeskAdmin;
        var isTestUser = await selfServiceAudienceService.IsTestUserAsync(token);
        var currentCustomer = isAdmin ? null : await ResolveCurrentCustomerAsync(db, access.CustomerId, token);
        var currentCustomerId = currentCustomer?.Id;
        if (!isAdmin && (string.IsNullOrWhiteSpace(currentCustomerId) ||
                         !IsOrganizationAllowed(access, currentCustomer?.OrganizationId)))
        {
            return null;
        }

        var effectiveOrganizationId = currentCustomer?.OrganizationId;
        var accessibleForms = BuildAccessibleRequestFormsQuery(
            db,
            selfServiceAudienceService,
            isAdmin,
            isTestUser,
            effectiveOrganizationId);
        var accessibleFormIds = accessibleForms.Select(f => f.Id);

        return await db.Requests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == requestId
                        && (isAdmin || r.CustomerId == currentCustomerId)
                        && (effectiveOrganizationId == null || r.OrganizationId == effectiveOrganizationId)
                        && (isAdmin
                            || string.IsNullOrWhiteSpace(r.RequestFormId)
                            || accessibleFormIds.Contains(r.RequestFormId!)))
            .Select(r => new RequestWithReleaseStatus(
                r.Id,
                r.TrackingId,
                r.Title,
                r.Description,
                r.State,
                r.PayloadJson,
                r.CreatedAt,
                r.UpdatedAt,
                string.IsNullOrWhiteSpace(r.RequestFormId)
                    ? (RequestFormReleaseStatus?)null
                    : accessibleForms
                        .Where(f => f.Id == r.RequestFormId)
                        .Select(f => (RequestFormReleaseStatus?)f.ReleaseStatus)
                        .FirstOrDefault()))
            .FirstOrDefaultAsync(token);
    }

    private static IQueryable<RequestForm> BuildAccessibleRequestFormsQuery(
        HelpdeskDbContext db,
        ISelfServiceAudienceService selfServiceAudienceService,
        bool isAdmin,
        bool isTestUser,
        string? effectiveOrganizationId)
    {
        if (isAdmin)
        {
            return db.RequestForms.IgnoreQueryFilters().AsNoTracking();
        }

        if (!string.IsNullOrWhiteSpace(effectiveOrganizationId))
        {
            return selfServiceAudienceService.ApplyAudienceFilter(
                db.RequestForms.IgnoreQueryFilters().AsNoTracking(),
                isAdmin,
                isTestUser,
                effectiveOrganizationId);
        }

        return db.RequestForms.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.ReleaseStatus == RequestFormReleaseStatus.Production || isTestUser);
    }

    private static async Task<Customer?> ResolveCurrentCustomerAsync(
        HelpdeskDbContext db,
        string? customerId,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return null;
        }

        return await db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == customerId && x.State == Helpdesk.Shared.Models.EntityState.Enabled, token);
    }

    // Customer identity identifies who submitted a request; it is not a grant
    // to that customer's home organization. This check is required because the
    // endpoint intentionally uses IgnoreQueryFilters for customer-owned data.
    private static bool IsOrganizationAllowed(CurrentUserAccessProfile access, string? organizationId) =>
        access.IsHelpdeskAdmin ||
        (!string.IsNullOrWhiteSpace(organizationId) && access.AllowedOrganizationIds.Contains(organizationId));

    private static string GetCorrelationId(ICorrelationContext correlation)
    {
        return correlation.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private sealed record RequestWithReleaseStatus(
        string Id,
        string TrackingId,
        string Title,
        string? Description,
        TicketState State,
        string? PayloadJson,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        RequestFormReleaseStatus? RequestFormReleaseStatus);
}
