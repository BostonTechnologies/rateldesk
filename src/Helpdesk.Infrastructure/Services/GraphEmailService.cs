using System.Diagnostics;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Azure.Identity;
using Helpdesk.Application.Events;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.Observability;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;

namespace Helpdesk.Infrastructure.Services;

public sealed class GraphEmailService : IEmailService
{
    private const int MaxRetryAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<
        string,
        Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody,
        CancellationToken,
        Task>? _sendMailAsync;
    private readonly ExchangeEmailOptions _options;
    private readonly IEmailSettingsProvider _emailSettingsProvider;
    private readonly ILogger<GraphEmailService> _logger;
    private readonly IDomainEventPublisher _domainEvents;
    private readonly ICorrelationContext _correlationContext;
    private readonly IRepository<TicketTimelineEvent> _timelineEvents;
    private readonly ITimelineEventBus _timelineEventBus;
    private readonly bool _initialized;
    private readonly HelpdeskDbContext? _db;

    public GraphEmailService(
        IOptions<ExchangeEmailOptions> opts,
        ILogger<GraphEmailService> logger,
        IDomainEventPublisher domainEvents,
        ICorrelationContext correlationContext,
        IRepository<TicketTimelineEvent> timelineEvents,
        ITimelineEventBus timelineEventBus,
        IEmailSettingsProvider emailSettingsProvider,
        HelpdeskDbContext? db = null)
        : this(
            opts,
            logger,
            domainEvents,
            correlationContext,
            timelineEvents,
            timelineEventBus,
            emailSettingsProvider,
            sendMailAsync: null)
    {
        _db = db;
    }

