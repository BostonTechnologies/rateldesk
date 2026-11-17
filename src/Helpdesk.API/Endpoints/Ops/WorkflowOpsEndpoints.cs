using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Ops;

public static class WorkflowOpsEndpoints
{
    public static void MapWorkflowOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ops/tasks")
            .WithTags("Workflow Ops")
            .RequireAuthorization();

        group.MapGet("/overdue", async (
            [FromServices] HelpdeskDbContext db,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(user, ct);
            if (!CanManageWorkflowOps(access))
            {
                return Results.Forbid();
            }

            var now = DateTimeOffset.UtcNow;
            return Results.Ok(await QueryOpsTasksAsync(
                db,
                access,
                page,
                pageSize,
                query => db.Database.IsSqlite()
                    ? query.Where(t => t.Status == RequestTaskStatus.InProgress && EF.Property<long?>(t, "DueAtUtcTicks") < now.UtcTicks)
                    : query.Where(t => t.Status == RequestTaskStatus.InProgress && t.DueAt.HasValue && t.DueAt < now),
                ct));
        });

        group.MapGet("/escalated", async (
            [FromServices] HelpdeskDbContext db,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(user, ct);
            if (!CanManageWorkflowOps(access))
            {
                return Results.Forbid();
            }

            return Results.Ok(await QueryOpsTasksAsync(
                db,
                access,
                page,
                pageSize,
                query => query.Where(t => t.Status == RequestTaskStatus.InProgress && t.Escalated),
                ct));
        });

        group.MapGet("/retries-pending", async (
            [FromServices] HelpdeskDbContext db,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(user, ct);
            if (!CanManageWorkflowOps(access))
            {
                return Results.Forbid();
            }

            return Results.Ok(await QueryOpsTasksAsync(
                db,
                access,
                page,
                pageSize,
                query => query.Where(t => t.Status == RequestTaskStatus.Failed && t.NextRetryAt.HasValue),
                ct));
        })
        .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/critical-failures", async (
            [FromServices] HelpdeskDbContext db,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            [FromQuery] DateTimeOffset? sinceUtc,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(user, ct);
            if (!CanManageWorkflowOps(access))
            {
                return Results.Forbid();
            }

