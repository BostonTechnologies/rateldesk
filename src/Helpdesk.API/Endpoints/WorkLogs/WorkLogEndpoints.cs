using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.Incidents;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
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
            if (!CanReadInternalNotes(authorization.Access!, authorization.OrganizationId))
            {
                timelineQuery = timelineQuery.Where(evt => evt.EventType != Helpdesk.Shared.Enums.TimelineEventType.InternalNote);
            }

            // Filter the authorized ticket in SQL, then sort its materialized events.
            // SQLite does not support DateTimeOffset ordering in SQL.
            var events = await timelineQuery.ToListAsync(ct);
            var ordered = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
                ? events.OrderBy(evt => evt.CreatedUtc)
                : events.OrderByDescending(evt => evt.CreatedUtc);
            var timeline = ordered.Select(evt => ToDto(evt, imageLinkSigner, storageOptions.Value.PublicApiBaseUrl)).ToList();

            return Results.Ok(timeline);
        })
        .WithName("GetIncidentTimeline")
        .WithSummary("Gets timeline events for an incident")
        .WithDescription("Retrieves timeline events for the specified incident ordered by CreatedUtc. Defaults to descending (newest first); use ?order=asc for oldest first.");

        group.MapGet("/{id}/timeline/stream", StreamTimeline)
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
                NotifyCustomer: !dto.IsInternalNote,
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

            var storageRoot = string.IsNullOrWhiteSpace(storageOptions.Value.RootPath)
                ? Path.Combine(env.ContentRootPath, "storage")
                : storageOptions.Value.RootPath;
            var rootPath = Path.Combine(storageRoot, "worklogs");
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, safeWorklogId, "inline", safeFilename));
            var fullRoot = Path.GetFullPath(rootPath);

            if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
                return Results.BadRequest("Invalid path.");

            if (!File.Exists(fullPath))
                return Results.NotFound();

            logger.LogInformation("Serving worklog inline image. WorklogId={WorklogId} Path={Path}", safeWorklogId, fullPath);

            var contentType = ContentTypeHelper.GetContentType(safeFilename);
            return Results.File(fullPath, contentType);
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
        var canReadInternalNotes = CanReadInternalNotes(authorization.Access!, authorization.OrganizationId);

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

                canReadInternalNotes = CanReadInternalNotes(currentAuthorization.Access!, currentAuthorization.OrganizationId);

                if (completedTask == waitForDataTask)
                {
                    if (!await waitForDataTask)
                    {
                        break;
                    }

                    while (reader.TryRead(out var evt))
                    {
                        if (!canReadInternalNotes && evt.EventType == Helpdesk.Shared.Enums.TimelineEventType.InternalNote)
                        {
                            continue;
                        }

                        // Project a fresh link per delivery without mutating the shared bus event.
                        var payload = JsonSerializer.SerializeToNode(evt)!.AsObject();
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
        return new TicketTimelineEventDto
        {
            Id = evt.Id,
            TicketId = evt.TicketId,
            CreatedUtc = evt.CreatedUtc,
            CreatedByUserId = evt.CreatedByUserId,
            CreatedByUserName = evt.CreatedByUserName,
            EventType = evt.EventType,
            MessageHtml = IncidentInlineImageLinks.Refresh(evt.MessageHtml, evt.TicketId, signer, publicApiBaseUrl),
            MessageText = evt.MessageText,
            EmailStatus = evt.EmailStatus,
            EmailRecipient = evt.EmailRecipient,
            RetryCount = evt.RetryCount,
            IsRetryable = evt.IsRetryable
        };
    }

    private static bool CanReadInternalNotes(CurrentUserAccessProfile access, string? organizationId) =>
        access.HasPermission(HelpdeskPermissions.IncidentRead, organizationId) || access.CanManageIncident(organizationId);

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
            return new IncidentAuthorization(null, null, null, Results.NotFound());
        }

        var customer = !string.IsNullOrWhiteSpace(incident.CustomerId)
            ? await db.Customers.AsNoTracking()
                .Where(candidate => candidate.Id == incident.CustomerId)
                .Select(candidate => new { candidate.Id, candidate.Email })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var access = await accessService.ResolveAsync(user, cancellationToken);
        var allowed = requireManager
            ? access.CanManageIncident(incident.OrganizationId)
            : access.CanViewIncident(
                incident.OrganizationId,
                customer?.Id ?? incident.CustomerId,
                customer?.Email ?? incident.RequesterEmail);
        return allowed
            ? new IncidentAuthorization(access, incident.OrganizationId, incident, null)
            : new IncidentAuthorization(null, null, null, Results.Forbid());
    }

    private sealed record IncidentScope(string? OrganizationId, string? CustomerId, string? RequesterEmail);

    private sealed record IncidentAuthorization(
        CurrentUserAccessProfile? Access,
        string? OrganizationId,
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
