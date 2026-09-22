using Azure.Identity;
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
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace Helpdesk.Application.Services.Email;

public class GraphEmailProcessor : IGraphEmailProcessor
{
    private static readonly Regex TrackingReferenceRegex = new(@"\b(?:INC|REQ)-[A-Z0-9-]+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DsnStatusRegex = new(@"\bStatus:\s*5(?:\.\d{1,3}){0,2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IRequestSender _requestSender;
    private readonly ILogger<GraphEmailProcessor> _logger;
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

    public GraphEmailProcessor(
        IRequestSender requestSender,
        ILogger<GraphEmailProcessor> logger,
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
        IInboundEmailRuleProcessor? inboundEmailRuleProcessor = null)
    {
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

    public async Task ProcessEmailByInternetMessageIdAsync(string internetMessageId, ImapEmailSettings settings, CancellationToken token)
    {
        var credential = new ClientSecretCredential(settings.TenantId, settings.ClientId, settings.ClientSecret);
        var graphClient = new GraphServiceClient(credential);
        var mailboxAddress = settings.UserEmail;

        var processedFolderId = await GetOrCreateMailFolderIdAsync(graphClient, mailboxAddress, settings.ProcessedFolder, token);
        var blockedFolderId = await GetOrCreateMailFolderIdAsync(graphClient, mailboxAddress, settings.BlockedFolder, token);

        if (string.IsNullOrEmpty(processedFolderId) || string.IsNullOrEmpty(blockedFolderId))
        {
            _logger.LogError("Could not find or create required mail folders. Halting processing for this message.");
            return;
        }

        var filter = $"internetMessageId eq '<{internetMessageId}>'";
        var messages = await graphClient.Users[mailboxAddress].Messages.GetAsync(req =>
        {
            req.QueryParameters.Filter = filter;
            req.QueryParameters.Select =
            [
                "id",
                "subject",
                "body",
                "from",
                "toRecipients",
                "ccRecipients",
                "receivedDateTime",
                "internetMessageId",
                "internetMessageHeaders"
            ];
        }, cancellationToken: token);

        var graphMessage = messages?.Value?.FirstOrDefault();
        if (graphMessage is null)
        {
            _logger.LogWarning("Could not find message in Graph with Internet-Message-ID: {MessageId}", internetMessageId);
            return;
        }

        var fromAddress = graphMessage.From?.EmailAddress?.Address;
        if (EmailAddressGuard.IsSameAddress(fromAddress, mailboxAddress))
        {
            _logger.LogWarning(
                "Skipping self-originated message {GraphId} from mailbox {MailboxAddress}; marking it read and moving it to the processed folder.",
                graphMessage.Id,
                mailboxAddress);
            await FinalizeGraphMessageAsync(graphClient, mailboxAddress, graphMessage, processedFolderId, null, token);
            return;
        }

        if (IsDeliveryFailureMessage(graphMessage))
        {
            var bouncedTicket = await ResolveDeliveryFailureTicketAsync(graphMessage, internetMessageId, token);
            await FinalizeGraphMessageAsync(graphClient, mailboxAddress, graphMessage, processedFolderId, bouncedTicket, token);
            return;
        }

        if (string.IsNullOrWhiteSpace(fromAddress) || !fromAddress.Contains('@'))
        {
            _logger.LogWarning("Message {GraphId} has no valid sender address. Skipping.", graphMessage.Id);
            return;
        }

        var fromName = graphMessage.From?.EmailAddress?.Name ?? string.Empty;
        var attachmentsResponse = await graphClient.Users[mailboxAddress].Messages[graphMessage.Id].Attachments.GetAsync(cancellationToken: token);
        var attachments = attachmentsResponse?.Value ?? Enumerable.Empty<Microsoft.Graph.Models.Attachment>();

        if (_inboundEmailRuleProcessor is not null)
        {
            var ruleResult = await _inboundEmailRuleProcessor.ProcessAsync(
                BuildInboundEmailContext(graphMessage, attachments, mailboxAddress, internetMessageId),
                token);
            if (ruleResult.Handled && ruleResult.StopDefaultProcessing)
            {
                await FinalizeGraphMessageAsync(graphClient, mailboxAddress, graphMessage, processedFolderId, ruleResult.Ticket, token);
                return;
            }
        }

        var domain = fromAddress.Split('@').Last();
        var organization = await _tenantProvisioningService.GetOrCreateOrganizationByDomainAsync(domain);
        var (customer, isNewCustomer) = await _tenantProvisioningService.GetOrCreateCustomerAsync(fromAddress, fromName, domain);

        if (customer.State == EntityState.Blocked || organization.State == EntityState.Blocked)
        {
            _logger.LogWarning("Sender {SenderEmail} is blocked.", fromAddress);
            await SendNotificationAsync("AccessDenied", fromAddress, tenantId: organization.Id);
            await MoveGraphMessageAsync(graphClient, mailboxAddress, graphMessage.Id, blockedFolderId); // use ID
            return;
        }

        if (isNewCustomer)
        {
            await SendNotificationAsync("WelcomeNewUser", customer.Email, userName: customer.Name, tenantId: customer.OrganizationId);
        }

        var ticket = await ProcessTicketEmailAsync(
            graphMessage,
            attachments,
            customer,
            token,
            internetMessageId);

        await FinalizeGraphMessageAsync(graphClient, mailboxAddress, graphMessage, processedFolderId, ticket, token);
    }

    internal Task<Incident> ProcessIncidentAsync(Message message, IEnumerable<Microsoft.Graph.Models.Attachment> attachments,
        Customer customer, CancellationToken token, string? internetMessageId = null)
        => CreateProcessor().ProcessIncidentAsync(BuildInboundEmailContext(message, attachments, string.Empty, internetMessageId ?? string.Empty),
            attachments.OfType<FileAttachment>().Select(ToAttachmentContext), customer, token, internetMessageId);

    internal Task<Ticket?> ProcessTicketEmailAsync(Message message, IEnumerable<Microsoft.Graph.Models.Attachment> attachments,
        Customer customer, CancellationToken token, string? internetMessageId = null)
        => CreateProcessor().ProcessTicketEmailAsync(BuildInboundEmailContext(message, attachments, string.Empty, internetMessageId ?? string.Empty),
            attachments.OfType<FileAttachment>().Select(ToAttachmentContext), customer, token, internetMessageId);

    internal static bool IsDeliveryFailureMessage(Message message)
        => InboundTicketProcessor.IsDeliveryFailureMessage(BuildInboundEmailContext(message, [], string.Empty, string.Empty));

    private Task<Ticket?> ResolveDeliveryFailureTicketAsync(Message message, string? internetMessageId, CancellationToken token)
        => CreateProcessor().ResolveDeliveryFailureTicketAsync(BuildInboundEmailContext(message, [], string.Empty, internetMessageId ?? string.Empty), internetMessageId, token);

    private InboundTicketProcessor CreateProcessor() => new(_requestSender, _logger, _tenantProvisioningService,
        _ticketNotificationService, _blockedRepo, _emailService, _templateRepo, _incidentRepo, _ticketRepo,
        _timelineRepo, _attachmentService, _inlineImageResolver, _templateRenderer, _layoutResolver,
        _tenantBrandingResolver, _domainEvents, _correlationContext, _inboundEmailRuleProcessor);

    private async Task FinalizeGraphMessageAsync(
        GraphServiceClient graphClient,
        string mailboxAddress,
        Message graphMessage,
        string processedFolderId,
        Ticket? ticket,
        CancellationToken token)
    {
        var update = new Message { IsRead = true };
        if (ticket is not null &&
            !string.IsNullOrWhiteSpace(ticket.TrackingId) &&
            !(graphMessage.Subject ?? string.Empty).Contains(ticket.TrackingId, StringComparison.OrdinalIgnoreCase))
        {
            update.Subject = $"[{ticket.TrackingId}] {graphMessage.Subject}";
        }

        await graphClient.Users[mailboxAddress].Messages[graphMessage.Id].PatchAsync(update, cancellationToken: token);
        await MoveGraphMessageAsync(graphClient, mailboxAddress, graphMessage.Id, processedFolderId);

        _logger.LogInformation("Patched and moved Graph Message ID {MessageId}", graphMessage.Id);
    }

    private static InboundEmailContext BuildInboundEmailContext(
        Message graphMessage,
        IEnumerable<Microsoft.Graph.Models.Attachment> attachments,
        string mailboxAddress,
        string internetMessageId)
    {
        var bodyContent = graphMessage.Body?.Content ?? string.Empty;
        var htmlBody = graphMessage.Body?.ContentType == BodyType.Html
            ? bodyContent
            : HtmlEncoder.Default.Encode(bodyContent);
        var textBody = graphMessage.Body?.ContentType == BodyType.Text
            ? bodyContent
            : string.Empty;

        return new InboundEmailContext(
            internetMessageId,
            graphMessage.Id,
            null,
            null,
            mailboxAddress,
            graphMessage.From?.EmailAddress?.Address ?? string.Empty,
            graphMessage.From?.EmailAddress?.Name,
            NormalizeEmails(graphMessage.ToRecipients?.Select(r => r.EmailAddress?.Address) ?? Enumerable.Empty<string>(), null),
            NormalizeEmails(graphMessage.CcRecipients?.Select(r => r.EmailAddress?.Address) ?? Enumerable.Empty<string>(), null),
            graphMessage.Subject ?? string.Empty,
            htmlBody,
            textBody,
            graphMessage.ReceivedDateTime,
            (graphMessage.InternetMessageHeaders ?? []).Where(h => !string.IsNullOrWhiteSpace(h.Name))
                .GroupBy(h => h.Name!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => string.Join("\n", g.Select(h => h.Value)), StringComparer.OrdinalIgnoreCase),
            attachments.OfType<FileAttachment>()
                .Select(ToAttachmentContext)
                .ToList());
    }

    private async Task<string?> GetOrCreateMailFolderIdAsync(GraphServiceClient graphClient, string mailbox, string folderName, CancellationToken token)
    {
        if (string.IsNullOrEmpty(folderName)) return null;

        try
        {
            var filter = $"displayName eq '{folderName}'";
            var folders = await graphClient.Users[mailbox].MailFolders.GetAsync(req => { req.QueryParameters.Filter = filter; }, token);
            var existingFolder = folders?.Value?.FirstOrDefault();
            if (existingFolder != null) return existingFolder.Id;

            _logger.LogInformation("Folder '{FolderName}' not found, creating it with Graph API.", folderName);
            var newFolder = new MailFolder { DisplayName = folderName };
            var createdFolder = await graphClient.Users[mailbox].MailFolders.PostAsync(newFolder, cancellationToken: token);
            return createdFolder?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get or create mail folder '{FolderName}'.", folderName);
            return null;
        }
    }

    private async Task<bool> SendNotificationAsync(
        string templateName,
        string to,
        IEnumerable<string>? cc = null,
        string? userName = null,
        string? ticketRef = null,
        string? ticketLink = null,
        string? tenantId = null)
    {
        var template = (await _templateRepo.GetAllAsync()).FirstOrDefault(t => t.Name == templateName);
        if (template is null)
        {
            _logger.LogWarning("Email template '{TemplateName}' not found. Cannot send notification.", templateName);
            return false;
        }

        var safeUser = userName ?? string.Empty;
        var safeRef = ticketRef ?? string.Empty;
        var safeLink = ticketLink ?? string.Empty;
        var layout = await _layoutResolver.ResolveAsync(template.LayoutId);
        var branding = await _tenantBrandingResolver.ResolveAsync(tenantId);

        var subject = (template.Subject ?? string.Empty).Replace("{{{TICKET_REF}}}", safeRef);
        var body = _templateRenderer.Render(template.HtmlContent ?? string.Empty, new EmailTemplateContext
        {
            UserName = safeUser,
            TicketRef = safeRef,
            TicketLink = safeLink,
            LayoutHtml = layout?.HtmlContent ?? string.Empty,
            BrandName = branding.BrandName,
            Brand = branding.TemplateBrand,
            LogoHtml = branding.LogoHtml,
            FooterHtml = branding.FooterHtml,
            PrimaryColor = branding.PrimaryColor
        });

        var sent = await _emailService.SendEmailAsync(
            new[] { to },
            subject,
            body,
            cc,
            fromName: branding.FromName,
            replyTo: branding.ReplyTo);
        if (sent)
        {
            _logger.LogInformation("Sent '{TemplateName}' notification to {Recipient}", templateName, to);
        }

        await _domainEvents.PublishAsync(
            new EmailSentEvent(
                Recipient: to,
                TemplateName: templateName,
                TenantId: tenantId,
                Reference: ticketRef,
                CorrelationId: GetCorrelationId(),
                Success: sent,
                Details: sent ? "Email sent successfully." : "Email send returned false."),
            CancellationToken.None);

        return sent;
    }

    private string GetCorrelationId()
    {
        return _correlationContext?.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private async Task MoveGraphMessageAsync(GraphServiceClient graphClient, string mailbox, string graphMessageId, string destinationFolderId)
    {
        if (string.IsNullOrEmpty(destinationFolderId)) return;
        try
        {
            await graphClient.Users[mailbox].Messages[graphMessageId].Move
                .PostAsync(new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody
                {
                    DestinationId = destinationFolderId
                });
        }
        catch (ODataError odataError) when (odataError.Error?.Code?.Equals("ErrorItemNotFound", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogWarning("Destination folder id '{Folder}' not found in mailbox.", destinationFolderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move Graph message {MessageId} to folder id {Folder}", graphMessageId, destinationFolderId);
        }
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

    private static InboundEmailAttachmentContext ToAttachmentContext(FileAttachment attachment) => new(
        attachment.Name ?? "attachment",
        attachment.ContentType ?? "application/octet-stream",
        attachment.ContentId,
        attachment.ContentBytes,
        attachment.Id,
        attachment.IsInline);
}
