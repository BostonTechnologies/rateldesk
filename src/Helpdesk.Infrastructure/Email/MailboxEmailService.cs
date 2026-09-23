using Helpdesk.Application.Services.Email;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Ticket-context sender for all IEmailService callers.</summary>
public sealed class MailboxEmailService(MailboxSenderResolver resolver, SmtpMailboxSender smtp,
    GraphMailboxSender graph, ILogger<MailboxEmailService> logger) : IEmailService
{
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
        var selected = await resolver.ResolveAsync(ticketId, null, ct);
        var submission = await SubmitAsync(selected, recipients, cc, subject, htmlMessage,
            attachments, replyTo, Guid.NewGuid(), ct);
        logger.LogInformation("Email submission result. TicketId={TicketId}, OrganizationId={OrganizationId}, MailboxId={MailboxId}, Transport={Transport}, Status={Status}, ErrorCode={ErrorCode}",
            ticketId, selected.OrganizationId, selected.Mailbox?.Id, selected.Outgoing?.Transport,
            submission.Status, submission.ErrorCode);
        return submission.Status is "Accepted by provider" or "Suppressed";
    }

    public async Task<MailboxSubmissionResult> SendPinnedAsync(Guid mailboxId, string? organizationId,
        string? ticketId, IEnumerable<string> recipients, IEnumerable<string>? cc, string subject,
        string htmlMessage, IEnumerable<EmailAttachmentData>? attachments, string? replyTo,
        Guid deliveryId, CancellationToken ct)
    {
        var selected = await resolver.ResolveAsync(ticketId, organizationId, ct);
        if (selected.Mailbox?.Id != mailboxId)
            return new("Needs review", "SenderRouteChanged");
        return await SubmitAsync(selected, recipients, cc, subject, htmlMessage,
            attachments, replyTo, deliveryId, ct);
    }

    private async Task<MailboxSubmissionResult> SubmitAsync(MailboxSenderSelection selected,
        IEnumerable<string> recipients, IEnumerable<string>? cc, string subject, string htmlMessage,
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
                selected.Outgoing, recipients, cc, subject, htmlMessage, attachments, deliveryId, ct),
            Helpdesk.Shared.Models.MailboxOutgoingTransport.Graph => await graph.SendAsync(selected.Mailbox,
                selected.Outgoing, recipients, cc, subject, htmlMessage, attachments, ct),
            _ => new MailboxSubmissionResult("Needs configuration", "UnsupportedTransport")
        };
        return submission;
    }

    public Task<bool> TestApiConnectionAsync() => resolver.AnyConfiguredSenderAsync(CancellationToken.None);
}
