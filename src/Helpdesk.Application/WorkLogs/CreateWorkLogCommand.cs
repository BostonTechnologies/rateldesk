using Dodo.Primitives;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Observability;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Application.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace Helpdesk.Application.WorkLogs;

public record CreateWorkLogCommand(
    string TicketId,
    double Hours,
    string? Notes,
    string? TechnicianId,
    string? TechnicianName,
    bool NotifyCustomer = true,
    TimelineEventType EventType = TimelineEventType.Worklog,
    string? EventCreatedByUserId = null,
    string? EventCreatedByUserName = null,
    bool IsInternalNote = false,
    Ticket? AuthorizedTicket = null) : IRequest<WorkLog>;

/// <summary>
/// Handles the creation of a work log entry for a ticket and performs related updates, such as notifying customers and
/// updating ticket details.
/// </summary>
/// <remarks>This handler processes a <see cref="CreateWorkLogCommand"/> to create a new work log entry, update
/// the associated ticket's state and metrics, log the activity, and optionally notify the customer via email and
/// notifications. It ensures that all related entities, such as tickets and activity logs, are updated
/// consistently.</remarks>
/// <param name="workLogs">The repository for managing <see cref="WorkLog"/> entities.</param>
/// <param name="incidents">The repository for managing <see cref="Incident"/> entities, including ticket updates.</param>
/// <param name="activityLogs">The repository for managing <see cref="ActivityLog"/> entities.</param>
/// <param name="notifications">The service for sending notifications to users.</param>
/// <param name="emailService">The service for sending email notifications to customers.</param>
/// <param name="templateRepo">The repository for managing email templates.</param>
/// <param name="ticketRepo">The repository for managing ticket details.</param>
/// <param name="customerRepo">The repository for managing customer details.</param>
/// <param name="config">The configuration provider for accessing application settings, such as public URLs.</param>
public class CreateWorkLogCommandHandler(
    IRepository<WorkLog> workLogs,
    IRepository<Incident> incidents,
    IRepository<ActivityLog> activityLogs,
    INotificationService notifications,
    IEmailService emailService,
    IRepository<EmailTemplate> templateRepo,
    IRepository<Ticket> ticketRepo,
    IRepository<Customer> customerRepo,
    IRepository<TicketTimelineEvent> timelineEvents,
    ITimelineEventBus timelineEventBus,
    IHtmlSanitizerService sanitizer,
    IWorklogImageStorageService imageStorage,
    IHtmlToPlainTextConverter htmlToPlainTextConverter,
    IEmailTemplateRenderer templateRenderer,
    IEmailLayoutResolver layoutResolver,
    ITenantBrandingResolver tenantBrandingResolver,
    IPublicTicketLinkSigner publicTicketLinkSigner,
    IConfiguration config,
    ITicketSlaService? ticketSlaService = null,
    ISlaEscalationEvaluator? slaEscalationEvaluator = null,
    ITicketSlaRepository? ticketSlaRepository = null,
    ILogger<CreateWorkLogCommandHandler>? logger = null,
    IIngressEffectContext? ingressEffects = null) : IRequestHandler<CreateWorkLogCommand, WorkLog>
{
    private readonly ITicketSlaService? _ticketSlaService = ticketSlaService;
    private readonly ISlaEscalationEvaluator? _slaEscalationEvaluator = slaEscalationEvaluator;
    private readonly ITicketSlaRepository? _ticketSlaRepository = ticketSlaRepository;
    private readonly ILogger<CreateWorkLogCommandHandler> _logger = logger ?? NullLogger<CreateWorkLogCommandHandler>.Instance;

    public async Task<WorkLog> Handle(CreateWorkLogCommand request, CancellationToken cancellationToken)
    {
        var workLogId = Uuid.CreateVersion7().ToString();
        var sanitizedHtml = sanitizer.Sanitize(request.Notes ?? string.Empty);
        var htmlWithStoredImages = await imageStorage.ExtractAndStoreImagesAsync(
            workLogId,
            sanitizedHtml);
        var plainText = htmlToPlainTextConverter.Convert(htmlWithStoredImages);

        var log = new WorkLog
        {
            Id = workLogId,
            TicketId = request.TicketId,
            Hours = request.Hours,
            IsInternalNote = request.IsInternalNote,
            NotesHtml = htmlWithStoredImages,
            NotesText = plainText,
            TechnicianId = request.TechnicianId,
            LoggedAt = DateTime.UtcNow
        };

        await workLogs.CreateAsync(log);

        var timelineEvent = new TicketTimelineEvent
        {
            TicketId = request.TicketId,
            CreatedUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = ResolveEventCreatedByUserId(request),
            CreatedByUserName = ResolveEventCreatedByUserName(request),
            EventType = request.IsInternalNote ? TimelineEventType.InternalNote : request.EventType,
            MessageHtml = htmlWithStoredImages,
            MessageText = plainText
        };
        await timelineEvents.CreateAsync(timelineEvent);
        await timelineEventBus.PublishAsync(new TicketTimelineEventDto
        {
            Id = timelineEvent.Id,
            TicketId = timelineEvent.TicketId,
            CreatedUtc = timelineEvent.CreatedUtc,
            CreatedByUserId = timelineEvent.CreatedByUserId,
            CreatedByUserName = timelineEvent.CreatedByUserName,
            EventType = timelineEvent.EventType,
            MessageHtml = timelineEvent.MessageHtml,
            MessageText = timelineEvent.MessageText,
            Hours = log.Hours,
            TechnicianName = request.TechnicianName,
            EmailStatus = timelineEvent.EmailStatus,
            EmailRecipient = timelineEvent.EmailRecipient,
            RetryCount = timelineEvent.RetryCount,
            IsRetryable = timelineEvent.IsRetryable
        });

        var ticketDetails = request.AuthorizedTicket ?? await ticketRepo.GetAsync(request.TicketId);
        var incident = request.AuthorizedTicket as Incident;
        if (incident is null && request.AuthorizedTicket is null)
        {
            incident = await incidents.GetAsync(request.TicketId);
        }
        var ticketForUpdate = incident as Ticket ?? ticketDetails;
        if (ticketForUpdate is not null)
        {
            ticketForUpdate.TimeSpentHours += request.Hours;
            if (request.EventType != TimelineEventType.SystemNotification)
            {
                ticketForUpdate.Replies += 1;
                ticketForUpdate.State = TicketState.Replied;
                ticketForUpdate.LastReplierName = request.TechnicianName;
            }

            ticketForUpdate.UpdatedAt = DateTime.UtcNow;

            if (incident is not null)
            {
                await incidents.UpdateAsync(incident);
            }
            else if (ticketDetails is not null)
            {
                await ticketRepo.UpdateAsync(ticketDetails);
            }
        }

        await activityLogs.CreateAsync(new ActivityLog
        {
            TicketId = request.TicketId,
            Timestamp = DateTime.UtcNow,
            UserId = request.TechnicianId ?? string.Empty,
            Message = "Work log added."
        });

        try
        {
            if (_ticketSlaService is not null && !string.IsNullOrWhiteSpace(request.TechnicianId))
            {
                if (request.AuthorizedTicket is not null)
                    await _ticketSlaService.PauseAsync(request.AuthorizedTicket, request.TechnicianId, "AgentResponded");
                else
                    await _ticketSlaService.PauseAsync(request.TicketId, request.TechnicianId, "AgentResponded");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "SLA pause failed after work-log creation. TicketId={TicketId}", request.TicketId);
        }

        try
        {
            if (_slaEscalationEvaluator is not null && _ticketSlaRepository is not null)
            {
                var ticketForSla = ticketForUpdate ?? await ticketRepo.GetAsync(request.TicketId);
                var slaState = await _ticketSlaRepository.GetByTicketIdAsync(request.TicketId);
                if (ticketForSla is not null && slaState is not null)
                {
                    await _slaEscalationEvaluator.EvaluateAndNotifyAsync(
                        ticketForSla,
                        slaState,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "SLA evaluation failed after work-log creation. TicketId={TicketId}", request.TicketId);
        }

        if (ticketDetails is not null &&
            request.NotifyCustomer &&
            !request.IsInternalNote &&
            ticketDetails.EmailExclusionReason == TicketEmailExclusionReason.None)
        {
            var customerId = FirstNonEmpty(ticketDetails.CustomerId, incident?.CustomerId);
            if (!string.IsNullOrWhiteSpace(customerId))
            {
                await notifications.NotifyUserAsync(customerId, "ticket-updated", request.TicketId);
            }

            var customer = string.IsNullOrWhiteSpace(customerId)
                ? null
                : await customerRepo.GetAsync(customerId);

            if (ticketDetails is not null)
            {
                var templateName = ticketDetails is Change ? "ChangeUpdate" : "TicketUpdated";
                var template = (await templateRepo.GetAllAsync())
                    .FirstOrDefault(t => t.Name == templateName);

                var recipients = ResolveEmailRecipients(ticketDetails, customer, incident);
                if (recipients.Count == 0)
                {
                    HelpdeskTelemetry.RecordEmailDeliveryAttempt("worklog", "configured", "skipped_no_recipient");
                    _logger.LogWarning(
                        "Worklog email delivery skipped because no recipients were available. TicketId={TicketId} TrackingId={TrackingId} OrganizationId={OrganizationId}",
                        request.TicketId,
                        ticketDetails.TrackingId,
                        ticketDetails.OrganizationId);
                    await CreateEmailDeliveryEventAsync(
                        request.TicketId,
                        "(none)",
                        [],
                        EmailDeliveryStatus.Failed,
                        "Email delivery skipped: ticket has no requester, customer, or CC recipient.",
                        timelineEvents,
                        timelineEventBus);
                }
                else if (template is null)
                {
                    HelpdeskTelemetry.RecordEmailDeliveryAttempt("worklog", "configured", "failed_missing_template");
                    _logger.LogWarning(
                        "Worklog email delivery failed because {TemplateName} email template was not found. TicketId={TicketId} TrackingId={TrackingId} OrganizationId={OrganizationId}",
                        templateName,
                        request.TicketId,
                        ticketDetails.TrackingId,
                        ticketDetails.OrganizationId);
                    await CreateEmailDeliveryEventAsync(
                        request.TicketId,
                        recipients[0],
                        recipients.Skip(1).ToArray(),
                        EmailDeliveryStatus.Failed,
                        $"Email delivery failed: {templateName} email template was not found.",
                        timelineEvents,
                        timelineEventBus);
                }
                else
                {
                    var publicUrl = (config["PublicWebAppUrl"] ?? string.Empty).TrimEnd('/');
                    var layout = await layoutResolver.ResolveAsync(template.LayoutId, cancellationToken);
                    var branding = await tenantBrandingResolver.ResolveAsync(ticketDetails.OrganizationId, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(branding.TemplateBrand.ApplicationUrl))
                        publicUrl = branding.TemplateBrand.ApplicationUrl.TrimEnd('/');
                    var primaryRecipient = recipients[0];
                    var ccFinal = recipients
                        .Skip(1)
                        .Where(cc => !string.Equals(cc, primaryRecipient, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var context = new EmailTemplateContext
                    {
                        UserName = string.IsNullOrWhiteSpace(customer?.Name) ? primaryRecipient : customer.Name,
                        TicketRef = ticketDetails.TrackingId ?? string.Empty,
                        TicketLink = BuildSignedPublicTicketLink(publicUrl, ticketDetails.TrackingId ?? string.Empty, primaryRecipient),
                        UpdateMessageHtml = htmlWithStoredImages,
                        UpdateMessageText = plainText,
                        LayoutHtml = layout?.HtmlContent ?? string.Empty,
                        BrandName = branding.BrandName,
                        Brand = branding.TemplateBrand,
                        LogoHtml = branding.LogoHtml,
                        FooterHtml = branding.FooterHtml,
                        PrimaryColor = branding.PrimaryColor
                    };
                    if (ticketDetails is Change change)
                    {
                        ApplyChangeEmailContext(context, change, primaryRecipient);
                    }

                    var subject = RenderSubject(template.Subject ?? string.Empty, context);
                    var body = templateRenderer.Render(template.HtmlContent ?? string.Empty, context);

                    var deliveryEvent = await CreateEmailDeliveryEventAsync(
                        request.TicketId,
                        primaryRecipient,
                        ccFinal,
                        EmailDeliveryStatus.Pending,
                        $"Email delivery pending to {primaryRecipient}",
                        timelineEvents,
                        timelineEventBus);
                    HelpdeskTelemetry.RecordEmailDeliveryAttempt("worklog", "configured", "pending");
                    _logger.LogInformation(
                        "Worklog email delivery pending. TicketId={TicketId} TrackingId={TrackingId} OrganizationId={OrganizationId} Recipient={Recipient} CcCount={CcCount}",
                        request.TicketId,
                        ticketDetails.TrackingId,
                        ticketDetails.OrganizationId,
                        primaryRecipient,
                        ccFinal.Count);

                    try
                    {
                        var started = DateTimeOffset.UtcNow;
                        bool sent;
                        var previousDelivery = ingressEffects?.TimelineDeliveryId;
                        try
                        {
                            if (ingressEffects?.IsActive == true)
                                ingressEffects.TimelineDeliveryId = deliveryEvent.Id;
                            sent = await emailService.SendEmailAsync(
                                new[] { primaryRecipient },
                                subject,
                                body,
                                ccFinal.Count > 0 ? ccFinal : null,
                                cancellationToken,
                                request.TicketId,
                                fromName: branding.FromName,
                                replyTo: branding.ReplyTo,
                                suppressTimeline: true);
                        }
                        finally
                        {
                            if (ingressEffects is not null)
                                ingressEffects.TimelineDeliveryId = previousDelivery;
                        }

                        if (ingressEffects?.IsActive == true)
                            return log; // Keep the delivery Pending until the durable effect is dispatched.

                        var durationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
                        HelpdeskTelemetry.RecordEmailDeliveryAttempt("worklog", "configured", sent ? "delivered" : "failed");
                        _logger.Log(
                            sent ? LogLevel.Information : LogLevel.Warning,
                            "Worklog email delivery completed. TicketId={TicketId} TrackingId={TrackingId} OrganizationId={OrganizationId} Recipient={Recipient} Status={Status} DurationMs={DurationMs}",
                            request.TicketId,
                            ticketDetails.TrackingId,
                            ticketDetails.OrganizationId,
                            primaryRecipient,
                            sent ? "Delivered" : "Failed",
                            durationMs);
                        await UpdateEmailDeliveryEventAsync(
                            deliveryEvent,
                            sent ? EmailDeliveryStatus.Delivered : EmailDeliveryStatus.Failed,
                            sent
                                ? $"Email successfully sent to {primaryRecipient}"
                                : $"Email delivery failed to {primaryRecipient}",
                            ccFinal,
                            timelineEvents,
                            timelineEventBus);
                    }
                    catch (Exception ex)
                    {
                        HelpdeskTelemetry.RecordEmailDeliveryAttempt("worklog", "configured", "failed_exception");
                        _logger.LogWarning(
                            ex,
                            "Worklog email delivery failed. TicketId={TicketId} TrackingId={TrackingId} OrganizationId={OrganizationId} Recipient={Recipient}",
                            request.TicketId,
                            ticketDetails.TrackingId,
                            ticketDetails.OrganizationId,
                            primaryRecipient);
                        await UpdateEmailDeliveryEventAsync(
                            deliveryEvent,
                            EmailDeliveryStatus.Failed,
                            $"Email delivery failed to {primaryRecipient}: {ex.Message}",
                            ccFinal,
                            timelineEvents,
                            timelineEventBus);
                    }
                }
            }
        }

        return log;
    }

    private static string ResolveEventCreatedByUserId(CreateWorkLogCommand request)
    {
        if (!string.IsNullOrWhiteSpace(request.EventCreatedByUserId))
            return request.EventCreatedByUserId;

        return request.TechnicianId ?? "system";
    }

    private static string ResolveEventCreatedByUserName(CreateWorkLogCommand request)
    {
        if (!string.IsNullOrWhiteSpace(request.EventCreatedByUserName))
            return request.EventCreatedByUserName;

        return string.IsNullOrWhiteSpace(request.TechnicianName) ? "System" : request.TechnicianName;
    }

    private string BuildSignedPublicTicketLink(string publicUrl, string trackingId, string email)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var token = publicTicketLinkSigner.GenerateToken(trackingId, email, expires);
        return $"{publicUrl}/view-ticket/{trackingId}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }

    private string BuildSignedChangeViewLink(string publicUrl, string trackingId, string email)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var token = publicTicketLinkSigner.GenerateToken(trackingId, email, expires);
        return $"{publicUrl}/change-approval/{Uri.EscapeDataString(trackingId)}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }

    private void ApplyChangeEmailContext(EmailTemplateContext context, Change change, string recipientEmail)
    {
        context.ChangeTitle = change.Title ?? string.Empty;
        context.ChangeDescription = change.Description ?? string.Empty;
        context.ChangeType = change.ChangeType ?? string.Empty;
        context.ChangePriority = change.Priority.ToString();
        context.ChangeImplementationStart = FormatTimestamp(change.ImplementationStartAt);
        context.ChangeImplementationEnd = FormatTimestamp(change.ImplementationEndAt);
        context.ChangeApprovalStatus = FormatLifecycleState(change.LifecycleState ?? ChangeLifecycleState.Draft);
        context.ChangeCompletionState = FormatCompletionState(change.LifecycleState ?? ChangeLifecycleState.Draft);
        context.ChangeViewLink = BuildSignedChangeViewLink(
            (config["PublicWebAppUrl"] ?? string.Empty).TrimEnd('/'),
            change.TrackingId ?? string.Empty,
            recipientEmail);
        context.TicketLink = context.ChangeViewLink;
    }

    private static string RenderSubject(string subject, EmailTemplateContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USER_NAME"] = context.UserName,
            ["TICKET_REF"] = context.TicketRef,
            ["TICKET_LINK"] = context.TicketLink,
            ["CHANGE_TITLE"] = context.ChangeTitle,
            ["CHANGE_TYPE"] = context.ChangeType,
            ["CHANGE_APPROVAL_STATUS"] = context.ChangeApprovalStatus,
            ["CHANGE_COMPLETION_STATE"] = context.ChangeCompletionState,
            ["CHANGE_VIEW_LINK"] = context.ChangeViewLink,
            ["UserName"] = context.UserName,
            ["TicketRef"] = context.TicketRef,
            ["TicketLink"] = context.TicketLink,
            ["ChangeTitle"] = context.ChangeTitle,
            ["ChangeType"] = context.ChangeType,
            ["ChangeApprovalStatus"] = context.ChangeApprovalStatus,
            ["ChangeCompletionState"] = context.ChangeCompletionState,
            ["ChangeViewLink"] = context.ChangeViewLink
        };

        var rendered = subject ?? string.Empty;
        foreach (var (key, value) in values)
        {
            rendered = rendered
                .Replace("{{{" + key + "}}}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{{" + key + "}}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return rendered;
    }

    private static string FormatTimestamp(DateTime? value) =>
        value.HasValue
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'")
            : string.Empty;

    private static string FormatLifecycleState(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.ImplementedSuccess => "Implemented - Success",
        ChangeLifecycleState.ImplementedBackedOut => "Implemented - Backed Out",
        ChangeLifecycleState.ImplementationInProgress => "Implementation In Progress",
        ChangeLifecycleState.ApprovedForImplementation => "Approved For Implementation",
        ChangeLifecycleState.PendingApproval => "Pending Approval",
        _ => state.ToString()
    };

    private static string FormatCompletionState(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.ImplementedSuccess => "Success",
        ChangeLifecycleState.ImplementedBackedOut => "Backed Out",
        _ => string.Empty
    };

    private static List<string> ResolveEmailRecipients(
        Ticket ticketDetails,
        Customer? customer,
        Incident? incident)
    {
        var candidates = new[]
            {
                customer?.Email,
                ticketDetails.RequesterEmail,
                incident?.RequesterEmail,
                incident?.EmailFrom,
                (ticketDetails as Incident)?.RequesterEmail,
                (ticketDetails as Incident)?.EmailFrom
            }
            .Concat(ticketDetails.CcRecipients)
            .Concat(incident?.CcRecipients ?? Enumerable.Empty<string>())
            .Concat((ticketDetails as Incident)?.CcRecipients ?? Enumerable.Empty<string>());

        return candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static async Task<TicketTimelineEvent> CreateEmailDeliveryEventAsync(
        string ticketId,
        string primaryRecipient,
        IReadOnlyCollection<string> ccRecipients,
        EmailDeliveryStatus status,
        string message,
        IRepository<TicketTimelineEvent> timelineEvents,
        ITimelineEventBus timelineEventBus)
    {
        var evt = new TicketTimelineEvent
        {
            TicketId = ticketId,
            CreatedUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = "system",
            CreatedByUserName = "System",
            EventType = TimelineEventType.EmailDelivery,
            EmailStatus = status,
            EmailRecipient = primaryRecipient,
            MessageText = message
        };

        await timelineEvents.CreateAsync(evt);
        await timelineEventBus.PublishAsync(ToEmailDeliveryDto(evt, ccRecipients));
        return evt;
    }

    private static async Task UpdateEmailDeliveryEventAsync(
        TicketTimelineEvent evt,
        EmailDeliveryStatus status,
        string message,
        IReadOnlyCollection<string> ccRecipients,
        IRepository<TicketTimelineEvent> timelineEvents,
        ITimelineEventBus timelineEventBus)
    {
        evt.EmailStatus = status;
        evt.MessageText = message;
        await timelineEvents.UpdateAsync(evt);
        await timelineEventBus.PublishAsync(ToEmailDeliveryDto(evt, ccRecipients));
    }

    private static TicketTimelineEventDto ToEmailDeliveryDto(
        TicketTimelineEvent evt,
        IReadOnlyCollection<string> ccRecipients)
    {
        var cc = ccRecipients
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recipients = new[] { evt.EmailRecipient }
            .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, "(none)", StringComparison.OrdinalIgnoreCase))
            .Select(x => x!.Trim())
            .Concat(cc)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
            Recipients = recipients,
            PrimaryRecipient = string.Equals(evt.EmailRecipient, "(none)", StringComparison.OrdinalIgnoreCase) ? null : evt.EmailRecipient,
            CcRecipients = cc,
            RetryCount = evt.RetryCount,
            IsRetryable = evt.IsRetryable
        };
    }
}
