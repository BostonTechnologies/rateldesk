using Helpdesk.Application.Services.Email;
using Helpdesk.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Ticket-context sender for all IEmailService callers.</summary>
public sealed class MailboxEmailService(MailboxSenderResolver resolver, SmtpMailboxSender smtp,
    GraphMailboxSender graph, MailboxOutboxStore outbox, IIngressEffectContext context,
    ILogger<MailboxEmailService> logger) : IEmailService, IDurableEmailService
{
    public bool QueuesDelivery => true;
    public Task<bool> SendEmailAsync(string recipient, string subject, string htmlMessage,
        IEnumerable<string>? cc = null, CancellationToken ct = default, string? ticketId = null,
        IEnumerable<EmailAttachmentData>? attachments = null, string? fromName = null,
        string? replyTo = null, bool suppressTimeline = false) =>
        SendEmailAsync([recipient], subject, htmlMessage, cc, ct, ticketId, attachments,
            fromName, replyTo, suppressTimeline);

    public async Task<bool> SendEmailAsync(IEnumerable<string> recipients, string subject, string htmlMessage,
        IEnumerable<string>? cc = null, CancellationToken ct = default, string? ticketId = null,
        IEnumerable<EmailAttachmentData>? attachments = null, string? fromName = null,
        string? replyTo = null, bool suppressTimeline = false)
    {
        return await SendEmailAsync(new EmailSendRequest(recipients.ToArray(), subject, htmlMessage)
        {
            Cc = cc?.ToArray() ?? [], TicketId = ticketId, Attachments = attachments?.ToArray() ?? [],
            FromName = fromName, ReplyTo = replyTo, SuppressTimeline = suppressTimeline
        }, ct);
    }

    public async Task<bool> SendEmailAsync(EmailSendRequest request, CancellationToken ct = default)
    {
        var selected = await resolver.ResolveAsync(request.TicketId, null, ct);
        var email = new IngressEmailEffect(request.Recipients, request.Subject, request.HtmlMessage,
            request.Cc, request.TicketId, request.Attachments, request.FromName, request.ReplyTo,
            request.SuppressTimeline, context.SupportDeliveryId, context.TimelineDeliveryId)
        {
            Bcc = request.Bcc,
            MailboxId = selected.Mailbox?.Id,
            OrganizationId = selected.OrganizationId,
            SenderBindingError = selected.ErrorCode,
            MailboxConfigurationVersion = selected.Mailbox?.Version,
            OutgoingConfigurationVersion = selected.Outgoing?.Version
        };
        var delivery = await outbox.QueueDirectAsync(email, ct);
        logger.LogInformation("Email queued. DeliveryId={DeliveryId}, TicketId={TicketId}, OrganizationId={OrganizationId}, MailboxId={MailboxId}, Transport={Transport}, BindingError={BindingError}",
            delivery.Id,
            request.TicketId, selected.OrganizationId, selected.Mailbox?.Id, selected.Outgoing?.Transport,
            selected.ErrorCode);
        return true;
    }

    public async Task<MailboxSubmissionResult> SendPinnedAsync(Guid mailboxId, string? organizationId,
        string? ticketId, IEnumerable<string> recipients, IEnumerable<string>? cc, string subject,
        string htmlMessage, IEnumerable<EmailAttachmentData>? attachments, string? replyTo,
        Guid deliveryId, CancellationToken ct, long? mailboxVersion = null, long? outgoingVersion = null,
        IEnumerable<string>? bcc = null)
    {
        var selected = await resolver.ResolveAsync(ticketId, organizationId, ct);
        if (selected.Mailbox?.Id != mailboxId)
            return new("Needs review", "SenderRouteChanged");
        if (mailboxVersion is not null && selected.Mailbox.Version != mailboxVersion ||
            outgoingVersion is not null && selected.Outgoing?.Version != outgoingVersion)
            return new("Needs review", "SenderConfigurationChanged");
        return await SubmitAsync(selected, recipients, cc, bcc, subject, htmlMessage,
            attachments, replyTo, deliveryId, ct);
    }

    public async Task<MailboxSubmissionResult> SendTestAsync(Guid mailboxId, string recipient, CancellationToken ct)
    {
        var selected = await resolver.ResolveExplicitAsync(mailboxId, ct);
        var deliveryId = Guid.NewGuid();
        var subject = $"RatelDesk mailbox send test {deliveryId:N}";
        return await SubmitAsync(selected, [recipient], [], [], subject,
            "<p>This is a confirmed RatelDesk mailbox send test.</p>", [], null, deliveryId, ct);
    }

    private async Task<MailboxSubmissionResult> SubmitAsync(MailboxSenderSelection selected,
        IEnumerable<string> recipients, IEnumerable<string>? cc, IEnumerable<string>? bcc,
        string subject, string htmlMessage,
        IEnumerable<EmailAttachmentData>? attachments, string? replyTo, Guid deliveryId, CancellationToken ct)
    {
        if (selected.Status != "Ready" || selected.Mailbox is null || selected.Outgoing is null)
        {
            return new("Needs configuration", selected.ErrorCode ?? "SenderUnavailable");
        }
        if (!string.IsNullOrWhiteSpace(replyTo) &&
            !EmailAddressGuard.IsSameAddress(replyTo, selected.Mailbox.MailboxAddress))
        {
            return new("Needs review", "ReplyToMismatch");
        }
        var submission = selected.Outgoing.Transport switch
        {
            Helpdesk.Shared.Models.MailboxOutgoingTransport.Smtp => await smtp.SendAsync(selected.Mailbox,
                selected.Outgoing, recipients, cc, subject, htmlMessage, attachments, deliveryId, ct, bcc),
            Helpdesk.Shared.Models.MailboxOutgoingTransport.Graph => await graph.SendAsync(selected.Mailbox,
                selected.Outgoing, recipients, cc, subject, htmlMessage, attachments, ct, bcc),
            _ => new MailboxSubmissionResult("Needs configuration", "UnsupportedTransport")
        };
        return submission;
    }

    public Task<bool> TestApiConnectionAsync() => resolver.AnyConfiguredSenderAsync(CancellationToken.None);
}
