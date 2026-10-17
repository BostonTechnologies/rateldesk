using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.Changes;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.API.Endpoints.Ticketing;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Change;
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

namespace Helpdesk.API.Endpoints.Changes;

public static class ChangeEndpoints
{
    public static void MapChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var publicGroup = app.MapGroup("/api/v1/changes/public")
            .WithTags("Changes");

        publicGroup.MapGet("/approval", GetPublicChangeApproval)
            .AllowAnonymous()
            .WithName("GetPublicChangeApproval")
            .WithSummary("Gets a signed public change approval view");

        publicGroup.MapPost("/approval/review", ReviewPublicChangeApproval)
            .AllowAnonymous()
            .WithName("ReviewPublicChangeApproval")
            .WithSummary("Marks a signed public change approval as reviewed.");

        var group = app.MapGroup("/api/v1/changes")
            .WithTags("Changes")
            .RequireAuthorization("ChangeAccess");

        group.MapGet("/", GetChanges);

        group.MapGet("/{id}/timeline", async (
            [FromRoute] string id,
            [FromQuery] string? order,
            HttpContext context,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            CancellationToken ct) =>
        {
            var authorization = await AuthorizeChangeAsync(id, context.User, accessService, db, requireManager: false, ct);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
            }

            var timelineEvents = await db.TicketTimelineEvents
                .AsNoTracking()
                .Where(evt => evt.TicketId == id)
                .Where(evt => CanReadInternalChange(authorization.Access!, authorization.OrganizationId) || evt.EventType != TimelineEventType.InternalNote)
                .ToListAsync(ct);

            var ordered = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
                ? timelineEvents.OrderBy(evt => evt.CreatedUtc)
                : timelineEvents.OrderByDescending(evt => evt.CreatedUtc);
            var timeline = ordered
                .Select(ToTimelineDto)
                .ToList();

            return Results.Ok(timeline);
        })
        .WithName("GetChangeTimeline")
        .WithSummary("Gets timeline events for a change")
        .WithDescription("Retrieves timeline events for the specified change ordered by CreatedUtc. Defaults to descending.");

        group.MapGet("/{id}/timeline/stream", StreamChangeTimeline)
            .WithName("StreamChangeTimeline")
            .WithSummary("Streams change timeline events in real time")
            .WithDescription("Pushes change timeline updates over server-sent events.");

        group.MapGet("/{id}", async (
            [FromRoute] string id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Change> repo,
            [FromServices] ITicketSlaService ticketSlaService,
            [FromServices] ITicketSlaRepository ticketSlaRepository,
            [FromServices] ISlaClockService slaClockService,
            [FromServices] ISlaEscalationEvaluator escalationEvaluator,
            [FromServices] IDomainEventPublisher domainEvents,
            [FromServices] ICorrelationContext correlationContext,
            [FromServices] ILoggerFactory loggerFactory,
            [FromServices] IChangeTemplateService changeTemplateService,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var entity = await repo.GetAsync(id);
            if (entity == null) return Results.Problem("Change not found", statusCode: 404);

            var organizationName = !string.IsNullOrWhiteSpace(entity.OrganizationId)
                ? await db.Organizations
                    .AsNoTracking()
                    .Where(x => x.Id == entity.OrganizationId)
                    .Select(x => x.Name)
                    .FirstOrDefaultAsync()
                : null;
            var customer = !string.IsNullOrWhiteSpace(entity.CustomerId)
                ? await db.Customers
                    .AsNoTracking()
                    .Where(x => x.Id == entity.CustomerId)
                    .Select(x => new { x.Id, x.Name, x.Email })
                    .FirstOrDefaultAsync()
                : null;

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanViewChange(entity.OrganizationId, customer?.Id ?? entity.CustomerId, customer?.Email ?? entity.RequesterEmail))
            {
                return Results.Forbid();
            }

            var categoryIds = await db.ChangeCategoryLinks
                .Where(x => x.ChangeId == entity.Id)
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
                loggerFactory.CreateLogger("ChangeSla"));
            var template = changeTemplateService.DeserializeTemplate(entity.ChangeTemplateJson);
            var templateValidation = changeTemplateService.ValidateTemplate(entity.ChangeType, template);
            var participantLookup = await BuildParticipantLookupAsync(db, [entity], CancellationToken.None);
            var approvals = (await db.ChangeApprovals
                    .AsNoTracking()
                    .Where(x => x.ChangeId == entity.Id)
                    .OrderBy(x => x.ApproverName)
                    .ToListAsync())
                .Select(ToApprovalDto)
                .ToList();

            var dto = new ChangeDto
            {
                OrganizationId = entity.OrganizationId,
                OrganizationName = organizationName,
                Id = entity.Id,
                Title = entity.Title,
                Description = entity.Description,
                Priority = entity.Priority,
                State = entity.State,
                LifecycleState = EffectiveLifecycleState(entity),
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                TrackingId = entity.TrackingId,
                AssignedToId = entity.AssignedToId,
                LastReplierName = entity.LastReplierName,
                CustomerOrgName = organizationName,
                CustomerId = customer?.Id,
                CustomerName = customer?.Name,
                CustomerEmail = customer?.Email,
                ChangeType = entity.ChangeType,
                RequestedForUserId = entity.RequestedForUserId,
                RequestedForUserName = GetUserName(participantLookup, entity.RequestedForUserId),
                RequestedForUserEmail = GetUserEmail(participantLookup, entity.RequestedForUserId),
                ImplementorUserId = entity.ImplementorUserId,
                ImplementorUserName = GetUserName(participantLookup, entity.ImplementorUserId),
                ImplementorUserEmail = GetUserEmail(participantLookup, entity.ImplementorUserId),
                ApproverUserIds = entity.ApproverUserIds,
                Approvers = GetUsers(participantLookup, entity.ApproverUserIds),
                Approvals = approvals,
                CcRecipients = entity.CcRecipients.ToList(),
                ImplementationStartAt = entity.ImplementationStartAt,
                ImplementationEndAt = entity.ImplementationEndAt,
                ChangeTemplate = template,
                IsTemplateComplete = templateValidation.IsComplete,
                TemplateValidationErrors = templateValidation.Errors,
                CategoryIds = categoryIds,
                Sla = ToSlaDto(slaSnapshot)
            };
            return Results.Ok(dto);
        });

        group.MapPost("/", async (
            [FromBody] CreateChangeDto dto,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Change> repo,
            [FromServices] ITicketSlaInitializer ticketSlaInitializer,
            [FromServices] Helpdesk.Application.Services.Tickets.ITicketRefGeneratorService refs,
            [FromServices] IChangeTemplateService changeTemplateService,
            [FromServices] ITicketNotificationService notificationService,
            [FromServices] ITimelineEventBus timelineEventBus,
            [FromServices] IDomainEventPublisher domainEvents,
            [FromServices] ICorrelationContext correlationContext,
            [FromServices] ILoggerFactory loggerFactory,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var normalizedChangeType = changeTemplateService.NormalizeChangeType(dto.ChangeType);
            if (string.IsNullOrWhiteSpace(normalizedChangeType))
            {
                return Results.BadRequest("ChangeType must be Standard, Normal, or Emergency.");
            }

            if (string.IsNullOrWhiteSpace(dto.OrganizationId))
            {
                return Results.BadRequest("OrganizationId is required when creating a change.");
            }

            var implementationWindowError = ValidateImplementationWindow(dto.ImplementationStartAt, dto.ImplementationEndAt, requireBoth: true);
            if (implementationWindowError is not null)
            {
                return implementationWindowError;
            }

            var organization = await db.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == dto.OrganizationId, token);
            if (organization is null)
            {
                return Results.BadRequest("The selected organization could not be found.");
            }

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanCreateChange(organization.Id, null, null))
            {
                return Results.Forbid();
            }

            var participantValidation = await ValidateChangeParticipantsAsync(
                db,
                organization,
                dto.RequestedForUserId,
                dto.ImplementorUserId,
                dto.ApproverUserIds,
                requireRequestedForAndImplementor: true,
                token);
            if (participantValidation is not null)
            {
                return participantValidation;
            }

            var templateValidation = changeTemplateService.ValidateTemplate(normalizedChangeType, dto.ChangeTemplate);
            var lifecycleState = ResolveInitialLifecycleState(templateValidation.IsComplete, dto.ApproverUserIds, access.CanApproveChange(organization.Id));

            var categoryIds = NormalizeCategoryIds(dto.CategoryIds);
            var validation = await ValidateCategorySelectionAsync(
                db,
                categoryIds,
                TicketCategoryType.Change,
                token, dto.OrganizationId);
            if (validation is not null)
            {
                return validation;
            }

            var entity = new Change
            {
                Title = dto.Title,
                Description = dto.Description,
                Priority = dto.Priority,
                CustomerId = null,
                OrganizationId = organization.Id,
                DueDate = dto.DueDate,
                ChangeType = normalizedChangeType,
                RequestedForUserId = dto.RequestedForUserId,
                ImplementorUserId = dto.ImplementorUserId,
                AssignedToId = dto.ImplementorUserId,
                ApproverUserIds = NormalizeUserIds(dto.ApproverUserIds),
                TrackingId = await refs.NextReferenceAsync("CHG"),
                LifecycleState = lifecycleState,
                ImplementationStartAt = dto.ImplementationStartAt,
                ImplementationEndAt = dto.ImplementationEndAt,
                ChangeTemplateJson = changeTemplateService.SerializeTemplate(dto.ChangeTemplate)
            };
            var participantLookup = await BuildParticipantLookupAsync(db, [entity], token);
            AddChangeListeners(entity, participantLookup);
            SeedApprovalRecords(entity, participantLookup);
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
                db.ChangeCategoryLinks.AddRange(categoryIds.Select(categoryId => new ChangeCategoryLink
                {
                    ChangeId = created.Id,
                    TicketCategoryId = categoryId
                }));
                await db.SaveChangesAsync(token);
            }

            await AddTimelineEventAsync(
                db,
                timelineEventBus,
                created.Id,
                EffectiveLifecycleState(created) == ChangeLifecycleState.Draft
                    ? "Change saved as draft."
                    : "Change submitted for approval.",
                token);
            foreach (var approval in created.Approvals.Where(_ => EffectiveLifecycleState(created) == ChangeLifecycleState.PendingApproval))
            {
                await AddTimelineEventAsync(db, timelineEventBus, created.Id, $"Approval requested from {approval.ApproverName}.", token);
            }

            await tx.CommitAsync(token);
            await SendChangeCreationNotificationsAsync(notificationService, created, organization.Name, participantLookup, loggerFactory, token);
            await db.SaveChangesAsync(token);
            await domainEvents.PublishAsync(
                new ChangeCreatedDomainEvent(
                    created.Id,
                    created.TrackingId,
                    created.Title,
                    created.OrganizationId,
                    created.ChangeType,
                    created.State,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlationContext)),
                token);
            var createdParticipantLookup = await BuildParticipantLookupAsync(db, [created], token);

            var responseDto = new ChangeDto
            {
                OrganizationId = created.OrganizationId,
                OrganizationName = organization.Name,
                Id = created.Id,
                Title = created.Title,
                Description = created.Description,
                Priority = created.Priority,
                State = created.State,
                LifecycleState = EffectiveLifecycleState(created),
                CreatedAt = created.CreatedAt,
                UpdatedAt = created.UpdatedAt,
                TrackingId = created.TrackingId,
                AssignedToId = created.AssignedToId,
                LastReplierName = created.LastReplierName,
                CustomerOrgName = organization.Name,
                CustomerId = created.CustomerId,
                ChangeType = created.ChangeType,
                RequestedForUserId = created.RequestedForUserId,
                RequestedForUserName = GetUserName(createdParticipantLookup, created.RequestedForUserId),
                RequestedForUserEmail = GetUserEmail(createdParticipantLookup, created.RequestedForUserId),
                ImplementorUserId = created.ImplementorUserId,
                ImplementorUserName = GetUserName(createdParticipantLookup, created.ImplementorUserId),
                ImplementorUserEmail = GetUserEmail(createdParticipantLookup, created.ImplementorUserId),
                ApproverUserIds = created.ApproverUserIds,
                Approvers = GetUsers(createdParticipantLookup, created.ApproverUserIds),
                Approvals = created.Approvals.Select(ToApprovalDto).ToList(),
                CcRecipients = created.CcRecipients.ToList(),
                ImplementationStartAt = created.ImplementationStartAt,
                ImplementationEndAt = created.ImplementationEndAt,
                ChangeTemplate = changeTemplateService.DeserializeTemplate(created.ChangeTemplateJson),
                IsTemplateComplete = templateValidation.IsComplete,
                TemplateValidationErrors = templateValidation.Errors,
                CategoryIds = categoryIds,
            };
            return Results.Created($"/api/v1/changes/{created.Id}", responseDto);
        });

        group.MapPut("/{id}", async (
            [FromRoute] string id,
            [FromBody] UpdateChangeDto dto,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Change> repo,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            [FromServices] ITicketSlaService ticketSlaService,
            [FromServices] ITicketSlaRepository ticketSlaRepository,
            [FromServices] ISlaClockService slaClockService,
            [FromServices] ISlaEscalationEvaluator escalationEvaluator,
            [FromServices] IDomainEventPublisher domainEvents,
            [FromServices] ICorrelationContext correlationContext,
            [FromServices] ILoggerFactory loggerFactory,
            [FromServices] IChangeTemplateService changeTemplateService,
            [FromServices] ITicketNotificationService notificationService,
            [FromServices] ITimelineEventBus timelineEventBus,
            CancellationToken token) =>
        {
            if (dto.State.HasValue && !Enum.IsDefined(dto.State.Value))
                return Results.BadRequest("Invalid change ticket state.");
            if (dto.Priority.HasValue && !Enum.IsDefined(dto.Priority.Value))
                return Results.BadRequest("Invalid change priority.");
            if (dto.LifecycleState.HasValue && !Enum.IsDefined(dto.LifecycleState.Value))
                return Results.BadRequest("Invalid change lifecycle state.");

            var existing = await repo.GetAsync(id);
            if (existing is null) return Results.Problem("Change not found", statusCode: 404);

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanManageChange(existing.OrganizationId)) return Results.Forbid();

            var previousState = existing.State;
            var previousTemplateJson = existing.ChangeTemplateJson;
            var previousChangeType = existing.ChangeType;
            var previousLifecycleState = EffectiveLifecycleState(existing);
            if (dto.State.HasValue)
            {
                var stateError = ValidateTicketStateTransition(existing, dto.State.Value);
                if (stateError is not null) return Results.BadRequest(stateError);
            }
            if (dto.LifecycleState == ChangeLifecycleState.ApprovedForImplementation &&
                previousLifecycleState != ChangeLifecycleState.ApprovedForImplementation &&
                !access.CanApproveChange(existing.OrganizationId))
            {
                return Results.Forbid();
            }
            if ((dto.LifecycleState is ChangeLifecycleState.ImplementationInProgress
                    or ChangeLifecycleState.ImplementedSuccess or ChangeLifecycleState.ImplementedBackedOut) &&
                dto.LifecycleState != previousLifecycleState &&
                previousLifecycleState is not (ChangeLifecycleState.ApprovedForImplementation or ChangeLifecycleState.ImplementationInProgress))
            {
                return Results.BadRequest("Change must be approved before implementation.");
            }

            if (previousLifecycleState != ChangeLifecycleState.Draft &&
                await HasLockedChangeFieldUpdatesAsync(db, existing, dto, changeTemplateService, token))
            {
                return Results.BadRequest("Change details are locked unless the change is moved back to Draft.");
            }

            existing.State = dto.State ?? existing.State;
            ApplyClosedAtTransition(existing, previousState, existing.State);
            existing.Priority = dto.Priority ?? existing.Priority;

            if (dto.OrganizationId is not null)
            {
                if (string.IsNullOrWhiteSpace(dto.OrganizationId))
                {
                    return Results.BadRequest("OrganizationId cannot be empty.");
                }

                var organization = await db.Organizations
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == dto.OrganizationId, token);
                if (organization is null)
                {
                    return Results.BadRequest("The selected organization could not be found.");
                }
                if (!access.CanManageChange(organization.Id))
                {
                    return Results.Forbid();
                }

                existing.CustomerId = null;
                existing.OrganizationId = organization.Id;
            }

            var currentOrganization = await db.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == existing.OrganizationId, token);
            if (currentOrganization is null)
            {
                return Results.BadRequest("The selected organization could not be found.");
            }

            if (dto.RequestedForUserId is not null)
            {
                existing.RequestedForUserId = string.IsNullOrWhiteSpace(dto.RequestedForUserId) ? null : dto.RequestedForUserId;
            }

            if (dto.ImplementorUserId is not null)
            {
                existing.ImplementorUserId = string.IsNullOrWhiteSpace(dto.ImplementorUserId) ? null : dto.ImplementorUserId;
                existing.AssignedToId = existing.ImplementorUserId;
            }

            if (dto.ApproverUserIds is not null)
            {
                existing.ApproverUserIds = NormalizeUserIds(dto.ApproverUserIds);
            }

            var participantValidation = await ValidateChangeParticipantsAsync(
                db,
                currentOrganization,
                existing.RequestedForUserId,
                existing.ImplementorUserId,
                existing.ApproverUserIds,
                requireRequestedForAndImplementor: true,
                token);
            if (participantValidation is not null)
            {
                return participantValidation;
            }

            if (dto.ApproverUserIds is not null)
            {
                var updateParticipantLookup = await BuildParticipantLookupAsync(db, [existing], token);
                AddChangeListeners(existing, updateParticipantLookup);
                await ReconcileApprovalRecordsAsync(db, existing, updateParticipantLookup, token);
            }

            if (!string.IsNullOrWhiteSpace(dto.ChangeType))
            {
                var normalizedChangeType = changeTemplateService.NormalizeChangeType(dto.ChangeType);
                if (string.IsNullOrWhiteSpace(normalizedChangeType))
                {
                    return Results.BadRequest("ChangeType must be Standard, Normal, or Emergency.");
                }

                existing.ChangeType = normalizedChangeType;
            }

            if (dto.ChangeTemplate is not null)
            {
                existing.ChangeTemplateJson = changeTemplateService.SerializeTemplate(dto.ChangeTemplate);
            }

            if (dto.ImplementationStartAt.HasValue)
            {
                existing.ImplementationStartAt = dto.ImplementationStartAt.Value;
            }

            if (dto.ImplementationEndAt.HasValue)
            {
                existing.ImplementationEndAt = dto.ImplementationEndAt.Value;
            }

            var implementationWindowUpdateError = ValidateImplementationWindow(
                existing.ImplementationStartAt,
                existing.ImplementationEndAt,
                requireBoth: false);
            if (implementationWindowUpdateError is not null)
            {
                return implementationWindowUpdateError;
            }

            var templateChanged = !string.Equals(previousTemplateJson, existing.ChangeTemplateJson, StringComparison.Ordinal)
                || !string.Equals(previousChangeType, existing.ChangeType, StringComparison.Ordinal);

            if (dto.LifecycleState.HasValue)
            {
                if (previousLifecycleState != ChangeLifecycleState.Draft &&
                    dto.LifecycleState.Value != ChangeLifecycleState.Draft &&
                    dto.LifecycleState.Value == ChangeLifecycleState.ApprovedForImplementation &&
                    await db.ChangeApprovals.AnyAsync(
                        x => x.ChangeId == existing.Id && x.Status == ChangeApprovalStatus.Pending,
                        token))
                {
                    return Results.BadRequest("Change cannot be approved for implementation while approvals are pending.");
                }

                existing.LifecycleState = dto.LifecycleState.Value;
            }

            ApplyTicketStateForLifecycle(existing, previousState);

            var currentTemplate = changeTemplateService.DeserializeTemplate(existing.ChangeTemplateJson);
            var currentTemplateValidation = changeTemplateService.ValidateTemplate(existing.ChangeType, currentTemplate);
            var movedBackToDraft = previousLifecycleState != ChangeLifecycleState.Draft &&
                EffectiveLifecycleState(existing) == ChangeLifecycleState.Draft;
            var submittedFromDraft = previousLifecycleState == ChangeLifecycleState.Draft &&
                dto.LifecycleState.HasValue &&
                dto.LifecycleState.Value != ChangeLifecycleState.Draft;

            if (submittedFromDraft)
            {
                existing.LifecycleState = ResolveInitialLifecycleState(
                    currentTemplateValidation.IsComplete,
                    existing.ApproverUserIds,
                    access.CanApproveChange(existing.OrganizationId));
                var updateParticipantLookup = await BuildParticipantLookupAsync(db, [existing], token);
                AddChangeListeners(existing, updateParticipantLookup);
                await ReconcileApprovalRecordsAsync(db, existing, updateParticipantLookup, token);
                await ResetApprovalReviewStateAsync(db, existing.Id, token);
            }
            else if (movedBackToDraft)
            {
                await ResetApprovalReviewStateAsync(db, existing.Id, token);
            }

            if (existing.State == TicketState.Resolved)
            {
                if (!currentTemplateValidation.IsComplete)
                {
                    return Results.BadRequest(new
                    {
                        message = "Change template must be complete before resolving the change.",
                        errors = currentTemplateValidation.Errors
                    });
                }

            }

            existing.UpdatedAt = DateTime.UtcNow;

            await using var tx = await db.Database.BeginTransactionAsync(token);

            if (dto.CategoryIds is not null)
            {
                var categoryIds = NormalizeCategoryIds(dto.CategoryIds);
                var validation = await ValidateCategorySelectionAsync(
                    db,
                    categoryIds,
                    TicketCategoryType.Change,
                    token, existing.OrganizationId,
                    await db.ChangeCategoryLinks.Where(x => x.ChangeId == id).Select(x => x.TicketCategoryId).ToListAsync(token));
                if (validation is not null)
                {
                    return validation;
                }

                await db.ChangeCategoryLinks
                    .Where(x => x.ChangeId == id)
                    .ExecuteDeleteAsync(token);

                if (categoryIds.Count > 0)
                {
                    db.ChangeCategoryLinks.AddRange(categoryIds.Select(categoryId => new ChangeCategoryLink
                    {
                        ChangeId = id,
                        TicketCategoryId = categoryId
                    }));
                }

                await db.SaveChangesAsync(token);
            }

            var updated = await repo.UpdateAsync(existing);
            await tx.CommitAsync(token);

            if (movedBackToDraft)
            {
                await AddTimelineEventAsync(db, timelineEventBus, updated!.Id, "Change moved back to draft; approval review state cleared.", token);
            }
            else if (submittedFromDraft)
            {
                await AddTimelineEventAsync(db, timelineEventBus, updated!.Id, "Change submitted for approval.", token);
                var notificationParticipantLookup = await BuildParticipantLookupAsync(db, [updated], token);
                var notificationOrganizationName = await db.Organizations
                    .AsNoTracking()
                    .Where(x => x.Id == updated.OrganizationId)
                    .Select(x => x.Name)
                    .FirstOrDefaultAsync(token);
                var approvals = await db.ChangeApprovals
                    .Where(x => x.ChangeId == updated.Id)
                    .ToListAsync(token);
                foreach (var approval in approvals.Where(_ => EffectiveLifecycleState(updated) == ChangeLifecycleState.PendingApproval))
                {
                    await AddTimelineEventAsync(db, timelineEventBus, updated.Id, $"Approval requested from {approval.ApproverName}.", token);
                }

                await SendChangeCreationNotificationsAsync(
                    notificationService,
                    updated,
                    notificationOrganizationName ?? string.Empty,
                    notificationParticipantLookup,
                    loggerFactory,
                    token);
                await db.SaveChangesAsync(token);
            }

            var updatedLifecycleState = EffectiveLifecycleState(updated!);
            if (previousLifecycleState != updatedLifecycleState &&
                updatedLifecycleState == ChangeLifecycleState.ImplementationInProgress)
            {
                await AddTimelineEventAsync(db, timelineEventBus, updated!.Id, "Change implementation is in progress.", token);
                await SendChangeLifecycleNotificationsAsync(
                    notificationService,
                    updated,
                    "implementation-in-progress",
                    loggerFactory,
                    db,
                    token);
            }
            else if (previousLifecycleState != updatedLifecycleState &&
                updatedLifecycleState is ChangeLifecycleState.ImplementedSuccess or ChangeLifecycleState.ImplementedBackedOut)
            {
                await AddTimelineEventAsync(
                    db,
                    timelineEventBus,
                    updated!.Id,
                    $"Change implemented - {FormatChangeCompletionState(updatedLifecycleState)}.",
                    token);
                await SendChangeLifecycleNotificationsAsync(
                    notificationService,
                    updated,
                    "implemented",
                    loggerFactory,
                    db,
                    token);
            }

            await domainEvents.PublishAsync(
                new ChangeUpdatedDomainEvent(
                    updated!.Id,
                    updated.TrackingId,
                    updated.Title,
                    updated.OrganizationId,
                    updated.ChangeType,
                    updated.State,
                    templateChanged,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlationContext)),
                token);

            if (previousState != updated.State)
            {
                await domainEvents.PublishAsync(
                    new ChangeStateChangedDomainEvent(
                        updated.Id,
                        updated.TrackingId,
                        updated.OrganizationId,
                        previousState,
                        updated.State,
                        DateTimeOffset.UtcNow,
                        GetCorrelationId(correlationContext)),
                    token);
            }

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
                loggerFactory.CreateLogger("ChangeSla"));

            var updatedParticipantLookup = await BuildParticipantLookupAsync(db, [updated], token);
            var updatedApprovals = (await db.ChangeApprovals
                    .AsNoTracking()
                    .Where(x => x.ChangeId == updated.Id)
                    .OrderBy(x => x.ApproverName)
                    .ToListAsync(token))
                .Select(ToApprovalDto)
                .ToList();

            var resultDto = new ChangeDto
            {
                OrganizationId = updated.OrganizationId,
                OrganizationName = await db.Organizations
                    .AsNoTracking()
                    .Where(x => x.Id == updated.OrganizationId)
                    .Select(x => x.Name)
                    .FirstOrDefaultAsync(token),
                Id = updated.Id,
                Title = updated.Title,
                Description = updated.Description,
                Priority = updated.Priority,
                State = updated.State,
                LifecycleState = EffectiveLifecycleState(updated),
                CreatedAt = updated.CreatedAt,
                UpdatedAt = updated.UpdatedAt,
                TrackingId = updated.TrackingId,
                AssignedToId = updated.AssignedToId,
                LastReplierName = updated.LastReplierName,
                CustomerOrgName = await db.Organizations
                    .AsNoTracking()
                    .Where(x => x.Id == updated.OrganizationId)
                    .Select(x => x.Name)
                    .FirstOrDefaultAsync(token),
                CustomerId = updated.CustomerId,
                CustomerName = !string.IsNullOrWhiteSpace(updated.CustomerId)
                    ? await db.Customers
                        .AsNoTracking()
                        .Where(x => x.Id == updated.CustomerId)
                        .Select(x => x.Name)
                        .FirstOrDefaultAsync(token)
                    : null,
                CustomerEmail = !string.IsNullOrWhiteSpace(updated.CustomerId)
                    ? await db.Customers
                        .AsNoTracking()
                        .Where(x => x.Id == updated.CustomerId)
                        .Select(x => x.Email)
                        .FirstOrDefaultAsync(token)
                    : null,
                ChangeType = updated.ChangeType,
                RequestedForUserId = updated.RequestedForUserId,
                RequestedForUserName = GetUserName(updatedParticipantLookup, updated.RequestedForUserId),
                RequestedForUserEmail = GetUserEmail(updatedParticipantLookup, updated.RequestedForUserId),
                ImplementorUserId = updated.ImplementorUserId,
                ImplementorUserName = GetUserName(updatedParticipantLookup, updated.ImplementorUserId),
                ImplementorUserEmail = GetUserEmail(updatedParticipantLookup, updated.ImplementorUserId),
                ApproverUserIds = updated.ApproverUserIds,
                Approvers = GetUsers(updatedParticipantLookup, updated.ApproverUserIds),
                Approvals = updatedApprovals,
                CcRecipients = updated.CcRecipients.ToList(),
                ImplementationStartAt = updated.ImplementationStartAt,
                ImplementationEndAt = updated.ImplementationEndAt,
                ChangeTemplate = currentTemplate,
                IsTemplateComplete = currentTemplateValidation.IsComplete,
                TemplateValidationErrors = currentTemplateValidation.Errors,
                CategoryIds = await db.ChangeCategoryLinks
                    .Where(x => x.ChangeId == updated.Id)
                    .Select(x => x.TicketCategoryId)
                    .ToListAsync(token),
                Sla = ToSlaDto(slaSnapshot)
            };
            return Results.Ok(resultDto);
        })
        .RequireAuthorization("ChangeManager");

        group.MapPost("/{id}/state", async (
            string id, ChangeStateRequest req, ClaimsPrincipal user,
            HelpdeskDbContext db, ICurrentUserAccessService accessService,
            IDomainEventPublisher domainEvents, ICorrelationContext correlationContext,
            ITimelineEventBus timelineEventBus, IChangeTemplateService changeTemplateService,
            CancellationToken token) =>
        {
            if (!Enum.IsDefined(req.NewState)) return Results.BadRequest("Invalid change ticket state.");
            var change = await db.Changes.FirstOrDefaultAsync(x => x.Id == id, token);
            if (change is null) return Results.NotFound();
            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanManageChange(change.OrganizationId)) return Results.Forbid();
            var validation = ValidateTicketStateTransition(change, req.NewState);
            if (validation is not null) return Results.BadRequest(validation);
            var template = changeTemplateService.DeserializeTemplate(change.ChangeTemplateJson);
            var templateValidation = changeTemplateService.ValidateTemplate(change.ChangeType, template);
            if (change.State == req.NewState)
                return Results.Ok(ToQuickLifecycleDto(change, EffectiveLifecycleState(change), template, templateValidation));

            var previousState = change.State;
            change.State = req.NewState;
            ApplyClosedAtTransition(change, previousState, change.State);
            change.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(token);
            await AddTimelineEventAsync(db, timelineEventBus, change.Id,
                $"Ticket state changed from {previousState} to {change.State}.", token);
            await domainEvents.PublishAsync(new ChangeStateChangedDomainEvent(
                change.Id, change.TrackingId, change.OrganizationId, previousState, change.State,
                DateTimeOffset.UtcNow, GetCorrelationId(correlationContext)), token);
            return Results.Ok(ToQuickLifecycleDto(change, EffectiveLifecycleState(change), template, templateValidation));
        })
        .RequireAuthorization("ChangeManager")
        .WithName("UpdateChangeTicketState")
        .WithSummary("Updates the ticket state of a draft change; lifecycle transitions use the lifecycle endpoint");

        group.MapPost("/bulk/state", async (
            [FromBody] BulkStateChangeRequest req,
            [FromServices] IRepository<Change> repo,
            HelpdeskDbContext db, ClaimsPrincipal user,
            ICurrentUserAccessService accessService, CancellationToken token) =>
        {
            if (req.Ids is null || req.Ids.Count == 0) return Results.BadRequest("No ids");
            if (!Enum.IsDefined(req.NewState)) return Results.BadRequest("Invalid change ticket state.");
            var changes = (await repo.GetAllAsync()).Where(x => req.Ids.Contains(x.Id)).ToList();
            if (changes.Count != req.Ids.Distinct().Count()) return Results.NotFound();
            var access = await accessService.ResolveAsync(user, token);
            if (changes.Any(x => !access.CanManageChange(x.OrganizationId))) return Results.Forbid();
            foreach (var change in changes)
            {
                var validation = ValidateTicketStateTransition(change, req.NewState);
                if (validation is not null) return Results.BadRequest(validation);
            }
            var changed = changes.Where(x => x.State != req.NewState).ToList();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            foreach (var change in changed)
            {
                var previousState = change.State;
                change.State = req.NewState;
                ApplyClosedAtTransition(change, previousState, change.State);
                change.UpdatedAt = DateTime.UtcNow;
            }

            foreach (var change in changed)
            {
                await repo.UpdateAsync(change);
            }
            await transaction.CommitAsync(token);
            return Results.Ok(new { updated = changed.Count });
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("BulkUpdateChangeState")
        .WithSummary("Bulk update change state")
        .WithDescription("Updates the state of multiple changes in one request.")
        .WithTags("Changes");

        group.MapPost("/{id}/assignee", async (
            string id, ChangeAssigneeRequest req, ClaimsPrincipal user,
            HelpdeskDbContext db, ICurrentUserAccessService accessService,
            IRepository<Change> repo, CancellationToken token) =>
        {
            var change = await repo.GetAsync(id);
            if (change is null) return Results.NotFound();
            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanManageChange(change.OrganizationId)) return Results.Forbid();
            var validation = await ValidateImplementorAssignmentAsync(db, change, req.ImplementorUserId, token);
            if (validation is not null) return validation;
            change.ImplementorUserId = req.ImplementorUserId!.Trim();
            change.AssignedToId = change.ImplementorUserId;
            change.UpdatedAt = DateTime.UtcNow;
            await repo.UpdateAsync(change);
            var assigned = await db.Users.AsNoTracking().Where(x => x.Id == change.ImplementorUserId)
                .Select(x => new UserDto(x.Id, x.Name, x.Email, x.Role, x.IsTestUser, x.OrganizationId))
                .SingleAsync(token);
            return Results.Ok(assigned);
        }).WithName("AssignChangeImplementor");

        group.MapPost("/bulk/assign", async (
            [FromBody] BulkAssignRequest req,
            [FromServices] IRepository<Change> repo,
            HelpdeskDbContext db, ClaimsPrincipal user,
            ICurrentUserAccessService accessService, CancellationToken token) =>
        {
            if (req.Ids is null || req.Ids.Count == 0) return Results.BadRequest("No ids");
            var changes = (await repo.GetAllAsync()).Where(x => req.Ids.Contains(x.Id)).ToList();
            if (changes.Count != req.Ids.Distinct().Count()) return Results.NotFound();
            var access = await accessService.ResolveAsync(user, token);
            if (changes.Any(x => !access.CanManageChange(x.OrganizationId))) return Results.Forbid();
            // Validate every target before writing any of them. Keep the admin-only policy.
            foreach (var change in changes)
            {
                var validation = await ValidateImplementorAssignmentAsync(db, change, req.AssignedToId, token);
                if (validation is not null) return validation;
            }
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            foreach (var change in changes)
            {
                change.ImplementorUserId = req.AssignedToId!.Trim();
                change.AssignedToId = change.ImplementorUserId;
                change.UpdatedAt = DateTime.UtcNow;
                await repo.UpdateAsync(change);
            }

            await transaction.CommitAsync(token);

            return Results.Ok(new { updated = changes.Count });
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("BulkAssignChanges")
        .WithSummary("Bulk assign changes to a user")
        .WithDescription("Assigns the selected changes to the specified team member.")
        .WithTags("Changes");

        group.MapPost("/{id}/lifecycle", async (
            [FromRoute] string id,
            [FromBody] QuickChangeLifecycleRequest req,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IChangeTemplateService changeTemplateService,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            [FromServices] ITicketNotificationService notificationService,
            [FromServices] ITimelineEventBus timelineEventBus,
            [FromServices] IDomainEventPublisher domainEvents,
            [FromServices] ICorrelationContext correlationContext,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken token) =>
        {
            if (!Enum.IsDefined(req.LifecycleState)) return Results.BadRequest("Invalid change lifecycle state.");
            var change = await db.Changes.FirstOrDefaultAsync(x => x.Id == id, token);
            if (change is null)
            {
                return Results.NotFound();
            }

            var access = await accessService.ResolveAsync(user, token);
            var isApproval = req.LifecycleState == ChangeLifecycleState.ApprovedForImplementation;
            if (isApproval
                ? !access.CanApproveChange(change.OrganizationId)
                : !access.CanManageChange(change.OrganizationId))
            {
                return Results.Forbid();
            }

            var previousState = change.State;
            var previousLifecycleState = EffectiveLifecycleState(change);
            var requestedLifecycleState = req.LifecycleState;
            var transitionError = ValidateQuickLifecycleTransition(previousLifecycleState, requestedLifecycleState);
            if (transitionError is not null)
            {
                return Results.BadRequest(transitionError);
            }

            if (previousLifecycleState == requestedLifecycleState)
            {
                return Results.Ok(ToQuickLifecycleDto(change, previousLifecycleState, null, null));
            }

            var currentTemplate = changeTemplateService.DeserializeTemplate(change.ChangeTemplateJson);
            var currentTemplateValidation = changeTemplateService.ValidateTemplate(change.ChangeType, currentTemplate);
            var nextLifecycleState = requestedLifecycleState;
            if (previousLifecycleState == ChangeLifecycleState.Draft &&
                requestedLifecycleState == ChangeLifecycleState.Submitted)
            {
                nextLifecycleState = ResolveInitialLifecycleState(
                    currentTemplateValidation.IsComplete,
                    change.ApproverUserIds,
                    access.CanApproveChange(change.OrganizationId));
            }

            if (nextLifecycleState == ChangeLifecycleState.ApprovedForImplementation &&
                await db.ChangeApprovals.AnyAsync(
                    x => x.ChangeId == change.Id && x.Status == ChangeApprovalStatus.Pending,
                    token))
            {
                return Results.BadRequest("Change cannot be approved for implementation while approvals are pending.");
            }

            if (IsImplementedLifecycleState(nextLifecycleState) && !currentTemplateValidation.IsComplete)
            {
                return Results.BadRequest(new
                {
                    message = "Change template must be complete before implementing the change.",
                    errors = currentTemplateValidation.Errors
                });
            }

            change.LifecycleState = nextLifecycleState;
            ApplyTicketStateForLifecycle(change, previousState);
            change.UpdatedAt = DateTime.UtcNow;

            var movedBackToDraft = previousLifecycleState != ChangeLifecycleState.Draft &&
                nextLifecycleState == ChangeLifecycleState.Draft;
            var submittedFromDraft = previousLifecycleState == ChangeLifecycleState.Draft &&
                requestedLifecycleState == ChangeLifecycleState.Submitted;

            if (submittedFromDraft)
            {
                var participantLookup = await BuildParticipantLookupAsync(db, [change], token);
                AddChangeListeners(change, participantLookup);
                await ReconcileApprovalRecordsAsync(db, change, participantLookup, token);
                await ResetApprovalReviewStateAsync(db, change.Id, token);
            }
            else if (movedBackToDraft)
            {
                await ResetApprovalReviewStateAsync(db, change.Id, token);
            }

            await db.SaveChangesAsync(token);

            if (movedBackToDraft)
            {
                await AddTimelineEventAsync(db, timelineEventBus, change.Id, "Change moved back to draft; approval review state cleared.", token);
            }
            else if (submittedFromDraft)
            {
                await AddTimelineEventAsync(db, timelineEventBus, change.Id, "Change submitted for approval.", token);
                var notificationParticipantLookup = await BuildParticipantLookupAsync(db, [change], token);
                var notificationOrganizationName = await db.Organizations
                    .AsNoTracking()
                    .Where(x => x.Id == change.OrganizationId)
                    .Select(x => x.Name)
                    .FirstOrDefaultAsync(token);
                var approvals = await db.ChangeApprovals
                    .Where(x => x.ChangeId == change.Id)
                    .ToListAsync(token);
                foreach (var approval in approvals.Where(_ => EffectiveLifecycleState(change) == ChangeLifecycleState.PendingApproval))
                {
                    await AddTimelineEventAsync(db, timelineEventBus, change.Id, $"Approval requested from {approval.ApproverName}.", token);
                }

                await SendChangeCreationNotificationsAsync(
                    notificationService,
                    change,
                    notificationOrganizationName ?? string.Empty,
                    notificationParticipantLookup,
                    loggerFactory,
                    token);
                await db.SaveChangesAsync(token);
            }

            if (previousLifecycleState != nextLifecycleState &&
                nextLifecycleState == ChangeLifecycleState.ImplementationInProgress)
            {
                await AddTimelineEventAsync(db, timelineEventBus, change.Id, "Change implementation is in progress.", token);
                await SendChangeLifecycleNotificationsAsync(
                    notificationService,
                    change,
                    "implementation-in-progress",
                    loggerFactory,
                    db,
                    token);
            }
            else if (previousLifecycleState != nextLifecycleState &&
                IsImplementedLifecycleState(nextLifecycleState))
            {
                await AddTimelineEventAsync(
                    db,
                    timelineEventBus,
                    change.Id,
                    $"Change implemented - {FormatChangeCompletionState(nextLifecycleState)}.",
                    token);
                await SendChangeLifecycleNotificationsAsync(
                    notificationService,
                    change,
                    "implemented",
                    loggerFactory,
                    db,
                    token);
                await ticketSlaCompletionService.HandleTicketClosedAsync(change.Id, "quick-lifecycle", DateTimeOffset.UtcNow);
            }

            await domainEvents.PublishAsync(
                new ChangeUpdatedDomainEvent(
                    change.Id,
                    change.TrackingId,
                    change.Title,
                    change.OrganizationId,
                    change.ChangeType,
                    change.State,
                    TemplateChanged: false,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlationContext)),
                token);

            if (previousState != change.State)
            {
                await domainEvents.PublishAsync(
                    new ChangeStateChangedDomainEvent(
                        change.Id,
                        change.TrackingId,
                        change.OrganizationId,
                        previousState,
                        change.State,
                        DateTimeOffset.UtcNow,
                        GetCorrelationId(correlationContext)),
                    token);
            }

            return Results.Ok(ToQuickLifecycleDto(change, nextLifecycleState, currentTemplate, currentTemplateValidation));
        })
        .WithName("QuickUpdateChangeLifecycle")
        .WithSummary("Quick update change lifecycle")
        .WithDescription("Updates one change lifecycle state from a list row state picker.")
        .WithTags("Changes");

        group.MapGet("/{id}/worklogs", async (
            [FromRoute] string id,
            HttpContext context,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<WorkLog> repo,
            CancellationToken ct) =>
        {
            var authorization = await AuthorizeChangeAsync(id, context.User, accessService, db, requireManager: false, ct);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
            }

            var allLogs = await repo.GetAllAsync();
            var logs = allLogs
                .Where(log => log.TicketId == id)
                .Where(log => CanReadInternalChange(authorization.Access!, authorization.OrganizationId) || !log.IsInternalNote)
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
            var authorization = await AuthorizeChangeAsync(id, context.User, accessService, db, requireManager: false, ct, requireContributor: true);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
            }

            if ((dto.IsInternalNote || dto.Hours != 0) && !authorization.Access!.CanManageChange(authorization.OrganizationId))
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
                TechnicianName = techName
            };
            return Results.Created($"/api/v1/worklogs/{response.Id}", response);
        });

        group.MapDelete("/{id}", async (
            [FromRoute] string id,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRepository<Change> repo,
            CancellationToken token) =>
        {
            var change = await repo.GetAsync(id);
            if (change is null) return Results.Problem("Change not found", statusCode: 404);

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanDeleteChange(change.OrganizationId)) return Results.Forbid();

            return await repo.DeleteAsync(id) ? Results.NoContent() : Results.Problem("Change not found", statusCode: 404);
        })
        .RequireAuthorization("ChangeAccess");
    }

    private static bool CanReadInternalChange(CurrentUserAccessProfile access, string? organizationId) =>
        access.CanManageChange(organizationId) ||
        access.HasPermission(Helpdesk.Shared.Auth.HelpdeskPermissions.ChangeRead, organizationId);

    private static async Task<IResult> GetPublicChangeApproval(
        [FromQuery] string trackingId,
        [FromQuery] string email,
        [FromQuery] string token,
        [FromServices] IPublicTicketLinkSigner signer,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITimelineEventBus timelineEventBus,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackingId) ||
            string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(token))
        {
            return Results.BadRequest("Tracking ID, email, and token are required.");
        }

        var change = await db.Changes
            .Include(x => x.Approvals)
            .FirstOrDefaultAsync(x => x.TrackingId == trackingId, ct);
        if (change is null)
        {
            return Results.Problem("Change not found", statusCode: 404);
        }

        if (!signer.ValidateToken(token, change.TrackingId, email))
        {
            return Results.Unauthorized();
        }

        var participantLookup = await BuildParticipantLookupAsync(db, [change], ct);
        var approval = change.Approvals.FirstOrDefault(x =>
            string.Equals(x.ApproverEmail, email, StringComparison.OrdinalIgnoreCase));
        var requestedForEmail = GetUserEmail(participantLookup, change.RequestedForUserId);
        var implementorEmail = GetUserEmail(participantLookup, change.ImplementorUserId);
        var isReadOnlyRecipient =
            string.Equals(requestedForEmail, email, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(implementorEmail, email, StringComparison.OrdinalIgnoreCase) ||
            change.CcRecipients.Any(x => string.Equals(x, email, StringComparison.OrdinalIgnoreCase));
        if (approval is null && !isReadOnlyRecipient)
        {
            return Results.Unauthorized();
        }

        var organizationName = await db.Organizations
            .AsNoTracking()
            .Where(x => x.Id == change.OrganizationId)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(ct);

        if (approval is not null && !approval.ViewedAtUtc.HasValue)
        {
            approval.ViewedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await AddTimelineEventAsync(db, timelineEventBus, change.Id, $"{approval.ApproverName} opened approval link.", ct);
        }

        var template = DeserializeTemplateForPublicView(change.ChangeTemplateJson);
        return Results.Ok(new PublicChangeApprovalDto
        {
            ChangeId = change.Id,
            TrackingId = change.TrackingId,
            Title = change.Title,
            Description = change.Description,
            ChangeType = change.ChangeType,
            LifecycleState = EffectiveLifecycleState(change),
            OrganizationName = organizationName,
            RequestedForName = GetUserName(participantLookup, change.RequestedForUserId),
            ImplementorName = GetUserName(participantLookup, change.ImplementorUserId),
            ImplementationStartAt = change.ImplementationStartAt,
            ImplementationEndAt = change.ImplementationEndAt,
            ScopeOfChange = template?.ScopeOfChange,
            AffectedSystems = template?.AffectedSystems ?? new(),
            ImplementationSteps = template?.ImplementationSteps ?? new(),
            ValidationSteps = template?.ValidationSteps ?? new(),
            RollbackPlan = template?.RollbackPlan,
            RollbackReference = template?.RollbackReference,
            CanReview = approval is not null,
            ApprovalStatus = approval?.Status ?? ChangeApprovalStatus.Pending,
            ReviewedAtUtc = approval?.ReviewedAtUtc,
            ViewedAtUtc = approval?.ViewedAtUtc
        });
    }

    private static async Task<IResult> ReviewPublicChangeApproval(
        [FromBody] ReviewChangeApprovalRequest request,
        [FromServices] IPublicTicketLinkSigner signer,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITicketNotificationService notificationService,
        [FromServices] ITimelineEventBus timelineEventBus,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TrackingId) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Token))
        {
            return Results.BadRequest("Tracking ID, email, and token are required.");
        }

        var change = await db.Changes
            .Include(x => x.Approvals)
            .FirstOrDefaultAsync(x => x.TrackingId == request.TrackingId, ct);
        if (change is null)
        {
            return Results.Problem("Change not found", statusCode: 404);
        }

        if (!signer.ValidateToken(request.Token, change.TrackingId, request.Email))
        {
            return Results.Unauthorized();
        }

        var approval = change.Approvals.FirstOrDefault(x =>
            string.Equals(x.ApproverEmail, request.Email, StringComparison.OrdinalIgnoreCase));
        if (approval is null)
        {
            return Results.Unauthorized();
        }

        if (approval.Status == ChangeApprovalStatus.Pending)
        {
            approval.Status = ChangeApprovalStatus.Reviewed;
            approval.ReviewedAtUtc = DateTimeOffset.UtcNow;
            change.UpdatedAt = DateTime.UtcNow;
            await AddTimelineEventAsync(db, timelineEventBus, change.Id, $"{approval.ApproverName} reviewed/approved the change.", ct);

            if (change.Approvals.All(x => x.Status == ChangeApprovalStatus.Reviewed))
            {
                change.LifecycleState = ChangeLifecycleState.ApprovedForImplementation;
                await AddTimelineEventAsync(db, timelineEventBus, change.Id, "All approvals received; change approved for implementation.", ct);
                await db.SaveChangesAsync(ct);

                var participantLookup = await BuildParticipantLookupAsync(db, [change], ct);
                var organizationName = await db.Organizations
                    .AsNoTracking()
                    .Where(x => x.Id == change.OrganizationId)
                    .Select(x => x.Name)
                    .FirstOrDefaultAsync(ct);
                await SendChangeApprovedNotificationsAsync(
                    notificationService,
                    change,
                    organizationName ?? string.Empty,
                    participantLookup,
                    loggerFactory,
                    ct);
            }
            else
            {
                await db.SaveChangesAsync(ct);
            }
        }

        return Results.Ok(new
        {
            approval.Status,
            approval.ReviewedAtUtc,
            LifecycleState = EffectiveLifecycleState(change)
        });
    }

    private static async Task StreamChangeTimeline(
        [FromRoute] string id,
        HttpContext context,
        [FromServices] ICurrentUserAccessService accessService,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITimelineEventBus eventBus,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var authorization = await AuthorizeChangeAsync(id, context.User, accessService, db, requireManager: false, ct);
        if (authorization.Failure is not null)
        {
            await authorization.Failure.ExecuteAsync(context);
            return;
        }

        var logger = loggerFactory.CreateLogger("ChangeEndpoints");
        var reader = eventBus.Subscribe(id);
        var canReadInternalChange = CanReadInternalChange(authorization.Access!, authorization.OrganizationId);

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Append("Connection", "keep-alive");
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Response.ContentType = "text/event-stream";

        try
        {
            logger.LogInformation("Change timeline stream connected {TicketId}", id);
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

                var currentAuthorization = await AuthorizeChangeAsync(
                    id,
                    context.User,
                    accessService,
                    db,
                    requireManager: false,
                    ct);
                if (currentAuthorization.Failure is not null)
                {
                    logger.LogInformation("Change timeline stream authorization revoked {TicketId}", id);
                    break;
                }

                canReadInternalChange = CanReadInternalChange(currentAuthorization.Access!, currentAuthorization.OrganizationId);

                if (completedTask == waitForDataTask)
                {
                    if (!await waitForDataTask)
                    {
                        break;
                    }

                    while (reader.TryRead(out var evt))
                    {
                        if (!canReadInternalChange && evt.EventType == TimelineEventType.InternalNote)
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
            // Connection terminated by client disconnect/cancellation.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Change timeline stream error {TicketId}", id);
        }
        finally
        {
            logger.LogInformation("Change timeline stream disconnected {TicketId}", id);
            eventBus.Unsubscribe(id, reader);
        }
    }

    private static TicketTimelineEventDto ToTimelineDto(TicketTimelineEvent evt) => new()
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

    private static async Task<ChangeAuthorization> AuthorizeChangeAsync(
        string changeId,
        ClaimsPrincipal user,
        ICurrentUserAccessService accessService,
        HelpdeskDbContext db,
        bool requireManager,
        CancellationToken cancellationToken,
        bool requireContributor = false)
    {
        var change = await db.Changes.AsNoTracking()
            .Where(candidate => candidate.Id == changeId)
            .Select(candidate => new ChangeScope(candidate.OrganizationId, candidate.CustomerId, candidate.RequesterEmail))
            .FirstOrDefaultAsync(cancellationToken);
        if (change is null)
        {
            return new ChangeAuthorization(null, null, Results.NotFound());
        }

        var customer = !string.IsNullOrWhiteSpace(change.CustomerId)
            ? await db.Customers.AsNoTracking()
                .Where(candidate => candidate.Id == change.CustomerId)
                .Select(candidate => new { candidate.Id, candidate.Email })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var access = await accessService.ResolveAsync(user, cancellationToken);
        var allowed = requireManager
            ? access.CanManageChange(change.OrganizationId)
            : requireContributor
                ? access.CanContributeChange(change.OrganizationId, customer?.Id ?? change.CustomerId, customer?.Email ?? change.RequesterEmail)
                : access.CanViewChange(
                change.OrganizationId,
                customer?.Id ?? change.CustomerId,
                customer?.Email ?? change.RequesterEmail);
        return allowed
            ? new ChangeAuthorization(access, change.OrganizationId, null)
            : new ChangeAuthorization(null, null, Results.Forbid());
    }

    private sealed record ChangeScope(string? OrganizationId, string? CustomerId, string? RequesterEmail);

    private sealed record ChangeAuthorization(
        CurrentUserAccessProfile? Access,
        string? OrganizationId,
        IResult? Failure);

    private static async Task<IResult> GetChanges(
        [FromServices] HelpdeskDbContext db,
        [FromServices] ICurrentUserAccessService accessService,
        ClaimsPrincipal user,
        HttpRequest httpRequest,
        CancellationToken cancellationToken,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] TicketState? state = null,
        [FromQuery] ChangeLifecycleState? lifecycleState = null,
        [FromQuery] bool activeOnly = false,
        [FromQuery] bool historicOnly = false,
        [FromQuery] bool includeTotal = true,
        [FromQuery] bool summaryOnly = false,
        [FromQuery] string? q = null)
    {
        if (!TicketListQuery.TryParse(httpRequest.Query, true, out var filters, out var filterError)) return Results.BadRequest(new { error = filterError });
        page = page.HasValue ? Math.Clamp(page.Value, 1, 1_000_000) : null;
        var effectivePageSize = Math.Clamp(pageSize ?? 10, 1, 100);

        var query = filters.Apply(db.Changes.AsNoTracking(), true)
            .Select(c => new
            {
                Change = c,
                OrgName = db.Organizations
                    .Where(o => o.Id == c.OrganizationId)
                    .Select(o => o.Name)
                    .FirstOrDefault(),
                CustomerName = db.Customers
                    .Where(customer => customer.Id == c.CustomerId)
                    .Select(customer => customer.Name)
                    .FirstOrDefault(),
                CustomerEmail = db.Customers
                    .Where(customer => customer.Id == c.CustomerId)
                    .Select(customer => customer.Email)
                    .FirstOrDefault()
            });

        var access = await accessService.ResolveAsync(user, cancellationToken);
        if (!access.IsHelpdeskAdmin)
        {
            var tenantReadOrganizationIds = access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.ChangeRead)
                .Concat(access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.ChangeWrite))
                .Concat(access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.ChangeManager))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var ownOrganizationIds = access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.ChangeUser).ToArray();
            query = query.Where(x =>
                tenantReadOrganizationIds.Contains(x.Change.OrganizationId) ||
                (ownOrganizationIds.Contains(x.Change.OrganizationId) &&
                 !string.IsNullOrWhiteSpace(access.CustomerId) &&
                 x.Change.CustomerId == access.CustomerId));
        }

        if (state is not null)
        {
            query = query.Where(x => x.Change.State == state);
        }

        if (!filters.ExplicitStates)
        {
            if (lifecycleState is not null)
            {
                query = query.Where(x => x.Change.LifecycleState == lifecycleState);
            }
            else if (historicOnly || httpRequest.Query["view"] == "historic")
            {
                query = query.Where(x =>
                    x.Change.LifecycleState == ChangeLifecycleState.ImplementedSuccess ||
                    x.Change.LifecycleState == ChangeLifecycleState.ImplementedBackedOut);
            }
            else if (activeOnly)
            {
                query = query.Where(x =>
                    x.Change.LifecycleState != ChangeLifecycleState.ImplementedSuccess &&
                    x.Change.LifecycleState != ChangeLifecycleState.ImplementedBackedOut);
            }
            else
            {
                query = query.Where(x =>
                    x.Change.LifecycleState != ChangeLifecycleState.ImplementedSuccess &&
                    x.Change.LifecycleState != ChangeLifecycleState.ImplementedBackedOut);
            }
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? query.Where(x =>
                    EF.Functions.ILike(x.Change.Title, like, "\\") ||
                    EF.Functions.ILike(x.Change.Description, like, "\\") ||
                    EF.Functions.ILike(x.Change.TrackingId, like, "\\") ||
                    (x.OrgName != null && EF.Functions.ILike(x.OrgName, like, "\\")) ||
                    (x.CustomerName != null && EF.Functions.ILike(x.CustomerName, like, "\\")) ||
                    (x.CustomerEmail != null && EF.Functions.ILike(x.CustomerEmail, like, "\\")))
                : query.Where(x =>
                    EF.Functions.Like(x.Change.Title, like, "\\") ||
                    EF.Functions.Like(x.Change.Description, like, "\\") ||
                    EF.Functions.Like(x.Change.TrackingId, like, "\\") ||
                    (x.OrgName != null && EF.Functions.Like(x.OrgName, like, "\\")) ||
                    (x.CustomerName != null && EF.Functions.Like(x.CustomerName, like, "\\")) ||
                    (x.CustomerEmail != null && EF.Functions.Like(x.CustomerEmail, like, "\\")));
        }

        var totalCount = includeTotal ? await query.CountAsync(cancellationToken) : 0;

        var orderedQuery = filters.Order(query, "Change")
            .Select(x => new
            {
                x.Change.OrganizationId,
                x.Change.Id,
                x.Change.Title,
                x.Change.Description,
                x.Change.Priority,
                x.Change.State,
                x.Change.LifecycleState,
                x.Change.CreatedAt,
                UpdatedAt = x.Change.UpdatedAt ?? x.Change.CreatedAt,
                x.Change.TrackingId,
                x.Change.AssignedToId,
                x.Change.LastReplierName,
                CustomerOrgName = x.OrgName,
                x.Change.CustomerId,
                x.CustomerName,
                x.CustomerEmail,
                x.Change.ChangeType,
                x.Change.RequestedForUserId,
                x.Change.ImplementorUserId,
                x.Change.ApproverUserIds,
                x.Change.CcRecipients,
                x.Change.ImplementationStartAt,
                x.Change.ImplementationEndAt,
                x.Change.ChangeTemplateJson
            });

        var rawItems = page.HasValue
            ? await orderedQuery
                .Skip((page.Value - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .ToListAsync(cancellationToken)
            : await orderedQuery.ToListAsync(cancellationToken);

        var changeIds = rawItems.Select(x => x.Id).ToList();
        var participantLookup = await BuildParticipantLookupAsync(
            db,
            rawItems.Select(x => new Change
            {
                RequestedForUserId = x.RequestedForUserId,
                ImplementorUserId = x.ImplementorUserId,
                ApproverUserIds = x.ApproverUserIds
            }),
            cancellationToken);
        if (summaryOnly)
        {
            var summaryItems = rawItems.Select(x => new ChangeDto
            {
                OrganizationId = x.OrganizationId,
                Id = x.Id,
                Title = x.Title,
                Description = x.Description,
                Priority = x.Priority,
                State = x.State,
                LifecycleState = x.LifecycleState ?? ChangeLifecycleState.Draft,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt,
                TrackingId = x.TrackingId,
                AssignedToId = x.AssignedToId,
                LastReplierName = x.LastReplierName,
                CustomerOrgName = x.CustomerOrgName,
                CustomerId = x.CustomerId,
                CustomerName = x.CustomerName,
                CustomerEmail = x.CustomerEmail,
                CategoryIds = [],
                ChangeType = x.ChangeType,
                RequestedForUserId = x.RequestedForUserId,
                RequestedForUserName = GetUserName(participantLookup, x.RequestedForUserId),
                RequestedForUserEmail = GetUserEmail(participantLookup, x.RequestedForUserId),
                ImplementorUserId = x.ImplementorUserId,
                ImplementorUserName = GetUserName(participantLookup, x.ImplementorUserId),
                ImplementorUserEmail = GetUserEmail(participantLookup, x.ImplementorUserId),
                ApproverUserIds = x.ApproverUserIds,
                Approvers = GetUsers(participantLookup, x.ApproverUserIds),
                CcRecipients = x.CcRecipients.ToList(),
                ImplementationStartAt = x.ImplementationStartAt,
                ImplementationEndAt = x.ImplementationEndAt,
                IsTemplateComplete = !string.IsNullOrWhiteSpace(x.ChangeTemplateJson)
            }).ToList();

            return Results.Ok(new PagedResponse<ChangeDto>
            {
                Page = page ?? 1,
                PageSize = effectivePageSize,
                TotalCount = includeTotal ? totalCount : summaryItems.Count,
                Items = summaryItems
            });
        }

        var categoryLookup = await db.ChangeCategoryLinks
            .Where(link => changeIds.Contains(link.ChangeId))
            .GroupBy(link => link.ChangeId)
            .ToDictionaryAsync(
                g => g.Key,
                g => g.Select(link => link.TicketCategoryId).ToList(), cancellationToken);
        var items = rawItems.Select(i => new ChangeDto
        {
            OrganizationId = i.OrganizationId,
            OrganizationName = i.CustomerOrgName,
            Id = i.Id,
            Title = i.Title,
            Description = i.Description,
            Priority = i.Priority,
            State = i.State,
            LifecycleState = i.LifecycleState ?? ChangeLifecycleState.Draft,
            CreatedAt = i.CreatedAt,
            UpdatedAt = i.UpdatedAt,
            TrackingId = i.TrackingId,
            AssignedToId = i.AssignedToId,
            LastReplierName = i.LastReplierName,
            CustomerOrgName = i.CustomerOrgName,
            CustomerId = i.CustomerId,
            CustomerName = i.CustomerName,
            CustomerEmail = i.CustomerEmail,
            ChangeType = i.ChangeType,
            RequestedForUserId = i.RequestedForUserId,
            RequestedForUserName = GetUserName(participantLookup, i.RequestedForUserId),
            RequestedForUserEmail = GetUserEmail(participantLookup, i.RequestedForUserId),
            ImplementorUserId = i.ImplementorUserId,
            ImplementorUserName = GetUserName(participantLookup, i.ImplementorUserId),
            ImplementorUserEmail = GetUserEmail(participantLookup, i.ImplementorUserId),
            ApproverUserIds = i.ApproverUserIds,
            Approvers = GetUsers(participantLookup, i.ApproverUserIds),
            CcRecipients = i.CcRecipients.ToList(),
            ImplementationStartAt = i.ImplementationStartAt,
            ImplementationEndAt = i.ImplementationEndAt,
            IsTemplateComplete = !string.IsNullOrWhiteSpace(i.ChangeTemplateJson),
            CategoryIds = categoryLookup.TryGetValue(i.Id, out var ids) ? ids : new List<Guid>()
        }).ToList();

        var response = new PagedResponse<ChangeDto>
        {
            Page = page ?? 1,
            PageSize = effectivePageSize,
            TotalCount = includeTotal ? totalCount : items.Count,
            Items = items
        };
        return Results.Ok(response);
    }

    private static ChangeLifecycleState ResolveInitialLifecycleState(bool isTemplateComplete, IEnumerable<string>? approverUserIds, bool canApprove)
    {
        if (!isTemplateComplete)
        {
            return ChangeLifecycleState.Draft;
        }

        return !canApprove || NormalizeUserIds(approverUserIds).Count > 0
            ? ChangeLifecycleState.PendingApproval
            : ChangeLifecycleState.ApprovedForImplementation;
    }

    private static ChangeLifecycleState EffectiveLifecycleState(Change change) =>
        change.LifecycleState ?? ChangeLifecycleState.Draft;

    private static bool IsImplementedLifecycleState(ChangeLifecycleState state) =>
        state is ChangeLifecycleState.ImplementedSuccess or ChangeLifecycleState.ImplementedBackedOut;

    private static string? ValidateTicketStateTransition(Change change, TicketState requestedState)
    {
        if (!Enum.IsDefined(requestedState)) return "Invalid change ticket state.";
        if (requestedState == change.State) return null;
        if (EffectiveLifecycleState(change) != ChangeLifecycleState.Draft)
            return "Change ticket state is locked outside Draft. Use the lifecycle operation to progress the change.";
        if (requestedState == TicketState.Resolved)
            return "Change must be implemented through the lifecycle operation before it can be resolved.";
        return null;
    }

    private static string? ValidateQuickLifecycleTransition(
        ChangeLifecycleState currentState,
        ChangeLifecycleState requestedState)
    {
        if (currentState == requestedState)
        {
            return null;
        }

        return currentState switch
        {
            ChangeLifecycleState.Draft when requestedState == ChangeLifecycleState.Submitted => null,
            ChangeLifecycleState.PendingApproval when requestedState is ChangeLifecycleState.Draft
                or ChangeLifecycleState.ApprovedForImplementation => null,
            ChangeLifecycleState.ApprovedForImplementation
                when requestedState is ChangeLifecycleState.ImplementationInProgress
                    or ChangeLifecycleState.ImplementedSuccess
                    or ChangeLifecycleState.ImplementedBackedOut => null,
            ChangeLifecycleState.ImplementationInProgress
                when requestedState is ChangeLifecycleState.ImplementedSuccess
                    or ChangeLifecycleState.ImplementedBackedOut => null,
            ChangeLifecycleState.ImplementedSuccess => "Implemented changes are terminal from the table state picker.",
            ChangeLifecycleState.ImplementedBackedOut => "Implemented changes are terminal from the table state picker.",
            ChangeLifecycleState.PendingApproval => "Pending approval changes can only be approved or moved back to Draft.",
            ChangeLifecycleState.ApprovedForImplementation => "Approved changes can only move forward from the table.",
            ChangeLifecycleState.ImplementationInProgress => "Changes in progress can only be completed from the table.",
            _ => "That lifecycle transition is not allowed from the table."
        };
    }

    private static ChangeDto ToQuickLifecycleDto(
        Change change,
        ChangeLifecycleState lifecycleState,
        ChangeTemplateDto? template,
        ChangeTemplateValidationDto? templateValidation) => new()
        {
            OrganizationId = change.OrganizationId,
            Id = change.Id,
            Title = change.Title,
            Description = change.Description,
            Priority = change.Priority,
            State = change.State,
            LifecycleState = lifecycleState,
            CreatedAt = change.CreatedAt,
            UpdatedAt = change.UpdatedAt,
            TrackingId = change.TrackingId,
            AssignedToId = change.AssignedToId,
            LastReplierName = change.LastReplierName,
            CustomerId = change.CustomerId,
            ChangeType = change.ChangeType,
            RequestedForUserId = change.RequestedForUserId,
            ImplementorUserId = change.ImplementorUserId,
            ApproverUserIds = change.ApproverUserIds,
            CcRecipients = change.CcRecipients.ToList(),
            ImplementationStartAt = change.ImplementationStartAt,
            ImplementationEndAt = change.ImplementationEndAt,
            ChangeTemplate = template,
            IsTemplateComplete = templateValidation?.IsComplete ?? false,
            TemplateValidationErrors = templateValidation?.Errors ?? new()
        };

    private static void ApplyTicketStateForLifecycle(Change change, TicketState previousState)
    {
        var lifecycleState = EffectiveLifecycleState(change);
        if (IsImplementedLifecycleState(lifecycleState))
        {
            change.State = TicketState.Resolved;
            ApplyClosedAtTransition(change, previousState, change.State);
        }
        else if (previousState == TicketState.Resolved && change.State == TicketState.Resolved)
        {
            change.State = TicketState.InProgress;
            ApplyClosedAtTransition(change, previousState, change.State);
        }
    }

    private static ChangeTemplateDto? DeserializeTemplateForPublicView(string? templateJson)
    {
        if (string.IsNullOrWhiteSpace(templateJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChangeTemplateDto>(
                templateJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> HasLockedChangeFieldUpdatesAsync(
        HelpdeskDbContext db,
        Change existing,
        UpdateChangeDto dto,
        IChangeTemplateService changeTemplateService,
        CancellationToken token)
    {
        if ((dto.State.HasValue && dto.State.Value != existing.State) ||
            (dto.Priority.HasValue && dto.Priority.Value != existing.Priority) ||
            !StringEqualsIfProvided(dto.OrganizationId, existing.OrganizationId) ||
            !StringEqualsIfProvided(dto.RequestedForUserId, existing.RequestedForUserId) ||
            !StringEqualsIfProvided(dto.ImplementorUserId, existing.ImplementorUserId))
        {
            return true;
        }

        if (dto.ApproverUserIds is not null &&
            !NormalizeUserIds(dto.ApproverUserIds).SequenceEqual(existing.ApproverUserIds, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(dto.ChangeType))
        {
            var normalizedChangeType = changeTemplateService.NormalizeChangeType(dto.ChangeType);
            if (!string.Equals(normalizedChangeType, existing.ChangeType, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (dto.ChangeTemplate is not null &&
            !string.Equals(
                changeTemplateService.SerializeTemplate(dto.ChangeTemplate),
                existing.ChangeTemplateJson,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (dto.ImplementationStartAt.HasValue &&
            Nullable.Compare(dto.ImplementationStartAt.Value, existing.ImplementationStartAt) != 0)
        {
            return true;
        }

        if (dto.ImplementationEndAt.HasValue &&
            Nullable.Compare(dto.ImplementationEndAt.Value, existing.ImplementationEndAt) != 0)
        {
            return true;
        }

        if (dto.CategoryIds is not null)
        {
            var existingCategoryIds = await db.ChangeCategoryLinks
                .AsNoTracking()
                .Where(x => x.ChangeId == existing.Id)
                .Select(x => x.TicketCategoryId)
                .ToListAsync(token);
            if (!NormalizeCategoryIds(dto.CategoryIds).OrderBy(x => x)
                    .SequenceEqual(existingCategoryIds.OrderBy(x => x)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StringEqualsIfProvided(string? candidate, string? existing) =>
        candidate is null || string.Equals(
            string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim(),
            string.IsNullOrWhiteSpace(existing) ? null : existing.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static async Task ResetApprovalReviewStateAsync(
        HelpdeskDbContext db,
        string changeId,
        CancellationToken token)
    {
        var approvals = await db.ChangeApprovals
            .Where(x => x.ChangeId == changeId)
            .ToListAsync(token);
        foreach (var approval in approvals)
        {
            approval.Status = ChangeApprovalStatus.Pending;
            approval.ReviewedAtUtc = null;
            approval.ViewedAtUtc = null;
            approval.TokenSentAtUtc = null;
        }
    }

    private static void SeedApprovalRecords(
        Change change,
        IReadOnlyDictionary<string, ChangeParticipantUserDto> participantLookup)
    {
        change.Approvals.Clear();
        foreach (var approver in GetUsers(participantLookup, change.ApproverUserIds)
                     .Where(x => !string.IsNullOrWhiteSpace(x.Email))
                     .DistinctBy(x => x.Email, StringComparer.OrdinalIgnoreCase))
        {
            change.Approvals.Add(new ChangeApproval
            {
                ChangeId = change.Id,
                ApproverId = approver.Id,
                ApproverName = string.IsNullOrWhiteSpace(approver.Name) ? approver.Email : approver.Name,
                ApproverEmail = approver.Email,
                Status = ChangeApprovalStatus.Pending
            });
        }
    }

    private static async Task ReconcileApprovalRecordsAsync(
        HelpdeskDbContext db,
        Change change,
        IReadOnlyDictionary<string, ChangeParticipantUserDto> participantLookup,
        CancellationToken token)
    {
        var selectedApprovers = GetUsers(participantLookup, change.ApproverUserIds)
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .DistinctBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedEmails = selectedApprovers
            .Select(x => x.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingApprovals = await db.ChangeApprovals
            .Where(x => x.ChangeId == change.Id)
            .ToListAsync(token);

        var removals = existingApprovals
            .Where(x => !selectedEmails.Contains(x.ApproverEmail))
            .ToList();
        if (removals.Count > 0)
        {
            db.ChangeApprovals.RemoveRange(removals);
        }

        var existingEmails = existingApprovals
            .Except(removals)
            .Select(x => x.ApproverEmail)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var approver in selectedApprovers.Where(x => !existingEmails.Contains(x.Email)))
        {
            db.ChangeApprovals.Add(new ChangeApproval
            {
                ChangeId = change.Id,
                ApproverId = approver.Id,
                ApproverName = string.IsNullOrWhiteSpace(approver.Name) ? approver.Email : approver.Name,
                ApproverEmail = approver.Email,
                Status = ChangeApprovalStatus.Pending
            });
        }
    }

    private static void AddChangeListeners(
        Change change,
        IReadOnlyDictionary<string, ChangeParticipantUserDto> participantLookup)
    {
        var recipients = new List<string>(change.CcRecipients);
        AddParticipantEmail(recipients, participantLookup, change.RequestedForUserId);
        AddParticipantEmail(recipients, participantLookup, change.ImplementorUserId);
        foreach (var approverId in change.ApproverUserIds)
        {
            AddParticipantEmail(recipients, participantLookup, approverId);
        }

        change.CcRecipients = recipients
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddParticipantEmail(
        ICollection<string> recipients,
        IReadOnlyDictionary<string, ChangeParticipantUserDto> participantLookup,
        string? participantId)
    {
        var email = GetUserEmail(participantLookup, participantId);
        if (!string.IsNullOrWhiteSpace(email))
        {
            recipients.Add(email.Trim());
        }
    }

    private static async Task AddTimelineEventAsync(
        HelpdeskDbContext db,
        ITimelineEventBus timelineEventBus,
        string changeId,
        string message,
        CancellationToken token)
    {
        var timelineEvent = new TicketTimelineEvent
        {
            TicketId = changeId,
            CreatedUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = "system",
            CreatedByUserName = "System",
            EventType = TimelineEventType.SystemNotification,
            MessageHtml = message,
            MessageText = message
        };
        db.TicketTimelineEvents.Add(timelineEvent);
        await db.SaveChangesAsync(token);
        await timelineEventBus.PublishAsync(new TicketTimelineEventDto
        {
            Id = timelineEvent.Id,
            TicketId = timelineEvent.TicketId,
            CreatedUtc = timelineEvent.CreatedUtc,
            CreatedByUserId = timelineEvent.CreatedByUserId,
            CreatedByUserName = timelineEvent.CreatedByUserName,
            EventType = timelineEvent.EventType,
            MessageHtml = timelineEvent.MessageHtml,
            MessageText = timelineEvent.MessageText
        });
    }

    private static async Task SendChangeCreationNotificationsAsync(
        ITicketNotificationService notificationService,
        Change change,
        string organizationName,
        IReadOnlyDictionary<string, ChangeParticipantUserDto> participantLookup,
        ILoggerFactory loggerFactory,
        CancellationToken token)
    {
        var logger = loggerFactory.CreateLogger("ChangeNotifications");
        var requestedForName = GetUserName(participantLookup, change.RequestedForUserId) ?? string.Empty;
        var requestedForEmail = GetUserEmail(participantLookup, change.RequestedForUserId);
        var implementorName = GetUserName(participantLookup, change.ImplementorUserId) ?? string.Empty;
        var implementorEmail = GetUserEmail(participantLookup, change.ImplementorUserId);
        var approvers = FormatApprovers(change.Approvals);

        try
        {
            if (!string.IsNullOrWhiteSpace(requestedForEmail))
            {
                await notificationService.SendChangeSubmittedAsync(
                    change,
                    requestedForEmail,
                    requestedForName,
                    organizationName,
                    requestedForName,
                    implementorName,
                    approvers,
                    change.CcRecipients.Where(x => !string.Equals(x, requestedForEmail, StringComparison.OrdinalIgnoreCase)),
                    token);
            }

            if (!string.IsNullOrWhiteSpace(implementorEmail) &&
                !string.Equals(implementorEmail, requestedForEmail, StringComparison.OrdinalIgnoreCase))
            {
                await notificationService.SendChangeSubmittedAsync(
                    change,
                    implementorEmail,
                    implementorName,
                    organizationName,
                    requestedForName,
                    implementorName,
                    approvers,
                    change.CcRecipients.Where(x => !string.Equals(x, implementorEmail, StringComparison.OrdinalIgnoreCase)),
                    token);
            }

            if (EffectiveLifecycleState(change) == ChangeLifecycleState.PendingApproval)
            {
                foreach (var approval in change.Approvals)
                {
                    await notificationService.SendChangeApprovalRequiredAsync(
                        change,
                        approval,
                        organizationName,
                        requestedForName,
                        implementorName,
                        approvers,
                        change.CcRecipients.Where(x => !string.Equals(x, approval.ApproverEmail, StringComparison.OrdinalIgnoreCase)),
                        token);
                    approval.TokenSentAtUtc = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send creation notifications for change {ChangeId}.", change.Id);
        }
    }

    private static async Task SendChangeApprovedNotificationsAsync(
        ITicketNotificationService notificationService,
        Change change,
        string organizationName,
        IReadOnlyDictionary<string, ChangeParticipantUserDto> participantLookup,
        ILoggerFactory loggerFactory,
        CancellationToken token)
    {
        var logger = loggerFactory.CreateLogger("ChangeNotifications");
        var requestedForName = GetUserName(participantLookup, change.RequestedForUserId) ?? string.Empty;
        var requestedForEmail = GetUserEmail(participantLookup, change.RequestedForUserId);
        var implementorName = GetUserName(participantLookup, change.ImplementorUserId) ?? string.Empty;
        var implementorEmail = GetUserEmail(participantLookup, change.ImplementorUserId);
        var approvers = FormatApprovers(change.Approvals);

        var recipients = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(requestedForEmail))
        {
            recipients[requestedForEmail] = requestedForName;
        }
        if (!string.IsNullOrWhiteSpace(implementorEmail))
        {
            recipients[implementorEmail] = implementorName;
        }
        foreach (var recipient in change.CcRecipients.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            recipients.TryAdd(recipient.Trim(), recipient.Trim());
        }

        try
        {
            foreach (var (email, name) in recipients)
            {
                await notificationService.SendChangeApprovedAsync(
                    change,
                    email,
                    name,
                    organizationName,
                    requestedForName,
                    implementorName,
                    approvers,
                    change.CcRecipients.Where(x => !string.Equals(x, email, StringComparison.OrdinalIgnoreCase)),
                    token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send approved notifications for change {ChangeId}.", change.Id);
        }
    }

    private static async Task SendChangeLifecycleNotificationsAsync(
        ITicketNotificationService notificationService,
        Change change,
        string notificationKind,
        ILoggerFactory loggerFactory,
        HelpdeskDbContext db,
        CancellationToken token)
    {
        var logger = loggerFactory.CreateLogger("ChangeNotifications");
        var participantLookup = await BuildParticipantLookupAsync(db, [change], token);
        var organizationName = await db.Organizations
            .AsNoTracking()
            .Where(x => x.Id == change.OrganizationId)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(token) ?? string.Empty;
        var requestedForName = GetUserName(participantLookup, change.RequestedForUserId) ?? string.Empty;
        var requestedForEmail = GetUserEmail(participantLookup, change.RequestedForUserId);
        var implementorName = GetUserName(participantLookup, change.ImplementorUserId) ?? string.Empty;
        var implementorEmail = GetUserEmail(participantLookup, change.ImplementorUserId);
        var approvals = await db.ChangeApprovals
            .AsNoTracking()
            .Where(x => x.ChangeId == change.Id)
            .ToListAsync(token);
        var approvers = FormatApprovers(approvals);

        var recipients = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(requestedForEmail))
        {
            recipients[requestedForEmail] = requestedForName;
        }
        if (!string.IsNullOrWhiteSpace(implementorEmail))
        {
            recipients[implementorEmail] = implementorName;
        }
        foreach (var recipient in change.CcRecipients.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            recipients.TryAdd(recipient.Trim(), recipient.Trim());
        }

        try
        {
            foreach (var (email, name) in recipients)
            {
                var cc = change.CcRecipients
                    .Where(x => !string.Equals(x, email, StringComparison.OrdinalIgnoreCase));

                if (string.Equals(notificationKind, "implementation-in-progress", StringComparison.OrdinalIgnoreCase))
                {
                    await notificationService.SendChangeImplementationInProgressAsync(
                        change,
                        email,
                        name,
                        organizationName,
                        requestedForName,
                        implementorName,
                        approvers,
                        cc,
                        token);
                }
                else
                {
                    await notificationService.SendChangeImplementedAsync(
                        change,
                        email,
                        name,
                        organizationName,
                        requestedForName,
                        implementorName,
                        approvers,
                        FormatChangeCompletionState(EffectiveLifecycleState(change)),
                        cc,
                        token);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send {NotificationKind} notifications for change {ChangeId}.", notificationKind, change.Id);
        }
    }

    private static string FormatChangeCompletionState(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.ImplementedSuccess => "Success",
        ChangeLifecycleState.ImplementedBackedOut => "Backed Out",
        _ => string.Empty
    };

    private static string FormatApprovers(IEnumerable<ChangeApproval> approvals) =>
        string.Join(
            ", ",
            approvals.Select(x => string.IsNullOrWhiteSpace(x.ApproverName)
                    ? x.ApproverEmail
                    : $"{x.ApproverName} ({x.ApproverEmail})")
                .Where(x => !string.IsNullOrWhiteSpace(x)));

    private static ChangeApprovalDto ToApprovalDto(ChangeApproval approval) => new()
    {
        Id = approval.Id,
        ChangeId = approval.ChangeId,
        ApproverId = approval.ApproverId,
        ApproverName = approval.ApproverName,
        ApproverEmail = approval.ApproverEmail,
        Status = approval.Status,
        CreatedAtUtc = approval.CreatedAtUtc,
        ReviewedAtUtc = approval.ReviewedAtUtc,
        ViewedAtUtc = approval.ViewedAtUtc,
        TokenSentAtUtc = approval.TokenSentAtUtc
    };

    private static List<Guid> NormalizeCategoryIds(IEnumerable<Guid>? categoryIds)
    {
        return categoryIds?
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList()
            ?? new List<Guid>();
    }

    private static List<string> NormalizeUserIds(IEnumerable<string>? userIds)
    {
        return userIds?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
    }

    private static async Task<IResult?> ValidateChangeParticipantsAsync(
        HelpdeskDbContext db,
        Helpdesk.Shared.Models.Organization organization,
        string? requestedForUserId,
        string? implementorUserId,
        IEnumerable<string>? approverUserIds,
        bool requireRequestedForAndImplementor,
        CancellationToken token)
    {
        if (requireRequestedForAndImplementor && string.IsNullOrWhiteSpace(requestedForUserId))
        {
            return Results.BadRequest("RequestedForUserId is required when creating a change.");
        }

        if (requireRequestedForAndImplementor && string.IsNullOrWhiteSpace(implementorUserId))
        {
            return Results.BadRequest("ImplementorUserId is required when creating a change.");
        }

        var requesterApproverIds = NormalizeUserIds(approverUserIds);
        if (!string.IsNullOrWhiteSpace(requestedForUserId))
        {
            requesterApproverIds.Add(requestedForUserId.Trim());
        }

        requesterApproverIds = requesterApproverIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requesterApproverIds.Count > 0)
        {
            var validUserIds = await db.Users
                .AsNoTracking()
                .Where(
                    user => requesterApproverIds.Contains(user.Id) &&
                            user.OrganizationId == organization.Id)
                .Select(x => x.Id)
                .ToListAsync(token);
            var remainingRequesterApproverIds = requesterApproverIds
                .Except(validUserIds, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var validCustomerCount = remainingRequesterApproverIds.Count == 0
                ? 0
                : await db.Customers
                    .AsNoTracking()
                    .CountAsync(
                        customer => remainingRequesterApproverIds.Contains(customer.Id) &&
                                    customer.OrganizationId == organization.Id &&
                                    customer.State == Helpdesk.Shared.Models.EntityState.Enabled,
                        token);
            if (validUserIds.Count + validCustomerCount != requesterApproverIds.Count)
            {
                return Results.BadRequest("Requested-for and approver people must belong to the selected organization.");
            }
        }

        if (!string.IsNullOrWhiteSpace(implementorUserId))
        {
            var implementorExists = await AssigneeQueries.Eligible(db, "change", organization)
                .AnyAsync(user => user.Id == implementorUserId.Trim(), token);

            if (!implementorExists)
            {
                return Results.BadRequest("Implementor must belong to the selected organization's ITIL/MSP manager organization, or be a HelpdeskAdmin/Technician fallback.");
            }
        }

        return null;
    }

    private static async Task<IResult?> ValidateImplementorAssignmentAsync(
        HelpdeskDbContext db, Change change, string? implementorId, CancellationToken token)
    {
        if (EffectiveLifecycleState(change) != ChangeLifecycleState.Draft)
            return Results.BadRequest("Change details are locked unless the change is moved back to Draft.");
        if (string.IsNullOrWhiteSpace(implementorId)) return Results.BadRequest("Implementor is required.");
        var organization = await db.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == change.OrganizationId, token);
        if (organization is null) return Results.BadRequest("The selected organization could not be found.");
        return await ValidateChangeParticipantsAsync(db, organization, change.RequestedForUserId,
            implementorId, change.ApproverUserIds, requireRequestedForAndImplementor: true, token);
    }

    private static async Task<Dictionary<string, ChangeParticipantUserDto>> BuildParticipantLookupAsync(
        HelpdeskDbContext db,
        IEnumerable<Change> changes,
        CancellationToken token)
    {
        var userIds = changes
            .SelectMany(change =>
                new[] { change.RequestedForUserId, change.ImplementorUserId }
                    .Concat(change.ApproverUserIds))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (userIds.Count == 0)
        {
            return new Dictionary<string, ChangeParticipantUserDto>(StringComparer.OrdinalIgnoreCase);
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(x => userIds.Contains(x.Id))
            .Select(x => new ChangeParticipantUserDto(x.Id, x.Name, x.Email, x.Role, x.OrganizationId))
            .ToDictionaryAsync(x => x.Id, StringComparer.OrdinalIgnoreCase, token);

        var missingIds = userIds.Except(users.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        if (missingIds.Count == 0)
        {
            return users;
        }

        var customers = await db.Customers
            .AsNoTracking()
            .Where(x => missingIds.Contains(x.Id))
            .Select(x => new ChangeParticipantUserDto(x.Id, x.Name, x.Email, "Customer", x.OrganizationId))
            .ToListAsync(token);
        foreach (var customer in customers)
        {
            users[customer.Id] = customer;
        }

        return users;
    }

    private static string? GetUserName(
        IReadOnlyDictionary<string, ChangeParticipantUserDto> users,
        string? userId) =>
        !string.IsNullOrWhiteSpace(userId) && users.TryGetValue(userId, out var user)
            ? user.Name
            : null;

    private static string? GetUserEmail(
        IReadOnlyDictionary<string, ChangeParticipantUserDto> users,
        string? userId) =>
        !string.IsNullOrWhiteSpace(userId) && users.TryGetValue(userId, out var user)
            ? user.Email
            : null;

    private static List<ChangeParticipantUserDto> GetUsers(
        IReadOnlyDictionary<string, ChangeParticipantUserDto> users,
        IEnumerable<string>? userIds) =>
        NormalizeUserIds(userIds)
            .Where(users.ContainsKey)
            .Select(id => users[id])
            .ToList();

    private static IResult? ValidateImplementationWindow(DateTime? startAt, DateTime? endAt, bool requireBoth)
    {
        if (requireBoth && (!startAt.HasValue || !endAt.HasValue))
        {
            return Results.BadRequest("ImplementationStartAt and ImplementationEndAt are required.");
        }

        if ((startAt.HasValue && !endAt.HasValue) || (!startAt.HasValue && endAt.HasValue))
        {
            return Results.BadRequest("ImplementationStartAt and ImplementationEndAt must both be supplied together.");
        }

        if (startAt.HasValue && endAt.HasValue && endAt.Value <= startAt.Value)
        {
            return Results.BadRequest("ImplementationEndAt must be later than ImplementationStartAt.");
        }

        return null;
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
        CancellationToken token,
        string? organizationId,
        IReadOnlyCollection<Guid>? existingIds = null)
    {
        if (categoryIds.Count == 0)
        {
            return null;
        }

        var tenantId = Guid.TryParse(organizationId, out var parsedTenantId) ? parsedTenantId : (Guid?)null;
        var preservedIds = existingIds ?? Array.Empty<Guid>();
        var found = await db.TicketCategories
            .AsNoTracking()
            .Where(x => categoryIds.Contains(x.Id) &&
                (preservedIds.Contains(x.Id) || (x.IsActive && (x.TenantId == null || x.TenantId == tenantId))))
            .Select(x => new { x.Id, x.Type })
            .ToListAsync(token);

        if (found.Count != categoryIds.Count)
        {
            return Results.BadRequest("One or more category IDs are invalid.");
        }

        if (found.Any(x => !preservedIds.Contains(x.Id) && x.Type != requestedType && x.Type != TicketCategoryType.Service))
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

    private static string GetCorrelationId(ICorrelationContext correlationContext)
    {
        return correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }
}

internal sealed record BulkStateChangeRequest(List<string> Ids, TicketState NewState, string? Comment);

internal sealed record ChangeStateRequest(TicketState NewState, string? Comment);

internal sealed record QuickChangeLifecycleRequest(ChangeLifecycleState LifecycleState);

internal sealed record BulkAssignRequest(List<string> Ids, string? AssignedToId);
internal sealed record ChangeAssigneeRequest(string? ImplementorUserId);
