using Helpdesk.API.Services;
using Helpdesk.Application.Events;
using Helpdesk.Application.Resources;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EntityState = Helpdesk.Shared.Models.EntityState;
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
            SelfServiceAudienceService audience, ClaimsPrincipal user,
            [FromQuery] string? q, [FromQuery] int? pageSize, CancellationToken token,
            [FromQuery] bool includeTotal = true) =>
        {
            var take = Math.Clamp(pageSize ?? 6, 1, 25);
            var term = q?.Trim();
            var catalogue = await ReadCatalogueAsync(audience, user, token);
            var combined = catalogue
                .Where(entry => string.IsNullOrEmpty(term) ||
                    entry.Item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    entry.Item.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Item)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
            var items = combined.Take(take).ToList();
            return Results.Ok(new PagedResponse<ServiceItemDto>
            {
                Page = 1, PageSize = take, TotalCount = includeTotal ? combined.Count : items.Count, Items = items
            });
        })
        .WithName("SearchServiceItems")
        .WithSummary("Search requestable self-service catalog items.");

        itemsGroup.MapGet("/{parentServiceId?}", async (
            string? parentServiceId, SelfServiceAudienceService audience, ClaimsPrincipal user, CancellationToken token) =>
        {
            var catalogue = await ReadCatalogueAsync(audience, user, token);
            return Results.Ok(catalogue.Where(entry => entry.ParentId == parentServiceId).Select(entry => entry.Item));
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
            string id, SelfServiceAudienceService audience, ClaimsPrincipal user, CancellationToken token) =>
        {
            var authority = await audience.ResolveAuthorityAsync(user, token);
            var catalogue = await audience.LoadCatalogueAsync(authority, token);
            if (!catalogue.CanViewFolder(id)) return Results.NotFound();
            var dto = ToDtoWithDepth(catalogue.Folders[id], catalogue.Folders);
            if (!authority.Access.IsHelpdeskAdmin)
            {
                dto.AllowedCustomerIds = [];
                dto.AllowedOrganizationIds = [];
            }
            return Results.Ok(dto);
        })
        .WithName("GetServiceById")
        .WithSummary("Get a single service by id");

        services.MapGet("/{id}/breadcrumb", async (
            string id, SelfServiceAudienceService audience, ClaimsPrincipal user, CancellationToken token) =>
        {
            var authority = await audience.ResolveAuthorityAsync(user, token);
            var catalogue = await audience.LoadCatalogueAsync(authority, token);
            if (!catalogue.CanViewFolder(id)) return Results.NotFound();
            return Results.Ok(BuildBreadcrumb(catalogue.Folders[id], catalogue.Folders));
        })
        .WithName("GetServiceBreadcrumb")
        .WithSummary("Get breadcrumb chain from root to this service");

        services.MapPost("/", async ([FromBody] CreateServiceDto dto, IRepository<Service> repo,
            HelpdeskDbContext db, CancellationToken token) =>
        {
            var audience = NormalizeAllowedOrganizations(dto.AllowedOrganizationIds);
            var parentId = dto.ParentId;
            if (await HasUnavailableOrganizationsAsync(db, audience, null, token))
                return Results.BadRequest("One or more organizations are unavailable for assignment.");
            if (parentId is not null && !await db.Services.AnyAsync(folder => folder.Id == parentId, token))
                return Results.BadRequest("The selected parent folder is unavailable.");

            var service = new Service
            {
                Name = dto.Name,
                Description = dto.Description ?? string.Empty,
                ParentServiceId = parentId,
                AllowedCustomerIds = dto.AllowedCustomerIds?.ToList() ?? new List<string>(),
                AllowedOrganizationIds = audience
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

        services.MapPut("/{id}", async (string id, [FromBody] UpdateServiceDto dto, IRepository<Service> repo,
            HelpdeskDbContext db, CancellationToken token) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound();

            if (dto.AllowedOrganizationIds is not null)
            {
                var audience = NormalizeAllowedOrganizations(dto.AllowedOrganizationIds);
                if (await HasUnavailableOrganizationsAsync(db, audience, existing.AllowedOrganizationIds, token))
                    return Results.BadRequest("One or more organizations are unavailable for assignment.");
                existing.AllowedOrganizationIds = audience;
            }

            if (dto.ParentIdHasValue || dto.ParentId is not null)
            {
                if (dto.ParentId is not null)
                {
                    var folders = await db.Services.AsNoTracking()
                        .ToDictionaryAsync(folder => folder.Id, folder => folder.ParentServiceId, token);
                    var visited = new HashSet<string>(StringComparer.Ordinal) { existing.Id };
                    for (var ancestor = dto.ParentId; ancestor is not null; ancestor = folders[ancestor])
                    {
                        if (!visited.Add(ancestor) || !folders.ContainsKey(ancestor))
                            return Results.BadRequest("Choose an existing parent outside this folder's descendants.");
                    }
                }
                existing.ParentServiceId = dto.ParentId;
            }

            existing.Name = dto.Name ?? existing.Name;
            existing.Description = dto.Description ?? existing.Description;
            if (dto.AllowedCustomerIds is not null)
                existing.AllowedCustomerIds = dto.AllowedCustomerIds;

            await db.SaveChangesAsync(token);

            var all = await repo.GetAllAsync();
            var dtoOut = ToDtoWithDepth(existing, all.ToDictionary(s => s.Id));

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
            string id, SelfServiceAudienceService audience, ClaimsPrincipal user, CancellationToken token) =>
        {
            var authority = await audience.ResolveAuthorityAsync(user, token);
            var form = await audience.FindFormAsync(authority, id, token);
            if (form is null) return Results.NotFound();
            var catalogue = await audience.LoadCatalogueAsync(authority, token, form.Id);
            var organizations = catalogue.FormOrganizations(form);
            if (!authority.Access.IsHelpdeskAdmin && organizations.Count == 0) return Results.NotFound();
            return Results.Ok(new RequestFormDto
            {
                Id = form.Id,
                ServiceId = form.ServiceId,
                Title = form.Title,
                Description = form.Description,
                JsonSchema = form.JsonSchema.RootElement.GetRawText(),
                OrganizationId = form.OrganizationId,
                AllowedOrganizationIds = authority.Access.IsHelpdeskAdmin ? form.AllowedOrganizationIds : [],
                ParentOrganizationIds = authority.Access.IsHelpdeskAdmin ? catalogue.ParentOrganizationIds(form.ServiceId) : null,
                EligibleOrganizations = organizations.Where(org => authority.CanSubmitAsSelf(org.Id)).ToList(),
                ReleaseStatus = form.ReleaseStatus
            });
        })
        .WithName("GetRequestFormById");

        formsTop.MapPost("/", async ([FromBody] CreateRequestFormDto dto,
                                     [FromServices] IRepository<RequestForm> repo,
                                     [FromServices] IRequestFormSchemaParser schemaParser,
                                     [FromServices] IRequestTaskDependencyGraphValidator dependencyGraphValidator,
                                     [FromServices] IRequestFormDatasetBindingValidator bindingValidator,
                                     [FromServices] IDomainEventPublisher domainEvents,
                                     [FromServices] ICorrelationContext correlationContext,
                                     ITenantContext tenant, HelpdeskDbContext db,
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

            var audience = NormalizeAllowedOrganizations(dto.AllowedOrganizationIds);
            var organizationId = ResolveRequestFormOrganizationId(dto.OrganizationId, audience, tenant.TenantId);
            if (await HasUnavailableOrganizationsAsync(db, WithOwner(audience, organizationId), null, token))
                return Results.BadRequest("One or more organizations are unavailable for assignment.");
            if (!await db.Services.AnyAsync(folder => folder.Id == dto.ServiceId, token))
                return Results.BadRequest("The selected folder is unavailable.");
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
                AllowedOrganizationIds = audience,
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
                                        HelpdeskDbContext db, CancellationToken token) =>
        {
            var f = await repo.GetByIdAsync(id);
            if (f is null) return Results.NotFound();

            var organizationId = dto.OrganizationId is null
                ? f.OrganizationId : ResolveRequestFormOrganizationId(dto.OrganizationId, null, null);
            var audience = dto.AllowedOrganizationIds is null
                ? f.AllowedOrganizationIds : NormalizeAllowedOrganizations(dto.AllowedOrganizationIds);
            if (await HasUnavailableOrganizationsAsync(db, WithOwner(audience, organizationId),
                WithOwner(f.AllowedOrganizationIds, f.OrganizationId), token))
                return Results.BadRequest("One or more organizations are unavailable for assignment.");

            var schemaDoc = f.JsonSchema;
            if (!string.IsNullOrWhiteSpace(dto.JsonSchema))
            {
                schemaDoc = JsonDocument.Parse(dto.JsonSchema);
                var validationError = ValidateTaskDependencyGraph(schemaDoc, schemaParser, dependencyGraphValidator);
                if (validationError is not null) return Results.BadRequest(validationError);
            }
            if (schemaDoc != f.JsonSchema || organizationId != f.OrganizationId)
            {
                var bindingError = await bindingValidator.ValidateAsync(organizationId ?? string.Empty, schemaParser.Parse(schemaDoc).Fields, token);
                if (bindingError is not null) return Results.BadRequest(bindingError);
            }

            if (dto.ServiceIdHasValue && !string.IsNullOrEmpty(dto.ServiceId) &&
                !await db.Services.AnyAsync(folder => folder.Id == dto.ServiceId, token))
                return Results.BadRequest("The selected folder is unavailable.");

            f.Title = dto.Title ?? f.Title;
            f.Description = dto.Description ?? f.Description;
            f.JsonSchema = schemaDoc;
            if (dto.ServiceIdHasValue) f.ServiceId = dto.ServiceId ?? string.Empty;
            f.AllowedOrganizationIds = audience;
            f.OrganizationId = organizationId;
            if (dto.ReleaseStatus.HasValue) f.ReleaseStatus = dto.ReleaseStatus.Value;

            await db.SaveChangesAsync(token);
            await domainEvents.PublishAsync(
                new RequestFormUpdatedDomainEvent(
                    f.Id,
                    f.ServiceId,
                    f.Title,
                    CountTaskTemplates(f.JsonSchema),
                    f.OrganizationId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlationContext)),
                token);

            var result = new RequestFormDto
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
                                        ITenantContext tenant, HelpdeskDbContext db,
                                        CancellationToken token) =>
        {
            var schemaDoc = JsonDocument.Parse(dto.JsonSchema);
            var validationError = ValidateTaskDependencyGraph(schemaDoc, schemaParser, dependencyGraphValidator);
            if (validationError is not null)
            {
                return Results.BadRequest(validationError);
            }

            var audience = NormalizeAllowedOrganizations(dto.AllowedOrganizationIds);
            var organizationId = ResolveRequestFormOrganizationId(dto.OrganizationId, audience, tenant.TenantId);
            if (await HasUnavailableOrganizationsAsync(db, WithOwner(audience, organizationId), null, token))
                return Results.BadRequest("One or more organizations are unavailable for assignment.");
            if (!await db.Services.AnyAsync(folder => folder.Id == serviceId, token))
                return Results.BadRequest("The selected folder is unavailable.");
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
                AllowedOrganizationIds = audience,
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
    private sealed record CatalogueEntry(string? ParentId, ServiceItemDto Item);

    private static async Task<List<CatalogueEntry>> ReadCatalogueAsync(
        SelfServiceAudienceService audience, ClaimsPrincipal user, CancellationToken token)
    {
        var authority = await audience.ResolveAuthorityAsync(user, token);
        var catalogue = await audience.LoadCatalogueAsync(authority, token);
        var admin = authority.Access.IsHelpdeskAdmin;
        var folders = catalogue.Folders.Values.Where(folder => catalogue.CanViewFolder(folder.Id)).ToList();
        var forms = await audience.CandidateForms(authority).ToListAsync(token);
        var entries = new List<CatalogueEntry>();
        foreach (var form in forms)
        {
            IReadOnlyList<SelfServiceTargetOrganizationDto> organizations = admin ? [] : catalogue.FormOrganizations(form);
            if (!admin && organizations.Count == 0) continue;
            entries.Add(new CatalogueEntry(string.IsNullOrEmpty(form.ServiceId) ? null : form.ServiceId, new ServiceItemDto
            {
                Id = form.Id,
                Name = form.Title,
                Description = form.Description,
                ItemType = ServiceItemType.RequestForm,
                AllowedOrganizationIds = admin ? form.AllowedOrganizationIds : null,
                ParentOrganizationIds = admin ? catalogue.ParentOrganizationIds(form.ServiceId) : null,
                EligibleOrganizations = organizations,
                ReleaseStatus = form.ReleaseStatus
            }));
        }

        var counts = BuildAvailableRequestCounts(
            folders.Select(folder => new ServiceCountNode(folder.Id, folder.ParentServiceId)),
            entries.Select(entry => entry.ParentId));
        entries.InsertRange(0, folders.Select(folder => new CatalogueEntry(folder.ParentServiceId, new ServiceItemDto
        {
            Id = folder.Id,
            Name = folder.Name,
            Description = folder.Description,
            ItemType = ServiceItemType.Service,
            AvailableRequestCount = counts.GetValueOrDefault(folder.Id),
            AllowedOrganizationIds = admin ? folder.AllowedOrganizationIds : null,
            ParentOrganizationIds = admin ? catalogue.ParentOrganizationIds(folder.ParentServiceId) : null
        })));
        return entries;
    }

    private sealed record ServiceCountNode(string Id, string? ParentServiceId);

    private static Dictionary<string, int> BuildAvailableRequestCounts(
        IEnumerable<ServiceCountNode> services,
        IEnumerable<string?> requestFormServiceIds)
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

    private static ServiceDto ToDtoWithDepth(Service s, IReadOnlyDictionary<string, Service> dict)
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
            AllowedOrganizationIds = s.AllowedOrganizationIds,
            Depth = depth
        };
    }

    private static List<BreadcrumbDto> BuildBreadcrumb(Service leaf, IReadOnlyDictionary<string, Service> dict)
    {
        var stack = new Stack<BreadcrumbDto>();
        var current = leaf;

        // climb to root
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current.Id))
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

    private static string? ResolveRequestFormOrganizationId(
        string? organizationId, IReadOnlyCollection<string>? allowedOrganizationIds, string? fallbackTenantId)
    {
        // An empty primary selection explicitly creates an ownerless form. The
        // omitted value retains the legacy ownership default for existing clients.
        if (organizationId is not null)
            return string.IsNullOrWhiteSpace(organizationId) ? null : organizationId.Trim();

        var explicitAudience = NormalizeAllowedOrganizations(allowedOrganizationIds);
        return explicitAudience.Count == 1 ? explicitAudience[0] : fallbackTenantId;
    }

    private static List<string> NormalizeAllowedOrganizations(IReadOnlyCollection<string>? allowedOrganizationIds) =>
        (allowedOrganizationIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IEnumerable<string> WithOwner(IEnumerable<string>? audience, string? organizationId) =>
        string.IsNullOrWhiteSpace(organizationId) ? audience ?? [] : (audience ?? []).Append(organizationId);

    private static async Task<bool> HasUnavailableOrganizationsAsync(
        HelpdeskDbContext db, IEnumerable<string> organizationIds, IEnumerable<string>? current, CancellationToken token)
    {
        var added = organizationIds.Except(current ?? [], StringComparer.Ordinal).ToArray();
        return added.Length > 0 && await db.Organizations.AsNoTracking()
            .CountAsync(org => org.State == EntityState.Enabled && added.Contains(org.Id), token) != added.Length;
    }
}
