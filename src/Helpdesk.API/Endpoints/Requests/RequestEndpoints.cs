using Helpdesk.API.Endpoints.Ticketing;
using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using AppTicketServices = Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Article;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.Sla;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Json;

namespace Helpdesk.API.Endpoints.Requests;

public static class RequestEndpoints
{
    public static void MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/requests")
            .WithTags("Requests")
            .RequireAuthorization("RequestAccess");
        var staffGroup = app.MapGroup("/api/v1/requests")
            .WithTags("Requests")
            .RequireAuthorization("RequestManager");

        group.MapGet("/", GetRequests);

        group.MapGet("/{id}", async (
            [FromRoute] string id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Request> repo,
            [FromServices] ITicketSlaService ticketSlaService,
            [FromServices] ITicketSlaRepository ticketSlaRepository,
            [FromServices] ISlaClockService slaClockService,
            [FromServices] ISlaEscalationEvaluator escalationEvaluator,
            [FromServices] IDomainEventPublisher domainEvents,
            [FromServices] ICorrelationContext correlationContext,
            [FromServices] ILoggerFactory loggerFactory,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var entity = await repo.GetAsync(id);
            if (entity == null) return Results.Problem("Request not found", statusCode: 404);

            var customer = !string.IsNullOrWhiteSpace(entity.CustomerId)
                ? await db.Customers
                    .AsNoTracking()
                    .Where(x => x.Id == entity.CustomerId)
                    .Select(x => new { x.Id, x.Name, x.Email })
                    .FirstOrDefaultAsync()
                : null;

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanViewRequest(entity.OrganizationId, customer?.Id ?? entity.CustomerId, customer?.Email ?? entity.RequesterEmail))
            {
                return Results.Forbid();
            }

            var categoryIds = await db.RequestCategoryLinks
                .Where(x => x.RequestId == entity.Id)
                .Select(x => x.TicketCategoryId)
                .ToListAsync();

            var slaSnapshot = await SyncAndComputeSlaAsync(
                entity.Id,
                ticketSlaService,
                ticketSlaRepository,
                slaClockService,
                entity,
                escalationEvaluator,
                domainEvents,
                correlationContext,
                loggerFactory.CreateLogger("RequestSla"));

            var sourceTicketTrackingId = string.IsNullOrWhiteSpace(entity.SourceTicketId)
                ? null
                : await db.Tickets
                    .AsNoTracking()
                    .Where(x => x.Id == entity.SourceTicketId)
                    .Select(x => x.TrackingId)
                    .FirstOrDefaultAsync();

            var dto = new RequestDto
            {
                OrganizationId = entity.OrganizationId,
                Id = entity.Id,
                Title = entity.Title,
                Description = entity.Description,
                Priority = entity.Priority,
                State = entity.State,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                TrackingId = entity.TrackingId,
                AssignedToId = entity.AssignedToId,
                CustomerId = customer?.Id ?? entity.CustomerId,
                CustomerName = customer?.Name,
                CustomerEmail = customer?.Email ?? entity.RequesterEmail,
                LastReplierName = entity.LastReplierName,
                CategoryIds = categoryIds,
                ServiceId = entity.ServiceId,
                RequestFormId = entity.RequestFormId,
                PayloadJson = entity.PayloadJson,
                SourceTicketId = entity.SourceTicketId,
                SourceTicketTrackingId = sourceTicketTrackingId,
                SourceAutomationBindingId = entity.SourceAutomationBindingId,
                WorkflowStatus = entity.WorkflowStatus,
                WorkflowBlockReason = entity.WorkflowBlockReason,
                WorkflowUpdatedAt = entity.WorkflowUpdatedAt,
                Sla = ToSlaDto(slaSnapshot)
            };
            return Results.Ok(dto);
        });

        group.MapGet("/{id}/tasks", async (
            [FromRoute] string id,
            HttpContext context,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Request> repo,
            CancellationToken token) =>
        {
            var authorization = await AuthorizeRequestAsync(id, context.User, accessService, db, requireManager: false, token);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
            }

            var request = await repo.GetAsync(id);
            if (request is null)
            {
                return Results.Problem("Request not found", statusCode: 404);
            }

            var tasks = await db.RequestTasks.AsNoTracking()
                .Where(t => t.RequestId == id)
                .OrderBy(t => t.Order)
                .ThenBy(t => t.CreatedAt)
                .ToListAsync(token);
            var bindingStates = await LoadAutomationBindingStatesAsync(
                db,
                request.RequestFormId,
                tasks.Select(t => t.TemplateId),
                token);

            return Results.Ok(tasks.Select(t =>
            {
                bindingStates.TryGetValue(t.TemplateId ?? string.Empty, out var bindingState);
                return new RequestTaskDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    Description = t.Description,
                    Type = t.Type,
                    Status = t.Status,
                    AssignedToId = t.AssignedToId,
                    Order = t.Order,
                    StartedAt = t.StartedAt,
                    CompletedAt = t.CompletedAt,
                    NextRetryAt = t.NextRetryAt,
                    DueAt = t.DueAt,
                    RetryCount = t.RetryCount,
                    FailureReason = t.FailureReason,
                    OrchestratorExecutionId = t.OrchestratorExecutionId,
                    AutomationBindingId = t.AutomationBindingId,
                    OrchestrationRequestDefinitionId = t.OrchestrationRequestDefinitionId,
                    OrchestrationJobDefinitionId = t.OrchestrationJobDefinitionId,
                    OrchestrationExternalRequestId = t.OrchestrationExternalRequestId,
                    OrchestrationExternalRunId = t.OrchestrationExternalRunId,
                    LastAutomationStatus = t.LastAutomationStatus,
                    LastAutomationUpdatedAt = t.LastAutomationUpdatedAt,
                    ExpectedRuntimeSeconds = t.ExpectedRuntimeSeconds,
                    GraceSeconds = t.GraceSeconds,
                    HardTimeoutSeconds = t.HardTimeoutSeconds,
                    TimeoutIncidentId = t.TimeoutIncidentId,
                    AutomationBindingEnabled = bindingState?.Enabled,
                    AutomationBindingSyncState = bindingState?.SyncState,
                    AutomationReadyToStart = bindingState?.ReadyToStart ?? true,
                    AutomationBlockReason = bindingState?.BlockReason
                };
            }).ToList());
        });

        group.MapPost("/", async (
            [FromBody] CreateRequestDto dto,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Request> repo,
            [FromServices] IRepository<Customer> customers,
            [FromServices] IRepository<User> users,
            [FromServices] ITicketSlaInitializer ticketSlaInitializer,
            [FromServices] AppTicketServices.ITicketRefGeneratorService refs,
            [FromServices] IRequestTaskGenerationService requestTaskGenerationService,
            [FromServices] IWorkflowEngine workflowEngine,
            [FromServices] ITicketNotificationService ticketNotificationService,
            [FromServices] IHtmlSanitizerService sanitizer,
            [FromServices] IHtmlToPlainTextConverter plainTextConverter,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Title) && string.IsNullOrWhiteSpace(dto.RequestFormId))
            {
                return Results.BadRequest("Title is required when RequestFormId is not provided.");
            }

            var categoryIds = NormalizeCategoryIds(dto.CategoryIds);
            var validation = await ValidateCategorySelectionAsync(
                db,
                categoryIds,
                TicketCategoryType.Request,
                token);
            if (validation is not null)
            {
                return validation;
            }

            var customerValidation = await ValidateCreateCustomerAsync(customers, dto.OrganizationId, dto.CustomerId);
            if (customerValidation.Result is not null)
            {
                return customerValidation.Result;
            }

            var customer = customerValidation.Customer!;
            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanCreateRequest(dto.OrganizationId, customer.Id, customer.Email))
            {
                return Results.Forbid();
            }

            var assignmentValidation = await ValidateCreateAssigneeAsync(users, dto.OrganizationId, dto.AssignedToId);
            if (assignmentValidation is not null)
            {
                return assignmentValidation;
            }

            var description = ToPlainTextDescription(sanitizer, plainTextConverter, dto.Description);

            var entity = new Request
            {
                Title = dto.Title ?? string.Empty,
                Description = description,
                Priority = dto.Priority,
                CustomerId = dto.CustomerId,
                OrganizationId = dto.OrganizationId,
                AssignedToId = string.IsNullOrWhiteSpace(dto.AssignedToId) ? null : dto.AssignedToId,
                TrackingId = await refs.NextReferenceAsync("REQ"),
                DueDate = dto.DueDate,
                LegacyCategory = dto.Category,
                ServiceId = dto.ServiceId,
                RequestFormId = dto.RequestFormId,
                PayloadJson = dto.PayloadJson,
                RequesterEmail = customer.Email,
                CcRecipients = NormalizeEmails(dto.CcRecipients).ToList()
            };

            if (dto.LinkedAssetIds is not null)
                entity.LinkedAssetIds = dto.LinkedAssetIds.ToList();
            if (dto.Attachments is not null)
                entity.Attachments = dto.Attachments.ToList();

            await using var tx = await db.Database.BeginTransactionAsync(token);

            var created = await repo.CreateAsync(entity);
            try
            {
                await ticketSlaInitializer.InitializeAsync(created);
            }
            catch
            {
                // Ticket creation must not fail if SLA initialization fails.
            }

            if (categoryIds.Count > 0)
            {
                db.RequestCategoryLinks.AddRange(categoryIds.Select(categoryId => new RequestCategoryLink
                {
                    RequestId = created.Id,
                    TicketCategoryId = categoryId
                }));
                await db.SaveChangesAsync(token);
            }

            try
            {
                await requestTaskGenerationService.GenerateForRequestAsync(created, token);
            }
            catch
            {
                // Request creation must not fail if task generation fails.
            }

            await tx.CommitAsync(token);

            await workflowEngine.RunAsync(created.Id, WorkflowRunReason.RequestCreated, token);

            var confirmationRecipient = FirstNonEmpty(created.RequesterEmail, customer.Email);
            if (!string.IsNullOrWhiteSpace(confirmationRecipient))
            {
                _ = await ticketNotificationService.SendNewTicketConfirmationAsync(
                    created,
                    confirmationRecipient,
                    FirstNonEmpty(customer.Name, confirmationRecipient) ?? confirmationRecipient,
                    created.CcRecipients,
                    token);
            }

            var responseDto = new RequestDto
            {
                OrganizationId = created.OrganizationId,
                Id = created.Id,
                Title = created.Title,
                Description = created.Description,
                Priority = created.Priority,
                State = created.State,
                CreatedAt = created.CreatedAt,
                UpdatedAt = created.UpdatedAt,
                TrackingId = created.TrackingId,
                AssignedToId = created.AssignedToId,
                CustomerId = created.CustomerId,
                CustomerName = customer.Name,
                CustomerEmail = customer.Email,
                LastReplierName = created.LastReplierName,
                CategoryIds = categoryIds,
                ServiceId = created.ServiceId,
                RequestFormId = created.RequestFormId,
                PayloadJson = created.PayloadJson,
                SourceTicketId = created.SourceTicketId,
                SourceAutomationBindingId = created.SourceAutomationBindingId,
                WorkflowStatus = created.WorkflowStatus,
                WorkflowBlockReason = created.WorkflowBlockReason,
                WorkflowUpdatedAt = created.WorkflowUpdatedAt
            };
            return Results.Created($"/api/v1/requests/{created.Id}", responseDto);
        });

        group.MapPut("/{id}", async (
            [FromRoute] string id,
            [FromBody] UpdateRequestDto dto,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Request> repo,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            [FromServices] ITicketSlaService ticketSlaService,
            [FromServices] ITicketSlaRepository ticketSlaRepository,
            [FromServices] ISlaClockService slaClockService,
            [FromServices] ISlaEscalationEvaluator escalationEvaluator,
            [FromServices] ITicketNotificationService ticketNotificationService,
            [FromServices] IDomainEventPublisher domainEvents,
            [FromServices] ICorrelationContext correlationContext,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken token) =>
        {
            var existing = await repo.GetAsync(id);
            if (existing is null) return Results.Problem("Request not found", statusCode: 404);

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanManageRequest(existing.OrganizationId)) return Results.Forbid();

            var previousState = existing.State;

            existing.State = dto.State;
            ApplyClosedAtTransition(existing, previousState, existing.State);
            existing.Priority = dto.Priority;
            existing.UpdatedAt = DateTime.UtcNow;

            await using var tx = await db.Database.BeginTransactionAsync(token);

            if (dto.CategoryIds is not null)
            {
                var categoryIds = NormalizeCategoryIds(dto.CategoryIds);
                var validation = await ValidateCategorySelectionAsync(
                    db,
                    categoryIds,
                    TicketCategoryType.Request,
                    token);
                if (validation is not null)
                {
                    return validation;
                }

                await db.RequestCategoryLinks
                    .Where(x => x.RequestId == id)
                    .ExecuteDeleteAsync(token);

                if (categoryIds.Count > 0)
                {
                    db.RequestCategoryLinks.AddRange(categoryIds.Select(categoryId => new RequestCategoryLink
                    {
                        RequestId = id,
                        TicketCategoryId = categoryId
                    }));
                }

                await db.SaveChangesAsync(token);
            }

            var updated = await repo.UpdateAsync(existing);
            await tx.CommitAsync(token);

            if (previousState != TicketState.Resolved && updated!.State == TicketState.Resolved)
            {
                await ticketSlaCompletionService.HandleTicketClosedAsync(updated.Id, "system", DateTimeOffset.UtcNow);
            }

            var slaSnapshot = await SyncAndComputeSlaAsync(
                id,
                ticketSlaService,
                ticketSlaRepository,
                slaClockService,
                updated!,
                escalationEvaluator,
                domainEvents,
                correlationContext,
                loggerFactory.CreateLogger("RequestSla"));

            if (previousState != TicketState.Resolved && updated!.State == TicketState.Resolved)
            {
                if (string.IsNullOrWhiteSpace(updated.RequestFormId))
                {
                    await SendResolvedNotificationAsync(updated, ticketNotificationService, token);
                }
            }

            var resultDto = new RequestDto
            {
                OrganizationId = updated.OrganizationId,
                Id = updated.Id,
                Title = updated.Title,
                Description = updated.Description,
                Priority = updated.Priority,
                State = updated.State,
                CreatedAt = updated.CreatedAt,
                UpdatedAt = updated.UpdatedAt,
                TrackingId = updated.TrackingId,
                AssignedToId = updated.AssignedToId,
                CustomerId = updated.CustomerId,
                CustomerEmail = updated.RequesterEmail,
                LastReplierName = updated.LastReplierName,
                CategoryIds = await db.RequestCategoryLinks
                    .Where(x => x.RequestId == updated.Id)
                    .Select(x => x.TicketCategoryId)
                    .ToListAsync(token),
                ServiceId = updated.ServiceId,
                RequestFormId = updated.RequestFormId,
                PayloadJson = updated.PayloadJson,
                SourceTicketId = updated.SourceTicketId,
                SourceAutomationBindingId = updated.SourceAutomationBindingId,
                WorkflowStatus = updated.WorkflowStatus,
                WorkflowBlockReason = updated.WorkflowBlockReason,
                WorkflowUpdatedAt = updated.WorkflowUpdatedAt,
                Sla = ToSlaDto(slaSnapshot)
            };
            return Results.Ok(resultDto);
        })
        .RequireAuthorization("RequestManager");

        group.MapPost("/bulk/state", async (
            [FromBody] BulkStateChangeRequest req,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Request> repo,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            [FromServices] ITicketNotificationService ticketNotificationService,
            CancellationToken token) =>
        {
            var ids = (req.Ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length is 0 or > 500) return Results.BadRequest("Select between 1 and 500 tickets.");
            if (!Enum.IsDefined(req.NewState)) return Results.BadRequest("Invalid ticket state.");
            var requests = (await repo.GetAllAsync()).Where(ticket => ids.Contains(ticket.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            var access = await accessService.ResolveAsync(user, token);
            if (requests.Any(ticket => !access.CanManageRequest(ticket.OrganizationId))) return Results.Forbid();
            if (requests.Count != ids.Length) return Results.NotFound();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(token)
                : null;
            var resolvedTransitions = new List<Request>();
            foreach (var request in requests)
            {
                var previousState = request.State;
                request.State = req.NewState;
                ApplyClosedAtTransition(request, previousState, request.State);
                request.UpdatedAt = DateTime.UtcNow;
                if (previousState != TicketState.Resolved && request.State == TicketState.Resolved)
                {
                    resolvedTransitions.Add(request);
                }
            }

            foreach (var request in requests)
            {
                await repo.UpdateAsync(request);
                if (request.State == TicketState.Resolved)
                {
                    await ticketSlaCompletionService.HandleTicketClosedAsync(request.Id, "bulk", DateTimeOffset.UtcNow);
                }
            }

            if (transaction is not null) await transaction.CommitAsync(token);
            foreach (var request in resolvedTransitions.Where(x => string.IsNullOrWhiteSpace(x.RequestFormId)))
            {
                await SendResolvedNotificationAsync(request, ticketNotificationService, token);
            }

            return Results.Ok(new { updated = requests.Count });
        })
        .RequireAuthorization("RequestManager")
        .WithName("BulkUpdateRequestState")
        .WithSummary("Bulk update request state")
        .WithDescription("Updates the state of multiple requests in one request.")
        .WithTags("Requests");

        group.MapPost("/bulk/assign", async (
            [FromBody] BulkAssignRequest req,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<User> users,
            [FromServices] IRepository<Request> repo,
            CancellationToken token) =>
        {
            var ids = (req.Ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length is 0 or > 500) return Results.BadRequest("Select between 1 and 500 tickets.");
            var requests = (await repo.GetAllAsync()).Where(ticket => ids.Contains(ticket.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            var access = await accessService.ResolveAsync(user, token);
            if (requests.Any(ticket => !access.CanManageRequest(ticket.OrganizationId))) return Results.Forbid();
            if (requests.Count != ids.Length) return Results.NotFound();
            foreach (var request in requests)
            {
                var validation = await ValidateCreateAssigneeAsync(users, request.OrganizationId, req.AssignedToId);
                if (validation is not null) return validation;
            }
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(token)
                : null;
            foreach (var request in requests)
            {
                request.AssignedToId = string.IsNullOrWhiteSpace(req.AssignedToId) ? null : req.AssignedToId.Trim();
                request.UpdatedAt = DateTime.UtcNow;
                await repo.UpdateAsync(request);
            }

            if (transaction is not null) await transaction.CommitAsync(token);
            return Results.Ok(new { updated = requests.Count });
        })
        .RequireAuthorization("RequestManager")
        .WithName("BulkAssignRequests")
        .WithSummary("Bulk assign requests to a user")
        .WithDescription("Assigns the selected requests to the specified team member.")
        .WithTags("Requests");

        staffGroup.MapPost("/{id}/state", async (
            [FromRoute] string id,
            [FromBody] QuickStateChangeRequest req,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRepository<Request> repo,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            [FromServices] ITicketNotificationService ticketNotificationService,
            CancellationToken token) =>
        {
            var request = await repo.GetAsync(id);
            if (request is null)
            {
                return Results.NotFound();
            }

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanManageRequest(request.OrganizationId))
            {
                return Results.Forbid();
            }

            var previousState = request.State;
            if (previousState == req.NewState)
            {
                return Results.Ok(new { request.Id, State = request.State });
            }

            request.State = req.NewState;
            ApplyClosedAtTransition(request, previousState, request.State);
            request.UpdatedAt = DateTime.UtcNow;
            await repo.UpdateAsync(request);

            if (request.State == TicketState.Resolved)
            {
                await ticketSlaCompletionService.HandleTicketClosedAsync(request.Id, "quick-state", DateTimeOffset.UtcNow);
            }

            if (previousState != TicketState.Resolved &&
                request.State == TicketState.Resolved &&
                string.IsNullOrWhiteSpace(request.RequestFormId))
            {
                await SendResolvedNotificationAsync(request, ticketNotificationService, token);
            }

            return Results.Ok(new { request.Id, State = request.State });
        })
        .WithName("QuickUpdateRequestState")
        .WithSummary("Quick update request state")
        .WithDescription("Updates one request state from a list row state picker.")
        .WithTags("Requests");

        group.MapGet("/{id}/worklogs", async (
            [FromRoute] string id,
            HttpContext context,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<WorkLog> repo,
            CancellationToken ct) =>
        {
            var authorization = await AuthorizeRequestAsync(id, context.User, accessService, db, requireManager: false, ct);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
            }

            var allLogs = await repo.GetAllAsync();
            var logs = allLogs
                .Where(log => log.TicketId == id)
                .Where(log => CanReadInternalRequest(authorization.Access!, authorization.OrganizationId) || !log.IsInternalNote)
                .Select(l => new WorkLogDto
            {
                Id = l.Id,
                TicketId = l.TicketId,
                NotesHtml = l.NotesHtml,
                NotesText = l.NotesText,
                Hours = l.Hours,
                IsInternalNote = l.IsInternalNote,
                LoggedAt = l.LoggedAt,
                TechnicianId = l.TechnicianId
            });
            return Results.Ok(logs);
        });

        group.MapGet("/{id}/timeline", async (
            [FromRoute] string id,
            [FromQuery] string? order,
            HttpContext context,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            CancellationToken ct) =>
        {
            var authorization = await AuthorizeRequestAsync(id, context.User, accessService, db, requireManager: false, ct);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
            }

            var timelineQuery = db.TicketTimelineEvents
                .AsNoTracking()
                .Where(evt => evt.TicketId == id);
            if (!CanReadInternalRequest(authorization.Access!, authorization.OrganizationId))
            {
                timelineQuery = timelineQuery.Where(evt => evt.EventType != TimelineEventType.InternalNote);
            }

            // The ticket and visibility filters remain in SQL. SQLite cannot order
            // DateTimeOffset, so order this already bounded ticket history in memory.
            var events = await timelineQuery.ToListAsync(ct);
            var ordered = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
                ? events.OrderBy(evt => evt.CreatedUtc)
                : events.OrderByDescending(evt => evt.CreatedUtc);
            var timeline = ordered.Select(ToTimelineDto).ToList();

            return Results.Ok(timeline);
        });

        group.MapGet("/{id}/timeline/stream", StreamTimeline);

        group.MapPost("/{id}/worklogs", async (
            [FromRoute] string id,
            [FromBody] CreateWorkLogDto dto,
            ClaimsPrincipal user,
            HttpContext context,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRequestSender sender,
            CancellationToken ct) =>
        {
            var authorization = await AuthorizeRequestAsync(id, context.User, accessService, db, requireManager: false, ct, requireContributor: true);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
            }

            if ((dto.IsInternalNote || dto.Hours != 0) && !authorization.Access!.CanManageRequest(authorization.OrganizationId))
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(dto.Notes))
            {
                return Results.Problem("Notes cannot be empty", statusCode: 400);
            }

            var techId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var techName = user.Identity?.Name;
            var created = await sender.Send(new CreateWorkLogCommand(
                id,
                dto.Hours,
                dto.Notes,
                techId,
                techName,
                NotifyCustomer: !dto.IsInternalNote,
                IsInternalNote: dto.IsInternalNote));

            var response = new WorkLogDto
            {
                Id = created.Id,
                TicketId = created.TicketId,
                NotesHtml = created.NotesHtml,
                NotesText = created.NotesText,
                Hours = created.Hours,
                IsInternalNote = created.IsInternalNote,
                LoggedAt = created.LoggedAt,
                TechnicianId = created.TechnicianId,
                TechnicianName = user.Identity?.Name
            };
            return Results.Created($"/api/v1/worklogs/{response.Id}", response);
        });

        group.MapDelete("/{id}", async (
            [FromRoute] string id,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRepository<Request> repo,
            CancellationToken cancellationToken) =>
        {
            var request = await repo.GetAsync(id);
            if (request is null) return Results.Problem("Request not found", statusCode: 404);

            var access = await accessService.ResolveAsync(user, cancellationToken);
            if (!access.CanDeleteRequest(request.OrganizationId)) return Results.Forbid();

            return await repo.DeleteAsync(id)
                ? Results.NoContent()
                : Results.Problem("Request not found", statusCode: 404);
        });
    }

    private static bool CanReadInternalRequest(CurrentUserAccessProfile access, string? organizationId) =>
        access.CanManageRequest(organizationId) ||
        access.HasPermission(Helpdesk.Shared.Auth.HelpdeskPermissions.RequestRead, organizationId);

    private static async Task<IResult> GetRequests(
        [FromServices] HelpdeskDbContext db,
        [FromServices] ICurrentUserAccessService accessService,
        ClaimsPrincipal user,
        HttpRequest httpRequest,
        CancellationToken cancellationToken,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] TicketState? state = null,
        [FromQuery] bool activeOnly = false,
        [FromQuery] bool historicOnly = false,
        [FromQuery] bool aiOnly = false,
        [FromQuery] bool includeTotal = true,
        [FromQuery] bool summaryOnly = false,
        [FromQuery] string? q = null)
    {
        if (!TicketListQuery.TryParse(httpRequest.Query, false, out var filters, out var filterError)) return Results.BadRequest(new { error = filterError });
        page = page.HasValue ? Math.Clamp(page.Value, 1, 1_000_000) : null;
        var effectivePageSize = Math.Clamp(pageSize ?? 10, 1, 100);

        var query =
            from r in filters.Apply(db.Requests.AsNoTracking(), false)
            join c in db.Customers.AsNoTracking() on r.CustomerId equals c.Id into rc
            from c in rc.DefaultIfEmpty()
            join o in db.Organizations.AsNoTracking() on r.OrganizationId equals o.Id into co
            from o in co.DefaultIfEmpty()
            select new
            {
                Request = r,
                OrgName = o != null ? o.Name : null,
                CustomerId = c != null ? c.Id : null,
                CustomerName = c != null ? c.Name : null,
                CustomerEmail = c != null ? c.Email : null
            };

        var access = await accessService.ResolveAsync(user, cancellationToken);
        if (!access.IsHelpdeskAdmin)
        {
            var tenantReadOrganizationIds = access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.RequestRead)
                .Concat(access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.RequestWrite))
                .Concat(access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.RequestManager))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var ownOrganizationIds = access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.RequestUser).ToArray();
            query = query.Where(x =>
                tenantReadOrganizationIds.Contains(x.Request.OrganizationId) ||
                (ownOrganizationIds.Contains(x.Request.OrganizationId) &&
                 !string.IsNullOrWhiteSpace(access.CustomerId) &&
                 x.CustomerId == access.CustomerId));
        }

        if (!filters.ExplicitStates)
        {
            if (state is not null)
            {
                query = query.Where(x => x.Request.State == state);
            }
            else if (historicOnly || httpRequest.Query["view"] == "historic")
            {
                query = query.Where(x => x.Request.State == TicketState.Resolved);
            }
            else if (activeOnly)
            {
                query = query.Where(x => x.Request.State != TicketState.Resolved);
            }
            else
            {
                query = query.Where(x => x.Request.State != TicketState.Resolved);
            }
        }

        if (aiOnly)
        {
            query = query.Where(x =>
                x.Request.SourceTicketId != null ||
                x.Request.SourceAutomationBindingId != null);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? query.Where(x =>
                    EF.Functions.ILike(x.Request.Title, like, "\\") ||
                    EF.Functions.ILike(x.Request.Description, like, "\\") ||
                    EF.Functions.ILike(x.Request.TrackingId, like, "\\") ||
                    EF.Functions.ILike(x.OrgName!, like, "\\") ||
                    EF.Functions.ILike(x.CustomerName!, like, "\\") ||
                    EF.Functions.ILike(x.CustomerEmail!, like, "\\"))
                : query.Where(x =>
                    EF.Functions.Like(x.Request.Title, like, "\\") ||
                    EF.Functions.Like(x.Request.Description, like, "\\") ||
                    EF.Functions.Like(x.Request.TrackingId, like, "\\") ||
                    EF.Functions.Like(x.OrgName!, like, "\\") ||
                    EF.Functions.Like(x.CustomerName!, like, "\\") ||
                    EF.Functions.Like(x.CustomerEmail!, like, "\\"));
        }

        var totalCount = includeTotal ? await query.CountAsync(cancellationToken) : 0;

        var orderedQuery = filters.Order(query, "Request")
            .Select(x => new
            {
                x.Request.OrganizationId,
                x.Request.Id,
                x.Request.Title,
                x.Request.Description,
                x.Request.Priority,
                x.Request.State,
                x.Request.CreatedAt,
                UpdatedAt = x.Request.UpdatedAt ?? x.Request.CreatedAt,
                x.Request.TrackingId,
                x.Request.AssignedToId,
                x.Request.LastReplierName,
                CustomerOrgName = x.OrgName,
                x.CustomerId,
                x.CustomerName,
                x.CustomerEmail,
                x.Request.ServiceId,
                x.Request.RequestFormId,
                x.Request.PayloadJson,
                x.Request.SourceTicketId,
                x.Request.SourceAutomationBindingId,
                x.Request.WorkflowStatus,
                x.Request.WorkflowBlockReason,
                x.Request.WorkflowUpdatedAt
            });

        var rawItems = page.HasValue
            ? await orderedQuery
                .Skip((page.Value - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .ToListAsync(cancellationToken)
            : await orderedQuery.ToListAsync(cancellationToken);

        var requestIds = rawItems.Select(x => x.Id).ToList();
        if (summaryOnly)
        {
            var summaryItems = rawItems.Select(i => new RequestDto
            {
                OrganizationId = i.OrganizationId,
                Id = i.Id,
                Title = i.Title,
                Description = i.Description,
                Priority = i.Priority,
                State = i.State,
                CreatedAt = i.CreatedAt,
                UpdatedAt = i.UpdatedAt,
                TrackingId = i.TrackingId,
                AssignedToId = i.AssignedToId,
                LastReplierName = i.LastReplierName,
                CustomerOrgName = i.CustomerOrgName,
                CustomerId = i.CustomerId,
                CustomerName = i.CustomerName,
                CustomerEmail = i.CustomerEmail,
                CategoryIds = [],
                ServiceId = i.ServiceId,
                RequestFormId = i.RequestFormId,
                PayloadJson = i.PayloadJson,
                SourceTicketId = i.SourceTicketId,
                SourceAutomationBindingId = i.SourceAutomationBindingId,
                WorkflowStatus = i.WorkflowStatus,
                WorkflowBlockReason = i.WorkflowBlockReason,
                WorkflowUpdatedAt = i.WorkflowUpdatedAt
            }).ToList();

            return Results.Ok(new PagedResponse<RequestDto>
            {
                Page = page ?? 1,
                PageSize = effectivePageSize,
                TotalCount = includeTotal ? totalCount : summaryItems.Count,
                Items = summaryItems
            });
        }

        var categoryLookup = await db.RequestCategoryLinks
            .Where(link => requestIds.Contains(link.RequestId))
            .GroupBy(link => link.RequestId)
            .ToDictionaryAsync(
                g => g.Key,
                g => g.Select(link => link.TicketCategoryId).ToList());
        var sourceTicketIds = rawItems
            .Select(x => x.SourceTicketId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
        var sourceTicketLookup = sourceTicketIds.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await db.Tickets
                .AsNoTracking()
                .Where(x => sourceTicketIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.TrackingId);

        var items = rawItems.Select(i => new RequestDto
        {
            OrganizationId = i.OrganizationId,
            Id = i.Id,
            Title = i.Title,
            Description = i.Description,
            Priority = i.Priority,
            State = i.State,
            CreatedAt = i.CreatedAt,
            UpdatedAt = i.UpdatedAt,
            TrackingId = i.TrackingId,
            AssignedToId = i.AssignedToId,
            LastReplierName = i.LastReplierName,
            CustomerOrgName = i.CustomerOrgName,
            CustomerId = i.CustomerId,
            CustomerName = i.CustomerName,
            CustomerEmail = i.CustomerEmail,
            CategoryIds = categoryLookup.TryGetValue(i.Id, out var ids) ? ids : new List<Guid>(),
            ServiceId = i.ServiceId,
            RequestFormId = i.RequestFormId,
            PayloadJson = i.PayloadJson,
            SourceTicketId = i.SourceTicketId,
            SourceTicketTrackingId = !string.IsNullOrWhiteSpace(i.SourceTicketId) && sourceTicketLookup.TryGetValue(i.SourceTicketId, out var trackingId)
                ? trackingId
                : null,
            SourceAutomationBindingId = i.SourceAutomationBindingId,
            WorkflowStatus = i.WorkflowStatus,
            WorkflowBlockReason = i.WorkflowBlockReason,
            WorkflowUpdatedAt = i.WorkflowUpdatedAt
        }).ToList();

        var response = new PagedResponse<RequestDto>
        {
            Page = page ?? 1,
            PageSize = effectivePageSize,
            TotalCount = includeTotal ? totalCount : items.Count,
            Items = items
        };

        return Results.Ok(response);
    }

    private static List<Guid> NormalizeCategoryIds(IEnumerable<Guid>? categoryIds)
    {
        return categoryIds?
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList()
            ?? new List<Guid>();
    }

    private static void ApplyClosedAtTransition(Ticket ticket, TicketState previousState, TicketState currentState)
    {
        if (previousState != TicketState.Resolved && currentState == TicketState.Resolved)
        {
            ticket.ClosedAt = DateTimeOffset.UtcNow;
        }
        else if (previousState == TicketState.Resolved && currentState != TicketState.Resolved)
        {
            ticket.ClosedAt = null;
        }
    }

    private static async Task<IResult?> ValidateCategorySelectionAsync(
        HelpdeskDbContext db,
        IReadOnlyCollection<Guid> categoryIds,
        TicketCategoryType requestedType,
        CancellationToken token)
    {
        if (categoryIds.Count == 0)
        {
            return null;
        }

        var found = await db.TicketCategories
            .AsNoTracking()
            .Where(x => categoryIds.Contains(x.Id) && x.IsActive)
            .Select(x => new { x.Id, x.Type })
            .ToListAsync(token);

        if (found.Count != categoryIds.Count)
        {
            return Results.BadRequest("One or more category IDs are invalid.");
        }

        if (found.Any(x => x.Type != requestedType && x.Type != TicketCategoryType.Service))
        {
            return Results.BadRequest($"Categories must match type '{requestedType}' or 'Service'.");
        }

        return null;
    }

    private static async Task<SlaClockSnapshot?> SyncAndComputeSlaAsync(
        string ticketId,
        ITicketSlaService ticketSlaService,
        ITicketSlaRepository ticketSlaRepository,
        ISlaClockService slaClockService,
        Ticket ticket,
        ISlaEscalationEvaluator escalationEvaluator,
        IDomainEventPublisher domainEvents,
        ICorrelationContext correlationContext,
        ILogger logger)
    {
        await ticketSlaService.AutoResumeIfDueAsync(ticketId);

        var state = await ticketSlaRepository.GetByTicketIdForUpdateAsync(ticketId);
        if (state is null)
        {
            return null;
        }

        var snapshot = slaClockService.Compute(state, DateTimeOffset.UtcNow);
        var changed = false;

        if (snapshot.ResponseBreached && !state.ResponseBreached)
        {
            state.ResponseBreached = true;
            changed = true;
            await domainEvents.PublishAsync(
                new TicketSlaBreachedDomainEvent(
                    ticketId,
                    ticket.OrganizationId,
                    SlaMetricType.Response,
                    state.Status,
                    DateTimeOffset.UtcNow,
                    SlaTriggerSources.System,
                    GetCorrelationId(correlationContext)),
                CancellationToken.None);
        }

        if (snapshot.ResolutionBreached && !state.ResolutionBreached)
        {
            state.ResolutionBreached = true;
            changed = true;
            await domainEvents.PublishAsync(
                new TicketSlaBreachedDomainEvent(
                    ticketId,
                    ticket.OrganizationId,
                    SlaMetricType.Resolution,
                    state.Status,
                    DateTimeOffset.UtcNow,
                    SlaTriggerSources.System,
                    GetCorrelationId(correlationContext)),
                CancellationToken.None);
        }

        if (snapshot.ResolutionBreached && state.Status != SlaStatus.Breached && state.Status != SlaStatus.Completed)
        {
            state.Status = SlaStatus.Breached;
            changed = true;
        }

        if (changed)
        {
            await ticketSlaRepository.UpdateAsync(state);
            snapshot = slaClockService.Compute(state, DateTimeOffset.UtcNow);
        }

        try
        {
            await escalationEvaluator.EvaluateAndNotifyAsync(ticket, state, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SLA escalation evaluation failed for ticket {TicketId}.", ticketId);
        }

        return snapshot;
    }

    private static TicketSlaDto? ToSlaDto(SlaClockSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new TicketSlaDto
        {
            StartedAt = snapshot.StartedAt,
            ResponseDueAt = snapshot.ResponseDueAt,
            ResolutionDueAt = snapshot.ResolutionDueAt,
            Status = snapshot.Status,
            PausedAt = snapshot.PausedAt,
            ResumeAt = snapshot.ResumeAt,
            PauseReason = snapshot.PauseReason,
            ResponseBreached = snapshot.ResponseBreached,
            ResolutionBreached = snapshot.ResolutionBreached,
            ResponseRemainingSeconds = (long)snapshot.ResponseRemaining.TotalSeconds,
            ResolutionRemainingSeconds = (long)snapshot.ResolutionRemaining.TotalSeconds,
            ResponsePercentUsed = snapshot.ResponsePercentUsed,
            ResolutionPercentUsed = snapshot.ResolutionPercentUsed
        };
    }

    private static async Task<Dictionary<string, AutomationBindingTaskState>> LoadAutomationBindingStatesAsync(
        HelpdeskDbContext db,
        string? requestFormId,
        IEnumerable<string?> templateIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestFormId))
        {
            return new Dictionary<string, AutomationBindingTaskState>(StringComparer.OrdinalIgnoreCase);
        }

        var templateGuids = new List<Guid>();
        foreach (var templateId in templateIds)
        {
            if (Guid.TryParse(templateId, out var parsed))
            {
                templateGuids.Add(parsed);
            }
        }

        if (templateGuids.Count == 0)
        {
            return new Dictionary<string, AutomationBindingTaskState>(StringComparer.OrdinalIgnoreCase);
        }

        var bindings = await db.AutomationBindings.AsNoTracking()
            .Where(x => x.RequestFormId == requestFormId && templateGuids.Contains(x.TaskTemplateId))
            .ToListAsync(cancellationToken);

        return bindings.ToDictionary(
            x => x.TaskTemplateId.ToString("D"),
            x => new AutomationBindingTaskState(
                x.Enabled,
                x.SyncState,
                BuildAutomationBlockReason(x.Enabled, x.SyncState)),
            StringComparer.OrdinalIgnoreCase);
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

    private static async Task StreamTimeline(
        [FromRoute] string id,
        HttpContext context,
        [FromServices] ICurrentUserAccessService accessService,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITimelineEventBus eventBus,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var authorization = await AuthorizeRequestAsync(id, context.User, accessService, db, requireManager: false, ct);
        if (authorization.Failure is not null)
        {
            await authorization.Failure.ExecuteAsync(context);
            return;
        }

        var logger = loggerFactory.CreateLogger("RequestEndpoints");
        var reader = eventBus.Subscribe(id);
        var canReadInternalRequest = CanReadInternalRequest(authorization.Access!, authorization.OrganizationId);

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Append("Connection", "keep-alive");
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Response.ContentType = "text/event-stream";

        try
        {
            logger.LogInformation("Request timeline stream connected {TicketId}", id);
            await context.Response.StartAsync(ct);
            await context.Response.WriteAsync(": connected\n\n", ct);
            await context.Response.Body.FlushAsync(ct);

            var keepAliveInterval = TimeSpan.FromSeconds(15);

            while (!ct.IsCancellationRequested)
            {
                var waitForDataTask = reader.WaitToReadAsync(ct).AsTask();
                var keepAliveTask = Task.Delay(keepAliveInterval, ct);
                var completedTask = await Task.WhenAny(waitForDataTask, keepAliveTask);

                if (!await Helpdesk.API.Endpoints.Authentication.LocalSessionValidator.IsValidAsync(context, ct))
                {
                    logger.LogInformation("Timeline stream session revoked {TicketId}", id);
                    break;
                }

                var currentAuthorization = await AuthorizeRequestAsync(
                    id,
                    context.User,
                    accessService,
                    db,
                    requireManager: false,
                    ct);
                if (currentAuthorization.Failure is not null)
                {
                    logger.LogInformation("Request timeline stream authorization revoked {TicketId}", id);
                    break;
                }

                canReadInternalRequest = CanReadInternalRequest(currentAuthorization.Access!, currentAuthorization.OrganizationId);

                if (completedTask == waitForDataTask)
                {
                    if (!await waitForDataTask)
                    {
                        break;
                    }

                    while (reader.TryRead(out var evt))
                    {
                        if (!canReadInternalRequest && evt.EventType == TimelineEventType.InternalNote)
                        {
                            continue;
                        }

                        var json = JsonSerializer.Serialize(evt);
                        await context.Response.WriteAsync("event: timeline\n", ct);
                        await context.Response.WriteAsync($"data: {json}\n\n", ct);
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
            logger.LogError(ex, "Request timeline stream error {TicketId}", id);
        }
        finally
        {
            logger.LogInformation("Request timeline stream disconnected {TicketId}", id);
            eventBus.Unsubscribe(id, reader);
        }
    }

    private static TicketTimelineEventDto ToTimelineDto(TicketTimelineEvent evt)
    {
        return new TicketTimelineEventDto
        {
            Id = evt.Id,
            TicketId = evt.TicketId,
            CreatedUtc = evt.CreatedUtc,
            CreatedByUserId = evt.CreatedByUserId,
            CreatedByUserName = evt.CreatedByUserName,
            EventType = evt.EventType,
            MessageHtml = evt.MessageHtml,
            MessageText = evt.MessageText,
            EmailStatus = evt.EmailStatus,
            EmailRecipient = evt.EmailRecipient,
            RetryCount = evt.RetryCount,
            IsRetryable = evt.IsRetryable
        };
    }

    private static async Task<RequestAuthorization> AuthorizeRequestAsync(
        string requestId,
        ClaimsPrincipal user,
        ICurrentUserAccessService accessService,
        HelpdeskDbContext db,
        bool requireManager,
        CancellationToken cancellationToken,
        bool requireContributor = false)
    {
        var request = await db.Requests.AsNoTracking()
            .Where(candidate => candidate.Id == requestId)
            .Select(candidate => new RequestScope(candidate.OrganizationId, candidate.CustomerId, candidate.RequesterEmail))
            .FirstOrDefaultAsync(cancellationToken);
        if (request is null)
        {
            return new RequestAuthorization(null, null, Results.NotFound());
        }

        var customer = !string.IsNullOrWhiteSpace(request.CustomerId)
            ? await db.Customers.AsNoTracking()
                .Where(candidate => candidate.Id == request.CustomerId)
                .Select(candidate => new { candidate.Id, candidate.Email })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var access = await accessService.ResolveAsync(user, cancellationToken);
        var allowed = requireManager
            ? access.CanManageRequest(request.OrganizationId)
            : requireContributor
                ? access.CanContributeRequest(request.OrganizationId, customer?.Id ?? request.CustomerId, customer?.Email ?? request.RequesterEmail)
                : access.CanViewRequest(
                request.OrganizationId,
                customer?.Id ?? request.CustomerId,
                customer?.Email ?? request.RequesterEmail);
        return allowed
            ? new RequestAuthorization(access, request.OrganizationId, null)
            : new RequestAuthorization(null, null, Results.Forbid());
    }

    private sealed record RequestScope(string? OrganizationId, string? CustomerId, string? RequesterEmail);

    private sealed record RequestAuthorization(
        CurrentUserAccessProfile? Access,
        string? OrganizationId,
        IResult? Failure);

    private static IEnumerable<string> NormalizeEmails(IEnumerable<string>? values)
    {
        return (values ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ToPlainTextDescription(
        IHtmlSanitizerService sanitizer,
        IHtmlToPlainTextConverter plainTextConverter,
        string? description)
    {
        var sanitized = sanitizer.Sanitize(description ?? string.Empty);
        return plainTextConverter.Convert(sanitized);
    }

    private static async Task<IResult?> ValidateCreateAssigneeAsync(
        IRepository<User> users,
        string? organizationId,
        string? assignedToId)
    {
        if (string.IsNullOrWhiteSpace(assignedToId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.BadRequest("Organization is required when assigning a ticket.");
        }

        var assignee = await users.GetAsync(assignedToId);
        if (assignee is null)
        {
            return Results.BadRequest("Assigned user was not found.");
        }

        if (!string.Equals(assignee.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Assigned user must belong to the selected organization.");
        }

        return null;
    }

    private static async Task<(IResult? Result, Customer? Customer)> ValidateCreateCustomerAsync(
        IRepository<Customer> customers,
        string? organizationId,
        string? customerId)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return (Results.BadRequest("Customer is required."), null);
        }

        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return (Results.BadRequest("Organization is required."), null);
        }

        var customer = await customers.GetAsync(customerId);
        if (customer is null)
        {
            return (Results.BadRequest("Customer was not found."), null);
        }

        if (!customer.IsEnabled)
        {
            return (Results.BadRequest("Customer is disabled."), null);
        }

        if (!string.Equals(customer.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase))
        {
            return (Results.BadRequest("Customer must belong to the selected organization."), null);
        }

        return (null, customer);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static async Task SendResolvedNotificationAsync(
        Request request,
        ITicketNotificationService ticketNotificationService,
        CancellationToken token)
    {
        var recipient = FirstNonEmpty(request.RequesterEmail);
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return;
        }

        await ticketNotificationService.SendTicketResolvedAsync(
            request,
            recipient,
            recipient,
            request.CcRecipients,
            token);
    }

    private static string GetCorrelationId(ICorrelationContext correlationContext)
    {
        return correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }
}

internal sealed record AutomationBindingTaskState(
    bool Enabled,
    AutomationBindingSyncState SyncState,
    string? BlockReason)
{
    public bool ReadyToStart => Enabled && SyncState != AutomationBindingSyncState.Broken;
}

internal sealed record BulkStateChangeRequest(List<string> Ids, TicketState NewState, string? Comment);

internal sealed record QuickStateChangeRequest(TicketState NewState);

internal sealed record BulkAssignRequest(List<string> Ids, string? AssignedToId);
