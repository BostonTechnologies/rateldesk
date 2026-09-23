using System.Security.Claims;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;

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
                IRepository<TicketTimelineEvent> timelineRepo,
                CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();

                var failed = (await timelineRepo.GetAllAsync())
                    .Where(x =>
                        x.EventType == TimelineEventType.EmailDelivery &&
                        x.EmailStatus == EmailDeliveryStatus.Failed)
                    .OrderByDescending(x => x.CreatedUtc)
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
                        IsRetryable = x.IsRetryable
                    })
                    .ToList();

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
