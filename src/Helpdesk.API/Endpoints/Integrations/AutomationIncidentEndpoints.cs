using System.Text.Json;
using AutomationTrace;
using AutomationTrace.Contracts;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.EmailTemplates;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Models;

namespace Helpdesk.API.Endpoints.Integrations;

public static class AutomationIncidentEndpoints
{
    public static void MapAutomationIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations/netratel").WithTags("Automation incident presentation").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!IncidentReceiverContract.TrySource(context.HttpContext.Request.Headers, out _))
                return IncidentReceiverContract.Problem(400, "invalid-source-header");
            return await next(context);
        });
        group.MapPost("/incidents", async (HttpContext http, CancellationToken ct) =>
        {
            if (!IncidentReceiverContract.TrySource(http.Request.Headers, out var source) ||
                !IncidentReceiverContract.TryKey(http.Request.Headers, out var key))
                return IncidentReceiverContract.Problem(400, "invalid-integration-headers");
            var body = await ReadAsync<AutomationIncidentCreate>(http.Request, ct);
            if (!AutomationTraceValidation.Create(body)) return IncidentReceiverContract.Problem(400, "invalid-incident-context");
            body = AutomationTraceValidation.Normalize(body!);
            var dto = new CreateIncidentDto { Title = "Automation execution", Description = "Source outcome: " + body.Context.SourceOutcome,
                OrganizationId = body.Target.OrganizationId, CustomerId = body.Target.CustomerId, AssignedToId = body.Target.AssignedToId,
                CategoryIds = body.Target.CategoryIds.Select(Guid.Parse).ToList(), Priority = (TicketPriority)body.Target.Priority };
            return await IncidentReceiver.ExecuteCreateAsync(http.RequestServices, http.User, source, key, dto, http, ct, body);
        });
        group.MapGet("/incident-templates", async (HttpContext http, IIncidentReceiverAuthorization authorization,
            InternalIncidentTemplateService templates, CancellationToken ct) =>
        {
            IncidentReceiverContract.TrySource(http.Request.Headers, out var source);
            var approved = await authorization.ResolveAsync(http.User, source, IncidentReceiverOperation.ReadPresentation, ct);
            return approved is null ? IncidentReceiverContract.Problem(403, "incident-template-access-denied") :
                Results.Ok(await templates.CatalogueAsync(approved.Organization.Id, ct));
        });
        group.MapPost("/incident-templates/preview", async (HttpContext http, IIncidentReceiverAuthorization authorization,
            InternalIncidentTemplateService templates, CancellationToken ct) =>
        {
            IncidentReceiverContract.TrySource(http.Request.Headers, out var source);
            var body = await ReadAsync<IncidentTemplatePreviewRequest>(http.Request, ct);
            var approved = await authorization.ResolveAsync(http.User, source, body?.Presentation?.Mode == "standard"
                ? IncidentReceiverOperation.ReadReceipt : IncidentReceiverOperation.ReadPresentation, ct);
            if (approved is null) return IncidentReceiverContract.Problem(403, "incident-template-access-denied");
            if (body is null || body.Context?.Origin is { } origin && origin.Organization.Id != approved.Organization.Id)
                return IncidentReceiverContract.Problem(400, "invalid-presentation-context");
            try { return Results.Ok(await templates.RenderAsync(approved.Organization.Id, body, ct)); }
            catch (Exception error) when (error is ArgumentException or Helpdesk.Application.Services.EmailTemplates.TemplateRenderException)
            { return IncidentReceiverContract.Problem(422, "incident-presentation-rejected"); }
        });
    }
    public static async Task<T?> ReadAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType() || request.ContentLength > IncidentReceiverContract.MaximumBodyBytes) return null;
        using var stream = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var length = await request.Body.ReadAsync(buffer, ct); if (length == 0) break;
            if (stream.Length + length > IncidentReceiverContract.MaximumBodyBytes) return null;
            await stream.WriteAsync(buffer.AsMemory(0, length), ct);
        }
        try
        {
            using var parsed = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
            return AutomationTraceValidation.NoDuplicateMembers(parsed.RootElement) ? parsed.Deserialize<T>(AutomationTraceValidation.Json) : null;
        }
        catch (JsonException) { return null; }
    }
}