            var since = sinceUtc ?? DateTimeOffset.UtcNow.AddDays(-1);
            return Results.Ok(await QueryOpsTasksAsync(
                db,
                access,
                page,
                pageSize,
                query => query.Where(t =>
                    t.Status == RequestTaskStatus.Failed
                    && t.IsCritical
                    && t.UpdatedAt != null
                    && t.UpdatedAt >= since.UtcDateTime),
                ct));
        });

        group.MapGet("/orchestration/callback-rejections", async (
            [FromServices] HelpdeskDbContext db,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] int? lookbackDays,
            CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(user, ct);
            if (!CanManageWorkflowOps(access))
            {
                return Results.Forbid();
            }

            var safePage = Math.Max(page ?? 1, 1);
            var safePageSize = Math.Clamp(pageSize ?? 25, 1, 200);
            var skip = (safePage - 1) * safePageSize;
            var since = DateTime.UtcNow.AddDays(-Math.Clamp(lookbackDays ?? 7, 1, 90));

            var notifications = db.Notifications.AsNoTracking()
                .Where(n => n.Title == "DomainEvent.Orchestration.External orchestration.CallbackRejected" && n.CreatedUtc >= since);

            if (!access.IsHelpdeskAdmin)
            {
                var requestManagerOrganizationIds = RequestManagerOrganizationIds(access).ToArray();
                notifications = requestManagerOrganizationIds.Length == 0
                    ? notifications.Where(_ => false)
                    : notifications.Where(n => n.TenantId != null && requestManagerOrganizationIds.Contains(n.TenantId));
            }

            var totalCount = await notifications.CountAsync(ct);
            var items = await notifications
                .OrderByDescending(n => n.CreatedUtc)
                .Skip(skip)
                .Take(safePageSize)
                .Select(n => new CallbackRejectionNotification(n.Id, n.CreatedUtc, n.Message, n.CorrelationId))
                .ToListAsync(ct);

            return Results.Ok(new PagedResponse<OrchestrationCallbackRejectionOpsDto>
            {
                Page = safePage,
                PageSize = safePageSize,
                TotalCount = totalCount,
                Items = items.Select(MapOrchestrationCallbackRejection).ToList()
            });
        });

        group.MapGet("/orchestration/binding-issues", async (
            [FromServices] HelpdeskDbContext db,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(user, ct);
            if (!CanManageWorkflowOps(access))
            {
                return Results.Forbid();
            }

            var safePage = Math.Max(page ?? 1, 1);
            var safePageSize = Math.Clamp(pageSize ?? 25, 1, 200);
            var skip = (safePage - 1) * safePageSize;

            var bindings = db.AutomationBindings.AsNoTracking()
                .Where(b => !b.Enabled || b.SyncState != AutomationBindingSyncState.InSync);

            if (!access.IsHelpdeskAdmin)
            {
                var requestManagerOrganizationIds = RequestManagerOrganizationIds(access).ToArray();
                bindings = requestManagerOrganizationIds.Length == 0
                    ? bindings.Where(_ => false)
                    : bindings.Where(b => requestManagerOrganizationIds.Contains(b.OrganizationId));
            }

            var query =
                from binding in bindings
                join requestForm in db.RequestForms.AsNoTracking() on binding.RequestFormId equals requestForm.Id
                join service in db.Services.AsNoTracking() on requestForm.ServiceId equals service.Id into serviceGroup
                from service in serviceGroup.DefaultIfEmpty()
                select new
                {
                    Binding = binding,
                    binding.Id,
                    binding.RequestFormId,
                    RequestFormTitle = requestForm.Title,
                    requestForm.ServiceId,
                    ServiceName = service != null ? service.Name : null,
                    binding.TaskTemplateId,
                    requestForm.JsonSchema,
                    binding.OrchestrationRequestDefinitionId,
                    binding.OrchestrationRequestDefinitionName,
                    binding.OrchestrationJobDefinitionId,
                    binding.OrchestrationJobDefinitionName,
                    binding.SyncState,
                    binding.Enabled,
                    binding.LastSyncedAtUtc,
                    binding.UpdatedAtUtc
                };

            var totalCount = await query.CountAsync(ct);
            var ordered = query.OrderByDescending(x => x.SyncState);
            ordered = db.Database.IsSqlite()
                ? ordered.ThenByDescending(x => EF.Property<long>(x.Binding, "UpdatedAtUtcSortTicks"))
                : ordered.ThenByDescending(x => x.UpdatedAtUtc);
            var items = await ordered
                .ThenBy(x => x.Id)
                .Skip(skip)
                .Take(safePageSize)
                .ToListAsync(ct);

            return Results.Ok(new PagedResponse<AutomationBindingIssueOpsDto>
            {
                Page = safePage,
                PageSize = safePageSize,
                TotalCount = totalCount,
                Items = items.Select(x => new AutomationBindingIssueOpsDto
                {
                    BindingId = x.Id,
                    RequestFormId = x.RequestFormId,
                    RequestFormTitle = x.RequestFormTitle,
                    ServiceId = x.ServiceId,
                    ServiceName = x.ServiceName,
                    TaskTemplateId = x.TaskTemplateId,
                    TaskName = ResolveTaskName(x.JsonSchema, x.TaskTemplateId),
                    OrchestrationRequestDefinitionId = x.OrchestrationRequestDefinitionId,
                    OrchestrationRequestDefinitionName = x.OrchestrationRequestDefinitionName,
                    OrchestrationJobDefinitionId = x.OrchestrationJobDefinitionId,
                    OrchestrationJobDefinitionName = x.OrchestrationJobDefinitionName,
                    SyncState = x.SyncState,
                    Enabled = x.Enabled,
                    LastSyncedAtUtc = x.LastSyncedAtUtc,
                    UpdatedAtUtc = x.UpdatedAtUtc
                }).ToList()
            });
        });

        app.MapGet("/api/v1/metrics/workflows", async (
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(user, ct);
            if (!CanManageWorkflowOps(access))
            {
                return Results.Forbid();
            }

            var tenantFilter = ApplyAllowedOrganizationFilter(db.RequestTasks.AsNoTracking(), access);
            var notificationFilter = ApplyAllowedNotificationFilter(db.Notifications.AsNoTracking(), access);

            var inProgressCount = await tenantFilter.CountAsync(t => t.Status == RequestTaskStatus.InProgress, ct);
            var failedCount = await tenantFilter.CountAsync(t => t.Status == RequestTaskStatus.Failed, ct);
            var now = DateTimeOffset.UtcNow;
            var overdueCount = db.Database.IsSqlite()
                ? await tenantFilter.CountAsync(t =>
                    t.Status == RequestTaskStatus.InProgress
                    && EF.Property<long?>(t, "DueAtUtcTicks") < now.UtcTicks,
                    ct)
                : await tenantFilter.CountAsync(t =>
                    t.Status == RequestTaskStatus.InProgress
                    && t.DueAt.HasValue
                    && t.DueAt < now,
                    ct);

            var escalationsTotal = await notificationFilter.CountAsync(n => n.Title == "DomainEvent.RequestTask.Escalated", ct);
            var retryScheduledTotal = await notificationFilter.CountAsync(n => n.Title == "DomainEvent.RequestTask.RetryScheduled", ct);
            var retryTriggeredTotal = await notificationFilter.CountAsync(n => n.Title == "DomainEvent.RequestTask.RetryTriggered", ct);
            var callbackRejectedTotal = await notificationFilter.CountAsync(n => n.Title == "DomainEvent.Orchestration.External orchestration.CallbackRejected", ct);
            var bindingIssueTotal = access.IsHelpdeskAdmin
                ? await db.AutomationBindings.AsNoTracking().CountAsync(b => !b.Enabled || b.SyncState != AutomationBindingSyncState.InSync, ct)
                : await ApplyAllowedAutomationBindingFilter(db.AutomationBindings.AsNoTracking(), access)
                    .CountAsync(b => !b.Enabled || b.SyncState != AutomationBindingSyncState.InSync, ct);

            var workflowRunsByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var reason in Enum.GetValues<WorkflowRunReason>())
            {
                var reasonValue = (int)reason;
                var count = await notificationFilter.CountAsync(n =>
                    n.Title == "DomainEvent.Workflow.Evaluated"
                    && EF.Functions.Like(n.Message, $"%\"reason\":{reasonValue}%"), ct);
                workflowRunsByReason[reason.ToString()] = count;
            }

            var lines = new List<string>
            {
                "# HELP helpdesk_task_escalations_total Total task escalations.",
                "# TYPE helpdesk_task_escalations_total counter",
                $"helpdesk_task_escalations_total {escalationsTotal}",
                "# HELP helpdesk_task_retries_scheduled_total Total retry schedules.",
                "# TYPE helpdesk_task_retries_scheduled_total counter",
                $"helpdesk_task_retries_scheduled_total {retryScheduledTotal}",
                "# HELP helpdesk_task_retries_triggered_total Total retry triggers.",
                "# TYPE helpdesk_task_retries_triggered_total counter",
                $"helpdesk_task_retries_triggered_total {retryTriggeredTotal}",
                "# HELP helpdesk_orchestration_callback_rejections_total Total external orchestration callbacks rejected by Helpdesk.",
                "# TYPE helpdesk_orchestration_callback_rejections_total counter",
                $"helpdesk_orchestration_callback_rejections_total {callbackRejectedTotal}",
                "# HELP helpdesk_orchestration_binding_issues Current non-healthy external orchestration automation bindings.",
                "# TYPE helpdesk_orchestration_binding_issues gauge",
                $"helpdesk_orchestration_binding_issues {bindingIssueTotal}",
                "# HELP helpdesk_tasks_overdue Current overdue tasks.",
                "# TYPE helpdesk_tasks_overdue gauge",
                $"helpdesk_tasks_overdue {overdueCount}",
                "# HELP helpdesk_tasks_in_progress Current in-progress tasks.",
                "# TYPE helpdesk_tasks_in_progress gauge",
                $"helpdesk_tasks_in_progress {inProgressCount}",
                "# HELP helpdesk_tasks_failed Current failed tasks.",
                "# TYPE helpdesk_tasks_failed gauge",
                $"helpdesk_tasks_failed {failedCount}",
                "# HELP helpdesk_workflow_runs_total Total workflow runs by reason.",
                "# TYPE helpdesk_workflow_runs_total counter"
            };

            lines.AddRange(workflowRunsByReason.Select(x =>
                $"helpdesk_workflow_runs_total{{reason=\"{x.Key}\"}} {x.Value}"));

            return Results.Text(string.Join('\n', lines), "text/plain");
        })
        .WithTags("Workflow Ops")
        .RequireAuthorization();
    }

    private static async Task<PagedResponse<TaskOpsRowDto>> QueryOpsTasksAsync(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        int? page,
        int? pageSize,
        Func<IQueryable<RequestTask>, IQueryable<RequestTask>> filter,
        CancellationToken ct)
    {
        var safePage = Math.Max(page ?? 1, 1);
        var safePageSize = Math.Clamp(pageSize ?? 25, 1, 200);
        var skip = (safePage - 1) * safePageSize;

        var tasksQuery = ApplyAllowedOrganizationFilter(db.RequestTasks.AsNoTracking(), access);
        var isSqlite = db.Database.IsSqlite();
        tasksQuery = filter(tasksQuery);
        if (isSqlite)
        {
            tasksQuery = tasksQuery
                .OrderByDescending(task => EF.Property<long?>(task, "NextRetryAtUtcTicks") ?? EF.Property<long?>(task, "DueAtUtcTicks") ?? long.MinValue)
                .ThenBy(task => task.RequestId)
                .ThenBy(task => task.Id);
        }

        var query =
            from task in tasksQuery
            join request in db.Requests.AsNoTracking() on task.RequestId equals request.Id
            join assignedUser in db.Users.AsNoTracking() on task.AssignedToId equals assignedUser.Id into assignedUsers
            from assignedUser in assignedUsers.DefaultIfEmpty()
            select new TaskOpsRowDto
            {
                TaskId = task.Id,
                RequestId = task.RequestId,
                RequestTrackingId = request.TrackingId,
                TaskName = task.Name,
                AssignedToId = task.AssignedToId,
                AssignedToDisplayName = assignedUser != null ? assignedUser.Name : null,
                DueAt = task.DueAt,
                NextRetryAt = task.NextRetryAt,
                Status = task.Status,
                Type = task.Type,
                Escalated = task.Escalated,
                IsCritical = task.IsCritical,
                FailurePolicy = task.FailurePolicy,
                FailureReason = task.FailureReason,
                RetryCount = task.RetryCount
            };

        int totalCount;
        List<TaskOpsRowDto> items;
        totalCount = await query.CountAsync(ct);
        items = isSqlite
            ? await query
                .Skip(skip)
                .Take(safePageSize)
                .ToListAsync(ct)
            : await query
                .OrderByDescending(t => t.NextRetryAt ?? t.DueAt ?? DateTimeOffset.MinValue)
                .ThenBy(t => t.RequestTrackingId)
                .ThenBy(t => t.TaskId)
                .Skip(skip)
                .Take(safePageSize)
                .ToListAsync(ct);

        return new PagedResponse<TaskOpsRowDto>
        {
            Page = safePage,
            PageSize = safePageSize,
            TotalCount = totalCount,
            Items = items
        };
    }

    private static bool CanManageWorkflowOps(CurrentUserAccessProfile access) =>
        access.IsHelpdeskAdmin || access.HasPermission(HelpdeskPermissions.RequestRead) || access.HasPermission(HelpdeskPermissions.RequestWrite) || access.HasPermission(HelpdeskPermissions.RequestManager);

    private static IReadOnlySet<string> RequestManagerOrganizationIds(CurrentUserAccessProfile access) =>
        access.OrganizationIdsForAny(HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager);

    private static IQueryable<RequestTask> ApplyAllowedOrganizationFilter(
        IQueryable<RequestTask> query,
        CurrentUserAccessProfile access)
    {
        if (access.IsHelpdeskAdmin)
        {
            return query;
        }

        var requestManagerOrganizationIds = RequestManagerOrganizationIds(access).ToArray();
        return requestManagerOrganizationIds.Length == 0
            ? query.Where(_ => false)
            : query.Where(t => t.OrganizationId != null && requestManagerOrganizationIds.Contains(t.OrganizationId));
    }

    private static IQueryable<NotificationEntity> ApplyAllowedNotificationFilter(
        IQueryable<NotificationEntity> query,
        CurrentUserAccessProfile access)
    {
        if (access.IsHelpdeskAdmin)
        {
            return query;
        }

        var requestManagerOrganizationIds = RequestManagerOrganizationIds(access).ToArray();
        return requestManagerOrganizationIds.Length == 0
            ? query.Where(_ => false)
            : query.Where(n => n.TenantId != null && requestManagerOrganizationIds.Contains(n.TenantId));
    }

    private static IQueryable<AutomationBinding> ApplyAllowedAutomationBindingFilter(
        IQueryable<AutomationBinding> query,
        CurrentUserAccessProfile access)
    {
        if (access.IsHelpdeskAdmin)
        {
            return query;
        }

        var requestManagerOrganizationIds = RequestManagerOrganizationIds(access).ToArray();
        return requestManagerOrganizationIds.Length == 0
            ? query.Where(_ => false)
            : query.Where(b => requestManagerOrganizationIds.Contains(b.OrganizationId));
    }

    private sealed record CallbackRejectionNotification(Guid Id, DateTime CreatedUtc, string Message, string? CorrelationId);

    private static OrchestrationCallbackRejectionOpsDto MapOrchestrationCallbackRejection(CallbackRejectionNotification notification)
    {
        var result = new OrchestrationCallbackRejectionOpsDto
        {
            NotificationId = notification.Id,
            CreatedUtc = notification.CreatedUtc,
            ReasonCode = "Unknown",
            CorrelationId = notification.CorrelationId
        };

        try
        {
            using var document = JsonDocument.Parse(notification.Message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;

            if (root.TryGetProperty("reasonCode", out var reason) && reason.ValueKind == JsonValueKind.String
                && reason.GetString() is { } code && !string.IsNullOrWhiteSpace(code))
                result.ReasonCode = code;

            if (root.TryGetProperty("callerClientId", out var caller) && caller.ValueKind == JsonValueKind.String)
                result.CallerClientId = caller.GetString();
        }
        catch (JsonException)
        {
            return result;
        }
        return result;
    }

    private static string? ResolveTaskName(JsonDocument schema, Guid taskTemplateId)
    {
        if (!schema.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var task in tasks.EnumerateArray())
        {
            if (!task.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!Guid.TryParse(idValue.GetString(), out var id) || id != taskTemplateId)
            {
                continue;
            }

            if (task.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                return nameValue.GetString();
            }

            return null;
        }

        return null;
    }
}
