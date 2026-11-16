using System.Text.Json;
using AutomationTrace.Contracts;
using Helpdesk.Infrastructure.EmailTemplates;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Ticketing;

public static class AutomationReportEndpoints
{
    public static void MapAutomationReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tickets/{id}/automation-reports", async (string id, HttpContext http,
            HelpdeskDbContext db, ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(http.User, ct);
            var relations = new TicketRelationService(db);
            var ticket = await relations.ResolveVisibleAsync(id, access, ct);
            if (ticket is null) return Results.NotFound();
            var org = ticket.OrganizationId!;
            var staff = ticket is Request ? access.CanManageRequest(org) || access.HasPermission(HelpdeskPermissions.RequestRead, org) :
                ticket is Incident && (access.CanManageIncident(org) || access.HasPermission(HelpdeskPermissions.IncidentRead, org));
            if (!staff) return Results.Forbid();
            var incidentIds = relations.Visible(access, org).OfType<Incident>().Select(x => x.Id);
            var reports = await (from report in db.Set<AutomationIncidentReportRecord>().AsNoTracking()
                                 join incident in db.Incidents.AsNoTracking() on report.IncidentId equals incident.Id
                                 where (report.IncidentId == ticket.Id || report.SourceRequestId == ticket.Id) && incidentIds.Contains(incident.Id)
                                 orderby report.CreatedAtUtc descending, report.Id
                                 select new { Report = report, incident.TrackingId }).Take(25).ToArrayAsync(ct);
            return Results.Ok(reports.Select(x => new InternalIncidentReportDto(x.Report.IncidentId, x.TrackingId, x.Report.SourceRequestId,
                x.Report.Html, x.Report.PlainText, JsonSerializer.Deserialize<IncidentPresentationReference>(x.Report.PresentationJson,
                    AutomationTrace.AutomationTraceValidation.Json)!, x.Report.CreatedAtUtc)));
        }).RequireAuthorization().WithTags("Automation Reports");
    }
}
