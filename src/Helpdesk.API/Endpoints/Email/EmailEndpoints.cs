using Helpdesk.API.Endpoints.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AppServices = Helpdesk.Application.Services;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
namespace Helpdesk.API.Endpoints.Email;

public static class EmailEndpoints
{
    public static void MapEmailEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/email")
            .WithTags("Email")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/imap/settings", () => Results.Json(new { message = "Use /api/v1/email-settings for canonical mailbox configuration." }, statusCode: 410));
        group.MapPost("/imap/test", () => Results.Json(new { message = "Use /api/v1/email-settings/test." }, statusCode: 410));
        group.MapPost("/imap/save", () => Results.Json(new { message = "Use /api/v1/email-settings; legacy writes are retired." }, statusCode: 410));

        group.MapPost("/send", async ([FromBody] SendEmailRequest request, [FromServices] AppServices.Email.IEmailService service,
            CancellationToken cancellationToken) =>
        {
            var recipients = request.Recipients?.ToArray();
            if (recipients is null || recipients.Length is < 1 or > 100 ||
                recipients.Any(recipient => string.IsNullOrWhiteSpace(recipient) || recipient.Length > 254 ||
                    !global::System.Net.Mail.MailAddress.TryCreate(recipient, out var address) ||
                    !string.Equals(address.Address, recipient, StringComparison.OrdinalIgnoreCase)) ||
                string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 998 ||
                string.IsNullOrWhiteSpace(request.HtmlMessage) || request.HtmlMessage.Length > 262144)
            {
                return Results.BadRequest(new { message = "Supply 1–100 valid recipients, a subject of at most 998 characters and a message of at most 262144 characters." });
            }

            bool accepted;
            try
            {
                accepted = await service.SendEmailAsync(new AppServices.Email.EmailSendRequest(
                    recipients, request.Subject, request.HtmlMessage) { RequireReadySender = true }, cancellationToken);
            }
            catch (AppServices.Email.EmailSenderUnavailableException error)
            {
                return Results.Conflict(new
                {
                    status = "Not queued", queued = false, deliveryConfirmed = false,
                    reason = error.ReasonCode,
                    nextAction = "Configure an enabled global sender before sending administrator email."
                });
            }
            if (!accepted)
            {
                return Results.Json(new
                {
                    status = "Not confirmed", deliveryConfirmed = false,
                    nextAction = "Inspect delivery history and provider evidence before retrying; a failed call is not proof that no recipient received the message."
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            var queued = service is AppServices.Email.IDurableEmailService { QueuesDelivery: true };
            return Results.Json(new
            {
                status = queued ? "Queued" : "Accepted by provider", queued, deliveryConfirmed = false
            }, statusCode: queued ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        })
        .WithName("SendEmail")
        .WithSummary("Send an email through the configured email provider")
        .WithDescription("Sends an email using the configured outbound email provider.");

        group.MapPost("/test", async ([FromServices] AppServices.Email.IEmailService service) =>
            await service.TestApiConnectionAsync()
                ? Results.Ok()
                : Results.Problem("Configured email provider unavailable", statusCode: 500))
            .WithName("TestEmail")
            .WithSummary("Tests connectivity to the configured email provider")
            .WithDescription("Tests the configured outbound email provider.");

    }
}

public record SendEmailRequest(IEnumerable<string> Recipients, string Subject, string HtmlMessage);
