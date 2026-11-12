using Helpdesk.API.Services;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.API.Endpoints.Requests;

public static partial class SelfServiceMyRequestsEndpoints
{
    private static async Task StreamOwnedRequest([FromRoute] string id, HttpContext context,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ISelfServiceAudienceService audience,
        [FromServices] ICurrentUserAccessService access,
        [FromServices] ITimelineEventBus eventBus,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (await GetOwnedRequestAsync(context.User, db, audience, access, id, ct) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await RequestTimelineSse.RunAsync(id, context, db, eventBus,
            async token => await GetOwnedRequestAsync(context.User, db, audience, access, id, token) is not null,
            // Self-service streams carry only invalidations, never operator/internal timeline data.
            includeTimeline: null, loggerFactory.CreateLogger("OwnRequestStream"), ct);
    }
}
