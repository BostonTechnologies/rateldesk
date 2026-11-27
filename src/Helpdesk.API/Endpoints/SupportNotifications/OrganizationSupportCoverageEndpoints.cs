using System.Security.Claims;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.SupportNotifications;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.SupportNotifications;

public static class OrganizationSupportCoverageEndpoints
{
    public static void MapOrganizationSupportCoverageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/support/coverage")
            .WithTags("Support Coverage")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/", async (
            [FromServices] IRepository<OrganizationSupportCoverage> repo,
            string? customerOrganizationId = null) =>
        {
            var items = (await repo.GetAllAsync())
                .Where(x => string.IsNullOrWhiteSpace(customerOrganizationId) || string.Equals(x.CustomerOrganizationId, customerOrganizationId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.CustomerOrganizationId)
                .ThenBy(x => x.ProviderOrganizationId)
                .Select(ToDto)
                .ToList();
            return Results.Ok(items);
        });

        group.MapPost("/", async (CreateOrganizationSupportCoverageDto dto, HelpdeskDbContext db, ClaimsPrincipal actor, CancellationToken ct) =>
        {
            var validation = await ValidateAsync(db, dto.CustomerOrganizationId, dto.ProviderOrganizationId, dto.SupportGroupId, dto.Role, ct);
            if (validation is not null) return validation;

            var created = new OrganizationSupportCoverage
            {
                CustomerOrganizationId = dto.CustomerOrganizationId.Trim(),
                ProviderOrganizationId = dto.ProviderOrganizationId.Trim(),
                SupportGroupId = dto.SupportGroupId.Trim(),
                Role = dto.Role,
                IsEnabled = dto.IsEnabled
            };
            db.OrganizationSupportCoverages.Add(created);
            SupportGroupEndpoints.RecordChange(db, actor, created.Id, "Created support coverage.");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/support/coverage/{created.Id}", ToDto(created));
        });

        group.MapPut("/{id}", async (string id, UpdateOrganizationSupportCoverageDto dto, HelpdeskDbContext db, ClaimsPrincipal actor, CancellationToken ct) =>
        {
            var existing = await db.OrganizationSupportCoverages.SingleOrDefaultAsync(coverage => coverage.Id == id, ct);
            if (existing is null) return Results.NotFound();
            var disabling = !dto.IsEnabled &&
                existing.CustomerOrganizationId == dto.CustomerOrganizationId?.Trim() &&
                existing.ProviderOrganizationId == dto.ProviderOrganizationId?.Trim() &&
                existing.SupportGroupId == dto.SupportGroupId?.Trim() && existing.Role == dto.Role;
            if (disabling)
            {
                existing.IsEnabled = false;
                existing.UpdatedUtc = DateTimeOffset.UtcNow;
                SupportGroupEndpoints.RecordChange(db, actor, id, "Disabled support coverage.");
                await db.SaveChangesAsync(ct);
                return Results.Ok(ToDto(existing));
            }

            var validation = await ValidateAsync(db, dto.CustomerOrganizationId, dto.ProviderOrganizationId, dto.SupportGroupId, dto.Role, ct);
            if (validation is not null) return validation;
            existing.CustomerOrganizationId = dto.CustomerOrganizationId.Trim();
            existing.ProviderOrganizationId = dto.ProviderOrganizationId.Trim();
            existing.SupportGroupId = dto.SupportGroupId.Trim();
            existing.Role = dto.Role;
            existing.IsEnabled = dto.IsEnabled;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            SupportGroupEndpoints.RecordChange(db, actor, id, "Updated support coverage.");
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(existing));
        });

        group.MapDelete("/{id}", async (string id, HelpdeskDbContext db, ClaimsPrincipal actor, CancellationToken ct) =>
        {
            var existing = await db.OrganizationSupportCoverages.SingleOrDefaultAsync(coverage => coverage.Id == id, ct);
            if (existing is null) return Results.NotFound();
            db.OrganizationSupportCoverages.Remove(existing);
            SupportGroupEndpoints.RecordChange(db, actor, id, "Removed support coverage.");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapPost("/api/v1/support/bootstrap/defaults", async (
            [FromServices] ISupportNotificationBootstrapper bootstrapper,
            CancellationToken ct) =>
        {
            var result = await bootstrapper.EnsureDefaultSupportConfigurationAsync(ct);
            return Results.Ok(result);
        })
        .WithTags("Support Coverage")
        .RequireAuthorization("HelpdeskAdmin");

        app.MapGet("/api/v1/support/organizations/{organizationId}/recipient-preview", async (
            string organizationId,
            [FromQuery] SupportNotificationEventType eventType,
            [FromServices] ISupportNotificationRecipientResolver resolver,
            CancellationToken ct) =>
        {
            var recipients = await resolver.ResolveOrganizationEventRecipientsAsync(
                organizationId,
                eventType,
                SupportNotificationChannel.Email,
                ct);
            return Results.Ok(new SupportNotificationRecipientPreviewDto
            {
                CustomerOrganizationId = organizationId,
                EventType = eventType,
                Channel = SupportNotificationChannel.Email,
                Recipients = recipients.Select(x => new SupportNotificationRecipientDto
                {
                    UserId = x.UserId,
                    UserName = x.UserName,
                    Email = x.Email,
                    SupportGroupId = x.SupportGroupId,
                    SupportGroupName = x.SupportGroupName
                }).ToList()
            });
        })
        .WithTags("Support Coverage")
        .RequireAuthorization("HelpdeskAdmin");
    }

    private static async Task<IResult?> ValidateAsync(
        HelpdeskDbContext db, string customerOrganizationId, string providerOrganizationId, string supportGroupId,
        SupportCoverageRole role, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerOrganizationId) ||
            string.IsNullOrWhiteSpace(providerOrganizationId) ||
            string.IsNullOrWhiteSpace(supportGroupId) || !Enum.IsDefined(role))
        {
            return Results.BadRequest("Choose a customer, provider, support group and valid coverage role.");
        }

        var customerId = customerOrganizationId.Trim();
        var providerId = providerOrganizationId.Trim();
        var groupId = supportGroupId.Trim();
        var valid = await db.SupportGroups.AnyAsync(group => group.Id == groupId &&
            group.OwningOrganizationId == providerId &&
            db.Organizations.Any(organization => organization.Id == providerId && organization.IsEnabled) &&
            db.Organizations.Any(organization => organization.Id == customerId && organization.IsEnabled), ct);
        return valid ? null : Results.BadRequest("Choose enabled organizations and a group owned by the provider.");
    }

    private static OrganizationSupportCoverageDto ToDto(OrganizationSupportCoverage source) => new()
    {
        Id = source.Id,
        CustomerOrganizationId = source.CustomerOrganizationId,
        ProviderOrganizationId = source.ProviderOrganizationId,
        SupportGroupId = source.SupportGroupId,
        Role = source.Role,
        IsEnabled = source.IsEnabled,
        CreatedUtc = source.CreatedUtc,
        UpdatedUtc = source.UpdatedUtc
    };
}
