using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Timeline;

public static class TimelineEndpoints
{
    public static void MapTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        MapTimelineGroup(app.MapGroup("/api/v1/timeline"), "V1");
        MapTimelineGroup(app.MapGroup("/api/timeline").ExcludeFromDescription(), "Legacy");
    }

    private static void MapTimelineGroup(RouteGroupBuilder group, string nameSuffix)
    {
        group
            .WithTags("Timeline")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapPost("/{id:guid}/retry",
            async (
                Guid id,
                HttpContext context,
                ITimelineService service,
                CancellationToken ct) =>
            {
                var userId = ResolveUserId(context);
                if (string.IsNullOrWhiteSpace(userId))
                    return Results.Unauthorized();

                try
                {
                    await service.RetryEmailAsync(id, userId, ct);
                    return Results.Ok();
                }
                catch (InvalidOperationException error)
                {
                    return Results.Conflict(new { message = error.Message });
                }
            })
            .WithName($"RetryTimelineEmail{nameSuffix}")
            .WithSummary("Retry a failed timeline email delivery")
            .WithDescription("Retries one failed email delivery timeline event.");

        group.MapGet("/{id:guid}/outgoing-retry-preview", async (Guid id,
            [FromServices] MailboxOutgoingRetryService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(id, ct)))
            .WithName($"PreviewTimelineOutgoingRetry{nameSuffix}");

        group.MapPost("/{id:guid}/retry-current-outgoing", async (Guid id,
            ConfirmMailboxOutgoingRetryRequest request, HttpContext context,
            [FromServices] MailboxOutgoingRetryService service, CancellationToken ct) =>
        {
            var userId = ResolveUserId(context);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
            if (!request.Confirmed) return Results.BadRequest(new { message = "Confirm the current outgoing revision." });
            var result = await service.RetryAsync(id, request.ExpectedOutgoingVersion, userId, ct);
            return result.CanRetry && result.Status == "Queued" ? Results.Accepted(value: result)
                : Results.Conflict(result);
        }).WithName($"RetryTimelineWithCurrentOutgoing{nameSuffix}");

        group.MapGet("/{id:guid}/uncertain-retry-preview", async (Guid id,
            [FromServices] MailboxOutgoingRetryService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewUncertainAsync(id, ct)))
            .WithName($"PreviewTimelineUncertainRetry{nameSuffix}");

        group.MapPost("/{id:guid}/retry-confirmed-undelivered", async (Guid id,
            ConfirmMailboxUndeliveredRetryRequest request, HttpContext context,
            [FromServices] MailboxOutgoingRetryService service, CancellationToken ct) =>
        {
            var userId = ResolveUserId(context);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
            var result = await service.RetryConfirmedUndeliveredAsync(id, request, userId, ct);
            return result.CanRetry && result.Status == "Queued" ? Results.Accepted(value: result)
                : Results.Conflict(result);
        }).WithName($"RetryTimelineConfirmedUndelivered{nameSuffix}");

        group.MapGet("/{id:guid}/changed-route-preview", async (Guid id,
            [FromServices] MailboxOutgoingRetryService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewChangedRouteAsync(id, ct)))
            .WithName($"PreviewTimelineChangedRoute{nameSuffix}");

        group.MapPost("/{id:guid}/retry-current-route", async (Guid id,
            ConfirmMailboxRouteRetryRequest request, HttpContext context,
            [FromServices] MailboxOutgoingRetryService service, CancellationToken ct) =>
        {
            var userId = ResolveUserId(context);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
            var result = await service.RetryWithCurrentRouteAsync(id, request, userId, ct);
            return result.CanRetry && result.Status == "Queued" ? Results.Accepted(value: result)
                : Results.Conflict(result);
        }).WithName($"RetryTimelineWithCurrentRoute{nameSuffix}");

        group.MapPost("/retry-all",
            async (
                HttpContext context,
                ITimelineService service,
                CancellationToken ct) =>
            {
                var userId = ResolveUserId(context);
                if (string.IsNullOrWhiteSpace(userId))
                    return Results.Unauthorized();

                var count = await service.RetryAllFailedEmailsAsync(userId, ct);
                return Results.Ok(count);
            })
            .WithName($"RetryAllTimelineEmails{nameSuffix}")
            .WithSummary("Retry all failed timeline email deliveries")
            .WithDescription("Retries all currently failed email delivery timeline events.");

        group.MapGet("/pending-count",
            async (
                HttpContext context,
                ITimelineService service,
                CancellationToken ct) =>
            {
                var userId = ResolveUserId(context);
                if (string.IsNullOrWhiteSpace(userId))
                    return Results.Unauthorized();

                var count = await service.GetPendingEmailCountAsync(userId, ct);
                return Results.Ok(count);
            })
            .WithName($"GetPendingTimelineEmailCount{nameSuffix}")
            .WithSummary("Get failed email delivery count")
            .WithDescription("Gets the count of failed email delivery timeline events pending retry.");

        group.MapGet("/failed",
            async (
                [FromServices] HelpdeskDbContext db,
                CancellationToken ct) =>
            {
                var failed = await db.TicketTimelineEvents.AsNoTracking()
                    .Where(x =>
                        x.EventType == TimelineEventType.EmailDelivery &&
                        x.EmailStatus == EmailDeliveryStatus.Failed)
                    .Select(x => new TicketTimelineEventDto
                    {
                        Id = x.Id,
                        TicketId = x.TicketId,
                        CreatedUtc = x.CreatedUtc,
                        CreatedByUserId = x.CreatedByUserId,
                        CreatedByUserName = x.CreatedByUserName,
                        EventType = x.EventType,
                        MessageHtml = x.MessageHtml,
                        MessageText = x.MessageText,
                        EmailStatus = x.EmailStatus,
                        EmailRecipient = x.EmailRecipient,
                        RetryCount = x.RetryCount,
                        IsRetryable = x.RetryError != "SenderRouteChanged" &&
                            x.RetryError != "DispatchOutcomeUnknown" &&
                            x.RetryError != "SubmissionOutcomeUnknown" &&
                            x.RetryError != "SmtpPartialRecipientAcceptance"
                    }).ToListAsync(ct);
                failed = failed.OrderByDescending(x => x.CreatedUtc).ToList();

                var deliveryIds = failed.Select(x => x.Id).ToArray();
                var results = await db.Set<MailboxOutboxEffect>().AsNoTracking()
                    .Where(x => x.DeliveryEventId.HasValue && deliveryIds.Contains(x.DeliveryEventId.Value))
                    .Select(x => new { x.DeliveryEventId, x.LastErrorCode, x.RecipientOutcomeJson })
                    .ToListAsync(ct);
                var byDeliveryId = results.ToDictionary(x => x.DeliveryEventId!.Value);
                foreach (var delivery in failed)
                {
                    if (!byDeliveryId.TryGetValue(delivery.Id, out var result)) continue;
                    delivery.DeliveryErrorCode = result.LastErrorCode;
                    if (result.LastErrorCode is "SenderRouteChanged" or "DispatchOutcomeUnknown" or "SubmissionOutcomeUnknown" or
                        "SmtpPartialRecipientAcceptance")
                        delivery.IsRetryable = false;
                    if (result.RecipientOutcomeJson is null) continue;
                    try
                    {
                        var recipients = JsonSerializer.Deserialize<MailboxRecipientOutcome>(result.RecipientOutcomeJson);
                        delivery.AcceptedRecipients = recipients?.AcceptedRecipients;
                        delivery.RejectedRecipients = recipients?.RejectedRecipients;
                    }
                    catch (JsonException) { delivery.RecipientOutcomeUnavailable = true; }
                }

                return Results.Ok(failed);
            })
            .WithName($"GetFailedTimelineEmails{nameSuffix}")
            .WithSummary("Get failed email deliveries")
            .WithDescription("Gets failed email delivery timeline events.");
    }

    private static string? ResolveUserId(HttpContext context)
    {
        var principal = context.User;

        var claimUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue("preferred_username");

        if (!string.IsNullOrWhiteSpace(claimUserId) &&
            !principal.IsInRole("system.blazor-web"))
        {
            return claimUserId;
        }

        if (context.Request.Headers.TryGetValue("X-Helpdesk-UserId", out var userIdHeader))
            return userIdHeader.ToString();

        return claimUserId;
    }
}
