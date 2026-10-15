using System.Security.Claims;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Category;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Categories;

public static class TicketCategoryEndpoints
{
    public static void MapTicketCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/categories")
            .WithTags("Ticket Categories")
            .RequireAuthorization("TicketReadAccess");

        group.MapGet("/", GetCategories);

        group.MapPost("/", CreateCategory)
            .RequireAuthorization("HelpdeskAdmin");

        group.MapPut("/{id:guid}", UpdateCategory)
            .RequireAuthorization("HelpdeskAdmin");

        group.MapDelete("/{id:guid}", DeleteCategory)
            .RequireAuthorization("HelpdeskAdmin");
    }

    private static async Task<IResult> GetCategories(
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITenantContext tenant,
        [FromQuery] TicketCategoryType? type,
        [FromQuery] string? search,
        [FromQuery] string? ids,
        [FromQuery] int? take,
        [FromQuery] Guid? tenantId,
        [FromQuery] bool? allScopes,
        [FromQuery] bool? includeInactive,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if ((allScopes == true || includeInactive == true) && !tenant.IsHelpdeskAdmin) return Results.Forbid();
        var currentTenantId = ParseTenantId(tenant.TenantId);
        if (tenantId.HasValue && !tenant.IsHelpdeskAdmin && tenantId != currentTenantId)
        {
            var access = CurrentUserAccessProfile.FromClaims(user);
            var organization = tenantId.Value.ToString();
            var permitted = access.OrganizationIdsForAny(
                HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentManager, HelpdeskPermissions.IncidentUser,
                HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager, HelpdeskPermissions.RequestUser,
                HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeManager, HelpdeskPermissions.ChangeUser);
            if (!permitted.Contains(organization)) return Results.Forbid();
        }
        tenantId ??= currentTenantId;
        var selectedIds = new List<Guid>();
        if (!string.IsNullOrEmpty(ids))
        {
            foreach (var value in ids.Split(','))
            {
                if (!Guid.TryParse(value, out var id) || selectedIds.Count >= 100)
                    return Results.BadRequest("A maximum of 100 valid category IDs is supported.");
                selectedIds.Add(id);
            }
        }
        var query = db.TicketCategories
            .AsNoTracking()
            .Where(x => (selectedIds.Count > 0 || includeInactive == true || x.IsActive) &&
                        (allScopes == true || x.TenantId == null || (tenantId.HasValue && x.TenantId == tenantId.Value)));

        if (selectedIds.Count > 0)
            query = query.Where(x => selectedIds.Contains(x.Id));

        if (type.HasValue)
        {
            var requestedType = type.Value;
            query = query.Where(x => x.Type == requestedType || x.Type == TicketCategoryType.Service);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = NormalizeName(search).ToLowerInvariant();
            if (term.Length > 120) return Results.BadRequest("Search must be 120 characters or fewer.");

            query = query
                .Where(x => x.Name.ToLower().Contains(term))
                .OrderBy(x => x.Name)
                .Take(50);
        }

        query = query.OrderBy(x => x.Type).ThenBy(x => x.SortOrder).ThenBy(x => x.Name);
        if (take.HasValue || !string.IsNullOrWhiteSpace(search))
            query = query.Take(Math.Clamp(take ?? 50, 1, 100));

        var items = await query
            .Select(x => new TicketCategoryDto
            {
                Id = x.Id,
                Name = x.Name,
                Description = x.Description,
                Type = x.Type,
                TenantId = x.TenantId,
                ParentCategoryId = x.ParentCategoryId,
                SortOrder = x.SortOrder,
                IsActive = x.IsActive,
                IsSystem = x.IsSystem,
                CreatedUtc = x.CreatedUtc,
                UpdatedUtc = x.UpdatedUtc
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(items);
    }

    private static async Task<IResult> CreateCategory(
        [FromBody] CreateTicketCategoryDto dto,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            return Results.BadRequest("Name is required.");
        }

        var name = NormalizeName(dto.Name);
        if (name.Length > 120 || !Enum.IsDefined(dto.Type))
            return Results.BadRequest("A valid category type and a name of at most 120 characters are required.");

        Guid? targetTenantId;
        var isSystem = dto.TenantId is null;

        if (isSystem)
        {
            if (!tenant.IsHelpdeskAdmin)
            {
                return Results.Forbid();
            }

            targetTenantId = null;
        }
        else
        {
            var exists = await db.Organizations.AnyAsync(x => x.Id == dto.TenantId!.Value.ToString());
            if (!exists)
            {
                return Results.BadRequest("Tenant does not exist.");
            }

            targetTenantId = dto.TenantId;
        }

        // Serialize catalogue writes across API replicas. SQLite's serializable transaction
        // acquires its write lock; PostgreSQL needs a table lock for the absent-row case.
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsNpgsql() ? global::System.Data.IsolationLevel.ReadCommitted : global::System.Data.IsolationLevel.Serializable, cancellationToken);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"TicketCategories\" IN SHARE ROW EXCLUSIVE MODE", cancellationToken);
        var existing = await FindDuplicate(db, name, dto.Type, targetTenantId, cancellationToken);
        if (existing is not null)
            return existing.IsActive ? Results.Ok(ToDto(existing)) : Results.Conflict("An inactive category already uses this name. Manage it in Categories.");

        var entity = new TicketCategory
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = dto.Description,
            Type = dto.Type,
            TenantId = targetTenantId,
            ParentCategoryId = dto.ParentCategoryId,
            SortOrder = dto.SortOrder,
            IsActive = dto.IsActive,
            IsSystem = isSystem,
            CreatedUtc = DateTime.UtcNow
        };

        db.TicketCategories.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.Created($"/api/v1/categories/{entity.Id}", ToDto(entity));
    }

    private static async Task<IResult> UpdateCategory(
        [FromRoute] Guid id,
        [FromBody] UpdateTicketCategoryDto dto,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITenantContext tenant)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || NormalizeName(dto.Name).Length > 120 || !Enum.IsDefined(dto.Type))
            return Results.BadRequest("A valid category type and a name of 1 to 120 characters are required.");
        var tenantId = ParseTenantId(tenant.TenantId);
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsNpgsql() ? global::System.Data.IsolationLevel.ReadCommitted : global::System.Data.IsolationLevel.Serializable);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"TicketCategories\" IN SHARE ROW EXCLUSIVE MODE");
        var entity = await db.TicketCategories.FirstOrDefaultAsync(x => x.Id == id);
        if (entity is null)
        {
            return Results.NotFound();
        }

        if (entity.IsSystem)
        {
            return Results.BadRequest("System categories are read-only.");
        }

        var duplicate = await FindDuplicate(db, NormalizeName(dto.Name), dto.Type, entity.TenantId, CancellationToken.None);
        if (duplicate is not null && duplicate.Id != id) return Results.Conflict("A category already uses this name in this scope.");

        if (tenant.IsHelpdeskAdmin)
        {
            entity.Name = NormalizeName(dto.Name);
            entity.Description = dto.Description;
            entity.Type = dto.Type;
            entity.ParentCategoryId = dto.ParentCategoryId;
            entity.SortOrder = dto.SortOrder;
            entity.IsActive = dto.IsActive;
            entity.UpdatedUtc = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.Ok();
        }

        if (!tenantId.HasValue)
        {
            return Results.BadRequest("Tenant context required to manage categories.");
        }

        if (!tenantId.HasValue || entity.TenantId != tenantId.Value)
        {
            return Results.Forbid();
        }

        entity.Name = NormalizeName(dto.Name);
        entity.Description = dto.Description;
        entity.Type = dto.Type;
        entity.ParentCategoryId = dto.ParentCategoryId;
        entity.SortOrder = dto.SortOrder;
        entity.IsActive = dto.IsActive;
        entity.UpdatedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Results.Ok();
    }

    private static async Task<IResult> DeleteCategory(
        [FromRoute] Guid id,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITenantContext tenant)
    {
        var tenantId = ParseTenantId(tenant.TenantId);
        var entity = await db.TicketCategories.FirstOrDefaultAsync(x => x.Id == id);
        if (entity is null)
        {
            return Results.NotFound();
        }

        if (entity.IsSystem)
        {
            return Results.BadRequest("System categories are read-only.");
        }

        if (tenant.IsHelpdeskAdmin)
        {
            db.TicketCategories.Remove(entity);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }

        if (!tenantId.HasValue)
        {
            return Results.BadRequest("Tenant context required to manage categories.");
        }

        if (!tenantId.HasValue || entity.TenantId != tenantId.Value)
        {
            return Results.Forbid();
        }

        db.TicketCategories.Remove(entity);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static string NormalizeName(string name) => string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task<TicketCategory?> FindDuplicate(HelpdeskDbContext db, string name, TicketCategoryType type, Guid? tenantId, CancellationToken cancellationToken)
    {
        // Existing data may predate whitespace normalization; compare without rewriting assignments.
        var scoped = await db.TicketCategories.Where(x => x.Type == type && x.TenantId == tenantId).ToListAsync(cancellationToken);
        return scoped.FirstOrDefault(x => string.Equals(NormalizeName(x.Name), name, StringComparison.OrdinalIgnoreCase));
    }

    private static TicketCategoryDto ToDto(TicketCategory entity) => new TicketCategoryDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Type = entity.Type,
            TenantId = entity.TenantId,
            ParentCategoryId = entity.ParentCategoryId,
            SortOrder = entity.SortOrder,
            IsActive = entity.IsActive,
            IsSystem = entity.IsSystem,
            CreatedUtc = entity.CreatedUtc,
            UpdatedUtc = entity.UpdatedUtc
        };

    private static Guid? ParseTenantId(string? tenantId)
    {
        return Guid.TryParse(tenantId, out var parsed) ? parsed : null;
    }
}
