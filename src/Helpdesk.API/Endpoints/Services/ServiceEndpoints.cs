using Helpdesk.API.Services;
using Helpdesk.Application.Events;
using Helpdesk.Application.Resources;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace Helpdesk.API.Endpoints.Services;

public static class ServiceEndpoints
{
    public static void MapServiceEndpoints(this IEndpointRouteBuilder app)
    {
        // -------- Mixed "items" (services + forms) for explorer --------
        var itemsGroup = app.MapGroup("/api/v1/service-items")
            .WithTags("Services")
            .RequireAuthorization(HelpdeskPermissions.SelfServiceUser);

        itemsGroup.MapGet("/search", async (
            [FromServices] IRepository<Service> servicesRepo,
            [FromServices] IRepository<RequestForm> formsRepo,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] ISelfServiceAudienceService selfServiceAudienceService,
            ClaimsPrincipal user,
            [FromQuery] string? q,
            [FromQuery] int? pageSize,
            CancellationToken token,
            [FromQuery] bool includeTotal = true) =>
        {
            var term = q?.Trim();
            var take = Math.Clamp(pageSize ?? 6, 1, 25);
            var isAdmin = tenant.IsHelpdeskAdmin;
            var access = await accessService.ResolveAsync(user, token);
            var isTestUser = await selfServiceAudienceService.IsTestUserAsync(token);
            var organizationId = tenant.TenantId;

            var servicesQuery = servicesRepo.Query().AsNoTracking().IgnoreQueryFilters();
            var formsQuery = selfServiceAudienceService.ApplyAudienceFilter(
                formsRepo.Query().AsNoTracking().IgnoreQueryFilters(),
                isAdmin,
                isTestUser,
                organizationId);

            if (!string.IsNullOrWhiteSpace(term))
            {
                var like = $"%{term}%";
                if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                {
                    servicesQuery = servicesQuery.Where(s =>
                        EF.Functions.ILike(s.Name, like) ||
                        EF.Functions.ILike(s.Description, like));
                    formsQuery = formsQuery.Where(f =>
                        EF.Functions.ILike(f.Title, like) ||
                        EF.Functions.ILike(f.Description!, like));
                }
                else
                {
                    servicesQuery = servicesQuery.Where(s =>
                        EF.Functions.Like(s.Name, like) ||
                        EF.Functions.Like(s.Description, like));
                    formsQuery = formsQuery.Where(f =>
                        EF.Functions.Like(f.Title, like) ||
                        EF.Functions.Like(f.Description!, like));
                }
            }

            var serviceRows = await servicesQuery.Select(s => new
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                AllowedOrganizationIds = s.AllowedOrganizationIds,
                AllowedCustomerIds = s.AllowedCustomerIds
            }).ToListAsync(token);

            var visibilityRows = string.IsNullOrWhiteSpace(term)
                ? serviceRows.Select(service => new
                {
                    service.Id,
                    service.AllowedOrganizationIds,
                    service.AllowedCustomerIds
                }).ToList()
                : await servicesRepo.Query()
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Select(service => new
                    {
                        service.Id,
                        service.AllowedOrganizationIds,
                        service.AllowedCustomerIds
                    })
                    .ToListAsync(token);
            var visibleServiceIds = visibilityRows
                .Where(service => isAdmin
                    || (IsAllowedForTenant(service.AllowedOrganizationIds, organizationId)
                        && IsAllowedForCustomer(service.AllowedCustomerIds, access.CustomerId)))
                .Select(service => service.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!isAdmin)
            {
                serviceRows = serviceRows
                    .Where(service => IsAllowedForTenant(service.AllowedOrganizationIds, organizationId)
                        && IsAllowedForCustomer(service.AllowedCustomerIds, access.CustomerId))
                    .ToList();
            }

            var serviceItems = serviceRows.Select(service => new ServiceItemDto
            {
                Id = service.Id,
                Name = service.Name,
                Description = service.Description,
                ItemType = ServiceItemType.Service,
                AvailableRequestCount = 0,
                AllowedOrganizationIds = service.AllowedOrganizationIds,
                ReleaseStatus = null
            }).ToList();

            var formRows = await formsQuery.Select(f => new
            {
                f.ServiceId,
                Item = new ServiceItemDto
                {
                    Id = f.Id,
                    Name = f.Title,
                    Description = f.Description ?? string.Empty,
                    ItemType = ServiceItemType.RequestForm,
                    AvailableRequestCount = 0,
                    AllowedOrganizationIds = f.AllowedOrganizationIds,
                    ReleaseStatus = f.ReleaseStatus
                }
            }).ToListAsync(token);
            if (!isAdmin)
            {
                formRows = formRows
                    .Where(form => string.IsNullOrWhiteSpace(form.ServiceId)
                        || visibleServiceIds.Contains(form.ServiceId))
                    .ToList();
            }
            var formItems = formRows.Select(form => form.Item).ToList();

            var combined = serviceItems.Concat(formItems).OrderBy(x => x.Name).ToList();
            var items = combined.Take(take).ToList();

            return Results.Ok(new PagedResponse<ServiceItemDto>
            {
                Page = 1,
                PageSize = take,
                TotalCount = includeTotal ? combined.Count : items.Count,
                Items = items
            });
        })
        .WithName("SearchServiceItems")
        .WithSummary("Search requestable self-service catalog items.");

        itemsGroup.MapGet("/{parentServiceId?}", async (
            string? parentServiceId,
            IRepository<Service> servicesRepo,
            IRepository<RequestForm> formsRepo,
            ITenantContext tenant,
            ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            ISelfServiceAudienceService selfServiceAudienceService,
            CancellationToken token) =>
        {
            var isAdmin = tenant.IsHelpdeskAdmin;
            var access = await accessService.ResolveAsync(user, token);
            var isTestUser = await selfServiceAudienceService.IsTestUserAsync(token);
            var organizationId = tenant.TenantId;

            var allServices = await servicesRepo.Query()
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Description,
                    s.ParentServiceId,
                    s.AllowedOrganizationIds,
                    s.AllowedCustomerIds
                })
                .ToListAsync(token);

            if (!isAdmin)
            {
                allServices = allServices
                    .Where(s => IsAllowedForTenant(s.AllowedOrganizationIds, organizationId)
                        && IsAllowedForCustomer(s.AllowedCustomerIds, access.CustomerId))
                    .ToList();
            }

            var visibleServiceIds = allServices
                .Select(service => service.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!isAdmin
                && !string.IsNullOrWhiteSpace(parentServiceId)
                && !visibleServiceIds.Contains(parentServiceId))
            {
                return Results.Ok(Array.Empty<ServiceItemDto>());
            }

            var services = allServices
                .Where(s => s.ParentServiceId == parentServiceId)
                .ToList();

            var formRows = await selfServiceAudienceService
                .ApplyAudienceFilter(formsRepo.Query().AsNoTracking().IgnoreQueryFilters(), isAdmin, isTestUser, organizationId)
                .Select(f => new
                {
                    f.Id,
                    f.Title,
                    f.Description,
                    f.ServiceId,
                    f.AllowedOrganizationIds,
                    f.ReleaseStatus
                })
                .ToListAsync(token);
            var forms = isAdmin
                ? formRows
                : formRows.Where(form => string.IsNullOrWhiteSpace(form.ServiceId)
                    || visibleServiceIds.Contains(form.ServiceId)).ToList();

            var requestCounts = BuildAvailableRequestCounts(
                allServices.Select(s => new ServiceCountNode(s.Id, s.ParentServiceId)),
                forms.Select(f => f.ServiceId));

            var result = new List<ServiceItemDto>();
            result.AddRange(services.Select(s => new ServiceItemDto
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                ItemType = ServiceItemType.Service,
                AvailableRequestCount = requestCounts.GetValueOrDefault(s.Id),
                AllowedOrganizationIds = s.AllowedOrganizationIds
            }));
            var parentForms = string.IsNullOrWhiteSpace(parentServiceId)
                ? forms.Where(form => string.IsNullOrWhiteSpace(form.ServiceId))
                : forms.Where(form => form.ServiceId == parentServiceId);
            result.AddRange(parentForms.Select(f => new ServiceItemDto
            {
                Id = f.Id,
                Name = f.Title,
                Description = f.Description ?? string.Empty,
                ItemType = ServiceItemType.RequestForm,
                AllowedOrganizationIds = f.AllowedOrganizationIds,
                ReleaseStatus = f.ReleaseStatus
            }));

            return Results.Ok(result);
        })
        .WithName("GetServiceItems")
        .WithSummary("List all items (services and forms) for a parent service.");

        // -------- Services CRUD + helpers --------
        var services = app.MapGroup("/api/v1/services")
            .WithTags("Services")
            .RequireAuthorization(HelpdeskPermissions.SelfServiceUser);

        services.MapGet("/", async ([FromServices] IRepository<Service> repo) =>
        {
            var all = await repo.GetAllAsync();
            var dict = all.ToDictionary(s => s.Id);
            var result = all.Select(s => ToDtoWithDepth(s, dict)).ToList();
            return Results.Ok(result);
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("GetServices")
        .WithSummary("List services")
        .WithDescription("Retrieves all services with computed depth.");

        services.MapGet("/{id}", async (
            string id,
            [FromServices] IRepository<Service> repo,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var entity = await repo.GetByIdAsync(id);
            if (entity is null) return Results.NotFound();
            var access = await accessService.ResolveAsync(user, token);
            if (!tenant.IsHelpdeskAdmin
                && (!IsAllowedForTenant(entity.AllowedOrganizationIds, tenant.TenantId)
                    || !IsAllowedForCustomer(entity.AllowedCustomerIds, access.CustomerId)))
            {
                return Results.Forbid();
            }

            var all = await repo.GetAllAsync();
            if (!tenant.IsHelpdeskAdmin)
            {
                all = all.Where(service =>
                    IsAllowedForTenant(service.AllowedOrganizationIds, tenant.TenantId)
                    && IsAllowedForCustomer(service.AllowedCustomerIds, access.CustomerId));
            }
            var dto = ToDtoWithDepth(entity, all.ToDictionary(s => s.Id));
            if (!tenant.IsHelpdeskAdmin)
            {
                dto.AllowedCustomerIds = [];
            }
            return Results.Ok(dto);
        })
        .RequireAuthorization()
        .WithName("GetServiceById")
        .WithSummary("Get a single service by id");

        services.MapGet("/{id}/breadcrumb", async (
            string id,
            [FromServices] IRepository<Service> repo,
            [FromServices] ITenantContext tenant,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            var all = await repo.GetAllAsync();
            var dict = all.ToDictionary(s => s.Id);
            if (!dict.TryGetValue(id, out var current)) return Results.NotFound();
            var access = await accessService.ResolveAsync(user, token);
            if (!tenant.IsHelpdeskAdmin
                && (!IsAllowedForTenant(current.AllowedOrganizationIds, tenant.TenantId)
                    || !IsAllowedForCustomer(current.AllowedCustomerIds, access.CustomerId)))
            {
                return Results.Forbid();
            }

            var visibleServices = tenant.IsHelpdeskAdmin
                ? all
                : all.Where(service =>
                    IsAllowedForTenant(service.AllowedOrganizationIds, tenant.TenantId)
                    && IsAllowedForCustomer(service.AllowedCustomerIds, access.CustomerId));
            var chain = BuildBreadcrumb(current, visibleServices.ToDictionary(service => service.Id));
            return Results.Ok(chain);
        })
        .RequireAuthorization()
        .WithName("GetServiceBreadcrumb")
        .WithSummary("Get breadcrumb chain from root to this service");

        services.MapPost("/", async ([FromBody] CreateServiceDto dto, [FromServices] IRepository<Service> repo) =>
        {
            var service = new Service
            {
                Name = dto.Name,
                Description = dto.Description ?? string.Empty,
                ParentServiceId = dto.ParentId,
                AllowedCustomerIds = dto.AllowedCustomerIds?.ToList() ?? new List<string>(),
                AllowedOrganizationIds = dto.AllowedOrganizationIds?.ToList() ?? new()
            };

            var created = await repo.CreateAsync(service);

            // compute depth for return
            var all = await repo.GetAllAsync();
            var dtoOut = ToDtoWithDepth(created, all.ToDictionary(s => s.Id));

            return Results.Created($"/api/v1/services/{created.Id}", dtoOut);
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("CreateService")
        .WithSummary("Create service")
        .WithDescription("Creates a new service (optionally nested).");

        services.MapPut("/{id}", async (string id, [FromBody] UpdateServiceDto dto, [FromServices] IRepository<Service> repo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound();

            existing.Name = dto.Name;
            existing.Description = dto.Description ?? string.Empty;
            if (dto.AllowedCustomerIds is not null)
                existing.AllowedCustomerIds = dto.AllowedCustomerIds;

            if (dto.AllowedOrganizationIds is not null)
                existing.AllowedOrganizationIds = dto.AllowedOrganizationIds;

            // move service (optional)
            if (dto.ParentId != existing.ParentServiceId)
                existing.ParentServiceId = dto.ParentId;

            var updated = await repo.UpdateAsync(existing);

            var all = await repo.GetAllAsync();
            var dtoOut = ToDtoWithDepth(updated, all.ToDictionary(s => s.Id));

            return Results.Ok(dtoOut);
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("UpdateService")
        .WithSummary("Update service");

        services.MapDelete("/{id}", async (string id, [FromServices] IRepository<Service> repo, [FromServices] IRepository<RequestForm> formsRepo) =>
        {
            // Optional: guard against deleting non-empty services
            var allServices = await repo.GetAllAsync();
            if (allServices.Any(s => s.ParentServiceId == id))
                return Results.BadRequest("Cannot delete a service that has child services.");

            var allForms = await formsRepo.GetAllAsync();
            if (allForms.Any(f => f.ServiceId == id))
                return Results.BadRequest("Cannot delete a service that still has request forms. Delete/move them first.");

            var ok = await repo.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("DeleteService")
        .WithSummary("Delete service");

        #region Top-level RequestForm endpoints (UI uses these)

        var formsTop = app.MapGroup("/api/v1/request-forms")
            .WithTags("Request Forms")
            .RequireAuthorization(HelpdeskPermissions.SelfServiceUser);

        formsTop.MapGet("/{id}", async (
            string id,
            [FromServices] IRepository<RequestForm> repo,
            [FromServices] ISelfServiceAudienceService selfServiceAudienceService,
            ITenantContext tenant,
            CancellationToken token) =>
        {
            var f = await repo.GetByIdAsync(id);
            if (f is null) return Results.NotFound();
            if (!tenant.IsHelpdeskAdmin && !await selfServiceAudienceService.CanAccessRequestFormAsync(f, token))
            {
                return Results.Forbid();
            }

            var dto = new RequestFormDto
            {
                Id = f.Id,
                ServiceId = f.ServiceId,
                Title = f.Title,
                Description = f.Description ?? string.Empty,
                JsonSchema = f.JsonSchema.RootElement.GetRawText(),
                OrganizationId = f.OrganizationId,
                AllowedOrganizationIds = f.AllowedOrganizationIds,
                ReleaseStatus = f.ReleaseStatus
            };

            return Results.Ok(dto);
        })
        .WithName("GetRequestFormById");

        formsTop.MapPost("/", async ([FromBody] CreateRequestFormDto dto,
                                     [FromServices] IRepository<RequestForm> repo,
                                     [FromServices] IRequestFormSchemaParser schemaParser,
                                     [FromServices] IRequestTaskDependencyGraphValidator dependencyGraphValidator,
                                     [FromServices] IRequestFormDatasetBindingValidator bindingValidator,
                                     [FromServices] IDomainEventPublisher domainEvents,
                                     [FromServices] ICorrelationContext correlationContext,
                                     ITenantContext tenant,
                                     CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(dto.ServiceId))
                return Results.BadRequest("ServiceId is required.");

            var schemaDoc = JsonDocument.Parse(dto.JsonSchema);
            var validationError = ValidateTaskDependencyGraph(schemaDoc, schemaParser, dependencyGraphValidator);
            if (validationError is not null)
            {
                return Results.BadRequest(validationError);
            }

            var organizationId = ResolveRequestFormOrganizationId(dto.OrganizationId, dto.AllowedOrganizationIds, tenant.TenantId);
            var bindingError = await bindingValidator.ValidateAsync(organizationId ?? string.Empty, schemaParser.Parse(schemaDoc).Fields, token);
            if (bindingError is not null)
            {
                return Results.BadRequest(bindingError);
            }

            var form = new RequestForm
            {
                ServiceId = dto.ServiceId,
                Title = dto.Title,
                Description = dto.Description ?? string.Empty,
                JsonSchema = schemaDoc,
                OrganizationId = organizationId,
                AllowedOrganizationIds = NormalizeAllowedOrganizations(dto.OrganizationId, dto.AllowedOrganizationIds),
                ReleaseStatus = dto.ReleaseStatus
            };

            var created = await repo.CreateAsync(form);
            await domainEvents.PublishAsync(
                new RequestFormCreatedDomainEvent(
                    created.Id,
                    created.ServiceId,
                    created.Title,
                    CountTaskTemplates(created.JsonSchema),
                    created.OrganizationId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlationContext)),
                token);

            var result = new RequestFormDto
            {
                Id = created.Id,
                ServiceId = created.ServiceId,
                Title = created.Title,
                Description = created.Description,
                JsonSchema = created.JsonSchema.RootElement.GetRawText(),
                OrganizationId = created.OrganizationId,
                AllowedOrganizationIds = created.AllowedOrganizationIds,
                ReleaseStatus = created.ReleaseStatus
            };
            return Results.Created($"/api/v1/request-forms/{created.Id}", result);
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("CreateRequestForm");

        formsTop.MapPut("/{id}", async (string id,
                                        [FromBody] UpdateRequestFormDto dto,
                                        [FromServices] IRepository<RequestForm> repo,
                                        [FromServices] IRequestFormSchemaParser schemaParser,
                                        [FromServices] IRequestTaskDependencyGraphValidator dependencyGraphValidator,
                                        [FromServices] IRequestFormDatasetBindingValidator bindingValidator,
                                        [FromServices] IDomainEventPublisher domainEvents,
                                        [FromServices] ICorrelationContext correlationContext,
                                        CancellationToken token) =>
        {
            var f = await repo.GetByIdAsync(id);
            if (f is null) return Results.NotFound();

            f.Title = dto.Title ?? f.Title;
            if (!string.IsNullOrWhiteSpace(dto.JsonSchema))
            {
                var schemaDoc = JsonDocument.Parse(dto.JsonSchema);
                var validationError = ValidateTaskDependencyGraph(schemaDoc, schemaParser, dependencyGraphValidator);
                if (validationError is not null)
                {
                    return Results.BadRequest(validationError);
                }

                var updatedOrganizationId = dto.OrganizationId is not null
                    ? ResolveRequestFormOrganizationId(dto.OrganizationId, dto.AllowedOrganizationIds?.Count > 0 ? dto.AllowedOrganizationIds : f.AllowedOrganizationIds, f.OrganizationId)
                    : f.OrganizationId;
                var bindingError = await bindingValidator.ValidateAsync(updatedOrganizationId ?? string.Empty, schemaParser.Parse(schemaDoc).Fields, token);
                if (bindingError is not null)
                {
                    return Results.BadRequest(bindingError);
                }

                f.JsonSchema = schemaDoc;
            }
            if (dto.Description is not null)
                f.Description = dto.Description;
            if (dto.ServiceIdHasValue) // optional move
                f.ServiceId = dto.ServiceId;

            if (dto.AllowedOrganizationIds is not null)
                f.AllowedOrganizationIds = NormalizeAllowedOrganizations(dto.OrganizationId, dto.AllowedOrganizationIds);
            if (dto.OrganizationId is not null)
                f.OrganizationId = ResolveRequestFormOrganizationId(dto.OrganizationId, f.AllowedOrganizationIds, f.OrganizationId);
            if (dto.ReleaseStatus.HasValue)
                f.ReleaseStatus = dto.ReleaseStatus.Value;

            var updated = await repo.UpdateAsync(f);
            await domainEvents.PublishAsync(
                new RequestFormUpdatedDomainEvent(
                    updated.Id,
                    updated.ServiceId,
                    updated.Title,
                    CountTaskTemplates(updated.JsonSchema),
                    updated.OrganizationId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlationContext)),
                token);

            var result = new RequestFormDto
            {
                Id = updated.Id,
                ServiceId = updated.ServiceId,
                Title = updated.Title,
                Description = updated.Description ?? string.Empty,
                JsonSchema = updated.JsonSchema.RootElement.GetRawText(),
                OrganizationId = updated.OrganizationId,
                AllowedOrganizationIds = updated.AllowedOrganizationIds,
                ReleaseStatus = updated.ReleaseStatus
            };
            return Results.Ok(result);
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("UpdateRequestForm");

        formsTop.MapDelete("/{id}", async (string id, [FromServices] IRepository<RequestForm> repo) =>
        {
            var ok = await repo.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("DeleteRequestForm");

        // -------- Keep your existing nested endpoints for compatibility --------
        var formsNested = services.MapGroup("/{serviceId}/forms");

        formsNested.MapGet("/", async ([FromRoute] string serviceId, [FromServices] IRepository<RequestForm> repo) =>
        {
            var all = await repo.GetAllAsync();
            var serviceForms = all.Where(f => f.ServiceId == serviceId)
                .Select(f => new RequestFormDto
                {
                    Id = f.Id,
                    ServiceId = f.ServiceId,
                    Title = f.Title,
                    Description = f.Description ?? string.Empty,
                    JsonSchema = f.JsonSchema.RootElement.GetRawText(),
                    OrganizationId = f.OrganizationId,
                    AllowedOrganizationIds = f.AllowedOrganizationIds,
                    ReleaseStatus = f.ReleaseStatus
                });
            return Results.Ok(serviceForms);
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("GetServiceForms");

        formsNested.MapPost("/", async ([FromRoute] string serviceId,
                                        [FromBody] CreateRequestFormDto dto,
                                        [FromServices] IRepository<RequestForm> repo,
                                        [FromServices] IRequestFormSchemaParser schemaParser,
                                        [FromServices] IRequestTaskDependencyGraphValidator dependencyGraphValidator,
                                        [FromServices] IRequestFormDatasetBindingValidator bindingValidator,
                                        [FromServices] IDomainEventPublisher domainEvents,
                                        [FromServices] ICorrelationContext correlationContext,
                                        ITenantContext tenant,
                                        CancellationToken token) =>
        {
            var schemaDoc = JsonDocument.Parse(dto.JsonSchema);
            var validationError = ValidateTaskDependencyGraph(schemaDoc, schemaParser, dependencyGraphValidator);
            if (validationError is not null)
            {
                return Results.BadRequest(validationError);
            }

            var organizationId = ResolveRequestFormOrganizationId(dto.OrganizationId, dto.AllowedOrganizationIds, tenant.TenantId);
            var bindingError = await bindingValidator.ValidateAsync(organizationId ?? string.Empty, schemaParser.Parse(schemaDoc).Fields, token);
            if (bindingError is not null)
            {
                return Results.BadRequest(bindingError);
            }

            var form = new RequestForm
            {
                ServiceId = serviceId,
                Title = dto.Title,
                Description = dto.Description,
                JsonSchema = schemaDoc,
                OrganizationId = organizationId,
                AllowedOrganizationIds = NormalizeAllowedOrganizations(dto.OrganizationId, dto.AllowedOrganizationIds),
                ReleaseStatus = dto.ReleaseStatus
            };
            var created = await repo.CreateAsync(form);
            await domainEvents.PublishAsync(
                new RequestFormCreatedDomainEvent(
                    created.Id,
                    created.ServiceId,
                    created.Title,
                    CountTaskTemplates(created.JsonSchema),
                    created.OrganizationId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlationContext)),
                token);

            var result = new RequestFormDto
            {
                Id = created.Id,
                ServiceId = created.ServiceId,
                Title = created.Title,
                Description = created.Description ?? string.Empty,
                JsonSchema = created.JsonSchema.RootElement.GetRawText(),
                OrganizationId = created.OrganizationId,
                AllowedOrganizationIds = created.AllowedOrganizationIds,
                ReleaseStatus = created.ReleaseStatus
            };
            return Results.Created($"/api/v1/services/{serviceId}/forms/{created.Id}", result);
        })
        .RequireAuthorization("HelpdeskAdmin")
        .WithName("CreateServiceForm");

        #endregion
    }

    // ---------- helpers ----------
    private sealed record ServiceCountNode(string Id, string? ParentServiceId);

    private static Dictionary<string, int> BuildAvailableRequestCounts(
        IEnumerable<ServiceCountNode> services,
        IEnumerable<string> requestFormServiceIds)
    {
        var counts = services.ToDictionary(s => s.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var parentByServiceId = services.ToDictionary(s => s.Id, s => s.ParentServiceId, StringComparer.OrdinalIgnoreCase);

        foreach (var serviceId in requestFormServiceIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            var currentServiceId = serviceId;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!string.IsNullOrWhiteSpace(currentServiceId)
                && counts.ContainsKey(currentServiceId)
                && seen.Add(currentServiceId))
            {
                counts[currentServiceId]++;
                currentServiceId = parentByServiceId.GetValueOrDefault(currentServiceId);
            }
        }

        return counts;
    }

    private static ServiceDto ToDtoWithDepth(Service s, IDictionary<string, Service> dict)
    {
        int depth = 0;
        var seen = new HashSet<string>();
        var cur = s;

        while (!string.IsNullOrEmpty(cur.ParentServiceId) && dict.TryGetValue(cur.ParentServiceId!, out var parent))
        {
            if (!seen.Add(cur.Id)) break; // guard against cycles
            depth++;
            cur = parent;
        }

        return new ServiceDto
        {
            Id = s.Id,
            Name = s.Name,
            Description = s.Description,
            ParentServiceId = s.ParentServiceId,
            AllowedCustomerIds = s.AllowedCustomerIds,
            Depth = depth
        };
    }

    private static List<BreadcrumbDto> BuildBreadcrumb(Service leaf, IDictionary<string, Service> dict)
    {
        var stack = new Stack<BreadcrumbDto>();
        var current = leaf;

        // climb to root
        while (true)
        {
            stack.Push(new BreadcrumbDto { Id = current.Id, Name = current.Name });

            if (string.IsNullOrEmpty(current.ParentServiceId) ||
                !dict.TryGetValue(current.ParentServiceId!, out var parent))
                break;

            current = parent;
        }

        return stack.ToList(); // from root → leaf
    }

    private static int CountTaskTemplates(JsonDocument schema)
    {
        if (schema.RootElement.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (!schema.RootElement.TryGetProperty("tasks", out var tasksNode) || tasksNode.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return tasksNode.GetArrayLength();
    }

    private static string? ValidateTaskDependencyGraph(
        JsonDocument schema,
        IRequestFormSchemaParser schemaParser,
        IRequestTaskDependencyGraphValidator dependencyGraphValidator)
    {
        var parsedSchema = schemaParser.Parse(schema);
        var slaRuleError = ValidateTaskSlaRules(parsedSchema.Tasks);
        if (slaRuleError is not null)
        {
            return slaRuleError;
        }

        var failurePolicyError = ValidateTaskFailurePolicyRules(parsedSchema.Tasks);
        if (failurePolicyError is not null)
        {
            return failurePolicyError;
        }

        var approvalError = ValidateTaskApprovalRules(parsedSchema.Tasks);
        if (approvalError is not null)
        {
            return approvalError;
        }

        var validationResult = dependencyGraphValidator.Validate(parsedSchema.Tasks);
        if (validationResult.IsValid)
        {
            return null;
        }

        return validationResult.HasCycle
            ? "Circular task dependency detected in workflow."
            : validationResult.ErrorMessage ?? "Invalid task dependency configuration.";
    }

    private static string? ValidateTaskSlaRules(IReadOnlyCollection<RequestTaskTemplateModel> templates)
    {
        foreach (var template in templates)
        {
            if (template.EscalateAfterMinutes.HasValue
                && template.TaskSlaMinutes.HasValue
                && template.EscalateAfterMinutes.Value > template.TaskSlaMinutes.Value)
            {
                return "Escalation threshold cannot exceed task SLA.";
            }
        }

        return null;
    }

    private static string? ValidateTaskFailurePolicyRules(IReadOnlyCollection<RequestTaskTemplateModel> templates)
    {
        foreach (var template in templates)
        {
            if (template.MaxRetries.HasValue && template.MaxRetries.Value < 0)
            {
                return "MaxRetries must be greater than or equal to 0.";
            }

            if (template.RetryDelayMinutes.HasValue && template.RetryDelayMinutes.Value < 0)
            {
                return "RetryDelayMinutes must be greater than or equal to 0.";
            }

            var isManualTask = !string.Equals(template.Type, "automation", StringComparison.OrdinalIgnoreCase);
            if (isManualTask && (template.MaxRetries.HasValue || template.RetryDelayMinutes.HasValue))
            {
                return "Retry policy is only supported for automation tasks.";
            }

            if (template.IsCritical
                && string.Equals(template.FailurePolicy, "Continue", StringComparison.OrdinalIgnoreCase))
            {
                return "Critical tasks cannot use the Continue failure policy.";
            }

            if (!string.IsNullOrWhiteSpace(template.FailurePolicy)
                && !string.Equals(template.FailurePolicy, "FailRequest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(template.FailurePolicy, "BlockRequest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(template.FailurePolicy, "Continue", StringComparison.OrdinalIgnoreCase))
            {
                return "FailurePolicy must be one of: FailRequest, BlockRequest, Continue.";
            }
        }

        return null;
    }

    private static string? ValidateTaskApprovalRules(IReadOnlyCollection<RequestTaskTemplateModel> templates)
    {
        foreach (var template in templates.Where(x => string.Equals(x.Type, "approval", StringComparison.OrdinalIgnoreCase)))
        {
            if (template.ApprovalAllowedDays is < 1 or > 14)
            {
                return "Approval time allowed must be between 1 and 14 days.";
            }

            if (template.ApprovalApprovers.Count == 0)
            {
                return "Approval tasks require at least one approver.";
            }

            if (template.ApprovalApprovers.Any(x => string.IsNullOrWhiteSpace(x.Email)))
            {
                return "Approval approvers require an email address.";
            }
        }

        return null;
    }

    private static string GetCorrelationId(ICorrelationContext correlationContext)
    {
        return correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private static bool IsAllowedForTenant(IReadOnlyCollection<string>? allowedOrganizationIds, string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        return allowedOrganizationIds == null
               || allowedOrganizationIds.Count == 0
               || allowedOrganizationIds.Contains(tenantId, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAllowedForCustomer(IReadOnlyCollection<string>? allowedCustomerIds, string? customerId)
    {
        if (allowedCustomerIds == null || allowedCustomerIds.Count == 0)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(customerId)
               && allowedCustomerIds.Contains(customerId, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ResolveRequestFormOrganizationId(
        string? explicitOrganizationId,
        IReadOnlyCollection<string>? allowedOrganizationIds,
        string? fallbackTenantId)
    {
        if (!string.IsNullOrWhiteSpace(explicitOrganizationId))
        {
            return explicitOrganizationId.Trim();
        }

        var normalizedAllowed = NormalizeAllowedOrganizations(null, allowedOrganizationIds);
        if (normalizedAllowed.Count == 1)
        {
            return normalizedAllowed[0];
        }

        return string.IsNullOrWhiteSpace(fallbackTenantId) ? null : fallbackTenantId.Trim();
    }

    private static List<string> NormalizeAllowedOrganizations(
        string? explicitOrganizationId,
        IReadOnlyCollection<string>? allowedOrganizationIds)
    {
        var values = (allowedOrganizationIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(explicitOrganizationId)
            && !values.Contains(explicitOrganizationId.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            values.Add(explicitOrganizationId.Trim());
        }

        return values;
    }
}
