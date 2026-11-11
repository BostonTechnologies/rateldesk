using System.Text.Json;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Worklog;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Requests;

/// <summary>The existing request stream carries timeline entries and committed snapshot invalidations.</summary>
internal static class RequestTimelineSse
{
    public static async Task RunAsync(string id, HttpContext context, HelpdeskDbContext db,
        ITimelineEventBus eventBus, Func<CancellationToken, Task<bool>> authorize,
        Func<TicketTimelineEventDto, bool>? includeTimeline, ILogger logger, CancellationToken ct)
    {
        var reader = eventBus.Subscribe(id);
        try
        {
            // Subscribe first. The ready frame then requests a canonical snapshot, so a transition
            // between the initial page fetch and subscription cannot leave the page stale.
            var cursor = await ReadCommittedCursorAsync(db, id, ct);
            if (!await IsAuthorizedAsync())
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers.Append("X-Accel-Buffering", "no");
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync(ct);
            await context.Response.WriteAsync("event: ready\ndata: {}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            var tick = timer.WaitForNextTickAsync(ct).AsTask();
            var data = reader.WaitToReadAsync(ct).AsTask();
            var lastHeartbeat = Environment.TickCount64;
            while (!ct.IsCancellationRequested)
            {
                await Task.WhenAny(data, tick);
                if (!await IsAuthorizedAsync()) break;
                var wrote = false;
                if (data.IsCompleted)
                {
                    if (!await data) break;
                    var invalidate = false;
                    while (reader.TryRead(out var item))
                    {
                        if (includeTimeline is null) invalidate = true;
                        else if (includeTimeline(item))
                        {
                            await context.Response.WriteAsync("event: timeline\n", ct);
                            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(item)}\n\n", ct);
                            wrote = true;
                        }
                    }
                    if (invalidate) { await InvalidateAsync(); wrote = true; }
                    data = reader.WaitToReadAsync(ct).AsTask();
                }
                if (tick.IsCompleted)
                {
                    if (!await tick) break;
                    // Notifications are the existing committed domain-event journal. Read only
                    // this authorized request's indexed references, including task references.
                    // This covers another API worker and a lost process-local wake-up. No row
                    // snapshots are polled by the browser or individually by the stream.
                    var next = await ReadCommittedCursorAsync(db, id, ct);
                    if (cursor != next)
                    {
                        cursor = next;
                        await InvalidateAsync();
                        wrote = true;
                    }
                    tick = timer.WaitForNextTickAsync(ct).AsTask();
                }
                if (Environment.TickCount64 - lastHeartbeat >= 15000)
                {
                    await context.Response.WriteAsync(": keepalive\n\n", ct);
                    lastHeartbeat = Environment.TickCount64;
                    wrote = true;
                }
                if (wrote) await context.Response.Body.FlushAsync(ct);
            }

            async Task InvalidateAsync() => await context.Response.WriteAsync(
                $"event: request-state\ndata: {JsonSerializer.Serialize(new { ticketId = id })}\n\n", ct);
            async Task<bool> IsAuthorizedAsync() =>
                await LocalSessionValidator.IsValidAsync(context, ct) && await authorize(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug("Request stream cancelled {RequestId}", id);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Request stream ended {RequestId}", id);
        }
        finally { eventBus.Unsubscribe(id, reader); }
    }

    private static Task<long> ReadCommittedCursorAsync(HelpdeskDbContext db, string requestId, CancellationToken ct)
    {
        var taskIds = db.RequestTasks.IgnoreQueryFilters().Where(x => x.RequestId == requestId).Select(x => x.Id);
        return db.Notifications.AsNoTracking().Where(x =>
                (x.Source == "RequestTask" || x.Source == "Workflow" || x.Source == "Request") &&
                (x.Reference == requestId || taskIds.Contains(x.Reference!)))
            .LongCountAsync(ct);
    }
}
