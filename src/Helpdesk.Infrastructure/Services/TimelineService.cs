using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.Services;

public class TimelineService(
    HelpdeskDbContext db,
    IEmailService emailService,
    IEmailTemplateRenderer templateRenderer,
    IEmailLayoutResolver layoutResolver,
    ITenantBrandingResolver tenantBrandingResolver,
    IPublicTicketLinkSigner publicTicketLinkSigner,
    ITimelineEventBus timelineEventBus,
    IConfiguration config,
    MailboxOutboxStore? mailboxOutbox = null,
    IIngressEffectContext? effectContext = null) : ITimelineService
{
    private readonly HelpdeskDbContext _db = db;
    private readonly IEmailService _emailService = emailService;
    private readonly ITimelineEventBus _timelineEventBus = timelineEventBus;
    private readonly IConfiguration _config = config;

    public async Task RetryEmailAsync(
        Guid timelineEventId,
        string userId,
        CancellationToken ct)
    {
        _ = userId;

        var evt = await _db.TicketTimelineEvents
            .FirstOrDefaultAsync(x =>
                x.Id == timelineEventId &&
                x.EventType == TimelineEventType.EmailDelivery &&
                x.EmailStatus == EmailDeliveryStatus.Failed,
                ct);

        if (evt == null)
            throw new InvalidOperationException("Retryable email not found.");

        if (mailboxOutbox is not null && await mailboxOutbox.RetryForTimelineAsync(timelineEventId, ct))
            return;

        var durable = _emailService is IDurableEmailService { QueuesDelivery: true };
        if (durable && effectContext is null)
            throw new InvalidOperationException("Durable timeline retry requires a delivery context.");

        var ccRecipients = await _db.Tickets
            .AsNoTracking()
            .Where(x => x.Id == evt.TicketId)
            .Select(x => x.CcRecipients)
            .FirstOrDefaultAsync(ct) ?? new List<string>();

        evt.RetryCount++;
        evt.LastRetryUtc = DateTimeOffset.UtcNow;
        evt.RetryError = null;
        evt.EmailStatus = EmailDeliveryStatus.Pending;

        await _db.SaveChangesAsync(ct);
        await _timelineEventBus.PublishAsync(ToDto(evt, ccRecipients));

        var previousTimelineId = effectContext?.TimelineDeliveryId;
        if (durable) effectContext!.TimelineDeliveryId = evt.Id;
        try
        {
            var sent = await SendWorklogEmailAsync(evt, durable, ct);
            if (!sent)
            {
                evt.EmailStatus = EmailDeliveryStatus.Failed;
                evt.RetryError = "Email service returned an unsuccessful result.";
                await _db.SaveChangesAsync(ct);
                await _timelineEventBus.PublishAsync(ToDto(evt, ccRecipients));
                throw new InvalidOperationException(evt.RetryError);
            }

            if (durable)
                return;

            evt.EmailStatus = EmailDeliveryStatus.Delivered;
            evt.RetryError = null;
            await _db.SaveChangesAsync(ct);
            await _timelineEventBus.PublishAsync(ToDto(evt, ccRecipients));
        }
        catch (Exception ex)
        {
            evt.EmailStatus = EmailDeliveryStatus.Failed;
            evt.RetryError = ex.Message;
            await _db.SaveChangesAsync(ct);
            await _timelineEventBus.PublishAsync(ToDto(evt, ccRecipients));
            throw;
        }
        finally
        {
            if (durable) effectContext!.TimelineDeliveryId = previousTimelineId;
        }
    }

    public async Task<int> RetryAllFailedEmailsAsync(
        string userId,
        CancellationToken ct)
    {
        var failedIds = await _db.TicketTimelineEvents
            .AsNoTracking()
            .Where(x =>
                x.EventType == TimelineEventType.EmailDelivery &&
                x.EmailStatus == EmailDeliveryStatus.Failed)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var success = 0;

        foreach (var id in failedIds)
        {
            try
            {
                await RetryEmailAsync(id, userId, ct);
                success++;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to retry timeline email {id}: {exception.Message}");
            }
        }

        return success;
    }

    public Task<int> GetPendingEmailCountAsync(
        string userId,
        CancellationToken ct)
    {
        _ = userId;

        return _db.TicketTimelineEvents
            .CountAsync(x =>
                x.EventType == TimelineEventType.EmailDelivery &&
                x.EmailStatus == EmailDeliveryStatus.Failed,
                ct);
    }

    private async Task<bool> SendWorklogEmailAsync(
        TicketTimelineEvent evt,
        bool suppressTimeline,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.EmailRecipient) ||
            string.Equals(evt.EmailRecipient, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Timeline event has no valid recipient.");
        }

        var ticket = await _db.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == evt.TicketId, ct);
        if (ticket is null)
            throw new InvalidOperationException("Ticket not found for retry.");

        var template = await _db.EmailTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == "TicketUpdated", ct);
        if (template is null)
            throw new InvalidOperationException("TicketUpdated email template was not found.");

        var recipient = evt.EmailRecipient.Trim();
        var customer = await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Email == recipient, ct);

        var publicUrl = (_config["PublicWebAppUrl"] ?? string.Empty).TrimEnd('/');
        var ticketRef = ticket.TrackingId ?? string.Empty;

        var subject = template.Subject
            .Replace("{{{TICKET_REF}}}", ticketRef);
        var layout = await layoutResolver.ResolveAsync(template.LayoutId, ct);
        var branding = await tenantBrandingResolver.ResolveAsync(ticket.OrganizationId, ct);
        if (!string.IsNullOrWhiteSpace(branding.TemplateBrand.ApplicationUrl))
            publicUrl = branding.TemplateBrand.ApplicationUrl.TrimEnd('/');

        var body = templateRenderer.Render(template.HtmlContent, new EmailTemplateContext
        {
            UserName = customer?.Name ?? "Customer",
            TicketRef = ticketRef,
            TicketLink = BuildSignedPublicTicketLink(publicUrl, ticketRef, recipient),
            UpdateMessageHtml = evt.MessageHtml ?? string.Empty,
            UpdateMessageText = evt.MessageText ?? string.Empty,
            LayoutHtml = layout?.HtmlContent ?? string.Empty,
            BrandName = branding.BrandName,
            Brand = branding.TemplateBrand,
            LogoHtml = branding.LogoHtml,
            FooterHtml = branding.FooterHtml,
            PrimaryColor = branding.PrimaryColor
        });

        var ccFinal = ticket.CcRecipients
            .Where(cc => !string.Equals(cc, recipient, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await _emailService.SendEmailAsync(
            new[] { recipient },
            subject,
            body,
            ccFinal.Count > 0 ? ccFinal : null,
            ct,
            evt.TicketId,
            fromName: branding.FromName,
            replyTo: branding.ReplyTo,
            suppressTimeline: suppressTimeline);
    }

    private static TicketTimelineEventDto ToDto(TicketTimelineEvent evt, IReadOnlyCollection<string>? ccRecipients = null)
    {
        var cc = (ccRecipients ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var recipients = new List<string>();
        if (!string.IsNullOrWhiteSpace(evt.EmailRecipient))
            recipients.Add(evt.EmailRecipient.Trim());
        recipients.AddRange(cc);

        return new TicketTimelineEventDto
        {
            Id = evt.Id,
            TicketId = evt.TicketId,
            CreatedUtc = evt.CreatedUtc,
            CreatedByUserId = evt.CreatedByUserId,
            CreatedByUserName = evt.CreatedByUserName,
            EventType = evt.EventType,
            MessageHtml = evt.MessageHtml,
            MessageText = evt.MessageText,
            EmailStatus = evt.EmailStatus,
            EmailRecipient = evt.EmailRecipient,
            Recipients = recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            PrimaryRecipient = evt.EmailRecipient,
            CcRecipients = cc,
            RetryCount = evt.RetryCount,
            IsRetryable = evt.IsRetryable
        };
    }

    private string BuildSignedPublicTicketLink(string publicUrl, string trackingId, string email)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var token = publicTicketLinkSigner.GenerateToken(trackingId, email, expires);
        return $"{publicUrl}/view-ticket/{trackingId}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }
}
