using Helpdesk.API.Endpoints.PublicIngress;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.Tenants;
using Helpdesk.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;

namespace Helpdesk.API.Endpoints.Tickets;

public static class TicketSubmissionEndpoints
{
    public static void MapTicketSubmissionEndpoints(this IEndpointRouteBuilder app)
    {
        var systemPolicy = new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes("System")
            .RequireAuthenticatedUser()
            .RequireRole("system.blazor-web")
            .Build();

        app.MapPost("/api/v1/public/tickets/submit", SubmitAsync)
            .WithTags("Public Tickets")
            .AllowAnonymous()
            .RequirePublicAdmission(PublicOperation.TicketSubmission);

        app.MapPost("/api/v1/tickets/submit", SubmitAsync)
            .WithTags("System Tickets")
            .RequireAuthorization(systemPolicy)
            .RequirePublicAdmission(PublicOperation.TicketSubmission);
    }

    private static async Task<IResult> SubmitAsync(SubmitTicketRequest dto, ICaptchaService captcha,
        ITenantProvisioningService provisioning, ITicketNotificationService notifications, IRequestSender sender,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!captcha.ValidateCaptcha(dto.CaptchaId, dto.CaptchaAnswer))
            return Results.BadRequest("Invalid captcha");

        try
        {
            var incident = await provisioning.ProcessAsync(dto.Email, dto.Name, async (customer, _, token) =>
            {
                var command = new CreateIncidentCommand(dto.Subject, dto.Message, dto.Priority, customer.Id,
                    customer.OrganizationId, null, null, null, null, customer.Email, null, null);
                var created = await sender.Send(command, token);
                await notifications.SendNewTicketConfirmationAsync(created, customer.Email, customer.Name, cancellationToken: token);
                return created;
            }, ct);
            return Results.Ok(new { incident.Id, incident.TrackingId });
        }
        catch (RequesterIntakeRejectedException rejection)
        {
            loggerFactory.CreateLogger("TicketSubmission").LogInformation(
                "Public ticket submission declined. Reason={Reason}", rejection.Reason);
            return Results.BadRequest(rejection.Message);
        }
    }
}
