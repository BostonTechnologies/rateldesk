using Helpdesk.API.Endpoints.Storage;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.Incidents;
using Helpdesk.API.Endpoints.Ticketing;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Infrastructure.Ticketing;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.WorkLogs;

public static class WorkLogEndpoints
{
    public static void MapWorkLogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/incidents")
            .WithTags("Work Logs")
            .RequireAuthorization();

        group.MapGet("/{id}/timeline", async (
            [FromRoute] string id,
            [FromQuery] string? order,
            HttpContext context,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IImageLinkSigner imageLinkSigner,
            [FromServices] IOptions<StorageOptions> storageOptions,
            CancellationToken ct) =>
        {
            var authorization = await AuthorizeIncidentAsync(id, context.User, accessService, db, requireManager: false, ct);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
            }

            var timelineQuery = db.TicketTimelineEvents
                .AsNoTracking()
                .Where(evt => evt.TicketId == id);
            var restricted = authorization.Access!.HasRestrictedWorkforceAccess;
            timelineQuery = TicketTimelineAccess.Filter(timelineQuery,
                TicketAccessPolicy.CanReadStaffContent(authorization.Access, authorization.Incident!), restricted);

            // Filter the authorized ticket in SQL, then sort its materialized events.
            // SQLite does not support DateTimeOffset ordering in SQL.
            var events = await timelineQuery.ToListAsync(ct);
            var ordered = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
                ? events.OrderBy(evt => evt.CreatedUtc)
                : events.OrderByDescending(evt => evt.CreatedUtc);
            var timeline = ordered.Select(evt => restricted
                ? TicketTimelineAccess.Project(TicketTimelineProjection.ToDto(evt), true)
                : ToDto(evt, imageLinkSigner, storageOptions.Value.PublicApiBaseUrl)).ToList();

            return Results.Ok(timeline);
        })
        .WithMetadata(new QueueAwareEndpoint())
        .WithName("GetIncidentTimeline")
        .WithSummary("Gets timeline events for an incident")
        .WithDescription("Retrieves timeline events for the specified incident ordered by CreatedUtc. Defaults to descending (newest first); use ?order=asc for oldest first.");

        group.MapGet("/{id}/timeline/stream", StreamTimeline)
            .WithMetadata(new QueueAwareEndpoint())
            .WithName("StreamIncidentTimeline")
            .WithSummary("Streams incident timeline events in real time")
            .WithDescription("Pushes timeline updates over server-sent events (SSE).");

        group.MapPost("/{id}/worklogs", async (
            [FromRoute] string id,
            [FromBody] CreateWorkLogDto dto,
            ClaimsPrincipal user,
            HttpContext context,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IRequestSender sender,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeIncidentAsync(id, context.User, accessService, db, requireManager: true, cancellationToken);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
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
                NotifyCustomer: !dto.IsInternalNote && !authorization.Access!.HasRestrictedWorkforceAccess,
                IsInternalNote: dto.IsInternalNote,
                AuthorizedTicket: authorization.Incident));

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

            return Results.Created($"/api/v1/incidents/{id}/worklogs/{response.Id}", response);
        })
        .WithMetadata(new QueueAwareEndpoint())
        .WithName("CreateIncidentWorkLog")
        .WithSummary("Creates a work log for an incident")
        .WithDescription("Adds a work log to the incident and notifies the customer");

        app.MapGet("/api/worklogs/{worklogId}/images/{filename}", (
            [FromRoute] string worklogId,
            [FromRoute] string filename,
            IWebHostEnvironment env,
            HttpContext httpContext,
            IImageLinkSigner signer,
            ILoggerFactory loggerFactory,
            IOptions<StorageOptions> storageOptions) =>
        {
            var logger = loggerFactory.CreateLogger("WorklogInlineImage");
            var safeWorklogId = SanitizePathSegment(worklogId);
            var safeFilename = Path.GetFileName(filename);
            if (!string.Equals(filename, safeFilename, StringComparison.Ordinal))
                return Results.BadRequest("Invalid file name.");

            var token = httpContext.Request.Query["token"].ToString();
            if (!signer.ValidateToken(token, "worklog", safeWorklogId, safeFilename))
            {
                logger.LogWarning("Worklog inline image token denied. WorklogId={WorklogId} File={File}", safeWorklogId, safeFilename);
                return Results.Unauthorized();
            }

            var storageRoot = storageOptions.Value.ResolveRootPath(env.ContentRootPath);
            return SignedRasterAsset.Serve(httpContext, Path.Combine(storageRoot, "worklogs", safeWorklogId, "inline"), safeFilename);
        })
        .WithTags("Work Logs")
        .WithName("GetWorklogImage")
        .WithSummary("Gets a stored worklog inline image");
    }

    private static async Task StreamTimeline(
        [FromRoute] string id,
        HttpContext context,
        [FromServices] ICurrentUserAccessService accessService,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITimelineEventBus eventBus,
        [FromServices] IImageLinkSigner imageLinkSigner,
        [FromServices] IOptions<StorageOptions> storageOptions,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var authorization = await AuthorizeIncidentAsync(id, context.User, accessService, db, requireManager: false, ct);
        if (authorization.Failure is not null)
        {
            await authorization.Failure.ExecuteAsync(context);
            return;
        }

        var logger = loggerFactory.CreateLogger("WorkLogEndpoints");
        var reader = eventBus.Subscribe(id);
        var canReadInternalNotes = TicketAccessPolicy.CanReadStaffContent(authorization.Access!, authorization.Incident!);
        var restricted = authorization.Access!.HasRestrictedWorkforceAccess;

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Append("Connection", "keep-alive");
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Response.ContentType = "text/event-stream";

        try
        {
            logger.LogInformation("Timeline stream connected {TicketId}", id);
            await context.Response.StartAsync(ct);
            await context.Response.WriteAsync(": connected\n\n", ct);
            await context.Response.Body.FlushAsync(ct);

            var keepAliveInterval = TimeSpan.FromSeconds(15);

            while (!ct.IsCancellationRequested)
            {
                var waitForDataTask = reader.WaitToReadAsync(ct).AsTask();
                var keepAliveTask = Task.Delay(keepAliveInterval, ct);
                var completedTask = await Task.WhenAny(waitForDataTask, keepAliveTask);

                // The stream may outlive the authentication request that opened it. Re-resolve the
                // incident scope before each delivery or keep-alive so membership revocation stops
                // an active subscription instead of waiting for the browser to reconnect.
                var currentAuthorization = await AuthorizeIncidentAsync(
                    id,
                    context.User,
                    accessService,
                    db,
                    requireManager: false,
                    ct);
                if (!await LocalSessionValidator.IsValidAsync(context, ct) || currentAuthorization.Failure is not null)
                {
                    logger.LogInformation("Timeline stream authorization revoked {TicketId}", id);
                    break;
                }

                canReadInternalNotes = TicketAccessPolicy.CanReadStaffContent(currentAuthorization.Access!, currentAuthorization.Incident!);
                restricted = currentAuthorization.Access!.HasRestrictedWorkforceAccess;

                if (completedTask == waitForDataTask)
                {
                    if (!await waitForDataTask)
                    {
                        break;
                    }

                    while (reader.TryRead(out var evt))
                    {
                        if (!TicketTimelineAccess.Includes(evt.EventType, canReadInternalNotes, restricted))
                        {
                            continue;
                        }

                        // Project a fresh link per delivery without mutating the shared bus event.
                        var payload = JsonSerializer.SerializeToNode(TicketTimelineAccess.Project(evt, restricted))!.AsObject();
                        if (!restricted)
                            payload[nameof(TicketTimelineEventDto.MessageHtml)] = IncidentInlineImageLinks.Refresh(
                                evt.MessageHtml, id, imageLinkSigner, storageOptions.Value.PublicApiBaseUrl);
                        var json = payload.ToJsonString();

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
            logger.LogError(ex, "Timeline stream error {TicketId}", id);
        }
        finally
        {
            logger.LogInformation("Timeline stream disconnected {TicketId}", id);
            eventBus.Unsubscribe(id, reader);
        }
    }

    private static TicketTimelineEventDto ToDto(TicketTimelineEvent evt, IImageLinkSigner signer, string? publicApiBaseUrl)
    {
        var dto = TicketTimelineProjection.ToDto(evt);
        dto.MessageHtml = IncidentInlineImageLinks.Refresh(evt.MessageHtml, evt.TicketId, signer, publicApiBaseUrl);
        return dto;
    }

    private static async Task<IncidentAuthorization> AuthorizeIncidentAsync(
        string incidentId,
        ClaimsPrincipal user,
        ICurrentUserAccessService accessService,
        HelpdeskDbContext db,
        bool requireManager,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == incidentId, cancellationToken);
        if (incident is null)
        {
            return new IncidentAuthorization(null, null, Results.NotFound());
        }

        var access = await accessService.ResolveAsync(user, cancellationToken);
        var canRead = TicketAccessPolicy.CanRead(access, incident);
        var allowed = requireManager ? TicketAccessPolicy.CanWrite(access, incident) : canRead;
        return allowed
            ? new IncidentAuthorization(access, incident, null)
            : new IncidentAuthorization(null, null, access.HasRestrictedWorkforceAccess && !canRead ? Results.NotFound() : Results.Forbid());
    }

    private sealed record IncidentAuthorization(
        CurrentUserAccessProfile? Access,
        Incident? Incident,
        IResult? Failure);

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var clean = global::System.Text.RegularExpressions.Regex.Replace(value, "[^a-zA-Z0-9_-]", "-");
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean;
    }
}