    internal GraphEmailService(
        IOptions<ExchangeEmailOptions> opts,
        ILogger<GraphEmailService> logger,
        IDomainEventPublisher domainEvents,
        ICorrelationContext correlationContext,
        IRepository<TicketTimelineEvent> timelineEvents,
        ITimelineEventBus timelineEventBus,
        IEmailSettingsProvider emailSettingsProvider,
        Func<
            string,
            Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody,
            CancellationToken,
            Task>? sendMailAsync,
        HelpdeskDbContext? db = null)
    {
        _db = db;
        _options = opts.Value;
        _logger = logger;
        _domainEvents = domainEvents;
        _correlationContext = correlationContext;
        _timelineEvents = timelineEvents;
        _timelineEventBus = timelineEventBus;
        _emailSettingsProvider = emailSettingsProvider;

        try
        {
            _logger.LogInformation(
                "GraphEmailService initializing. TenantId={TenantId}, ClientId={ClientId}, Mailbox={Mailbox}",
                _options.TenantId,
                _options.ClientId,
                _options.MailboxAddress);

            if (!_options.Enabled)
            {
                _logger.LogInformation("GraphEmailService is disabled by configuration.");
                return;
            }

            if (!ValidateOptions(_options))
            {
                _logger.LogError(
                    "GraphEmailService disabled due to invalid ExchangeEmailOptions configuration.");
                return;
            }

            if (!Guid.TryParse(_options.TenantId, out _) &&
                !_options.TenantId.Contains('.'))
            {
                _logger.LogCritical(
                    "Invalid TenantId format: {TenantId}",
                    _options.TenantId);

                return;
            }

            if (sendMailAsync is null)
            {
                var credential = new ClientSecretCredential(
                    _options.TenantId,
                    _options.ClientId,
                    _options.ClientSecret);

                var graph = new GraphServiceClient(credential);
                _sendMailAsync = (mailbox, request, token) => graph
                    .Users[mailbox]
                    .SendMail
                    .PostAsync(request, cancellationToken: token);
            }
            else
            {
                _sendMailAsync = sendMailAsync;
            }

            _initialized = true;

            _logger.LogInformation(
                "GraphEmailService initialized successfully for mailbox {Mailbox}",
                _options.MailboxAddress);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "GraphEmailService failed to initialize.");
            _initialized = false;
        }
    }

    private static bool ValidateOptions(ExchangeEmailOptions o)
    {
        return
            !string.IsNullOrWhiteSpace(o.TenantId) &&
            !string.IsNullOrWhiteSpace(o.ClientId) &&
            !string.IsNullOrWhiteSpace(o.ClientSecret) &&
            !string.IsNullOrWhiteSpace(o.MailboxAddress);
    }

    public async Task<bool> SendEmailAsync(
        IEnumerable<string> recipients,
        string subject,
        string htmlMessage,
        IEnumerable<string>? cc = null,
        CancellationToken ct = default,
        string? ticketId = null,
        IEnumerable<EmailAttachmentData>? attachments = null,
        string? fromName = null,
        string? replyTo = null,
        bool suppressTimeline = false)
    {
        if (!_initialized || _sendMailAsync is null)
        {
            _logger.LogError(
                "GraphEmailService not initialized. Email skipped. Subject={Subject}",
                subject);
            if (!suppressTimeline)
            {
                await LogEmailTimelineAsync(
                    ticketId,
                    recipients ?? Enumerable.Empty<string>(),
                    cc,
                    EmailDeliveryStatus.Failed,
                    $"Email delivery failed: GraphEmailService not initialized. Subject: {subject}",
                    ct);
            }
            await TryPublishEmailEventAsync(
                recipient: string.Join(",", recipients ?? Enumerable.Empty<string>()),
                templateName: "Delivery",
                reference: ticketId,
                success: false,
                details: $"GraphEmailService not initialized. Subject: {subject}",
                ct: ct);
            return false;
        }

        var senderMailbox = await ResolveSenderMailboxAsync(ct);
        if (_db is not null && !string.IsNullOrWhiteSpace(ticketId))
        {
            var organizationId = await _db.Tickets.IgnoreQueryFilters().Where(x => x.Id == ticketId).Select(x => x.OrganizationId).SingleOrDefaultAsync(ct);
            var assignments = await _db.EmailInboxSettings.AsNoTracking().Where(x => !x.Archived &&
                (x.OrganizationId == organizationId || x.Scope == MailboxScope.Global)).ToListAsync(ct);
            var ingress = assignments.SingleOrDefault(x => x.OrganizationId == organizationId && x.Scope == MailboxScope.Organization)
                ?? assignments.SingleOrDefault(x => x.Scope == MailboxScope.Global);
            if (ingress is not null)
            {
                if (!string.IsNullOrWhiteSpace(replyTo) && !EmailAddressGuard.IsSameAddress(replyTo, ingress.MailboxAddress))
                    throw new InvalidOperationException("Configured Reply-To conflicts with the ticket organization's ingress mailbox.");
                replyTo = ingress.MailboxAddress;
            }
        }
        var normalizedTo = EmailAddressGuard.NormalizeRecipients(recipients);
        var normalizedCc = EmailAddressGuard.NormalizeRecipients(cc);
        var filteredTo = EmailAddressGuard.NormalizeRecipients(normalizedTo, senderMailbox);
        var filteredCc = EmailAddressGuard.NormalizeRecipients(normalizedCc, senderMailbox);
        var removedSelfRecipientCount =
            normalizedTo.Count - filteredTo.Count +
            normalizedCc.Count - filteredCc.Count;

        var toList = filteredTo
            .Select(x => new Recipient
            {
                EmailAddress = new EmailAddress { Address = x }
            })
            .ToList();

        var ccList = filteredCc
            .Select(x => new Recipient
            {
                EmailAddress = new EmailAddress { Address = x }
            })
            .ToList();

        if (toList.Count == 0)
        {
            if (removedSelfRecipientCount > 0)
            {
                _logger.LogWarning(
                    "GraphEmailService suppressed self-recipient delivery to mailbox {MailboxAddress}. RemovedRecipientCount={RemovedRecipientCount}, Subject={Subject}",
                    senderMailbox,
                    removedSelfRecipientCount,
                    subject);
                HelpdeskTelemetry.RecordEmailDeliveryAttempt(
                    "email_service",
                    "graph",
                    "skipped_self_recipient");
                return false;
            }

            _logger.LogWarning(
                "GraphEmailService email skipped because recipients list is empty. Subject={Subject}",
                subject);
            if (!suppressTimeline)
            {
                await LogEmailTimelineAsync(
                    ticketId,
                    recipients ?? Enumerable.Empty<string>(),
                    cc,
                    EmailDeliveryStatus.Failed,
                    $"Email delivery failed: no recipients provided. Subject: {subject}",
                    ct);
            }
            await TryPublishEmailEventAsync(
                recipient: "(none)",
                templateName: "Delivery",
                reference: ticketId,
                success: false,
                details: $"No recipients provided. Subject: {subject}",
                ct: ct);
            return false;
        }

        if (removedSelfRecipientCount > 0)
        {
            _logger.LogWarning(
                "GraphEmailService removed the sender mailbox {MailboxAddress} from recipients. RemovedRecipientCount={RemovedRecipientCount}, Subject={Subject}",
                senderMailbox,
                removedSelfRecipientCount,
                subject);
        }

        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = htmlMessage
            },
            ToRecipients = toList,
            CcRecipients = ccList
        };
        if (!string.IsNullOrWhiteSpace(fromName))
        {
            message.From = new Recipient
            {
                EmailAddress = new EmailAddress
                {
                    Address = senderMailbox,
                    Name = fromName.Trim()
                }
            };
        }
        if (!string.IsNullOrWhiteSpace(replyTo))
        {
            message.ReplyTo = new List<Recipient>
            {
                new()
                {
                    EmailAddress = new EmailAddress { Address = replyTo.Trim() }
                }
            };
        }

        var attachmentList = (attachments ?? Enumerable.Empty<EmailAttachmentData>())
            .Where(x => x.ContentBytes.Length > 0 && !string.IsNullOrWhiteSpace(x.FileName))
            .Select(x => (Microsoft.Graph.Models.Attachment)new FileAttachment
            {
                Name = x.FileName,
                ContentType = x.ContentType,
                ContentBytes = x.ContentBytes
            })
            .ToList();
        if (attachmentList.Count > 0)
        {
            message.Attachments = attachmentList;
        }

        var request = new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        var totalSw = Stopwatch.StartNew();

        for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(RequestTimeout);

                await _sendMailAsync(senderMailbox, request, timeoutCts.Token);

                totalSw.Stop();
                _logger.LogInformation(
                    "GraphEmailService email sent successfully. Subject={Subject}, Attempt={Attempt}, DurationMs={Duration}",
                    subject,
                    attempt,
                    totalSw.ElapsedMilliseconds);
                HelpdeskTelemetry.RecordEmailDeliveryAttempt("email_service", "graph", "delivered");
                if (!suppressTimeline)
                {
                    await LogEmailTimelineAsync(
                        ticketId,
                        toList.Select(r => r.EmailAddress?.Address),
                        ccList.Select(r => r.EmailAddress?.Address),
                        EmailDeliveryStatus.Delivered,
                        "Email successfully delivered",
                        ct);
                }
                await TryPublishEmailEventAsync(
                    recipient: string.Join(",", toList.Select(x => x.EmailAddress?.Address)),
                    templateName: "Delivery",
                    reference: ticketId,
                    success: true,
                    details: $"Email sent successfully. Subject: {subject}",
                    ct: ct);

                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "GraphEmailService email send cancelled by caller. Subject={Subject}, Attempt={Attempt}",
                    subject,
                    attempt);
                return false;
            }
            catch (OperationCanceledException ex) when (attempt < MaxRetryAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "GraphEmailService email send timed out. Retrying. Subject={Subject}, Attempt={Attempt}",
                    subject,
                    attempt);
                await TryPublishEmailEventAsync(
                    recipient: string.Join(",", toList.Select(x => x.EmailAddress?.Address)),
                    templateName: "Delivery",
                    reference: ticketId,
                    success: false,
                    details: $"Retry {attempt} for subject {subject}",
                    ct: ct);

                await Task.Delay(GetBackoff(attempt), ct);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetryAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "GraphEmailService transient failure. Retrying. Subject={Subject}, Attempt={Attempt}",
                    subject,
                    attempt);
                await TryPublishEmailEventAsync(
                    recipient: string.Join(",", toList.Select(x => x.EmailAddress?.Address)),
                    templateName: "Delivery",
                    reference: ticketId,
                    success: false,
                    details: $"Retry {attempt} for subject {subject}",
                    ct: ct);

                await Task.Delay(GetBackoff(attempt), ct);
            }
            catch (Exception ex)
            {
                totalSw.Stop();
                _logger.LogError(
                    ex,
                    "GraphEmailService email send failed. Subject={Subject}, Attempt={Attempt}, DurationMs={Duration}",
                    subject,
                    attempt,
                    totalSw.ElapsedMilliseconds);
                HelpdeskTelemetry.RecordEmailDeliveryAttempt("email_service", "graph", "failed");
                if (!suppressTimeline)
                {
                    await LogEmailTimelineAsync(
                        ticketId,
                        toList.Select(r => r.EmailAddress?.Address),
                        ccList.Select(r => r.EmailAddress?.Address),
                        EmailDeliveryStatus.Failed,
                        $"Email delivery failed: {ex.Message}",
                        ct);
                }
                await TryPublishEmailEventAsync(
                    recipient: string.Join(",", toList.Select(x => x.EmailAddress?.Address)),
                    templateName: "Delivery",
                    reference: ticketId,
                    success: false,
                    details: ex.Message,
                    ct: ct);

                return false;
            }
        }

        totalSw.Stop();
        _logger.LogError(
            "GraphEmailService email send exhausted retries. Subject={Subject}, DurationMs={Duration}",
            subject,
            totalSw.ElapsedMilliseconds);
        HelpdeskTelemetry.RecordEmailDeliveryAttempt("email_service", "graph", "failed_exhausted");
        if (!suppressTimeline)
        {
            await LogEmailTimelineAsync(
                ticketId,
                toList.Select(r => r.EmailAddress?.Address),
                ccList.Select(r => r.EmailAddress?.Address),
                EmailDeliveryStatus.Failed,
                $"Email delivery failed: send exhausted retries. Subject: {subject}",
                ct);
        }
        await TryPublishEmailEventAsync(
            recipient: string.Join(",", toList.Select(x => x.EmailAddress?.Address)),
            templateName: "Delivery",
            reference: ticketId,
            success: false,
            details: $"Email send exhausted retries. Subject: {subject}",
            ct: ct);
        return false;
    }

    private async Task<string> ResolveSenderMailboxAsync(CancellationToken ct)
    {
        if (_db is not null)
        {
            var pin = await _db.Set<MailboxMigrationState>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
            if (!string.IsNullOrWhiteSpace(pin?.OutboundMailboxAddress)) return pin.OutboundMailboxAddress;
        }
        return _options.MailboxAddress.Trim();
    }

    public Task<bool> SendEmailAsync(
        string recipient,
        string subject,
        string htmlMessage,
        IEnumerable<string>? cc = null,
        CancellationToken ct = default,
        string? ticketId = null,
        IEnumerable<EmailAttachmentData>? attachments = null,
        string? fromName = null,
        string? replyTo = null,
        bool suppressTimeline = false)
    {
        return SendEmailAsync(new[] { recipient }, subject, htmlMessage, cc, ct, ticketId, attachments, fromName, replyTo, suppressTimeline);
    }

    public Task<bool> TestApiConnectionAsync()
    {
        return Task.FromResult(_initialized);
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException || ex is HttpRequestException)
        {
            return true;
        }

        if (ex is ApiException apiEx)
        {
            var statusCode = apiEx.ResponseStatusCode;
            return statusCode == 408 || statusCode == 429 || statusCode >= 500;
        }

        return false;
    }

    private static TimeSpan GetBackoff(int attempt)
    {
        var jitterMs = Random.Shared.Next(25, 150);
        var delayMs = (int)(Math.Pow(2, attempt - 1) * 250) + jitterMs;
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private async Task TryPublishEmailEventAsync(
        string recipient,
        string templateName,
        string? reference,
        bool success,
        string details,
        CancellationToken ct)
    {
        try
        {
            await _domainEvents.PublishAsync(
                new EmailSentEvent(
                    Recipient: recipient,
                    TemplateName: templateName,
                    TenantId: null,
                    Reference: reference,
                    CorrelationId: _correlationContext.GetCorrelationId(),
                    Success: success,
                    Details: details),
                ct);
        }
        catch (Exception eventEx)
        {
            _logger.LogWarning(
                eventEx,
                "Failed to publish email domain event. Recipient={Recipient}",
                recipient);
        }
    }

    private async Task LogEmailTimelineAsync(
        string? ticketId,
        IEnumerable<string?> recipients,
        IEnumerable<string?>? ccRecipients,
        EmailDeliveryStatus status,
        string message,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return;
        }

        var recipientList = recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ccList = (ccRecipients ?? Enumerable.Empty<string?>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allRecipients = recipientList
            .Concat(ccList)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (recipientList.Count == 0)
        {
            recipientList.Add("(none)");
        }

        foreach (var recipient in recipientList)
        {
            var timelineEvent = new TicketTimelineEvent
            {
                TicketId = ticketId,
                CreatedUtc = DateTimeOffset.UtcNow,
                CreatedByUserId = "system",
                CreatedByUserName = "System",
                EventType = TimelineEventType.EmailDelivery,
                EmailStatus = status,
                EmailRecipient = recipient,
                MessageText = recipient == "(none)" ? message : $"{message} to {recipient}"
            };

            await _timelineEvents.CreateAsync(timelineEvent);
            await _timelineEventBus.PublishAsync(new TicketTimelineEventDto
            {
                Id = timelineEvent.Id,
                TicketId = timelineEvent.TicketId,
                CreatedUtc = timelineEvent.CreatedUtc,
                CreatedByUserId = timelineEvent.CreatedByUserId,
                CreatedByUserName = timelineEvent.CreatedByUserName,
                EventType = timelineEvent.EventType,
                MessageHtml = timelineEvent.MessageHtml,
                MessageText = timelineEvent.MessageText,
                EmailStatus = timelineEvent.EmailStatus,
                EmailRecipient = timelineEvent.EmailRecipient,
                Recipients = allRecipients,
                PrimaryRecipient = string.Equals(recipient, "(none)", StringComparison.OrdinalIgnoreCase) ? null : recipient,
                CcRecipients = ccList.ToArray(),
                RetryCount = timelineEvent.RetryCount,
                IsRetryable = timelineEvent.IsRetryable
            });
        }
    }
}
