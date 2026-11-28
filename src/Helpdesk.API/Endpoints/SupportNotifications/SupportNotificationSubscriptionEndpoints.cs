using Helpdesk.Shared.DTOs.SupportNotifications;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.API.Endpoints.SupportNotifications;

public static class SupportNotificationSubscriptionEndpoints
{
    public static void MapSupportNotificationSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/support/notification-subscriptions")
            .WithTags("Support Notification Subscriptions")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/", async (
            [FromServices] IRepository<SupportNotificationSubscription> repo,
            string? customerOrganizationId = null) =>
        {
            var items = (await repo.GetAllAsync())
                .Where(x => string.IsNullOrWhiteSpace(customerOrganizationId) || string.Equals(x.CustomerOrganizationId, customerOrganizationId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.CustomerOrganizationId)
                .ThenBy(x => x.EventType)
                .Select(ToDto)
                .ToList();
            return Results.Ok(items);
        });

        group.MapPost("/", async ([FromBody] CreateSupportNotificationSubscriptionDto dto, [FromServices] IRepository<SupportNotificationSubscription> repo) =>
        {
            var validation = Validate(dto.CustomerOrganizationId, dto.RecipientId, dto.Channel, dto.EventType, dto.RecipientType);
            if (validation is not null) return validation;

            var created = await repo.CreateAsync(new SupportNotificationSubscription
            {
                CustomerOrganizationId = dto.CustomerOrganizationId.Trim(),
                EventType = dto.EventType,
                RecipientType = dto.RecipientType,
                RecipientId = dto.RecipientId.Trim(),
                Channel = dto.Channel,
                IsEnabled = dto.IsEnabled
            });
            return Results.Created($"/api/v1/support/notification-subscriptions/{created.Id}", ToDto(created));
        });

        group.MapPut("/{id}", async (string id, [FromBody] UpdateSupportNotificationSubscriptionDto dto, [FromServices] IRepository<SupportNotificationSubscription> repo) =>
        {
            var existing = await repo.GetAsync(id);
            if (existing is null) return Results.NotFound();
            var validation = Validate(dto.CustomerOrganizationId, dto.RecipientId, dto.Channel, dto.EventType, dto.RecipientType);
            if (validation is not null) return validation;

            existing.CustomerOrganizationId = dto.CustomerOrganizationId.Trim();
            existing.EventType = dto.EventType;
            existing.RecipientType = dto.RecipientType;
            existing.RecipientId = dto.RecipientId.Trim();
            existing.Channel = dto.Channel;
            existing.IsEnabled = dto.IsEnabled;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            var updated = await repo.UpdateAsync(existing);
            return Results.Ok(ToDto(updated!));
        });

        group.MapDelete("/{id}", async (string id, [FromServices] IRepository<SupportNotificationSubscription> repo) =>
            await repo.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());
    }

    private static IResult? Validate(string customerOrganizationId, string recipientId, SupportNotificationChannel channel,
        SupportNotificationEventType eventType, SupportNotificationRecipientType recipientType)
    {
        if (string.IsNullOrWhiteSpace(customerOrganizationId) || string.IsNullOrWhiteSpace(recipientId))
        {
            return Results.BadRequest("Customer organization and recipient are required.");
        }

        if (channel != SupportNotificationChannel.Email)
        {
            return Results.BadRequest("Only email support notifications are supported in v1.");
        }
        if (eventType == SupportNotificationEventType.TicketAssignedToSupportGroup && recipientType != SupportNotificationRecipientType.SupportGroup)
            return Results.BadRequest("Group assignment notifications require a support group recipient.");

        return null;
    }

    private static SupportNotificationSubscriptionDto ToDto(SupportNotificationSubscription source) => new()
    {
        Id = source.Id,
        CustomerOrganizationId = source.CustomerOrganizationId,
        EventType = source.EventType,
        RecipientType = source.RecipientType,
        RecipientId = source.RecipientId,
        Channel = source.Channel,
        IsEnabled = source.IsEnabled,
        CreatedUtc = source.CreatedUtc,
        UpdatedUtc = source.UpdatedUtc
    };
}
