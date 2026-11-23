using Helpdesk.Application.Events;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EntityState = Helpdesk.Shared.Models.EntityState;
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

        group.MapGet("/{id}/timeline/stream", StreamOwnedRequest)
            .WithName("StreamMySelfServiceRequest")
            .WithSummary("Stream authorized own-request snapshot invalidations");

        group.MapGet("/{id}/tasks", GetMyRequestTasks)
            .WithName("GetMySelfServiceRequestTasks")
            .WithSummary("Get tasks for one self-service request submitted by current user");
    }

    private static async Task<IResult> GetMyRequests(
        ClaimsPrincipal user,
        [FromServices] HelpdeskDbContext db,
        [FromServices] SelfServiceAudienceService audience,
        [FromServices] ITenantContext tenant,
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

        var authority = await audience.ResolveAuthorityAsync(user, token);
        var accessibleForms = HistoryForms(db, authority);
        var query = OwnedRequests(db, authority, accessibleForms)
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
                OrganizationId = r.Request.OrganizationId,
                OrganizationName = db.Organizations.Where(org => org.Id == r.Request.OrganizationId)
                    .Select(org => org.Name).FirstOrDefault(),
                ReleaseStatus = r.ReleaseStatus,
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(r.Request.CreatedAt, DateTimeKind.Utc)),
                UpdatedAt = r.Request.UpdatedAt.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(r.Request.UpdatedAt.Value, DateTimeKind.Utc))
                    : null
            })
            .ToListAsync(token);

        await domainEvents.PublishAsync(
            new MyRequestsViewedEvent(
                authority.Identity?.HomeCustomer?.Id ?? tenant.UserId,
                resolvedPage,
                resolvedPageSize,
                string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
                items.Count,
                authority.Organizations.Count == 1 ? authority.Organizations.Keys.Single() : null,
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
        [FromServices] SelfServiceAudienceService audience,
        [FromServices] ITenantContext tenant,
        [FromServices] IDomainEventPublisher domainEvents,
        [FromServices] ICorrelationContext correlationContext,
        CancellationToken token)
    {
        var request = await GetOwnedRequestAsync(user, db, audience, id, token);
        if (request is null)
        {
            return Results.NotFound();
        }

        await domainEvents.PublishAsync(
            new MyRequestOpenedEvent(
                tenant.UserId,
                request.Id,
                request.TrackingId,
                request.OrganizationId,
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
            OrganizationId = request.OrganizationId,
            OrganizationName = request.OrganizationName,
            ReleaseStatus = request.RequestFormReleaseStatus,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(request.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt = request.UpdatedAt.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(request.UpdatedAt.Value, DateTimeKind.Utc))
                : null,
            WorkflowStatus = request.WorkflowStatus,
            WorkflowBlockReason = request.WorkflowBlockReason,
            WorkflowUpdatedAt = request.WorkflowUpdatedAt,
            PayloadJson = string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson
        });
    }

    private static async Task<IResult> GetMyRequestTasks(
        ClaimsPrincipal user,
        [FromRoute] string id,
        [FromServices] HelpdeskDbContext db,
        [FromServices] SelfServiceAudienceService audience,
        CancellationToken token)
    {
        var request = await GetOwnedRequestAsync(user, db, audience, id, token);
        if (request is null)
        {
            return Results.NotFound();
        }

        var ownedTasks = db.RequestTasks.IgnoreQueryFilters().AsNoTracking()
            .Where(task => task.RequestId == request.Id && task.OrganizationId == request.OrganizationId);
        var tasks = await ownedTasks.OrderBy(t => t.Order)
            .ThenBy(t => t.CreatedAt).ThenBy(t => t.Id)
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
                FailureReason = t.FailureReason,
                LastAutomationStatus = t.LastAutomationStatus
            })
            .ToListAsync(token);

        var taskIds = tasks.Select(x => x.Id).ToList();
        var pendingApprovals = taskIds.Count == 0
            ? new List<MyRequestPendingApprovalDto>()
            : await db.RequestTaskApprovals.AsNoTracking()
                .Where(x => taskIds.Contains(x.RequestTaskId)
                    && x.Status == RequestTaskApprovalStatus.Pending)
                .Join(
                    ownedTasks,
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
        ClaimsPrincipal user, HelpdeskDbContext db, SelfServiceAudienceService audience,
        string requestId, CancellationToken token)
    {
        var authority = await audience.ResolveAuthorityAsync(user, token);
        var forms = HistoryForms(db, authority);
        return await OwnedRequests(db, authority, forms).Where(r => r.Id == requestId)
            .Select(r => new RequestWithReleaseStatus(
                r.Id,
                r.TrackingId,
                r.Title,
                r.Description,
                r.State,
                r.OrganizationId,
                db.Organizations.Where(org => org.Id == r.OrganizationId).Select(org => org.Name).FirstOrDefault(),
                r.PayloadJson,
                r.CreatedAt,
                r.UpdatedAt,
                r.WorkflowStatus,
                r.WorkflowBlockReason,
                r.WorkflowUpdatedAt,
                forms.Where(f => f.Id == r.RequestFormId).Select(f => (RequestFormReleaseStatus?)f.ReleaseStatus).FirstOrDefault()))
            .FirstOrDefaultAsync(token);
    }

    private static IQueryable<RequestForm> HistoryForms(HelpdeskDbContext db, SelfServiceAuthority authority)
    {
        // Publication controls new requests. Existing receipts retain the form's
        // release/test boundary independently of its current audience or folder.
        var forms = db.RequestForms.IgnoreQueryFilters().AsNoTracking();
        return authority.IsTestUser ? forms : forms.Where(form => form.ReleaseStatus == RequestFormReleaseStatus.Production);
    }

    private static IQueryable<Request> OwnedRequests(
        HelpdeskDbContext db, SelfServiceAuthority authority, IQueryable<RequestForm> releaseVisibleForms)
    {
        var requests = db.Requests.IgnoreQueryFilters().AsNoTracking();
        if (authority.Access.IsHelpdeskAdmin) return requests;

        var organizations = authority.Organizations.Keys.ToArray();
        var customerIds = authority.Customers.Values.Where(customer => customer.IsEnabled &&
            authority.Organizations.ContainsKey(customer.OrganizationId)).Select(customer => customer.Id).ToArray();
        return requests.Where(request => organizations.Contains(request.OrganizationId) &&
            db.Customers.Any(customer => customer.Id == request.CustomerId && customerIds.Contains(customer.Id) &&
                customer.OrganizationId == request.OrganizationId && customer.State == EntityState.Enabled) &&
            (string.IsNullOrWhiteSpace(request.RequestFormId) ||
                releaseVisibleForms.Any(form => form.Id == request.RequestFormId)));
    }

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
        string OrganizationId,
        string? OrganizationName,
        string? PayloadJson,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        string? WorkflowStatus,
        string? WorkflowBlockReason,
        DateTimeOffset? WorkflowUpdatedAt,
        RequestFormReleaseStatus? RequestFormReleaseStatus);
}
