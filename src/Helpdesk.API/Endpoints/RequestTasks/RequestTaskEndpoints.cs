using System.ComponentModel.DataAnnotations;
using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.RequestTasks;

public static class RequestTaskEndpoints
{
    public static void MapRequestTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/request-tasks")
            .WithTags("Request Tasks")
            .RequireAuthorization();

        MapCrud(group);
        MapLifecycle(group);

        // Legacy route kept for backward compatibility.
        var legacyGroup = app.MapGroup("/api/v1/requestTasks")
            .WithTags("Request Tasks")
            .RequireAuthorization()
            .ExcludeFromDescription();
        MapCrud(legacyGroup);
    }

    private static void MapCrud(RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            [FromServices] HelpdeskDbContext db,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IDomainEventPublisher domainEvents,
            [FromServices] ICorrelationContext correlationContext,
            ClaimsPrincipal user,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] bool? assignedToMe,
            [FromQuery] string? assignedToId,
            [FromQuery] RequestTaskStatus? status,
            [FromQuery] RequestTaskType? type,
            [FromQuery] string? requestId,
            [FromQuery] string? serviceId,
            [FromQuery] string? q,
            [FromQuery] bool? historicOnly,
            CancellationToken token,
            [FromQuery] bool includeTotal = true) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanReadTasks(access))
            {
                return Results.Forbid();
            }

            var effectivePage = Math.Max(page ?? 1, 1);
            var effectivePageSize = Math.Clamp(pageSize ?? 10, 1, 100);
            var shouldFilterAssignedToMe = assignedToMe ?? false;
            var currentUserId = tenant.UserId;

            var query =
                from task in db.RequestTasks.AsNoTracking()
                join request in db.Requests.AsNoTracking() on task.RequestId equals request.Id
                join assignedUser in db.Users.AsNoTracking() on task.AssignedToId equals assignedUser.Id into assignedUsers
                from assignedUser in assignedUsers.DefaultIfEmpty()
                select new
                {
                    Task = task,
                    Request = request,
                    AssignedUserName = assignedUser != null ? assignedUser.Name : null
                };

            if (!access.IsHelpdeskAdmin)
            {
                var requestManagerOrganizationIds = RequestReaderOrganizationIds(access).ToArray();
                if (requestManagerOrganizationIds.Length == 0)
                {
                    return Results.Ok(new PagedResponse<RequestTaskListItemDto>
                    {
                        Page = effectivePage,
                        PageSize = effectivePageSize,
                        TotalCount = 0,
                        Items = new List<RequestTaskListItemDto>()
                    });
                }

                query = query.Where(x => x.Task.OrganizationId != null && requestManagerOrganizationIds.Contains(x.Task.OrganizationId));
            }

            if (shouldFilterAssignedToMe)
            {
                if (string.IsNullOrWhiteSpace(currentUserId))
                {
                    return Results.Ok(new PagedResponse<RequestTaskListItemDto>
                    {
                        Page = effectivePage,
                        PageSize = effectivePageSize,
                        TotalCount = 0,
                        Items = new List<RequestTaskListItemDto>()
                    });
                }

                query = query.Where(x => x.Task.AssignedToId == currentUserId);
            }

            if (!string.IsNullOrWhiteSpace(assignedToId))
            {
                query = query.Where(x => x.Task.AssignedToId == assignedToId);
            }

            if (historicOnly == true)
            {
                query = query.Where(x => x.Task.Status == RequestTaskStatus.Completed
                    || x.Task.Status == RequestTaskStatus.Skipped
                    || x.Task.Status == RequestTaskStatus.Cancelled);
            }
            else
            {
                query = query.Where(x => x.Task.Status != RequestTaskStatus.Completed
                    && x.Task.Status != RequestTaskStatus.Skipped
                    && x.Task.Status != RequestTaskStatus.Cancelled);
            }

            if (status is not null)
            {
                query = query.Where(x => x.Task.Status == status);
            }

            if (type is not null)
            {
                query = query.Where(x => x.Task.Type == type);
            }

            if (!string.IsNullOrWhiteSpace(requestId))
            {
                query = query.Where(x => x.Task.RequestId == requestId);
            }

            if (!string.IsNullOrWhiteSpace(serviceId))
            {
                query = query.Where(x => x.Task.ServiceId == serviceId);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? query.Where(x => EF.Functions.ILike(x.Task.Title, like) || EF.Functions.ILike(x.Task.Description, like) || EF.Functions.ILike(x.Request.Title, like) || EF.Functions.ILike(x.Request.Description, like) || EF.Functions.ILike(x.Request.TrackingId, like))
                    : query.Where(x => EF.Functions.Like(x.Task.Title, like) || EF.Functions.Like(x.Task.Description, like) || EF.Functions.Like(x.Request.Title, like) || EF.Functions.Like(x.Request.Description, like) || EF.Functions.Like(x.Request.TrackingId, like));
            }

            var totalCount = includeTotal ? await query.CountAsync(token) : 0;
            var taskRows = await query
                .OrderBy(x => x.Task.Status == RequestTaskStatus.InProgress ? 0 : 1)
                .ThenByDescending(x => x.Task.CreatedAt)
                .ThenBy(x => x.Task.Id)
                // Preserve the direct API's page normalization without allowing a
                // large positive page to wrap around into an earlier page.
                .Skip((int)Math.Min((long)(effectivePage - 1) * effectivePageSize, int.MaxValue))
                .Take(effectivePageSize)
                .ToListAsync(token);

            var bindingStates = await LoadAutomationBindingStatesAsync(
                db,
                taskRows.Select(x => (RequestFormId: x.Request.RequestFormId, TemplateId: (string?)x.Task.TemplateId)),
                token);

            var items = taskRows
                .Select(x =>
                {
                    bindingStates.TryGetValue(BuildBindingLookupKey(x.Request.RequestFormId, x.Task.TemplateId), out var bindingState);
                    return new RequestTaskListItemDto
                    {
                        Id = x.Task.Id,
                        RequestId = x.Task.RequestId,
                        OrganizationId = x.Task.OrganizationId,
                        RequestTrackingId = x.Request.TrackingId,
                        RequestTitle = x.Request.Title,
                        Name = x.Task.Name,
                        Type = x.Task.Type,
                        Status = x.Task.Status,
                        AssignedToId = x.Task.AssignedToId,
                        AssignedToDisplayName = x.AssignedUserName,
                        Order = x.Task.Order,
                        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(x.Task.CreatedAt, DateTimeKind.Utc)),
                        StartedAt = x.Task.StartedAt,
                        DueAt = x.Task.DueAt,
                        NextRetryAt = x.Task.NextRetryAt,
                        RetryCount = x.Task.RetryCount,
                        FailureReason = x.Task.FailureReason,
                        OrchestrationExternalRequestId = x.Task.OrchestrationExternalRequestId,
                        OrchestrationExternalRunId = x.Task.OrchestrationExternalRunId,
                        LastAutomationStatus = x.Task.LastAutomationStatus,
                        ExpectedRuntimeSeconds = x.Task.ExpectedRuntimeSeconds,
                        GraceSeconds = x.Task.GraceSeconds,
                        HardTimeoutSeconds = x.Task.HardTimeoutSeconds,
                        TimeoutIncidentId = x.Task.TimeoutIncidentId,
                        AutomationBindingEnabled = bindingState?.Enabled,
                        AutomationBindingSyncState = bindingState?.SyncState,
                        AutomationReadyToStart = bindingState?.ReadyToStart ?? true,
                        AutomationBlockReason = bindingState?.BlockReason
                    };
                })
                .ToList();

            await domainEvents.PublishAsync(
                new TasksDashboardViewedEvent(
                    tenant.UserId,
                    shouldFilterAssignedToMe,
                    assignedToId,
                    status?.ToString(),
                    type?.ToString(),
                    requestId,
                    serviceId,
                    q,
                    items.Count,
                    tenant.TenantId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlationContext)),
                token);

            return Results.Ok(new PagedResponse<RequestTaskListItemDto>
            {
                Page = effectivePage,
                PageSize = effectivePageSize,
                TotalCount = includeTotal ? totalCount : items.Count,
                Items = items
            });
        });

        group.MapGet("/{id}", async (
            [FromRoute] string id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanReadTasks(access))
            {
                return Results.Forbid();
            }

            var task = await db.RequestTasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, token);
            if (task is null)
            {
                return Results.Problem("Task not found", statusCode: 404);
            }

            return CanReadOrganization(access, task.OrganizationId)
                ? Results.Ok(task)
                : Results.Forbid();
        });

        group.MapPost("/", async (
            [FromBody] CreateRequestTaskDto dto,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRequestSender sender,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanManageTasks(access))
            {
                return Results.Forbid();
            }

            if (dto.Priority is { } priority && !Enum.IsDefined(priority))
            {
                return Results.BadRequest("Invalid ticket priority.");
            }

            var parent = await db.Requests.AsNoTracking()
                .IgnoreQueryFilters()
                .Where(request => request.Id == dto.RequestId)
                .Select(request => new { request.OrganizationId, request.CustomerId })
                .FirstOrDefaultAsync(token);
            if (parent is null || !CanAccessOrganization(access, parent.OrganizationId))
            {
                return Results.NotFound();
            }

            if ((!string.IsNullOrWhiteSpace(dto.OrganizationId) &&
                 !string.Equals(dto.OrganizationId, parent.OrganizationId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(dto.CustomerId) &&
                 !string.Equals(dto.CustomerId, parent.CustomerId, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.BadRequest("Request-task organization and customer must match the parent request.");
            }

            var command = new CreateRequestTaskCommand(
                dto.Title,
                dto.Description,
                dto.RequestId,
                dto.Priority,
                parent.CustomerId,
                parent.OrganizationId,
                dto.LinkedAssetIds,
                dto.Attachments,
                dto.DueDate);
            var created = await sender.Send(command);
            return Results.Created($"/api/v1/request-tasks/{created.Id}", created);
        });

        group.MapPut("/{id}", async (
            [FromRoute] string id,
            [FromBody] RequestTask task,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<RequestTask> repo,
            [FromServices] IRepository<User> users,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanManageTasks(access))
            {
                return Results.Forbid();
            }

            var existing = await db.RequestTasks.AsNoTracking()
                .Where(x => x.Id == id)
                .FirstOrDefaultAsync(token);
            if (existing is null)
            {
                return Results.Problem("Task not found", statusCode: 404);
            }

            if (!CanAccessOrganization(access, existing.OrganizationId))
            {
                return Results.Forbid();
            }

            if (!Enum.IsDefined(task.Priority))
            {
                return Results.BadRequest("Invalid ticket priority.");
            }

            if (!string.Equals(task.OrganizationId, existing.OrganizationId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(task.RequestId, existing.RequestId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(task.CustomerId, existing.CustomerId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Request-task organization, customer, and parent request cannot be changed.");
            }

            var assignmentValidation = await ValidateAssigneeAsync(users, existing.OrganizationId, task.AssignedToId);
            if (assignmentValidation is not null)
            {
                return assignmentValidation;
            }

            // Full-object input must never overwrite workflow status, task type, approvals,
            // automation bindings, or execution results. Those have dedicated state transitions.
            existing.Title = task.Title;
            existing.Description = task.Description;
            existing.Priority = task.Priority;
            existing.AssignedToId = task.AssignedToId;
            existing.LinkedAssetIds = task.LinkedAssetIds;
            existing.Attachments = task.Attachments;
            existing.DueDate = task.DueDate;
            existing.UpdatedAt = DateTime.UtcNow;
            var updated = await repo.UpdateAsync(existing);
            return updated is null
                ? Results.Problem("Task not found", statusCode: 404)
                : Results.Ok(updated);
        });

        group.MapMethods("/{id}", [HttpMethods.Patch], async (
            [FromRoute] string id,
            [FromBody] UpdateRequestTaskDto dto,
            [FromServices] IRepository<RequestTask> repo,
            [FromServices] IRepository<User> users,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanManageTasks(access))
            {
                return Results.Forbid();
            }

            var existing = await repo.GetAsync(id);
            if (existing is null)
            {
                return Results.Problem("Task not found", statusCode: 404);
            }

            if (!CanAccessOrganization(access, existing.OrganizationId))
            {
                return Results.Forbid();
            }

            if (!dto.HasUpdate)
            {
                return Results.BadRequest("Provide at least one supported request-task update field.");
            }

            if (dto.Priority is { } priority && !Enum.IsDefined(priority))
            {
                return Results.BadRequest("Invalid ticket priority.");
            }

            if (dto.ClearAssignment && dto.AssignedToId is not null)
            {
                return Results.BadRequest("clearAssignment cannot be combined with assignedToId.");
            }

            if (dto.ClearDueDate && dto.DueDate.HasValue)
            {
                return Results.BadRequest("clearDueDate cannot be combined with dueDate.");
            }

            if (!string.IsNullOrWhiteSpace(dto.AssignedToId))
            {
                var assignmentValidation = await ValidateAssigneeAsync(users, existing.OrganizationId, dto.AssignedToId);
                if (assignmentValidation is not null)
                {
                    return assignmentValidation;
                }
            }

            if (dto.Title is not null)
            {
                if (string.IsNullOrWhiteSpace(dto.Title)) return Results.BadRequest("Title cannot be empty.");
                existing.Title = dto.Title.Trim();
            }

            if (dto.Description is not null)
            {
                if (string.IsNullOrWhiteSpace(dto.Description)) return Results.BadRequest("Description cannot be empty.");
                existing.Description = dto.Description.Trim();
            }

            if (dto.Priority.HasValue) existing.Priority = dto.Priority.Value;
            if (dto.AssignedToId is not null) existing.AssignedToId = string.IsNullOrWhiteSpace(dto.AssignedToId) ? null : dto.AssignedToId.Trim();
            if (dto.ClearAssignment) existing.AssignedToId = null;
            if (dto.LinkedAssetIds is not null) existing.LinkedAssetIds = NormalizeIds(dto.LinkedAssetIds);
            if (dto.Attachments is not null) existing.Attachments = NormalizeIds(dto.Attachments);
            if (dto.DueDate.HasValue) existing.DueDate = dto.DueDate.Value;
            if (dto.ClearDueDate) existing.DueDate = null;
            existing.UpdatedAt = DateTime.UtcNow;

            var updated = await repo.UpdateAsync(existing);
            return Results.Ok(updated);
        });

        group.MapDelete("/{id}", async (
            [FromRoute] string id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<RequestTask> repo,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!access.IsHelpdeskAdmin && !access.HasPermission(HelpdeskPermissions.RequestDelete) && !access.HasPermission(HelpdeskPermissions.RequestManager))
            {
                return Results.Forbid();
            }

            var organizationId = await db.RequestTasks.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => x.OrganizationId)
                .FirstOrDefaultAsync(token);
            if (organizationId is null)
            {
                return Results.Problem("Task not found", statusCode: 404);
            }

            if (!access.CanDeleteRequest(organizationId))
            {
                return Results.Forbid();
            }

            return await repo.DeleteAsync(id)
                ? Results.NoContent()
                : Results.Problem("Task not found", statusCode: 404);
        });
    }

    private static void MapLifecycle(RouteGroupBuilder group)
    {
        group.MapPost("/{id}/start", async (
            [FromRoute] string id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRequestTaskLifecycleService lifecycleService,
            [FromServices] IWorkflowEngine workflowEngine,
            [FromServices] ICorrelationContext correlationContext,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!await CanAccessTaskAsync(db, access, id, token))
            {
                return Results.Forbid();
            }

            RequestTask task;
            try
            {
                task = await lifecycleService.StartAsync(id, token);
            }
            catch (KeyNotFoundException)
            {
                return Results.Problem("Task not found", statusCode: 404);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(task.RequestId))
            {
                await workflowEngine.RunAsync(task.RequestId, WorkflowRunReason.ManualTrigger, token);
            }

            return Results.Ok(ToTransitionDto(task, GetCorrelationId(correlationContext)));
        }).Produces<RequestTaskTransitionDto>();

        group.MapPost("/{id}/complete", async (
            [FromRoute] string id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRequestTaskLifecycleService lifecycleService,
            [FromServices] IWorkflowEngine workflowEngine,
            [FromServices] ICorrelationContext correlationContext,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!await CanAccessTaskAsync(db, access, id, token))
            {
                return Results.Forbid();
            }

            RequestTask task;
            try
            {
                task = await lifecycleService.CompleteAsync(id, null, token);
            }
            catch (KeyNotFoundException)
            {
                return Results.Problem("Task not found", statusCode: 404);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(task.RequestId))
            {
                await workflowEngine.RunAsync(task.RequestId, WorkflowRunReason.TaskCompleted, token);
            }

            return Results.Ok(ToTransitionDto(task, GetCorrelationId(correlationContext)));
        }).Produces<RequestTaskTransitionDto>();

        group.MapPost("/{id}/fail", async (
            [FromRoute] string id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRequestTaskLifecycleService lifecycleService,
            [FromServices] IWorkflowEngine workflowEngine,
            [FromServices] ICorrelationContext correlationContext,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!await CanAccessTaskAsync(db, access, id, token))
            {
                return Results.Forbid();
            }

            RequestTask task;
            try
            {
                task = await lifecycleService.FailAsync(id, "Failed manually.", token);
            }
            catch (KeyNotFoundException)
            {
                return Results.Problem("Task not found", statusCode: 404);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(task.RequestId))
            {
                await workflowEngine.RunAsync(task.RequestId, WorkflowRunReason.TaskFailed, token);
            }

            return Results.Ok(ToTransitionDto(task, GetCorrelationId(correlationContext)));
        }).Produces<RequestTaskTransitionDto>();

        group.MapPost("/{id}/retry", async (
            [FromRoute] string id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            [FromServices] IRequestTaskLifecycleService lifecycleService,
            [FromServices] IWorkflowEngine workflowEngine,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!await CanAccessTaskAsync(db, access, id, token)) return Results.Forbid();
            RequestTask task;
            try
            {
                task = await lifecycleService.RetryAsync(id, token);
            }
            catch (KeyNotFoundException)
            {
                return Results.Problem("Task not found", statusCode: 404);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(task.RequestId))
            {
                await workflowEngine.RunAsync(task.RequestId, WorkflowRunReason.TaskRetried, token);
            }

            return Results.NoContent();
        });

        group.MapPost("/bulk/start", async (
            [FromBody] BulkRequestTaskActionDto dto,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<RequestTask> repo,
            [FromServices] IRequestTaskLifecycleService lifecycleService,
            [FromServices] IWorkflowEngine workflowEngine,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanManageTasks(access) && !access.HasPermission(HelpdeskPermissions.RequestExecute))
            {
                return Results.Forbid();
            }

            var ids = NormalizeIds(dto.Ids);
            if (ids.Count == 0)
            {
                return Results.BadRequest("No ids.");
            }

            ids = await FilterVisibleTaskIdsAsync(db, access, ids, token);
            var updated = 0;
            var touchedRequestIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var task = await repo.GetAsync(id);
                if (task is null || !CanTransitionTask(access, task))
                {
                    continue;
                }

                try
                {
                    task = await lifecycleService.StartAsync(id, token);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(task.RequestId))
                {
                    touchedRequestIds.Add(task.RequestId);
                }
                updated++;
            }

            foreach (var requestId in touchedRequestIds)
            {
                await workflowEngine.RunAsync(requestId, WorkflowRunReason.ManualTrigger, token);
            }

            return Results.Ok(new { updated });
        });

        group.MapPost("/bulk/complete", async (
            [FromBody] BulkRequestTaskActionDto dto,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<RequestTask> repo,
            [FromServices] IRequestTaskLifecycleService lifecycleService,
            [FromServices] IWorkflowEngine workflowEngine,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanManageTasks(access) && !access.HasPermission(HelpdeskPermissions.RequestExecute))
            {
                return Results.Forbid();
            }

            var ids = NormalizeIds(dto.Ids);
            if (ids.Count == 0)
            {
                return Results.BadRequest("No ids.");
            }

            ids = await FilterVisibleTaskIdsAsync(db, access, ids, token);
            var updated = 0;
            var touchedRequestIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var task = await repo.GetAsync(id);
                if (task is null || !CanTransitionTask(access, task)
                    || task.Type != RequestTaskType.Manual
                    || task.Status != RequestTaskStatus.InProgress)
                {
                    continue;
                }

                try
                {
                    task = await lifecycleService.CompleteAsync(id, null, token);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(task.RequestId))
                {
                    touchedRequestIds.Add(task.RequestId);
                }
                updated++;
            }

            foreach (var requestId in touchedRequestIds)
            {
                await workflowEngine.RunAsync(requestId, WorkflowRunReason.TaskCompleted, token);
            }

            return Results.Ok(new { updated });
        });

        group.MapPost("/bulk/retry", async (
            [FromBody] BulkRequestTaskActionDto dto,
            [FromServices] IRequestTaskLifecycleService lifecycleService,
            [FromServices] IWorkflowEngine workflowEngine,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!access.IsHelpdeskAdmin && !access.HasPermission(HelpdeskPermissions.RequestExecute) && !access.HasPermission(HelpdeskPermissions.RequestManager))
            {
                return Results.Forbid();
            }

            var ids = NormalizeIds(dto.Ids);
            if (ids.Count == 0)
            {
                return Results.BadRequest("No ids.");
            }

            var updated = 0;
            var touchedRequestIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!await CanAccessTaskAsync(db, access, id, token)) continue;
                RequestTask task;
                try
                {
                    task = await lifecycleService.RetryAsync(id, token);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(task.RequestId))
                {
                    touchedRequestIds.Add(task.RequestId);
                }
                updated++;
            }

            foreach (var requestId in touchedRequestIds)
            {
                await workflowEngine.RunAsync(requestId, WorkflowRunReason.TaskRetried, token);
            }

            return Results.Ok(new { updated });
        });
    }

    private static async Task<Dictionary<string, RequestTaskBindingStateProjection>> LoadAutomationBindingStatesAsync(
        HelpdeskDbContext db,
        IEnumerable<(string? RequestFormId, string? TemplateId)> keys,
        CancellationToken cancellationToken)
    {
        var requested = keys
            .Where(x => !string.IsNullOrWhiteSpace(x.RequestFormId) && Guid.TryParse(x.TemplateId, out _))
            .Select(x => new { RequestFormId = x.RequestFormId!, TaskTemplateId = Guid.Parse(x.TemplateId!) })
            .Distinct()
            .ToList();
        if (requested.Count == 0)
        {
            return new Dictionary<string, RequestTaskBindingStateProjection>(StringComparer.OrdinalIgnoreCase);
        }

        var requestFormIds = requested.Select(x => x.RequestFormId).Distinct().ToList();
        var taskTemplateIds = requested.Select(x => x.TaskTemplateId).Distinct().ToList();
        var bindings = await db.AutomationBindings.AsNoTracking()
            .Where(x => requestFormIds.Contains(x.RequestFormId) && taskTemplateIds.Contains(x.TaskTemplateId))
            .ToListAsync(cancellationToken);

        return bindings.ToDictionary(
            x => BuildBindingLookupKey(x.RequestFormId, x.TaskTemplateId.ToString("D")),
            x => new RequestTaskBindingStateProjection(
                x.Enabled,
                x.SyncState,
                BuildAutomationBlockReason(x.Enabled, x.SyncState)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildBindingLookupKey(string? requestFormId, string? templateId)
    {
        return $"{requestFormId ?? string.Empty}:{templateId ?? string.Empty}";
    }

    private static string? BuildAutomationBlockReason(bool enabled, AutomationBindingSyncState syncState)
    {
        if (!enabled || syncState != AutomationBindingSyncState.Broken)
        {
            return null;
        }

        return syncState switch
        {
            AutomationBindingSyncState.Broken => "Bound External orchestration target is broken or unavailable and cannot execute.",
            _ => null
        };
    }

    private static RequestTaskTransitionDto ToTransitionDto(RequestTask task, string correlationId) => new()
    {
        Id = task.Id,
        Title = task.Title,
        Description = task.Description,
        State = task.State,
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt,
        AssignedToId = task.AssignedToId,
        CustomerId = task.CustomerId,
        OrganizationId = task.OrganizationId,
        ServiceId = task.ServiceId,
        Priority = task.Priority,
        LinkedAssetIds = task.LinkedAssetIds,
        SlaPolicyId = task.SlaPolicyId,
        DueDate = task.DueDate,
        ClosedAt = task.ClosedAt,
        EmailExclusionReason = task.EmailExclusionReason,
        Attachments = task.Attachments,
        RequesterEmail = task.RequesterEmail,
        CcRecipients = task.CcRecipients,
        LastViewedByCustomerAt = task.LastViewedByCustomerAt,
        TrackingId = task.TrackingId,
        Replies = task.Replies,
        TimeSpentHours = task.TimeSpentHours,
        LastReplierName = task.LastReplierName,
        RequestId = task.RequestId,
        TemplateId = task.TemplateId,
        Type = task.Type,
        Status = task.Status,
        StartedAt = task.StartedAt,
        CompletedAt = task.CompletedAt,
        TaskSlaMinutes = task.TaskSlaMinutes,
        EscalateAfterMinutes = task.EscalateAfterMinutes,
        EscalationUserId = task.EscalationUserId,
        EscalationRole = task.EscalationRole,
        SlaStartedAt = task.SlaStartedAt,
        DueAt = task.DueAt,
        Escalated = task.Escalated,
        EscalatedAt = task.EscalatedAt,
        SlaBreached = task.SlaBreached,
        IsBlocked = task.IsBlocked,
        UnblockedAt = task.UnblockedAt,
        ConditionExpression = task.ConditionExpression,
        RetryCount = task.RetryCount,
        NextRetryAt = task.NextRetryAt,
        FailureReason = task.FailureReason,
        IsCritical = task.IsCritical,
        FailurePolicy = task.FailurePolicy,
        MaxRetries = task.MaxRetries,
        RetryDelayMinutes = task.RetryDelayMinutes,
        AutomationBindingId = task.AutomationBindingId,
        OrchestrationRequestDefinitionId = task.OrchestrationRequestDefinitionId,
        OrchestrationJobDefinitionId = task.OrchestrationJobDefinitionId,
        OrchestrationExternalRequestId = task.OrchestrationExternalRequestId,
        OrchestrationExternalRunId = task.OrchestrationExternalRunId,
        LastAutomationStatus = task.LastAutomationStatus,
        LastAutomationUpdatedAt = task.LastAutomationUpdatedAt,
        ExpectedRuntimeSeconds = task.ExpectedRuntimeSeconds,
        GraceSeconds = task.GraceSeconds,
        HardTimeoutSeconds = task.HardTimeoutSeconds,
        TimeoutIncidentId = task.TimeoutIncidentId,
        OrchestratorExecutionId = task.OrchestratorExecutionId,
        OrchestrationLinkId = task.OrchestrationLinkId,
        OrchestrationPeerInstanceId = task.OrchestrationPeerInstanceId,
        OrchestrationLinkRevision = task.OrchestrationLinkRevision,
        ResultJson = task.ResultJson,
        Order = task.Order,
        Name = task.Name,
        CorrelationId = correlationId
    };

    private static string GetCorrelationId(ICorrelationContext correlationContext)
    {
        return correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private static bool CanReadTasks(CurrentUserAccessProfile access) =>
        CanManageTasks(access) || access.HasPermission(HelpdeskPermissions.RequestRead);

    private static bool CanReadOrganization(CurrentUserAccessProfile access, string? organizationId) =>
        access.IsHelpdeskAdmin || organizationId is not null && RequestReaderOrganizationIds(access).Contains(organizationId);

    private static IReadOnlySet<string> RequestReaderOrganizationIds(CurrentUserAccessProfile access) =>
        access.OrganizationIdsForAny(HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager);

    private static bool CanManageTasks(CurrentUserAccessProfile access) =>
        access.IsHelpdeskAdmin || access.HasPermission(HelpdeskPermissions.RequestWrite) || access.HasPermission(HelpdeskPermissions.RequestManager);

    private static bool CanAccessOrganization(CurrentUserAccessProfile access, string? organizationId) =>
        access.IsHelpdeskAdmin
        || (!string.IsNullOrWhiteSpace(organizationId) && RequestManagerOrganizationIds(access).Contains(organizationId));

    private static async Task<IResult?> ValidateAssigneeAsync(
        IRepository<User> users,
        string? organizationId,
        string? assignedToId)
    {
        if (string.IsNullOrWhiteSpace(assignedToId))
        {
            return null;
        }

        var assignee = await users.GetAsync(assignedToId);
        if (assignee is null || !string.Equals(assignee.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Assigned user must belong to the request-task organization.");
        }

        return null;
    }

    private static IReadOnlySet<string> RequestManagerOrganizationIds(CurrentUserAccessProfile access) =>
        access.OrganizationIdsForAny(HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager);

    private static async Task<bool> CanAccessTaskAsync(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        string taskId,
        CancellationToken token)
    {
        var task = await db.RequestTasks.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == taskId, token);
        return task is not null && CanTransitionTask(access, task);
    }

    private static bool CanTransitionTask(CurrentUserAccessProfile access, RequestTask task) => task.Type switch
    {
        RequestTaskType.Manual => access.CanManageRequest(task.OrganizationId),
        RequestTaskType.Automation => access.CanExecuteRequest(task.OrganizationId),
        // Approval tasks are decided by their designated approver using the signed review flow.
        _ => false
    };

    private static async Task<List<string>> FilterVisibleTaskIdsAsync(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        IReadOnlyCollection<string> ids,
        CancellationToken token)
    {
        if (ids.Count == 0)
        {
            return new List<string>();
        }

        var query = db.RequestTasks.AsNoTracking().Where(x => ids.Contains(x.Id));
        if (!access.IsHelpdeskAdmin)
        {
            var requestManagerOrganizationIds = access.OrganizationIdsForAny(HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestExecute, HelpdeskPermissions.RequestManager).ToArray();
            if (requestManagerOrganizationIds.Length == 0)
            {
                return new List<string>();
            }

            query = query.Where(x => x.OrganizationId != null && requestManagerOrganizationIds.Contains(x.OrganizationId));
        }

        return await query.Select(x => x.Id).ToListAsync(token);
    }

    private static bool IsHelpdeskAdmin(ClaimsPrincipal user, ITenantContext tenant)
    {
        if (tenant.IsHelpdeskAdmin || user.IsInRole("HelpdeskAdmin"))
        {
            return true;
        }

        return user.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "roles")
            && string.Equals(c.Value, "HelpdeskAdmin", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> NormalizeIds(IEnumerable<string>? ids)
    {
        return ids?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
    }
}

internal sealed record RequestTaskBindingStateProjection(
    bool Enabled,
    AutomationBindingSyncState SyncState,
    string? BlockReason)
{
    public bool ReadyToStart => Enabled && SyncState != AutomationBindingSyncState.Broken;
}

public record CreateRequestTaskDto
{
    [Required]
    public string Title { get; init; } = string.Empty;
    [Required]
    public string Description { get; init; } = string.Empty;
    [Required]
    public string RequestId { get; init; } = string.Empty;
    public TicketPriority? Priority { get; init; }
    public string? CustomerId { get; init; }
    public string? OrganizationId { get; init; }
    public List<string>? LinkedAssetIds { get; init; }
    public List<string>? Attachments { get; init; }
    public DateTime? DueDate { get; init; }
}

/// <summary>Bounded PATCH contract for agent-safe request-task metadata changes.</summary>
public sealed record UpdateRequestTaskDto
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public TicketPriority? Priority { get; init; }
    public string? AssignedToId { get; init; }
    public bool ClearAssignment { get; init; }
    public List<string>? LinkedAssetIds { get; init; }
    public List<string>? Attachments { get; init; }
    public DateTime? DueDate { get; init; }
    public bool ClearDueDate { get; init; }

    public bool HasUpdate => Title is not null || Description is not null || Priority.HasValue || AssignedToId is not null || ClearAssignment || LinkedAssetIds is not null || Attachments is not null || DueDate.HasValue || ClearDueDate;
}
