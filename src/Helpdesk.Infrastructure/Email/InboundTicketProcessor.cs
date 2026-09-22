using Helpdesk.Application.Events;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.Tenants;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Shared.DTOs.Attachment;
using Helpdesk.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Helpdesk.Infrastructure.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace Helpdesk.Application.Services.Email;

public sealed class InboundTicketProcessor
{
    private static readonly Regex TrackingReferenceRegex = new(@"\b(?:INC|REQ)-[A-Z0-9-]+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DsnStatusRegex = new(@"\bStatus:\s*5(?:\.\d{1,3}){0,2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HelpdeskDbContext? _db;
    private readonly IRequestSender _requestSender;
    private readonly ILogger _logger;
    private readonly ITenantProvisioningService _tenantProvisioningService;
    private readonly ITicketNotificationService _ticketNotificationService;
    private readonly IRepository<BlockedEntity> _blockedRepo;
    private readonly IEmailService _emailService;
    private readonly IRepository<EmailTemplate> _templateRepo;
    private readonly IRepository<Incident> _incidentRepo;
    private readonly IRepository<Ticket> _ticketRepo;
    private readonly IRepository<TicketTimelineEvent> _timelineRepo;
    private readonly ITicketAttachmentService _attachmentService;
    private readonly IInboundInlineImageResolver _inlineImageResolver;
    private readonly IEmailTemplateRenderer _templateRenderer;
    private readonly IEmailLayoutResolver _layoutResolver;
    private readonly ITenantBrandingResolver _tenantBrandingResolver;
    private readonly IDomainEventPublisher _domainEvents;
    private readonly ICorrelationContext? _correlationContext;
    private readonly IInboundEmailRuleProcessor? _inboundEmailRuleProcessor;

    public InboundTicketProcessor(
        IRequestSender requestSender,
        ILogger logger,
        ITenantProvisioningService tenantProvisioningService,
        ITicketNotificationService ticketNotificationService,
        IRepository<BlockedEntity> blockedRepo,
        IEmailService emailService,
        IRepository<EmailTemplate> templateRepo,
        IRepository<Incident> incidentRepo,
        IRepository<Ticket> ticketRepo,
        IRepository<TicketTimelineEvent> timelineRepo,
        ITicketAttachmentService attachmentService,
        IInboundInlineImageResolver inlineImageResolver,
        IEmailTemplateRenderer templateRenderer,
        IEmailLayoutResolver layoutResolver,
        ITenantBrandingResolver tenantBrandingResolver,
        IDomainEventPublisher? domainEvents = null,
        ICorrelationContext? correlationContext = null,
        IInboundEmailRuleProcessor? inboundEmailRuleProcessor = null,
        HelpdeskDbContext? db = null)
    {
        _db = db;
        _requestSender = requestSender;
        _logger = logger;
        _tenantProvisioningService = tenantProvisioningService;
        _ticketNotificationService = ticketNotificationService;
        _blockedRepo = blockedRepo;
        _emailService = emailService;
        _templateRepo = templateRepo;
        _incidentRepo = incidentRepo;
        _ticketRepo = ticketRepo;
        _timelineRepo = timelineRepo;
        _attachmentService = attachmentService;
        _inlineImageResolver = inlineImageResolver;
        _templateRenderer = templateRenderer;
        _layoutResolver = layoutResolver;
        _tenantBrandingResolver = tenantBrandingResolver;
        _domainEvents = domainEvents ?? NoopDomainEventPublisher.Instance;
        _correlationContext = correlationContext;
        _inboundEmailRuleProcessor = inboundEmailRuleProcessor;
    }

    internal async Task<Incident> ProcessIncidentAsync(
        InboundEmailContext message,
        IEnumerable<InboundEmailAttachmentContext> attachments,
        Customer customer,
        CancellationToken token,
        string? internetMessageId = null)
    {
        var ticket = await ProcessTicketEmailAsync(message, attachments, customer, token, internetMessageId);
        if (ticket is Incident incident)
        {
            return incident;
        }

        if (ticket is null)
        {
            throw new InvalidOperationException("Inbound email was ignored and did not create or update an incident.");
        }

        throw new InvalidOperationException($"Inbound email resolved to non-incident ticket {ticket.TrackingId}.");
    }

    public async Task<Ticket?> ProcessTicketEmailAsync(
        InboundEmailContext message,
        IEnumerable<InboundEmailAttachmentContext> attachments,
        Customer customer,
        CancellationToken token,
        string? internetMessageId = null,
        string? authorizedTicketId = null)
    {
        Ticket? ticket = authorizedTicketId is null ? null : await _ticketRepo.GetByIdAsync(authorizedTicketId);
        if (authorizedTicketId is not null && (ticket is null || ticket.OrganizationId != customer.OrganizationId))
            throw new InvalidOperationException("Authorized ticket is unavailable for this organization.");
        var subject = message.Subject ?? string.Empty;
        var match = TrackingReferenceRegex.Match(subject);
        if (ticket is null && match.Success)
        {
            var trackingId = match.Value;
            ticket = _db is not null
                ? await _db.Tickets.FirstOrDefaultAsync(i => i.OrganizationId == customer.OrganizationId && i.TrackingId.ToUpper() == trackingId.ToUpper(), token)
                : (await _ticketRepo.GetAllAsync()).FirstOrDefault(i => i.OrganizationId == customer.OrganizationId && i.TrackingId.Equals(trackingId, StringComparison.OrdinalIgnoreCase));

            if (_db is null && ticket is null && trackingId.StartsWith("INC-", StringComparison.OrdinalIgnoreCase))
            {
                ticket = (await _incidentRepo.GetAllAsync())
                    .FirstOrDefault(i => i.OrganizationId == customer.OrganizationId && i.TrackingId.Equals(trackingId, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (IsDeliveryFailureMessage(message))
        {
            return await ResolveDeliveryFailureTicketAsync(message, internetMessageId, token);
        }

        if (ticket is not null && ticket.EmailExclusionReason != TicketEmailExclusionReason.None)
        {
            await RecordInboundMessageMarkerAsync(
                ticket.Id,
                internetMessageId,
                message.FromEmail,
                message.FromDisplayName ?? customer.Name);
            _logger.LogInformation(
                "Skipping inbound email update for excluded ticket {TrackingId}. Reason={Reason}",
                ticket.TrackingId,
                ticket.EmailExclusionReason);
            return ticket;
        }

        if (ticket is null && !string.IsNullOrWhiteSpace(internetMessageId))
        {
            ticket = await FindTicketByInboundMessageIdAsync(internetMessageId, customer.OrganizationId);
            if (ticket is not null)
            {
                _logger.LogInformation(
                    "Skipping duplicate inbound email ticket creation for MessageId={MessageId}. Existing ticket={TrackingId}",
                    internetMessageId,
                    ticket.TrackingId);
                return ticket;
            }
        }

        var incomingCc = NormalizeEmails(
            message.CcRecipients ?? Enumerable.Empty<string>(),
            customer.Email);

        var bodyContent = message.TextBody;
        var htmlBody = message.HtmlBody;

        var inlineAttachments = attachments.ToList();
        var resolution = new InboundInlineImageResult(htmlBody, new HashSet<int>());
        var updatedHtml = htmlBody;
        if (ticket is not null)
        {
            resolution = await _inlineImageResolver.ResolveAsync(ticket.Id, htmlBody, inlineAttachments, message.GraphMessageId, internetMessageId, token);
            updatedHtml = resolution.Html;
        }

        var skipFinalUpdate = false;
        if (ticket is null)
        {
            var incident = await _requestSender.Send(new CreateIncidentCommand(
                message.Subject ?? "Email Ticket",
                updatedHtml,
                null, customer.Id, customer.OrganizationId, null, null, null, null,
                customer.Email,
                incomingCc,
                null), token);

            resolution = await _inlineImageResolver.ResolveAsync(incident.Id, htmlBody, inlineAttachments, message.GraphMessageId, internetMessageId, token);
            updatedHtml = resolution.Html;
            incident.Description = updatedHtml;
            incident.OriginalEmailHtml = updatedHtml;
            incident.OriginalEmailText = bodyContent;
            incident.EmailFrom = message.FromEmail;
            incident.EmailReceivedUtc = message.ReceivedUtc;
            // Apply final state changes up-front so we only update once for creation path
            incident.State = TicketState.WaitingReply;
            incident.UpdatedAt = DateTime.UtcNow;
            incident.LastReplierName = customer.Name;
            await _incidentRepo.UpdateAsync(incident);
            await RecordInboundMessageMarkerAsync(
                incident.Id,
                internetMessageId,
                message.FromEmail,
                message.FromDisplayName ?? customer.Name);
            skipFinalUpdate = true;

            var sent = await _ticketNotificationService.SendNewTicketConfirmationAsync(
                incident,
                customer.Email,
                customer.Name,
                incident.CcRecipients,
                token);

            if (!sent)
            {
                _logger.LogWarning("Failed to send new ticket confirmation for incident {TrackingId}", incident.TrackingId);
            }

            _logger.LogInformation("Successfully created Incident {TrackingId}", incident.TrackingId);
            ticket = incident;
        }
        else
        {
            if (incomingCc.Count > 0)
            {
                ticket.CcRecipients = ticket.CcRecipients
                    .Concat(incomingCc)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            if (string.IsNullOrWhiteSpace(ticket.RequesterEmail))
            {
                ticket.RequesterEmail = customer.Email;
            }

            var senderAddress = message.FromEmail?.Trim();
            var senderName = message.FromDisplayName?.Trim();
            var timelineCreatedByUserId = !string.IsNullOrWhiteSpace(internetMessageId)
                ? internetMessageId
                : (!string.IsNullOrWhiteSpace(senderAddress) ? senderAddress : customer.Email);
            var timelineCreatedByUserName = !string.IsNullOrWhiteSpace(senderAddress)
                ? senderAddress
                : (!string.IsNullOrWhiteSpace(senderName) ? senderName : customer.Name);

            var alreadyProcessed = await HasInboundMarkerAsync(ticket.Id, timelineCreatedByUserId);

            if (alreadyProcessed)
            {
                _logger.LogInformation(
                    "Skipping duplicate inbound email worklog for ticket {TicketId}. MessageId={MessageId}",
                    ticket.Id,
                    timelineCreatedByUserId);
            }
            else
            {
                await _requestSender.Send(new CreateWorkLogCommand(
                    ticket.Id,
                    0,
                    updatedHtml,
                    null,
                    customer.Name,
                    false,
                    TimelineEventType.CustomerReply,
                    timelineCreatedByUserId,
                    timelineCreatedByUserName), token);
            }
        }

        var tracked = await _ticketRepo.GetAsync(ticket.Id);
        tracked ??= ticket is Incident incidentForLookup
            ? await _incidentRepo.GetAsync(incidentForLookup.Id) ?? ticket
            : ticket;
        if (!skipFinalUpdate)
        {
            tracked.CcRecipients = ticket.CcRecipients;
            tracked.RequesterEmail = ticket.RequesterEmail;
            tracked.State = TicketState.WaitingReply;
            tracked.UpdatedAt = DateTime.UtcNow;
            tracked.LastReplierName = customer.Name;
            if (tracked is Incident trackedIncident && ticket is Incident sourceIncident)
            {
                trackedIncident.OriginalEmailHtml = sourceIncident.OriginalEmailHtml;
                trackedIncident.OriginalEmailText = sourceIncident.OriginalEmailText;
                trackedIncident.EmailFrom = sourceIncident.EmailFrom;
                trackedIncident.EmailReceivedUtc = sourceIncident.EmailReceivedUtc;
                await _incidentRepo.UpdateAsync(trackedIncident);
            }
            else
            {
                await _ticketRepo.UpdateAsync(tracked);
            }
        }

        var uploads = inlineAttachments
            .Where((a, index) => a.ContentBytes is not null && !resolution.ConsumedAttachmentIndexes.Contains(index))
            .Select(a => new AttachmentUpload(
                a.Name ?? "attachment",
                a.ContentType ?? "application/octet-stream",
                a.ContentBytes!))
            .ToList();

        if (uploads.Count > 0)
        {
            await _attachmentService.SaveAsync(tracked.Id, uploads, null, token);
        }

        return tracked;
    }

    internal async Task<Ticket?> ResolveDeliveryFailureTicketAsync(
        InboundEmailContext message,
        string? internetMessageId,
        CancellationToken token)
    {
        var references = ExtractTrackingReferences(message).ToList();
        if (references.Count != 1)
        {
            _logger.LogInformation(
                "Ignoring inbound delivery failure MessageId={MessageId}. ReferenceCount={ReferenceCount}",
                internetMessageId,
                references.Count);
            return null;
        }

        var trackingId = references[0];
        var ticket = (await _ticketRepo.GetAllAsync())
            .FirstOrDefault(i => string.Equals(i.TrackingId, trackingId, StringComparison.OrdinalIgnoreCase));

        if (_db is null && ticket is null && trackingId.StartsWith("INC-", StringComparison.OrdinalIgnoreCase))
        {
            ticket = (await _incidentRepo.GetAllAsync())
                .FirstOrDefault(i => string.Equals(i.TrackingId, trackingId, StringComparison.OrdinalIgnoreCase));
        }

        _logger.LogInformation(
            "Ignoring inbound delivery failure MessageId={MessageId} for TrackingId={TrackingId}. TicketFound={TicketFound}",
            internetMessageId,
            trackingId,
            ticket is not null);

        return ticket;
    }

    internal static bool IsDeliveryFailureMessage(InboundEmailContext message)
    {
        var headers = GetHeaders(message);
        var body = message.HtmlBody ?? string.Empty;
        var sender = message.FromEmail ?? string.Empty;
        var subject = message.Subject ?? string.Empty;

        if (HasDsnMimeProof(headers))
        {
            return true;
        }

        if (!IsKnownSystemSender(sender) && !HeadersContainKnownSystemSender(headers))
        {
            return false;
        }

        var markerCount = CountDsnBodyHeaderMarkers(headers, body);
        if (markerCount >= 2)
        {
            return true;
        }

        return HasDeliveryFailureSubject(subject) && HasSupportingDsnSignal(headers, body);
    }

    private static bool HasDsnMimeProof(IReadOnlyDictionary<string, List<string>> headers)
    {
        var contentType = HeaderValues(headers, "content-type");
        if (contentType.Any(value =>
                value.Contains("multipart/report", StringComparison.OrdinalIgnoreCase) &&
                value.Contains("delivery-status", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (contentType.Any(value => value.Contains("message/delivery-status", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool HasSupportingDsnSignal(IReadOnlyDictionary<string, List<string>> headers, string body)
    {
        return HasEmptyReturnPath(headers) ||
               HasAutoSubmittedHeader(headers) ||
               CountDsnBodyHeaderMarkers(headers, body) > 0;
    }

    private static bool HasEmptyReturnPath(IReadOnlyDictionary<string, List<string>> headers)
    {
        return HeaderValues(headers, "return-path").Any(value => value.Trim() == "<>");
    }

    private static bool HasAutoSubmittedHeader(IReadOnlyDictionary<string, List<string>> headers)
    {
        return HeaderValues(headers, "auto-submitted").Any(value =>
            value.Contains("auto-replied", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("auto-generated", StringComparison.OrdinalIgnoreCase));
    }

    private static int CountDsnBodyHeaderMarkers(IReadOnlyDictionary<string, List<string>> headers, string body)
    {
        var count = 0;

        if (HeaderValues(headers, "x-failed-recipients").Any(value => !string.IsNullOrWhiteSpace(value)))
        {
            count++;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return count;
        }

        if (body.Contains("Final-Recipient:", StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        if (body.Contains("Diagnostic-Code:", StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        if (DsnStatusRegex.IsMatch(body))
        {
            count++;
        }

        if (body.Contains("Action: failed", StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    private static bool HasDeliveryFailureSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        return subject.Contains("undeliverable", StringComparison.OrdinalIgnoreCase) ||
               subject.Contains("delivery status notification", StringComparison.OrdinalIgnoreCase) ||
               subject.Contains("delivery has failed", StringComparison.OrdinalIgnoreCase) ||
               subject.Contains("delivery failure", StringComparison.OrdinalIgnoreCase) ||
               subject.Contains("message not delivered", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HeadersContainKnownSystemSender(IReadOnlyDictionary<string, List<string>> headers)
    {
        return HeaderValues(headers, "from")
            .Concat(HeaderValues(headers, "sender"))
            .Any(IsKnownSystemSender);
    }

    private static bool IsKnownSystemSender(string sender)
    {
        if (string.IsNullOrWhiteSpace(sender))
        {
            return false;
        }

        var address = NormalizeSenderAddress(sender);
        var atIndex = address.LastIndexOf('@');
        var localPart = atIndex >= 0 ? address[..atIndex] : address;
        var domain = atIndex >= 0 ? address[(atIndex + 1)..] : string.Empty;

        return localPart is "postmaster" or "mailer-daemon" or "mail-daemon" or "mdaemon" ||
               localPart.StartsWith("microsoftexchange", StringComparison.OrdinalIgnoreCase) ||
               IsTrustedMicrosoftProtectionDomain(domain);
    }

    private static bool IsTrustedMicrosoftProtectionDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        return domain.Equals("protection.outlook.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith(".protection.outlook.com", StringComparison.OrdinalIgnoreCase) ||
               domain.Equals("mail.protection.outlook.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith(".mail.protection.outlook.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSenderAddress(string sender)
    {
        var value = sender.Trim().ToLowerInvariant();
        var start = value.IndexOf('<');
        var end = value.IndexOf('>');
        if (start >= 0 && end > start)
        {
            value = value[(start + 1)..end].Trim();
        }

        return value.Trim('"', '\'');
    }

    private static IEnumerable<string> ExtractTrackingReferences(InboundEmailContext message)
    {
        var values = new[]
        {
            message.Subject ?? string.Empty,
            message.HtmlBody ?? string.Empty
        };

        return values
            .SelectMany(value => TrackingReferenceRegex.Matches(value).Select(match => match.Value.ToUpperInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, List<string>> GetHeaders(InboundEmailContext message)
        => message.RepeatedHeaders.Count > 0
            ? message.RepeatedHeaders.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase)
            : message.Headers.ToDictionary(x => x.Key, x => new List<string> { x.Value }, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> HeaderValues(IReadOnlyDictionary<string, List<string>> headers, string name) =>
        headers.TryGetValue(name, out var values) ? values : [];

    private async Task<bool> HasInboundMarkerAsync(string ticketId, string marker)
    {
        if (_db is not null)
            return await _db.TicketTimelineEvents.AnyAsync(x => x.TicketId == ticketId &&
                x.EventType == TimelineEventType.CustomerReply && x.CreatedByUserId == marker);
        return (await _timelineRepo.GetAllAsync()).Any(x => x.TicketId == ticketId &&
            x.EventType == TimelineEventType.CustomerReply && string.Equals(x.CreatedByUserId, marker, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Ticket?> FindTicketByInboundMessageIdAsync(string internetMessageId, string organizationId)
    {
        if (_db is not null)
            return await _db.Tickets.FirstOrDefaultAsync(ticket => ticket.OrganizationId == organizationId &&
                _db.TicketTimelineEvents.Any(marker => marker.TicketId == ticket.Id &&
                    marker.EventType == TimelineEventType.CustomerReply && marker.CreatedByUserId == internetMessageId));
        var tickets = (await _ticketRepo.GetAllAsync()).Where(x => x.OrganizationId == organizationId).ToDictionary(x => x.Id);
        foreach (var incident in (await _incidentRepo.GetAllAsync()).Where(x => x.OrganizationId == organizationId)) tickets.TryAdd(incident.Id, incident);
        var marker = (await _timelineRepo.GetAllAsync()).Where(e => tickets.ContainsKey(e.TicketId) &&
            e.EventType == TimelineEventType.CustomerReply && string.Equals(e.CreatedByUserId, internetMessageId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.CreatedUtc).FirstOrDefault();
        return marker is null ? null : tickets[marker.TicketId];
    }

    private async Task RecordInboundMessageMarkerAsync(
        string incidentId,
        string? internetMessageId,
        string? senderAddress,
        string? senderName)
    {
        if (string.IsNullOrWhiteSpace(internetMessageId))
        {
            return;
        }

        var alreadyRecorded = await HasInboundMarkerAsync(incidentId, internetMessageId);

        if (alreadyRecorded)
        {
            return;
        }

        await _timelineRepo.CreateAsync(new TicketTimelineEvent
        {
            TicketId = incidentId,
            EventType = TimelineEventType.CustomerReply,
            CreatedByUserId = internetMessageId,
            CreatedByUserName = !string.IsNullOrWhiteSpace(senderAddress)
                ? senderAddress
                : (!string.IsNullOrWhiteSpace(senderName) ? senderName : "Inbound email"),
            MessageText = "Inbound email received."
        });
    }

    private static List<string> NormalizeEmails(IEnumerable<string?> emails, string? requester)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in emails)
        {
            var e = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(e)) continue;
            set.Add(e);
        }
        if (!string.IsNullOrWhiteSpace(requester))
        {
            set.Remove(requester.Trim().ToLowerInvariant());
        }
        return set.ToList();
    }

}
