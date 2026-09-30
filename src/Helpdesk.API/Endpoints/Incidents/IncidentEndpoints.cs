using Helpdesk.API.Background;
using Helpdesk.API.Services;
using Helpdesk.Application.Events;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Observability;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Article;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.DTOs.Sla;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Application.Timeline;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Ganss.Xss;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Incidents;

public static class IncidentEndpoints
{
    public static void MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/incidents")
            .WithTags("Incidents")
            .RequireAuthorization("IncidentAccess");
        var staffGroup = app.MapGroup("/api/v1/incidents")
            .WithTags("Incidents")
            .RequireAuthorization("IncidentManager");

        group.MapGet("/", async (
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            int page = 1,
            int pageSize = 10,
            [FromQuery] TicketState? state = null,
            [FromQuery] bool activeOnly = false,
            [FromQuery] bool historicOnly = false,
            [FromQuery] bool includeTotal = true,
            [FromQuery] bool summaryOnly = false,
            string? organizationId = null,
            string? excludeId = null,
            string? requesterEmail = null,
            string? q = null) =>
        {
            var query =
                from i in db.Incidents.AsNoTracking()
                join c in db.Customers.AsNoTracking() on i.CustomerId equals c.Id into ic
                from c in ic.DefaultIfEmpty()
                join o in db.Organizations.AsNoTracking() on c!.OrganizationId equals o.Id into co
                from o in co.DefaultIfEmpty()
                select new
                {
                    Incident = i,
                    OrgName = o != null ? o.Name : null,
                    CustomerId = c != null ? c.Id : null,
                    CustomerName = c != null ? c.Name : null,
                    CustomerEmail = c != null ? c.Email : null
                };

            var access = await accessService.ResolveAsync(user);
            if (!access.IsHelpdeskAdmin)
            {
                var tenantReadOrganizationIds = access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.IncidentRead)
                    .Concat(access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.IncidentWrite))
                    .Concat(access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.IncidentManager))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var ownOrganizationIds = access.OrganizationIdsFor(Helpdesk.Shared.Auth.HelpdeskPermissions.IncidentUser).ToArray();
                query = query.Where(x =>
                    tenantReadOrganizationIds.Contains(x.Incident.OrganizationId) ||
                    (ownOrganizationIds.Contains(x.Incident.OrganizationId) &&
                     !string.IsNullOrWhiteSpace(access.CustomerId) &&
                     x.CustomerId == access.CustomerId));
            }

            if (!string.IsNullOrWhiteSpace(organizationId))
            {
                var requestedOrganizationId = organizationId.Trim();
                query = query.Where(x => x.Incident.OrganizationId == requestedOrganizationId);
            }

            if (!string.IsNullOrWhiteSpace(excludeId))
            {
                var excludedId = excludeId.Trim();
                query = query.Where(x => x.Incident.Id != excludedId && x.Incident.TrackingId != excludedId);
            }

            if (!string.IsNullOrWhiteSpace(requesterEmail))
            {
                var requestedEmail = requesterEmail.Trim().ToLowerInvariant();
                query = query.Where(x =>
                    (x.Incident.RequesterEmail != null && x.Incident.RequesterEmail.ToLower() == requestedEmail) ||
                    (x.CustomerEmail != null && x.CustomerEmail.ToLower() == requestedEmail));
            }

            if (state is not null)
            {
                query = query.Where(x => x.Incident.State == state);
            }
            else if (historicOnly)
            {
                query = query.Where(x => x.Incident.State == TicketState.Resolved);
            }
            else if (activeOnly)
            {
                query = query.Where(x => x.Incident.State != TicketState.Resolved);
            }
            else
            {
                query = query.Where(x => x.Incident.State != TicketState.Resolved);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? query.Where(x =>
                        EF.Functions.ILike(x.Incident.Title, like) ||
                        EF.Functions.ILike(x.Incident.Description, like) ||
                        EF.Functions.ILike(x.Incident.TrackingId, like) ||
                        EF.Functions.ILike(x.OrgName!, like) ||
                        EF.Functions.ILike(x.CustomerName!, like) ||
                        EF.Functions.ILike(x.Incident.RequesterEmail!, like) ||
                        EF.Functions.ILike(x.CustomerEmail!, like))
                    : query.Where(x =>
                        EF.Functions.Like(x.Incident.Title, like) ||
                        EF.Functions.Like(x.Incident.Description, like) ||
                        EF.Functions.Like(x.Incident.TrackingId, like) ||
                        EF.Functions.Like(x.OrgName!, like) ||
                        EF.Functions.Like(x.CustomerName!, like) ||
                        EF.Functions.Like(x.Incident.RequesterEmail!, like) ||
                        EF.Functions.Like(x.CustomerEmail!, like));
            }

            var totalCount = includeTotal ? await query.CountAsync() : 0;

            var rawItems = await query
                .OrderByDescending(x => x.Incident.UpdatedAt ?? x.Incident.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    x.Incident.OrganizationId,
                    x.Incident.Id,
                    x.Incident.TrackingId,
                    Subject = x.Incident.Title,
                    x.Incident.State,
                    x.Incident.Priority,
                    UpdatedAt = x.Incident.UpdatedAt ?? x.Incident.CreatedAt,
                    CustomerOrgName = x.OrgName,
                    x.CustomerId,
                    x.CustomerName,
                    x.CustomerEmail,
                    x.Incident.RequesterEmail,
                    x.Incident.LastReplierName,
                    x.Incident.AssignedToId
                })
                .ToListAsync();

            var incidentIds = rawItems.Select(x => x.Id).ToList();
            if (summaryOnly)
            {
                var summaryItems = rawItems.Select(x => new IncidentDto
                {
                    OrganizationId = x.OrganizationId,
                    Id = x.Id,
                    TrackingId = x.TrackingId,
                    Subject = x.Subject,
                    State = x.State,
                    Priority = x.Priority,
                    UpdatedAt = x.UpdatedAt,
                    CustomerOrgName = x.CustomerOrgName,
                    CustomerId = x.CustomerId,
                    CustomerName = x.CustomerName,
                    CustomerEmail = x.CustomerEmail ?? x.RequesterEmail,
                    RequesterEmail = x.RequesterEmail,
                    LastReplierName = x.LastReplierName,
                    AssignedToId = x.AssignedToId,
                    CategoryIds = []
                }).ToList();

                return Results.Ok(new PagedResponse<IncidentDto>
                {
                    Items = summaryItems,
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = includeTotal ? totalCount : summaryItems.Count
                });
            }

            var categoryLookup = await db.IncidentCategoryLinks
                .Where(link => incidentIds.Contains(link.IncidentId))
                .GroupBy(link => link.IncidentId)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.Select(link => link.TicketCategoryId).ToList());
            var items = rawItems.Select(x => new IncidentDto
            {
                OrganizationId = x.OrganizationId,
                Id = x.Id,
                TrackingId = x.TrackingId,
                Subject = x.Subject,
                State = x.State,
                Priority = x.Priority,
                UpdatedAt = x.UpdatedAt,
                CustomerOrgName = x.CustomerOrgName,
                CustomerId = x.CustomerId,
                CustomerName = x.CustomerName,
                CustomerEmail = x.CustomerEmail ?? x.RequesterEmail,
                RequesterEmail = x.RequesterEmail,
                LastReplierName = x.LastReplierName,
                AssignedToId = x.AssignedToId,
                CategoryIds = categoryLookup.TryGetValue(x.Id, out var ids) ? ids : new List<Guid>()
            }).ToList();

            return Results.Ok(new PagedResponse<IncidentDto>
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = includeTotal ? totalCount : items.Count
            });
        });

        group.MapGet("/{id}", async (
            [FromRoute] string id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Incident> repo,
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
            var incident = await repo.GetAsync(id);
            if (incident == null) return Results.Problem("Incident not found", statusCode: 404);

            var customer = !string.IsNullOrWhiteSpace(incident.CustomerId)
                ? await db.Customers
                    .AsNoTracking()
                    .Where(x => x.Id == incident.CustomerId)
                    .Select(x => new { x.Id, x.Name, x.Email, x.OrganizationId })
                    .FirstOrDefaultAsync()
                : null;
            var organizationName = await db.Organizations
                .AsNoTracking()
                .Where(x => x.Id == (customer != null ? customer.OrganizationId : incident.OrganizationId))
                .Select(x => x.Name)
                .FirstOrDefaultAsync();

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanViewIncident(incident.OrganizationId, customer?.Id ?? incident.CustomerId, customer?.Email ?? incident.RequesterEmail))
            {
                return Results.Forbid();
            }

            var categoryIds = await db.IncidentCategoryLinks
                .Where(x => x.IncidentId == incident.Id)
                .Select(x => x.TicketCategoryId)
                .ToListAsync();

            var slaSnapshot = await SyncAndComputeSlaAsync(
                incident.Id,
                ticketSlaService,
                ticketSlaRepository,
                slaClockService,
                incident,
                escalationEvaluator,
                domainEvents,
                correlationContext,
                loggerFactory.CreateLogger("IncidentSla"));
            var incidentDto = new IncidentDto
            {
                OrganizationId = incident.OrganizationId,
                Id = incident.Id,
                TrackingId = incident.TrackingId,
                Subject = incident.Title,
                State = incident.State,
                Priority = incident.Priority,
                UpdatedAt = incident.UpdatedAt ?? incident.CreatedAt,
                CustomerOrgName = organizationName,
                CustomerId = customer?.Id ?? incident.CustomerId,
                CustomerName = customer?.Name,
                LastReplierName = incident.LastReplierName,
                RequesterEmail = incident.RequesterEmail,
                CustomerEmail = customer?.Email ?? incident.RequesterEmail,
                CcRecipients = incident.CcRecipients,
                AssignedToId = incident.AssignedToId,
                CategoryIds = categoryIds,
                Sla = ToSlaDto(slaSnapshot)
            };
            return Results.Ok(incidentDto);
        });

        group.MapPost("/", async (
            [FromBody] CreateIncidentDto dto,
            [FromServices] IRequestSender sender,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Customer> customers,
            [FromServices] ISupportAccessService supportAccessService,
            [FromServices] ITicketNotificationService ticketNotificationService,
            [FromServices] IHtmlSanitizerService sanitizer,
            [FromServices] IHtmlToPlainTextConverter plainTextConverter,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var categoryIds = NormalizeCategoryIds(dto.CategoryIds);
            var validation = await ValidateCategorySelectionAsync(
                db,
                categoryIds,
                TicketCategoryType.Incident,
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
            if (!access.CanCreateIncident(dto.OrganizationId, customer.Id, customer.Email))
            {
                return Results.Forbid();
            }

            var assignmentValidation = await ValidateCreateAssigneeAsync(supportAccessService, dto.OrganizationId, dto.AssignedToId, token);
            if (assignmentValidation is not null)
            {
                return assignmentValidation;
            }

            var cc = NormalizeEmails(dto.CcRecipients, customer.Email);
            var description = ToPlainTextDescription(sanitizer, plainTextConverter, dto.Description);

            await using var tx = await db.Database.BeginTransactionAsync(token);

            var command = new CreateIncidentCommand(
                dto.Title,
                description,
                dto.Priority,
                dto.CustomerId,
                dto.OrganizationId,
                dto.LinkedAssetIds,
                dto.Attachments,
                dto.DueDate,
                dto.Impact,
                customer.Email,
                cc,
                dto.AssignedToId);
            var created = await sender.Send(command);

            if (categoryIds.Count > 0)
            {
                db.IncidentCategoryLinks.AddRange(categoryIds.Select(categoryId => new IncidentCategoryLink
                {
                    IncidentId = created.Id,
                    TicketCategoryId = categoryId
                }));
                await db.SaveChangesAsync(token);
            }

            await tx.CommitAsync(token);

            _ = await ticketNotificationService.SendNewTicketConfirmationAsync(
                created,
                customer.Email,
                customer.Name,
                created.CcRecipients,
                token);
            var createdOrganizationName = await db.Organizations
                .AsNoTracking()
                .Where(x => x.Id == customer.OrganizationId)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(token);

            var createdDto = new IncidentDto
            {
                OrganizationId = created.OrganizationId,
                Id = created.Id,
                TrackingId = created.TrackingId,
                Subject = created.Title,
                State = created.State,
                Priority = created.Priority,
                UpdatedAt = created.UpdatedAt ?? created.CreatedAt,
                CustomerOrgName = createdOrganizationName,
                CustomerId = customer.Id,
                CustomerName = customer.Name,
                CustomerEmail = customer.Email,
                LastReplierName = created.LastReplierName,
                RequesterEmail = created.RequesterEmail,
                CcRecipients = created.CcRecipients,
                AssignedToId = created.AssignedToId,
                CategoryIds = categoryIds
            };
            return Results.Created($"/api/v1/incidents/{created.Id}", createdDto);
        });

        group.MapPut("/{id}", async (
            [FromRoute] string id,
            [FromBody] UpdateIncidentDto dto,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Incident> repo,
            [FromServices] IBackgroundJobQueue jobs,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            [FromServices] ITicketSlaService ticketSlaService,
            [FromServices] ITicketSlaRepository ticketSlaRepository,
            [FromServices] ISlaClockService slaClockService,
            [FromServices] ISlaEscalationEvaluator escalationEvaluator,
            [FromServices] ITicketNotificationService ticketNotificationService,
            [FromServices] ISupportNotificationService supportNotificationService,
            [FromServices] ISupportAccessService supportAccessService,
            [FromServices] IDomainEventPublisher domainEvents,
            [FromServices] ICorrelationContext correlationContext,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken token) =>
        {
            var existingIncident = await repo.GetAsync(id);
            if (existingIncident is null)
            {
                return Results.Problem("Incident not found", statusCode: 404);
            }

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanManageIncident(existingIncident.OrganizationId))
            {
                return Results.Forbid();
            }

            var previousState = existingIncident.State;
            var previousAssignedToId = existingIncident.AssignedToId;

            if (dto.State is not null) existingIncident.State = dto.State.Value;
            ApplyClosedAtTransition(existingIncident, previousState, existingIncident.State);
            if (dto.Priority is not null) existingIncident.Priority = dto.Priority.Value;
            if (dto.AssignedToId is not null)
            {
                var requestedAssignedToId = string.IsNullOrWhiteSpace(dto.AssignedToId)
                    ? null
                    : dto.AssignedToId;
                if (!string.IsNullOrWhiteSpace(requestedAssignedToId))
                {
                    var assignmentValidation = await ValidateCreateAssigneeAsync(
                        supportAccessService,
                        existingIncident.OrganizationId,
                        requestedAssignedToId,
                        token);
                    if (assignmentValidation is not null)
                    {
                        return assignmentValidation;
                    }
                }

                existingIncident.AssignedToId = string.IsNullOrWhiteSpace(dto.AssignedToId)
                    ? null
                    : dto.AssignedToId;
            }
            existingIncident.UpdatedAt = DateTime.UtcNow;

            if (dto.CcRecipients is not null)
            {
                existingIncident.CcRecipients = NormalizeEmails(dto.CcRecipients, existingIncident.RequesterEmail);
            }

            await using var tx = await db.Database.BeginTransactionAsync(token);

            if (dto.CategoryIds is not null)
            {
                var categoryIds = NormalizeCategoryIds(dto.CategoryIds);
                var validation = await ValidateCategorySelectionAsync(
                    db,
                    categoryIds,
                    TicketCategoryType.Incident,
                    token);
                if (validation is not null)
                {
                    return validation;
                }

                await db.IncidentCategoryLinks
                    .Where(x => x.IncidentId == id)
                    .ExecuteDeleteAsync(token);

                if (categoryIds.Count > 0)
                {
                    db.IncidentCategoryLinks.AddRange(categoryIds.Select(categoryId => new IncidentCategoryLink
                    {
                        IncidentId = id,
                        TicketCategoryId = categoryId
                    }));
                }

                await db.SaveChangesAsync(token);
            }

            var updatedIncident = await repo.UpdateAsync(existingIncident);
            await tx.CommitAsync(token);

            if (previousState != TicketState.Resolved && updatedIncident!.State == TicketState.Resolved)
            {
                var closedByUserId = "system";
                await ticketSlaCompletionService.HandleTicketClosedAsync(updatedIncident.Id, closedByUserId, DateTimeOffset.UtcNow);
            }

            var slaSnapshot = await SyncAndComputeSlaAsync(
                id,
                ticketSlaService,
                ticketSlaRepository,
                slaClockService,
                updatedIncident!,
                escalationEvaluator,
                domainEvents,
                correlationContext,
                loggerFactory.CreateLogger("IncidentSla"));

            if (previousState != TicketState.Resolved && updatedIncident!.State == TicketState.Resolved)
            {
                await SendResolvedNotificationAsync(updatedIncident, ticketNotificationService, token);
            }

            await SendAssignmentNotificationSafelyAsync(
                updatedIncident!,
                previousAssignedToId,
                updatedIncident.AssignedToId,
                supportNotificationService,
                token);

            var updatedCustomer = !string.IsNullOrWhiteSpace(updatedIncident.CustomerId)
                ? await db.Customers
                    .AsNoTracking()
                    .Where(x => x.Id == updatedIncident.CustomerId)
                    .Select(x => new { x.Id, x.Name, x.Email, x.OrganizationId })
                    .FirstOrDefaultAsync(token)
                : null;
            var updatedOrganizationName = await db.Organizations
                .AsNoTracking()
                .Where(x => x.Id == (updatedCustomer != null ? updatedCustomer.OrganizationId : updatedIncident.OrganizationId))
                .Select(x => x.Name)
                .FirstOrDefaultAsync(token);

            var resultDto = new IncidentDto
            {
                OrganizationId = updatedIncident.OrganizationId,
                Id = updatedIncident.Id,
                TrackingId = updatedIncident.TrackingId,
                Subject = updatedIncident.Title,
                State = updatedIncident.State,
                Priority = updatedIncident.Priority,
                UpdatedAt = updatedIncident.UpdatedAt ?? updatedIncident.CreatedAt,
                CustomerOrgName = updatedOrganizationName,
                CustomerId = updatedCustomer?.Id ?? updatedIncident.CustomerId,
                CustomerName = updatedCustomer?.Name,
                CustomerEmail = updatedCustomer?.Email ?? updatedIncident.RequesterEmail,
                LastReplierName = updatedIncident.LastReplierName,
                RequesterEmail = updatedIncident.RequesterEmail,
                CcRecipients = updatedIncident.CcRecipients,
                AssignedToId = updatedIncident.AssignedToId,
                CategoryIds = await db.IncidentCategoryLinks
                    .Where(x => x.IncidentId == updatedIncident.Id)
                    .Select(x => x.TicketCategoryId)
                    .ToListAsync(token),
                Sla = ToSlaDto(slaSnapshot)
            };

            return Results.Ok(resultDto);
        })
        .RequireAuthorization("IncidentManager");


        group.MapGet("/{id}/relations", async (
            [FromRoute] string id,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            CancellationToken token) =>
        {
            var incident = await ResolveIncidentAsync(db, id, false, token);
            if (incident is null)
            {
                return Results.NotFound();
            }

            var access = await accessService.ResolveAsync(user, token);
            var viewedIncidentCustomer = !string.IsNullOrWhiteSpace(incident.CustomerId)
                ? await db.Customers.AsNoTracking()
                    .Where(customer => customer.Id == incident.CustomerId)
                    .Select(customer => new { customer.Id, customer.Email })
                    .FirstOrDefaultAsync(token)
                : null;
            if (!access.CanViewIncident(
                    incident.OrganizationId,
                    viewedIncidentCustomer?.Id ?? incident.CustomerId,
                    viewedIncidentCustomer?.Email ?? incident.RequesterEmail))
            {
                return Results.Forbid();
            }

            var relations = await db.TicketRelations
                .AsNoTracking()
                .Where(x => x.SourceTicketId == incident.Id || x.TargetTicketId == incident.Id)
                .ToListAsync(token);
            relations = relations
                .OrderByDescending(x => x.CreatedUtc)
                .ToList();

            var ticketIds = relations
                .SelectMany(x => new[] { x.SourceTicketId, x.TargetTicketId })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var tickets = await db.Incidents
                .AsNoTracking()
                .Where(x => ticketIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, token);
            var customerIds = tickets.Values
                .Select(ticket => ticket.CustomerId)
                .Where(customerId => !string.IsNullOrWhiteSpace(customerId))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var customerEmails = await db.Customers
                .AsNoTracking()
                .Where(customer => customerIds.Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, customer => customer.Email, token);

            bool CanView(Incident candidate)
            {
                var customerEmail = !string.IsNullOrWhiteSpace(candidate.CustomerId) &&
                                    customerEmails.TryGetValue(candidate.CustomerId, out var email)
                    ? email
                    : candidate.RequesterEmail;
                return access.CanViewIncident(candidate.OrganizationId, candidate.CustomerId, customerEmail);
            }

            return Results.Ok(relations
                .Where(x =>
                    tickets.TryGetValue(x.SourceTicketId, out var source) &&
                    tickets.TryGetValue(x.TargetTicketId, out var target) &&
                    CanView(source) &&
                    CanView(target))
                .Select(x => ToRelationDto(x, tickets[x.SourceTicketId], tickets[x.TargetTicketId], incident.Id))
                .ToList());
        })
        .RequireAuthorization("IncidentAccess")
        .WithName("GetIncidentRelations")
        .WithSummary("Gets incident relationships")
        .WithDescription("Returns incoming and outgoing duplicate/related incident links.")
        .WithTags("Incidents");

        group.MapPost("/{id}/relations", async (
            [FromRoute] string id,
            [FromBody] CreateTicketRelationDto dto,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            [FromServices] IDomainEventPublisher domainEvents,
            [FromServices] ICorrelationContext correlationContext,
            [FromServices] ITimelineEventBus timelineEventBus,
            CancellationToken token) =>
        {
            if (dto is null || string.IsNullOrWhiteSpace(dto.TargetTicketId))
            {
                return Results.BadRequest("Target ticket is required.");
            }

            if (id.Equals(dto.TargetTicketId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("An incident cannot be related to itself.");
            }

            if (!Enum.IsDefined(typeof(TicketRelationType), dto.RelationType))
            {
                return Results.BadRequest("Invalid relation type.");
            }

            var source = await ResolveIncidentAsync(db, id, true, token);
            if (source is null)
            {
                return Results.NotFound();
            }

            var targetKey = dto.TargetTicketId.Trim();
            var target = await ResolveIncidentAsync(db, targetKey, true, token);
            if (target is null)
            {
                return Results.BadRequest("Target incident was not found.");
            }

            if (source.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("An incident cannot be related to itself.");
            }

            if (!string.Equals(source.OrganizationId, target.OrganizationId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Related incidents must belong to the same organization.");
            }

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanManageIncident(source.OrganizationId) || !access.CanManageIncident(target.OrganizationId))
            {
                return Results.Forbid();
            }

            var exists = await db.TicketRelations.AnyAsync(x =>
                x.SourceTicketId == source.Id &&
                x.TargetTicketId == target.Id &&
                x.RelationType == dto.RelationType,
                token);
            if (exists)
            {
                return Results.Conflict("This relation already exists.");
            }

            var closeSource = dto.RelationType == TicketRelationType.DuplicateOf || dto.CloseSourceTicket;
            var previousState = source.State;
            var addedParentListeners = CopySourceListenersToTarget(source, target);
            if (closeSource)
            {
                source.State = TicketState.Resolved;
                ApplyClosedAtTransition(source, previousState, source.State);
            }
            source.UpdatedAt = DateTime.UtcNow;
            target.UpdatedAt = DateTime.UtcNow;

            var relation = new TicketRelation
            {
                SourceTicketId = source.Id,
                TargetTicketId = target.Id,
                RelationType = dto.RelationType,
                Note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim(),
                CreatedByUserId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                CreatedByUserName = user.Identity?.Name ?? "System"
            };

            await using var tx = await db.Database.BeginTransactionAsync(token);
            db.TicketRelations.Add(relation);
            await AddRelationTimelineEventsAsync(db, timelineEventBus, relation, source, target, closeSource, addedParentListeners, token);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            if (closeSource && previousState != TicketState.Resolved)
            {
                await ticketSlaCompletionService.HandleTicketClosedAsync(source.Id, relation.CreatedByUserId, DateTimeOffset.UtcNow);
            }

            await domainEvents.PublishAsync(
                new TicketRelationCreatedDomainEvent(
                    source.Id,
                    source.TrackingId,
                    target.Id,
                    target.TrackingId,
                    dto.RelationType,
                    closeSource,
                    addedParentListeners.Count,
                    source.OrganizationId,
                    GetCorrelationId(correlationContext)),
                token);

            var result = new TicketRelationCreateResultDto
            {
                Relation = ToRelationDto(relation, source, target, source.Id),
                SourceIncident = ToIncidentDto(source),
                TargetIncident = ToIncidentDto(target),
                AddedParentListeners = addedParentListeners
            };

            return Results.Created($"/api/v1/incidents/{source.Id}/relations/{relation.Id}", result);
        })
        .RequireAuthorization("IncidentManager")
        .WithName("CreateIncidentRelation")
        .WithSummary("Links or closes an incident as duplicate/related")
        .WithDescription("Creates an incident relationship, optionally closes the source incident, and copies source listeners to the target incident.")
        .WithTags("Incidents");

        group.MapDelete("/{id}/relations/{relationId:guid}", async (
            [FromRoute] string id,
            [FromRoute] Guid relationId,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            CancellationToken token) =>
        {
            var incident = await ResolveIncidentAsync(db, id, false, token);
            if (incident is null)
            {
                return Results.NotFound();
            }

            var relation = await db.TicketRelations.FirstOrDefaultAsync(x => x.Id == relationId, token);
            if (relation is null ||
                (!string.Equals(relation.SourceTicketId, incident.Id, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(relation.TargetTicketId, incident.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.NotFound();
            }

            var otherIncidentId = string.Equals(relation.SourceTicketId, incident.Id, StringComparison.OrdinalIgnoreCase)
                ? relation.TargetTicketId
                : relation.SourceTicketId;
            var otherIncident = await ResolveIncidentAsync(db, otherIncidentId, false, token);
            if (otherIncident is null) return Results.NotFound();

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanManageIncident(incident.OrganizationId) || !access.CanManageIncident(otherIncident.OrganizationId))
            {
                return Results.Forbid();
            }

            db.TicketRelations.Remove(relation);
            await db.SaveChangesAsync(token);
            return Results.NoContent();
        })
        .RequireAuthorization("IncidentManager")
        .WithName("DeleteIncidentRelation")
        .WithSummary("Removes an incident relationship")
        .WithDescription("Deletes an incident duplicate/related link from the current incident.")
        .WithTags("Incidents");

        group.MapPost("/bulk/state", async (
            [FromBody] BulkStateChangeRequest req,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Incident> repo,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            [FromServices] ITicketNotificationService ticketNotificationService,
            CancellationToken token) =>
        {
            var ids = (req.Ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length is 0 or > 500) return Results.BadRequest("Select between 1 and 500 tickets.");
            if (!Enum.IsDefined(req.NewState)) return Results.BadRequest("Invalid ticket state.");
            var incidents = (await repo.GetAllAsync()).Where(ticket => ids.Contains(ticket.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            var access = await accessService.ResolveAsync(user, token);
            if (incidents.Any(ticket => !access.CanManageIncident(ticket.OrganizationId))) return Results.Forbid();
            if (incidents.Count != ids.Length) return Results.NotFound();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(token)
                : null;
            var resolvedTransitions = new List<Incident>();
            foreach (var inc in incidents)
            {
                var previousState = inc.State;
                inc.State = req.NewState;
                ApplyClosedAtTransition(inc, previousState, inc.State);
                inc.UpdatedAt = DateTime.UtcNow;
                if (previousState != TicketState.Resolved && inc.State == TicketState.Resolved)
                {
                    resolvedTransitions.Add(inc);
                }
            }
            foreach (var inc in incidents)
            {
                await repo.UpdateAsync(inc);
                if (inc.State == TicketState.Resolved)
                {
                    await ticketSlaCompletionService.HandleTicketClosedAsync(inc.Id, "bulk", DateTimeOffset.UtcNow);
                }
            }
            if (transaction is not null) await transaction.CommitAsync(token);
            foreach (var inc in resolvedTransitions)
            {
                await SendResolvedNotificationAsync(inc, ticketNotificationService, token);
            }
            return Results.Ok(new { updated = incidents.Count });
        })
        .RequireAuthorization("IncidentManager")
        .WithName("BulkUpdateIncidentState")
        .WithSummary("Bulk update incident state")
        .WithDescription("Updates the state of multiple incidents in one request.")
        .WithTags("Incidents");

        group.MapPost("/bulk/assign", async (
            [FromBody] BulkAssignRequest req,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRepository<Incident> repo,
            [FromServices] ISupportNotificationService supportNotificationService,
            [FromServices] ISupportAccessService supportAccessService,
            CancellationToken token) =>
        {
            var ids = (req.Ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length is 0 or > 500) return Results.BadRequest("Select between 1 and 500 tickets.");
            var incidents = (await repo.GetAllAsync()).Where(ticket => ids.Contains(ticket.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            var access = await accessService.ResolveAsync(user, token);
            if (incidents.Any(ticket => !access.CanManageIncident(ticket.OrganizationId))) return Results.Forbid();
            if (incidents.Count != ids.Length) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(req.AssignedToId))
            {
                foreach (var inc in incidents)
                {
                    var assignmentValidation = await ValidateCreateAssigneeAsync(
                        supportAccessService,
                        inc.OrganizationId,
                        req.AssignedToId,
                        token);
                    if (assignmentValidation is not null)
                    {
                        return assignmentValidation;
                    }
                }
            }

            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(token)
                : null;
            var changedAssignments = new List<(Incident Incident, string? PreviousAssignedToId)>();
            foreach (var inc in incidents)
            {
                var previousAssignedToId = inc.AssignedToId;
                inc.AssignedToId = string.IsNullOrWhiteSpace(req.AssignedToId) ? null : req.AssignedToId.Trim();
                inc.UpdatedAt = DateTime.UtcNow;
                await repo.UpdateAsync(inc);
                if (!string.Equals(previousAssignedToId, inc.AssignedToId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(inc.AssignedToId))
                {
                    changedAssignments.Add((inc, previousAssignedToId));
                }
            }
            if (transaction is not null) await transaction.CommitAsync(token);
            foreach (var (incident, previousAssignedToId) in changedAssignments)
            {
                await SendAssignmentNotificationSafelyAsync(
                    incident,
                    previousAssignedToId,
                    incident.AssignedToId,
                    supportNotificationService,
                    token);
            }
            return Results.Ok(new { updated = incidents.Count });
        })
        .RequireAuthorization("IncidentManager")
        .WithName("BulkAssignIncidents")
        .WithSummary("Bulk assign incidents to a user")
        .WithDescription("Assigns the selected incidents to the specified team member.")
        .WithTags("Incidents");

        group.MapPost("/bulk/delete", async (
            [FromBody] BulkIncidentIdsRequest req,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRepository<Incident> repo,
            CancellationToken cancellationToken) =>
        {
            var ids = NormalizeBulkIds(req.Ids);
            if (ids.Count == 0) return Results.BadRequest("No ids");

            var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var incidents = (await repo.GetAllAsync()).Where(incident => idSet.Contains(incident.Id)).ToList();
            var access = await accessService.ResolveAsync(user, cancellationToken);
            if (incidents.Any(incident => !access.CanDeleteIncident(incident.OrganizationId))) return Results.Forbid();

            var deleted = 0;
            var missing = 0;
            foreach (var id in ids)
            {
                if (await repo.DeleteAsync(id))
                {
                    deleted++;
                }
                else
                {
                    missing++;
                }
            }

            return Results.Ok(new { deleted, missing });
        })
        .RequireAuthorization("IncidentAccess")
        .WithName("BulkDeleteIncidents")
        .WithSummary("Bulk delete incidents")
        .WithDescription("Deletes selected incidents without notifying requesters.")
        .WithTags("Incidents");

        group.MapPost("/bulk/marketing-spam", async (
            [FromBody] BulkIncidentIdsRequest req,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRepository<Incident> repo,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            CancellationToken cancellationToken) =>
        {
            var ids = NormalizeBulkIds(req.Ids);
            if (ids.Count == 0) return Results.BadRequest("No ids");

            var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var incidents = (await repo.GetAllAsync()).Where(i => idSet.Contains(i.Id)).ToList();
            var access = await accessService.ResolveAsync(user, cancellationToken);
            if (incidents.Any(incident => !access.CanManageIncident(incident.OrganizationId))) return Results.Forbid();
            var now = DateTimeOffset.UtcNow;

            foreach (var inc in incidents)
            {
                var previousState = inc.State;
                inc.EmailExclusionReason = TicketEmailExclusionReason.MarketingSpam;
                inc.State = TicketState.Resolved;
                inc.ClosedAt = now;
                inc.UpdatedAt = now.UtcDateTime;
                await repo.UpdateAsync(inc);

                if (previousState != TicketState.Resolved)
                {
                    await ticketSlaCompletionService.HandleTicketClosedAsync(inc.Id, "bulk-marketing-spam", now);
                }
            }

            return Results.Ok(new { updated = incidents.Count, missing = idSet.Count - incidents.Count });
        })
        .RequireAuthorization("IncidentManager")
        .WithName("BulkMarkIncidentsMarketingSpam")
        .WithSummary("Bulk mark incidents as marketing or spam")
        .WithDescription("Silently closes selected incidents and excludes them from requester outbound email.")
        .WithTags("Incidents");

        staffGroup.MapPost("/{id}/state", async (
            [FromRoute] string id,
            [FromBody] QuickStateChangeRequest req,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRepository<Incident> repo,
            [FromServices] ITicketSlaCompletionService ticketSlaCompletionService,
            [FromServices] ITicketNotificationService ticketNotificationService,
            CancellationToken token) =>
        {
            var incident = await repo.GetAsync(id);
            if (incident is null)
            {
                return Results.NotFound();
            }

            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanManageIncident(incident.OrganizationId))
            {
                return Results.Forbid();
            }

            var previousState = incident.State;
            if (previousState == req.NewState)
            {
                return Results.Ok(new { incident.Id, State = incident.State });
            }

            incident.State = req.NewState;
            ApplyClosedAtTransition(incident, previousState, incident.State);
            incident.UpdatedAt = DateTime.UtcNow;
            await repo.UpdateAsync(incident);

            if (incident.State == TicketState.Resolved)
            {
                await ticketSlaCompletionService.HandleTicketClosedAsync(incident.Id, "quick-state", DateTimeOffset.UtcNow);
            }

            if (previousState != TicketState.Resolved && incident.State == TicketState.Resolved)
            {
                await SendResolvedNotificationAsync(incident, ticketNotificationService, token);
            }

            return Results.Ok(new { incident.Id, State = incident.State });
        })
        .WithName("QuickUpdateIncidentState")
        .WithSummary("Quick update incident state")
        .WithDescription("Updates one incident state from a list row state picker.")
        .WithTags("Incidents");

        group.MapGet("/{id}/peek", async (
            string id,
            [FromServices] IRepository<Incident> repo,
            [FromServices] IImageLinkSigner imageLinkSigner,
            [FromServices] IOptions<StorageOptions> storageOptions) =>
        {
            var inc = await repo.GetAsync(id);
            if (inc is null) return Results.NotFound();

            string html = IncidentInlineImageLinks.Refresh(
                inc.OriginalEmailHtml ?? string.Empty,
                inc.Id,
                imageLinkSigner,
                storageOptions.Value.PublicApiBaseUrl);
            string text = inc.OriginalEmailText ?? inc.Description ?? string.Empty;

            var sanitizer = new HtmlSanitizer();
            sanitizer.AllowedSchemes.Add("data");
            var safeHtml = sanitizer.Sanitize(html);

            if (string.IsNullOrWhiteSpace(safeHtml) && !string.IsNullOrWhiteSpace(text))
                safeHtml = $"<pre style=\"white-space:pre-wrap\">{WebUtility.HtmlEncode(text)}</pre>";

            safeHtml = TicketMessageLinkifier.LinkifyPlainUrls(safeHtml);

            return Results.Ok(new IncidentPeekDto(
                inc.Id, inc.Title, inc.EmailFrom, inc.EmailReceivedUtc, safeHtml,
                string.IsNullOrWhiteSpace(text) ? null : text[..Math.Min(text.Length, 400)]
            ));
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("IncidentPeek")
        .WithSummary("Get incident email preview")
        .WithDescription("Returns sanitized HTML and a text snippet for an incident.")
        .WithTags("Incidents");

        group.MapDelete("/{id}", async (
            [FromRoute] string id,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IRepository<Incident> repo,
            CancellationToken cancellationToken) =>
        {
            var incident = await repo.GetAsync(id);
            if (incident is null) return Results.Problem("Incident not found", statusCode: 404);

            var access = await accessService.ResolveAsync(user, cancellationToken);
            if (!access.CanDeleteIncident(incident.OrganizationId)) return Results.Forbid();

            return await repo.DeleteAsync(id)
                ? Results.NoContent()
                : Results.Problem("Incident not found", statusCode: 404);
        });

        app.MapGet("/api/incidents/{incidentId}/images/{filename}", (
            [FromRoute] string incidentId,
            [FromRoute] string filename,
            IWebHostEnvironment env,
            HttpContext httpContext,
            IImageLinkSigner signer,
            ILoggerFactory loggerFactory,
            IOptions<StorageOptions> storageOptions) =>
        {
            var logger = loggerFactory.CreateLogger("IncidentInlineImage");
            var safeIncidentId = SanitizePathSegment(incidentId);
            var safeFilename = Path.GetFileName(filename);
            if (!string.Equals(filename, safeFilename, StringComparison.Ordinal))
                return Results.BadRequest("Invalid file name.");

            var token = httpContext.Request.Query["token"].ToString();
            if (!signer.ValidateToken(token, "incident", safeIncidentId, safeFilename))
            {
                logger.LogWarning("Incident inline image token denied. IncidentId={IncidentId} File={File}", safeIncidentId, safeFilename);
                return Results.Unauthorized();
            }

            var storageRoot = string.IsNullOrWhiteSpace(storageOptions.Value.RootPath)
                ? Path.Combine(env.ContentRootPath, "storage")
                : storageOptions.Value.RootPath;
            var rootPath = Path.Combine(storageRoot, "incidents");
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, safeIncidentId, "inline", safeFilename));
            var fullRoot = Path.GetFullPath(rootPath);

            if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
                return Results.BadRequest("Invalid path.");

            if (!File.Exists(fullPath))
                return Results.NotFound();

            logger.LogInformation("Serving incident inline image. IncidentId={IncidentId} Path={Path}", safeIncidentId, fullPath);

            var contentType = ContentTypeHelper.GetContentType(safeFilename);
            return Results.File(fullPath, contentType);
        })
        .WithTags("Incidents")
        .WithName("GetIncidentInlineImage")
        .WithSummary("Gets a stored incident inline image");
    }

    private static IncidentDto ToIncidentDto(Incident incident) => new()
    {
        OrganizationId = incident.OrganizationId,
        Id = incident.Id,
        TrackingId = incident.TrackingId,
        Subject = incident.Title,
        State = incident.State,
        Priority = incident.Priority,
        UpdatedAt = incident.UpdatedAt ?? incident.CreatedAt,
        CustomerId = incident.CustomerId,
        CustomerEmail = incident.RequesterEmail,
        RequesterEmail = incident.RequesterEmail,
        CcRecipients = incident.CcRecipients,
        AssignedToId = incident.AssignedToId
    };

    private static Task<Incident?> ResolveIncidentAsync(
        HelpdeskDbContext db,
        string idOrTrackingId,
        bool track,
        CancellationToken token)
    {
        var key = idOrTrackingId.Trim();
        var query = track ? db.Incidents : db.Incidents.AsNoTracking();

        return query.FirstOrDefaultAsync(
            x => x.Id == key || x.TrackingId == key,
            token);
    }

    private static TicketRelationDto ToRelationDto(TicketRelation relation, Incident source, Incident target, string viewedFromTicketId) => new()
    {
        Id = relation.Id,
        SourceTicketId = source.Id,
        SourceTrackingId = source.TrackingId,
        SourceSubject = source.Title,
        SourceState = source.State,
        TargetTicketId = target.Id,
        TargetTrackingId = target.TrackingId,
        TargetSubject = target.Title,
        TargetState = target.State,
        RelationType = relation.RelationType,
        CreatedUtc = relation.CreatedUtc,
        CreatedByUserName = relation.CreatedByUserName,
        Note = relation.Note,
        IsOutgoing = relation.SourceTicketId.Equals(viewedFromTicketId, StringComparison.OrdinalIgnoreCase)
    };

    private static List<string> CopySourceListenersToTarget(Incident source, Incident target)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(source.RequesterEmail)) candidates.Add(source.RequesterEmail);
        candidates.AddRange(source.CcRecipients);
        var existing = new HashSet<string>(target.CcRecipients, StringComparer.OrdinalIgnoreCase);
        var targetListeners = target.CcRecipients.ToList();
        if (!string.IsNullOrWhiteSpace(target.RequesterEmail)) existing.Add(target.RequesterEmail);
        var added = new List<string>();
        foreach (var candidate in candidates)
        {
            var normalized = (candidate ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized) || existing.Contains(normalized)) continue;
            existing.Add(normalized);
            targetListeners.Add(normalized);
            added.Add(normalized);
        }
        target.CcRecipients = targetListeners;
        return added;
    }

    private static async Task AddRelationTimelineEventsAsync(HelpdeskDbContext db, ITimelineEventBus timelineEventBus, TicketRelation relation, Incident source, Incident target, bool closeSource, IReadOnlyCollection<string> addedParentListeners, CancellationToken token)
    {
        var relationLabel = relation.RelationType == TicketRelationType.DuplicateOf ? "duplicate of" : "related to";
        var sourceMessage = $"Marked as {relationLabel} {target.TrackingId}{(closeSource ? " and closed" : string.Empty)}.";
        var targetMessage = $"{source.TrackingId} was linked as {relationLabel} this incident.";
        if (addedParentListeners.Count > 0) targetMessage += $" Added listeners: {string.Join(", ", addedParentListeners)}.";
        await AddTimelineEventAsync(db, timelineEventBus, source.Id, sourceMessage, token);
        await AddTimelineEventAsync(db, timelineEventBus, target.Id, targetMessage, token);
    }

    private static async Task AddTimelineEventAsync(HelpdeskDbContext db, ITimelineEventBus timelineEventBus, string ticketId, string message, CancellationToken token)
    {
        var timelineEvent = new TicketTimelineEvent
        {
            TicketId = ticketId,
            CreatedUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = "system",
            CreatedByUserName = "System",
            EventType = TimelineEventType.SystemNotification,
            MessageText = message,
            MessageHtml = WebUtility.HtmlEncode(message)
        };
        db.TicketTimelineEvents.Add(timelineEvent);
        await timelineEventBus.PublishAsync(new TicketTimelineEventDto
        {
            Id = timelineEvent.Id,
            TicketId = timelineEvent.TicketId,
            CreatedUtc = timelineEvent.CreatedUtc,
            CreatedByUserId = timelineEvent.CreatedByUserId,
            CreatedByUserName = timelineEvent.CreatedByUserName,
            EventType = timelineEvent.EventType,
            MessageHtml = timelineEvent.MessageHtml,
            MessageText = timelineEvent.MessageText,
            EmailStatus = timelineEvent.EmailStatus,
            EmailRecipient = timelineEvent.EmailRecipient,
            RetryCount = timelineEvent.RetryCount,
            IsRetryable = timelineEvent.IsRetryable
        });
    }

    private static List<string> NormalizeEmails(IEnumerable<string>? emails, string? requester)
    {
        if (emails is null) return new();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in emails)
        {
            var e = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(e)) continue;
            set.Add(e);
        }
        if (!string.IsNullOrWhiteSpace(requester))
        {
            set.Remove(requester.Trim().ToLowerInvariant());
        }
        return set.ToList();
    }

    private static async Task SendResolvedNotificationAsync(
        Incident incident,
        ITicketNotificationService ticketNotificationService,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(incident.RequesterEmail))
        {
            return;
        }

        await ticketNotificationService.SendTicketResolvedAsync(
            incident,
            incident.RequesterEmail,
            incident.RequesterEmail,
            incident.CcRecipients,
            token);
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
        ISupportAccessService supportAccessService,
        string? organizationId,
        string? assignedToId,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(assignedToId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.BadRequest("Organization is required when assigning a ticket.");
        }

        var canSupport = await supportAccessService.CanUserSupportOrganizationAsync(assignedToId, organizationId, token);
        if (!canSupport)
        {
            return Results.BadRequest("Assigned user is not configured to support the selected organization.");
        }

        return null;
    }

    private static async Task SendAssignmentNotificationSafelyAsync(
        Incident incident,
        string? previousAssignedToId,
        string? newAssignedToId,
        ISupportNotificationService supportNotificationService,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(newAssignedToId) ||
            string.Equals(previousAssignedToId, newAssignedToId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await supportNotificationService.NotifyTicketAssignedAsync(
                incident,
                previousAssignedToId,
                newAssignedToId,
                token);
        }
        catch
        {
            // Assignment must not fail if support notification routing fails.
        }
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

    private static List<Guid> NormalizeCategoryIds(IEnumerable<Guid>? categoryIds)
    {
        return categoryIds?
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList()
            ?? new List<Guid>();
    }

    private static List<string> NormalizeBulkIds(IEnumerable<string>? ids)
    {
        return ids?
            .Select(id => id?.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
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

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var clean = global::System.Text.RegularExpressions.Regex.Replace(value, "[^a-zA-Z0-9_-]", "-");
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean;
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

    private static string SummarizeException(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null && messages.Count < 3; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}

internal sealed record BulkStateChangeRequest(List<string> Ids, TicketState NewState, string? Comment);

internal sealed record BulkIncidentIdsRequest(List<string> Ids);

internal sealed record QuickStateChangeRequest(TicketState NewState);

internal sealed record BulkAssignRequest(List<string> Ids, string? AssignedToId);

internal sealed record IncidentPeekDto(
    string Id,
    string? Subject,
    string? From,
    DateTimeOffset? ReceivedUtc,
    string Html,
    string? SnippetText);
