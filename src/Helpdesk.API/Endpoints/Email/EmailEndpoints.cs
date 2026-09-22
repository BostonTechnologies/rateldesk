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

        group.MapPost("/send", async ([FromBody] SendEmailRequest request, [FromServices] AppServices.Email.IEmailService service) =>
        {
            _ = await service.SendEmailAsync(request.Recipients, request.Subject, request.HtmlMessage, cc: null);
            return Results.Ok();
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
