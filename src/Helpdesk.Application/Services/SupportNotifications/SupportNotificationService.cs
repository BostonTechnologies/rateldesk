using Helpdesk.Shared.DTOs.PublicAccess;
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
    IPublicLinkIssuer publicLinks,
    IConfiguration configuration,
    ILogger<SupportNotificationService> logger,
    IIngressEffectContext? ingressEffects = null) : ISupportNotificationService
{
    private const string TicketCreatedUnassignedTemplate = "SupportTicketCreatedUnassigned";
    private const string TicketAssignedTemplate = "SupportTicketAssigned";
    private const string TicketAssignedToSupportGroupTemplate = "SupportTicketAssignedToSupportGroup";

    private readonly string _publicWebAppUrl = configuration["PublicWebAppUrl"] ?? string.Empty;

    public async Task NotifyTicketCreatedUnassignedAsync(Ticket ticket, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(ticket.AssignedToId) || ticket.AssignedSupportGroupId != null)
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

    public Task NotifyTicketAssignedToSupportGroupAsync(Ticket ticket, string transitionId,
        IReadOnlyList<SupportNotificationRecipient> recipients, CancellationToken ct = default) =>
        NotifyAsync(ticket, SupportNotificationEventType.TicketAssignedToSupportGroup, TicketAssignedToSupportGroupTemplate,
            recipients, recipient => $"support:queue-assignment:{transitionId}:user:{recipient.UserId}", ct, transitionId);

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
        CancellationToken ct,
        string? queueTransitionId = null)
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
            var duplicate = queueTransitionId == null
                ? (await deliveries.GetAllAsync()).Any(x => string.Equals(x.DeduplicationKey, dedupeKey, StringComparison.OrdinalIgnoreCase))
                : await deliveries.CountAsync(delivery => delivery.DeduplicationKey == dedupeKey, ct) > 0;
            if (duplicate)
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
                QueueTransitionId = queueTransitionId,
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
            var context = await BuildContextAsync(ticket, recipient, layout, branding, ct);
            var subject = RenderSubject(template.Subject ?? string.Empty, context);
            var body = templateRenderer.Render(template.HtmlContent ?? string.Empty, context);
            bool sent;
            var previousDelivery = ingressEffects?.SupportDeliveryId;
            try
            {
                if (ingressEffects is not null)
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

            if (ingressEffects?.IsActive == true || emailService is IDurableEmailService { QueuesDelivery: true })
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

    private async Task<EmailTemplateContext> BuildContextAsync(
        Ticket ticket,
        SupportNotificationRecipient recipient,
        EmailLayout? layout,
        TenantBrandingResolved branding, CancellationToken ct)
    {
        var ticketRef = ticket.TrackingId ?? string.Empty;
        var appUrl = branding.TemplateBrand.ApplicationUrl ?? _publicWebAppUrl;
        var ticketLink = ticket.AssignedSupportGroupId != null
            ? AuthenticatedTicketLink(ticket, appUrl)
            : await publicLinks.IssueAsync(ticket, recipient.Email, PublicLinkPurpose.Ticket, appUrl, ct);
        return new EmailTemplateContext
        {
            UserName = string.IsNullOrWhiteSpace(recipient.UserName) ? recipient.Email : recipient.UserName,
            TicketRef = ticketRef,
            TicketType = ticket switch { Incident => "Incident", Request => "Request", Change => "Change", _ => "Ticket" },
            TicketTypeLower = ticket switch { Incident => "incident", Request => "request", Change => "change", _ => "ticket" },
            TicketLink = ticketLink,
            RequestTitle = ticket.Title ?? string.Empty,
            RequestDescription = ticket.AssignedSupportGroupId != null ? string.Empty : ticket.Description ?? string.Empty,
            UpdateMessageText = $"Support notification for {ticketRef}",
            LayoutHtml = layout?.HtmlContent ?? string.Empty,
            BrandName = branding.BrandName,
            Brand = branding.TemplateBrand,
            LogoHtml = branding.LogoHtml,
            FooterHtml = branding.FooterHtml,
            PrimaryColor = branding.PrimaryColor
        };
    }

    private static string AuthenticatedTicketLink(Ticket ticket, string appUrl)
    {
        if (!Uri.TryCreate(appUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("An authenticated ticket link requires a configured Web URL.");
        var path = ticket switch { Incident => "incidents", Request => "requests", Change => "changes", _ => "tickets" };
        return $"{uri.AbsoluteUri.TrimEnd('/')}/{path}/{Uri.EscapeDataString(ticket.Id)}";
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
