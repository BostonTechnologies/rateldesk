namespace Helpdesk.Application.Services.Email;

public interface IEmailService
{
    Task<bool> SendEmailAsync(EmailSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Bcc.Length != 0)
            throw new NotSupportedException("This email service does not support Bcc recipients.");
        return SendEmailAsync(request.Recipients, request.Subject, request.HtmlMessage,
            request.Cc, ct, request.TicketId, request.Attachments, request.FromName,
            request.ReplyTo, request.SuppressTimeline);
    }

    Task<bool> SendEmailAsync(
        string recipient,
        string subject,
        string htmlMessage,
        IEnumerable<string>? cc = null,
        CancellationToken ct = default,
        string? ticketId = null,
        IEnumerable<EmailAttachmentData>? attachments = null,
        string? fromName = null,
        string? replyTo = null,
        bool suppressTimeline = false);

    Task<bool> SendEmailAsync(
        IEnumerable<string> recipients,
        string subject,
        string htmlMessage,
        IEnumerable<string>? cc = null,
        CancellationToken ct = default,
        string? ticketId = null,
        IEnumerable<EmailAttachmentData>? attachments = null,
        string? fromName = null,
        string? replyTo = null,
        bool suppressTimeline = false);

    Task<bool> TestApiConnectionAsync();
}

public sealed record EmailSendRequest(string[] Recipients, string Subject, string HtmlMessage)
{
    public string[] Cc { get; init; } = [];
    public string[] Bcc { get; init; } = [];
    public string? TicketId { get; init; }
    public EmailAttachmentData[] Attachments { get; init; } = [];
    public string? FromName { get; init; }
    public string? ReplyTo { get; init; }
    public bool SuppressTimeline { get; init; }
}

/// <summary>Indicates that a successful send call means durable queue acceptance.</summary>
public interface IDurableEmailService
{
    bool QueuesDelivery { get; }
}
