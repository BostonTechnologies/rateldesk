using Helpdesk.Application.Services.Tickets;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Attachment;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Attachments;

public static class AttachmentEndpoints
{
    /// <summary>
    /// Configures attachment download, list, and upload endpoints.
    /// </summary>
    /// <remarks>
    /// Attachment access always derives from the parent ticket. Read operations require ticket visibility and uploads
    /// require the corresponding ticket-manager grant. This prevents an authenticated principal from reading or
    /// modifying attachments in another tenant by guessing an attachment or ticket identifier.
    /// </remarks>
    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var individualAttachmentGroup = app.MapGroup("/api/v1/attachments")
            .WithTags("Attachments")
            .RequireAuthorization();

        individualAttachmentGroup.MapGet("/{id:guid}", async (
            Guid id,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            TicketAttachmentFileStore fileStore,
            CancellationToken token) =>
        {
            var attachment = await db.Attachments.FindAsync([id], token);
            if (attachment is null ||
                await GetAccessibleTicketAsync(attachment.TicketId, db, accessService, user, requireManager: false, token) is null)
            {
                return Results.NotFound("Attachment not found.");
            }

            var filePath = fileStore.GetReadPath(attachment.FilePath);
            if (filePath is null)
            {
                return Results.NotFound("Attachment not found.");
            }
            return Results.File(filePath, attachment.ContentType, attachment.FileName);
        });

        var ticketAttachmentGroup = app.MapGroup("/api/v1/tickets/{ticketId}/attachments")
            .WithTags("Attachments")
            .RequireAuthorization();

        ticketAttachmentGroup.MapGet("/", async (
            [FromRoute] string ticketId,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            CancellationToken token) =>
        {
            if (await GetAccessibleTicketAsync(ticketId, db, accessService, user, requireManager: false, token) is null)
            {
                return Results.NotFound();
            }

            var attachments = await db.Attachments
                .Where(attachment => attachment.TicketId == ticketId)
                .Select(attachment => new AttachmentDto(attachment.Id, attachment.TicketId, attachment.FileName, attachment.ContentType, attachment.SizeBytes, attachment.CreatedAt))
                .ToListAsync(token);
            return Results.Ok(attachments);
        });

        ticketAttachmentGroup.MapPost("/", async (
            [FromRoute] string ticketId,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] ITicketAttachmentService attachmentService,
            ClaimsPrincipal user,
            HttpRequest request,
            CancellationToken token) =>
        {
            if (await GetAccessibleTicketAsync(ticketId, db, accessService, user, requireManager: true, token) is null)
            {
                return Results.NotFound();
            }

            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Attachments must be sent as multipart form data.");
            }

            var form = await request.ReadFormAsync(token);
            var files = form.Files;
            if (files.Count == 0)
            {
                return Results.BadRequest("No files were provided.");
            }

            var uploads = new List<AttachmentUpload>();
            foreach (var file in files)
            {
                await using var stream = new MemoryStream();
                await file.CopyToAsync(stream, token);
                uploads.Add(new AttachmentUpload(file.FileName, file.ContentType, stream.ToArray()));
            }

            var uploadedById = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var saved = await attachmentService.SaveAsync(ticketId, uploads, uploadedById, token);
            return Results.Ok(saved);
        })
        .Accepts<IFormCollection>("multipart/form-data")
        .DisableAntiforgery();
    }

    private static async Task<Ticket?> GetAccessibleTicketAsync(
        string ticketId,
        HelpdeskDbContext db,
        ICurrentUserAccessService accessService,
        ClaimsPrincipal user,
        bool requireManager,
        CancellationToken token)
    {
        var ticket = await db.Tickets.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == ticketId, token);
        if (ticket is null)
        {
            return null;
        }

        var access = await accessService.ResolveAsync(user, token);
        return CanAccessTicket(access, ticket, requireManager) ? ticket : null;
    }

    private static bool CanAccessTicket(CurrentUserAccessProfile access, Ticket ticket, bool requireManager) =>
        requireManager
            ? CanManageTicket(access, ticket) || CanSelfServiceUpload(access, ticket)
            : CanViewTicket(access, ticket);

    private static bool CanViewTicket(CurrentUserAccessProfile access, Ticket ticket) => ticket switch
    {
        Incident incident => access.CanViewIncident(incident.OrganizationId, incident.CustomerId, incident.RequesterEmail),
        Request request => access.CanViewRequest(request.OrganizationId, request.CustomerId, request.RequesterEmail),
        Change change => access.CanViewChange(change.OrganizationId, change.CustomerId, change.RequesterEmail),
        _ => false
    };

    private static bool CanManageTicket(CurrentUserAccessProfile access, Ticket ticket) => ticket switch
    {
        Incident incident => access.CanManageIncident(incident.OrganizationId),
        Request request => access.CanManageRequest(request.OrganizationId),
        Change change => access.CanManageChange(change.OrganizationId),
        _ => false
    };

    private static bool CanSelfServiceUpload(CurrentUserAccessProfile access, Ticket ticket) =>
        ticket is Incident or Request &&
        access.HasPermission(HelpdeskPermissions.SelfServiceUser, ticket.OrganizationId) &&
        CanViewTicket(access, ticket);
}
