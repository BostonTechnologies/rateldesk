using System.Text.Json;
using AutomationTrace.Contracts;
using Helpdesk.Infrastructure.EmailTemplates;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Email;

public static class InternalIncidentTemplateEndpoints
{
    public static void MapInternalIncidentTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/internal-incident-templates").WithTags("Internal Incident Templates").RequireAuthorization("HelpdeskAdmin");
        group.MapGet("/", async (string? organizationId, int? page, int? pageSize, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var query = db.Set<InternalIncidentTemplateRecord>().AsNoTracking();
            if (!string.IsNullOrEmpty(organizationId)) query = query.Where(x => x.OrganizationId == organizationId);
            var number = Math.Clamp(page ?? 1, 1, 100_000); var size = pageSize is 50 or 100 ? pageSize.Value : 25;
            var count = await query.CountAsync(ct);
            var items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip((number - 1) * size).Take(size).ToArrayAsync(ct);
            return Results.Ok(new { items = items.Select(InternalIncidentTemplateService.Dto), totalCount = count });
        });
        group.MapGet("/defaults", async (InternalIncidentTemplateService templates, CancellationToken ct) => Results.Ok(await templates.CatalogueAsync("", ct)));
        group.MapGet("/defaults/{id}/draft", (string id) =>
        {
            try { return Results.Ok(InternalIncidentTemplateService.SystemDraft(id)); }
            catch (ArgumentException) { return Results.NotFound(); }
        });
        group.MapPost("/", async (InternalIncidentTemplateDraft draft, InternalIncidentTemplateService templates, CancellationToken ct) =>
        {
            try { return Results.Ok(await templates.SaveAsync(draft, ct)); }
            catch (Exception error) when (error is ArgumentException or FormatException) { return Results.BadRequest(new { code = "invalid-internal-template" }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "template-version-conflict" }); }
        });
        group.MapPost("/{id:guid}/publish", async (Guid id, InternalIncidentTemplateState state, InternalIncidentTemplateService templates, CancellationToken ct) =>
        {
            try { return Results.Ok(await templates.PublishAsync(id, state.ExpectedVersion, ct)); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "template-version-conflict" }); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Helpdesk.Application.Services.EmailTemplates.TemplateRenderException)
            { return Results.UnprocessableEntity(new { code = "template-publication-rejected" }); }
        });
        group.MapPost("/{id:guid}/retire", async (Guid id, InternalIncidentTemplateState state, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var row = await db.Set<InternalIncidentTemplateRecord>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (row.Version != state.ExpectedVersion) return Results.Conflict();
            row.Retired = state.Retired; row.Version++;
            try { await db.SaveChangesAsync(ct); return Results.Ok(InternalIncidentTemplateService.Dto(row)); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(); }
        });
        group.MapPost("/preview", async (InternalIncidentTemplatePreview input, InternalIncidentTemplateService templates, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await templates.PreviewDraftAsync(input.Draft, input.Context, ct));
            }
            catch (Exception error) when (error is ArgumentException or Helpdesk.Application.Services.EmailTemplates.TemplateRenderException)
            { return Results.UnprocessableEntity(new { code = "template-preview-rejected" }); }
        });
    }
}
