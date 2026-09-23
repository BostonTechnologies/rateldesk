using System.Threading.Channels;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Events;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Models;

namespace Helpdesk.Infrastructure.Email;

public sealed class IngressEmailService(MailboxEmailService inner, MailboxSenderResolver resolver,
    IIngressEffectContext context) : IEmailService, IDurableEmailService
{
    public bool QueuesDelivery => true;
    public Task<bool> TestApiConnectionAsync() => inner.TestApiConnectionAsync();

    public Task<bool> SendEmailAsync(string recipient, string subject, string htmlMessage,
        IEnumerable<string>? cc = null, CancellationToken ct = default, string? ticketId = null,
        IEnumerable<EmailAttachmentData>? attachments = null, string? fromName = null,
        string? replyTo = null, bool suppressTimeline = false) =>
        SendEmailAsync([recipient], subject, htmlMessage, cc, ct, ticketId, attachments, fromName, replyTo, suppressTimeline);

    public async Task<bool> SendEmailAsync(IEnumerable<string> recipients, string subject, string htmlMessage,
        IEnumerable<string>? cc = null, CancellationToken ct = default, string? ticketId = null,
        IEnumerable<EmailAttachmentData>? attachments = null, string? fromName = null,
        string? replyTo = null, bool suppressTimeline = false)
    {
        ct.ThrowIfCancellationRequested();
        if (!context.IsActive)
            return await inner.SendEmailAsync(recipients, subject, htmlMessage, cc, ct, ticketId, attachments, fromName, replyTo, suppressTimeline);
        var selection = await resolver.ResolveAsync(ticketId, context.OrganizationId, ct);
        context.Capture(MailboxEffectKind.Email, new IngressEmailEffect(recipients.ToArray(), subject,
            htmlMessage, cc?.ToArray() ?? [], ticketId, attachments?.ToArray() ?? [], fromName,
            replyTo, suppressTimeline, context.SupportDeliveryId, context.TimelineDeliveryId)
        {
            MailboxId = selection.Mailbox?.Id,
            OrganizationId = selection.OrganizationId,
            SenderBindingError = selection.ErrorCode,
            MailboxConfigurationVersion = selection.Mailbox?.Version,
            OutgoingConfigurationVersion = selection.Outgoing?.Version
        });
        // This means durably accepted when the enclosing transaction commits, not delivered.
        return true;
    }
}

public sealed class IngressTimelineEventBus(TimelineEventBus inner, IIngressEffectContext context) : ITimelineEventBus
{
    public ChannelReader<TicketTimelineEventDto> Subscribe(string ticketId) => inner.Subscribe(ticketId);
    public void Unsubscribe(string ticketId, ChannelReader<TicketTimelineEventDto> reader) => inner.Unsubscribe(ticketId, reader);
    public ValueTask PublishAsync(TicketTimelineEventDto evt)
    {
        if (!context.IsActive)
            return inner.PublishAsync(evt);
        context.Capture(MailboxEffectKind.Timeline, evt);
        return ValueTask.CompletedTask;
    }
}

public sealed class IngressNotificationEventBus(NotificationEventBus inner, IIngressEffectContext context) : INotificationEventBus
{
    public ChannelReader<NotificationDto> Subscribe(string? tenantId) => inner.Subscribe(tenantId);
    public void Unsubscribe(string? tenantId, ChannelReader<NotificationDto> reader) => inner.Unsubscribe(tenantId, reader);
    public void Publish(NotificationDto notification)
    {
        if (context.IsActive)
            context.Capture(MailboxEffectKind.Notification, notification);
        else
            inner.Publish(notification);
    }
}
