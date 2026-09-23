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
        if (selected.Status != "Ready" || selected.Mailbox is null || selected.Outgoing is null)
        {
            logger.LogWarning("Email submission blocked. TicketId={TicketId}, OrganizationId={OrganizationId}, MailboxId={MailboxId}, ErrorCode={ErrorCode}",
                ticketId, selected.OrganizationId, selected.Mailbox?.Id, selected.ErrorCode);
            return false;
        }
        if (!string.IsNullOrWhiteSpace(replyTo) &&
            !EmailAddressGuard.IsSameAddress(replyTo, selected.Mailbox.MailboxAddress))
        {
            logger.LogWarning("Email submission blocked by mismatched Reply-To. TicketId={TicketId}, MailboxId={MailboxId}",
                ticketId, selected.Mailbox.Id);
            return false;
        }
        var submission = selected.Outgoing.Transport switch
        {
            Helpdesk.Shared.Models.MailboxOutgoingTransport.Smtp => await smtp.SendAsync(selected.Mailbox,
                selected.Outgoing, recipients, cc, subject, htmlMessage, attachments, Guid.NewGuid(), ct),
            Helpdesk.Shared.Models.MailboxOutgoingTransport.Graph => await graph.SendAsync(selected.Mailbox,
                selected.Outgoing, recipients, cc, subject, htmlMessage, attachments, ct),
            _ => new MailboxSubmissionResult("Needs configuration", "UnsupportedTransport")
        };
        logger.LogInformation("Email submission result. TicketId={TicketId}, OrganizationId={OrganizationId}, MailboxId={MailboxId}, Transport={Transport}, Status={Status}, ErrorCode={ErrorCode}",
            ticketId, selected.OrganizationId, selected.Mailbox.Id, selected.Outgoing.Transport,
            submission.Status, submission.ErrorCode);
        return submission.Status is "Accepted by provider" or "Suppressed";
    }

    public Task<bool> TestApiConnectionAsync() => resolver.AnyConfiguredSenderAsync(CancellationToken.None);
}
