using System.Net;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Services.SupportNotifications;

public sealed class SupportNotificationService(
    ISupportNotificationRecipientResolver recipientResolver,
    IRepository<SupportNotificationDelivery> deliveries,
    IRepository<EmailTemplate> templates,
    IEmailLayoutResolver layoutResolver,
    ITenantBrandingResolver tenantBrandingResolver,
    IEmailTemplateRenderer templateRenderer,
    IEmailService emailService,
    IPublicTicketLinkSigner publicTicketLinkSigner,
    IConfiguration configuration,
    ILogger<SupportNotificationService> logger,
    IIngressEffectContext? ingressEffects = null) : ISupportNotificationService
{
    private const string TicketCreatedUnassignedTemplate = "SupportTicketCreatedUnassigned";
    private const string TicketAssignedTemplate = "SupportTicketAssigned";

    private readonly string _publicWebAppUrl = configuration["PublicWebAppUrl"] ?? string.Empty;

    public async Task NotifyTicketCreatedUnassignedAsync(Ticket ticket, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(ticket.AssignedToId))
        {
            return;
        }

        await NotifyAsync(
            ticket,
            SupportNotificationEventType.TicketCreatedUnassigned,
            TicketCreatedUnassignedTemplate,
            await recipientResolver.ResolveTicketEventRecipientsAsync(
                ticket,
                SupportNotificationEventType.TicketCreatedUnassigned,
                SupportNotificationChannel.Email,
                ct),
            recipient => $"support:ticket-created-unassigned:{ticket.Id}:user:{recipient.UserId}",
            ct);
    }

    public async Task NotifyTicketAssignedAsync(
        Ticket ticket,
        string? previousAssignedToId,
        string? newAssignedToId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newAssignedToId) ||
            string.Equals(previousAssignedToId, newAssignedToId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await NotifyAsync(
            ticket,
            SupportNotificationEventType.TicketAssigned,
            TicketAssignedTemplate,
            await recipientResolver.ResolveAssignmentRecipientsAsync(
                ticket,
                newAssignedToId,
                SupportNotificationChannel.Email,
                ct),
            recipient => $"support:ticket-assigned:{ticket.Id}:user:{recipient.UserId}",
            ct);
    }

    private async Task NotifyAsync(
        Ticket ticket,
        SupportNotificationEventType eventType,
        string templateName,
        IReadOnlyList<SupportNotificationRecipient> recipients,
        Func<SupportNotificationRecipient, string> dedupeKeyFactory,
        CancellationToken ct)
    {
        if (recipients.Count == 0)
        {
            logger.LogInformation(
                "No support notification recipients resolved. EventType={EventType} TicketId={TicketId} TrackingId={TrackingId}",
                eventType,
                ticket.Id,
                ticket.TrackingId);
            return;
        }

        foreach (var recipient in recipients)
        {
            var dedupeKey = dedupeKeyFactory(recipient);
            if ((await deliveries.GetAllAsync()).Any(x => string.Equals(x.DeduplicationKey, dedupeKey, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogInformation(
                    "Skipping duplicate support notification delivery. DeduplicationKey={DeduplicationKey}",
                    dedupeKey);
                continue;
            }

            var delivery = new SupportNotificationDelivery
            {
                DeduplicationKey = dedupeKey,
                TicketId = ticket.Id,
                TicketTrackingId = ticket.TrackingId,
                EventType = eventType,
                Channel = SupportNotificationChannel.Email,
                RecipientUserId = recipient.UserId,
                RecipientEmail = recipient.Email,
                Status = SupportNotificationDeliveryStatus.Pending
            };

            try
            {
                await deliveries.CreateAsync(delivery);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not create support notification delivery row. DeduplicationKey={DeduplicationKey}",
                    dedupeKey);
                continue;
            }

            await AttemptSendAsync(ticket, recipient, delivery, templateName, ct);
        }
    }

    private async Task AttemptSendAsync(
        Ticket ticket,
        SupportNotificationRecipient recipient,
        SupportNotificationDelivery delivery,
        string templateName,
        CancellationToken ct)
    {
        delivery.AttemptedUtc = DateTimeOffset.UtcNow;
        delivery.UpdatedUtc = delivery.AttemptedUtc;

        try
        {
            var template = (await templates.GetAllAsync())
                .FirstOrDefault(x => string.Equals(x.Name, templateName, StringComparison.Ordinal));
            if (template is null)
            {
                throw new InvalidOperationException($"Email template '{templateName}' was not found.");
            }

            var layout = await layoutResolver.ResolveAsync(template.LayoutId, ct);
            var branding = await tenantBrandingResolver.ResolveAsync(ticket.OrganizationId, ct);
            var context = BuildContext(ticket, recipient, layout, branding);
            var subject = RenderSubject(template.Subject ?? string.Empty, context);
            var body = templateRenderer.Render(template.HtmlContent ?? string.Empty, context);
            bool sent;
            var previousDelivery = ingressEffects?.SupportDeliveryId;
            try
            {
                if (ingressEffects?.IsActive == true)
                    ingressEffects.SupportDeliveryId = delivery.Id;
                sent = await emailService.SendEmailAsync(
                    [recipient.Email],
                    subject,
                    body,
                    null,
                    ct,
                    ticket.Id,
                    fromName: branding.FromName,
                    replyTo: branding.ReplyTo);
            }
            finally
            {
                if (ingressEffects is not null)
                    ingressEffects.SupportDeliveryId = previousDelivery;
            }

            if (!sent)
            {
                throw new InvalidOperationException("Email service returned false.");
            }

            if (ingressEffects?.IsActive == true)
                return; // The outbox dispatcher records the actual delivery outcome after commit.

            delivery.Status = SupportNotificationDeliveryStatus.Sent;
            delivery.SentUtc = DateTimeOffset.UtcNow;
            delivery.UpdatedUtc = delivery.SentUtc;
            await deliveries.UpdateAsync(delivery);
        }
        catch (Exception ex)
        {
            delivery.Status = SupportNotificationDeliveryStatus.Failed;
            delivery.FailedUtc = DateTimeOffset.UtcNow;
            delivery.UpdatedUtc = delivery.FailedUtc;
            delivery.FailureReason = ex.Message;
            try
            {
                await deliveries.UpdateAsync(delivery);
            }
            catch (Exception updateEx)
            {
                logger.LogWarning(
                    updateEx,
                    "Could not mark support notification delivery failed. DeliveryId={DeliveryId}",
                    delivery.Id);
            }

            logger.LogWarning(
                ex,
                "Support notification email failed. DeliveryId={DeliveryId} TicketId={TicketId} Recipient={Recipient}",
                delivery.Id,
                ticket.Id,
                recipient.Email);
        }
    }

    private EmailTemplateContext BuildContext(
        Ticket ticket,
        SupportNotificationRecipient recipient,
        EmailLayout? layout,
        TenantBrandingResolved branding)
    {
        var ticketRef = ticket.TrackingId ?? string.Empty;
        var ticketLink = BuildSignedPublicTicketLink(ticketRef, recipient.Email, branding.TemplateBrand.ApplicationUrl);
        return new EmailTemplateContext
        {
            UserName = string.IsNullOrWhiteSpace(recipient.UserName) ? recipient.Email : recipient.UserName,
            TicketRef = ticketRef,
            TicketType = ticket is Incident ? "Incident" : "Ticket",
            TicketTypeLower = ticket is Incident ? "incident" : "ticket",
            TicketLink = ticketLink,
            RequestTitle = ticket.Title ?? string.Empty,
            RequestDescription = ticket.Description ?? string.Empty,
            UpdateMessageText = $"Support notification for {ticketRef}",
            LayoutHtml = layout?.HtmlContent ?? string.Empty,
            BrandName = branding.BrandName,
            Brand = branding.TemplateBrand,
            LogoHtml = branding.LogoHtml,
            FooterHtml = branding.FooterHtml,
            PrimaryColor = branding.PrimaryColor
        };
    }

    private string BuildSignedPublicTicketLink(string trackingId, string email, string? applicationUrl = null)
    {
        var baseUrl = string.IsNullOrWhiteSpace(applicationUrl) ? _publicWebAppUrl : applicationUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(trackingId) ||
            string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var token = publicTicketLinkSigner.GenerateToken(
            trackingId,
            email,
            DateTimeOffset.UtcNow.AddDays(30));
        return $"{baseUrl.TrimEnd('/')}/view-ticket/{Uri.EscapeDataString(trackingId)}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }

    private static string RenderSubject(string subject, EmailTemplateContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USER_NAME"] = context.UserName,
            ["TICKET_REF"] = context.TicketRef,
            ["TICKET_TYPE"] = context.TicketType,
            ["TICKET_TYPE_LOWER"] = context.TicketTypeLower,
            ["TICKET_LINK"] = context.TicketLink,
            ["REQUEST_TITLE"] = context.RequestTitle,
            ["REQUEST_DESCRIPTION"] = context.RequestDescription,
            ["UPDATE_MESSAGE"] = context.UpdateMessageText,
            ["UserName"] = context.UserName,
            ["TicketRef"] = context.TicketRef,
            ["TicketType"] = context.TicketType,
            ["TicketTypeLower"] = context.TicketTypeLower,
            ["TicketLink"] = context.TicketLink,
            ["RequestTitle"] = context.RequestTitle,
            ["RequestDescription"] = context.RequestDescription,
            ["UpdateMessage"] = context.UpdateMessageText
        };

        var rendered = subject ?? string.Empty;
        foreach (var (key, value) in values)
        {
            var encoded = WebUtility.HtmlDecode(value ?? string.Empty);
            rendered = rendered
                .Replace("{{{" + key + "}}}", encoded, StringComparison.OrdinalIgnoreCase)
                .Replace("{{" + key + "}}", encoded, StringComparison.OrdinalIgnoreCase);
        }

        return rendered;
    }
}
