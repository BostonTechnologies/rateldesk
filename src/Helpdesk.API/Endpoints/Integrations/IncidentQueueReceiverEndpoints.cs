using AutomationTrace;
using Helpdesk.API.Endpoints.Ticketing;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Ticketing;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.DTOs.TicketQueue;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Integrations;

public static class IncidentQueueReceiverEndpoints
{
    public static void MapIncidentQueueReceiverEndpoints(this IEndpointRouteBuilder app)
    {
        const string prefix = "/api/v1/integrations/netratel";
        var group = app.MapGroup(prefix).WithTags("NetRatel Queue Routing")
            .RequireAuthorization().WithMetadata(new QueueAwareEndpoint());
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        group.MapGet("/queue-capabilities", async (HttpContext http, IIncidentReceiverAuthorization authorization,
            IncidentReceiver receiver, CancellationToken ct) =>
        {
            if (!IncidentReceiverContract.TrySource(http.Request.Headers, out var sourceId))
                return IncidentReceiverContract.Problem(400, "invalid-source-header");
            var approved = await authorization.ResolveAsync(http.User, sourceId, IncidentReceiverOperation.CreateQueued, ct);
            if (approved == null) return IncidentReceiverContract.Problem(403, "source-not-authorized");
            var receiverId = await receiver.ReceiverIdAsync(ct);
            if (receiverId == null) return IncidentReceiverContract.Problem(503, "receiver-not-initialized");
            return Results.Ok(new
            {
                contractVersion = IncidentQueueContract.Version,
                receiverInstanceId = receiverId.Value.ToString("D"),
                sourceInstanceId = sourceId.ToString("D"),
                sourceNamespaceId = approved.Source.SourceNamespaceId.ToString("D"),
                organizationId = approved.Organization.Id,
                customerId = approved.Customer.Id,
                queuePath = $"{http.Request.PathBase}{prefix}/queues",
                createPath = $"{http.Request.PathBase}{prefix}/queued-incidents",
                receiptPath = $"{http.Request.PathBase}{prefix}/queue-receipt",
                keyHeader = IncidentReceiverContract.KeyHeader,
                sourceHeader = IncidentReceiverContract.SourceHeader
            });
        }).WithName("GetIncidentQueueReceiverCapabilities");

        group.MapGet("/queues", async (HttpContext http, string? q, string? id, int? take,
            IIncidentReceiverAuthorization authorization, HelpdeskDbContext db, CancellationToken ct) =>
        {
            if (!IncidentReceiverContract.TrySource(http.Request.Headers, out var sourceId) || q?.Length > 200 ||
                (id != null && !IncidentQueueContract.ValidGroupId(id)))
                return IncidentReceiverContract.Problem(400, "invalid-queue-lookup");
            var approved = await authorization.ResolveAsync(http.User, sourceId, IncidentReceiverOperation.CreateQueued, ct);
            if (approved == null) return IncidentReceiverContract.Problem(403, "source-not-authorized");
            var routes = db.IncidentReceiverQueueRoutes.Where(route => route.SourceNamespaceId == approved.Source.SourceNamespaceId);
            var queues = TicketQueueAssignment.Destinations(db, approved.Organization.Id)
                .Where(queue => routes.Any(route => route.SupportGroupId == queue.Id));
            if (id != null) queues = queues.Where(queue => queue.Id == id);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var text = q.Trim().ToLowerInvariant();
                queues = queues.Where(queue => queue.Name.ToLower().Contains(text));
            }
            return Results.Ok(await queues.OrderBy(queue => queue.Name).ThenBy(queue => queue.Id).Take(Math.Clamp(take ?? 25, 1, 100))
                .Select(queue => new TicketQueueOptionDto(queue.Id, queue.Name)).ToListAsync(ct));
        }).WithName("SearchIncidentReceiverQueues");

        group.MapPost("/queued-incidents", async (HttpContext http, CancellationToken ct) =>
        {
            if (!IncidentReceiverContract.TrySource(http.Request.Headers, out var source) ||
                !IncidentReceiverContract.TryKey(http.Request.Headers, out var key))
                return IncidentReceiverContract.Problem(400, "invalid-integration-headers");
            var body = await AutomationIncidentEndpoints.ReadAsync<IncidentQueueContract.Create>(http.Request, ct);
            if (body?.ContractVersion != IncidentQueueContract.Version || !IncidentQueueContract.ValidGroupId(body.AssignedSupportGroupId) ||
                !AutomationTraceValidation.Create(body.Incident))
                return IncidentReceiverContract.Problem(400, "invalid-queue-request");
            var incident = AutomationTraceValidation.Normalize(body.Incident);
            var dto = new CreateIncidentDto
            {
                Title = "Automation execution", Description = "Source outcome: " + incident.Context.SourceOutcome,
                OrganizationId = incident.Target.OrganizationId, CustomerId = incident.Target.CustomerId,
                AssignedToId = incident.Target.AssignedToId, CategoryIds = incident.Target.CategoryIds.Select(Guid.Parse).ToList(),
                Priority = (TicketPriority)incident.Target.Priority
            };
            return await IncidentReceiver.ExecuteCreateAsync(http.RequestServices, http.User, source, key, dto, http, ct, incident,
                body.AssignedSupportGroupId);
        }).Accepts<IncidentQueueContract.Create>("application/json").WithName("CreateQueuedReceiverIncident");

        group.MapGet("/queue-receipt", async (HttpContext http, IncidentReceiver receiver, CancellationToken ct) =>
        {
            if (!IncidentReceiverContract.TrySource(http.Request.Headers, out var source) ||
                !IncidentReceiverContract.TryKey(http.Request.Headers, out var key) || http.Request.QueryString.HasValue)
                return IncidentReceiverContract.Problem(400, "invalid-integration-headers");
            return await receiver.LookupAsync(http.User, source, key, http, ct, queued: true);
        }).WithName("GetQueuedIncidentReceipt");
    }
}
