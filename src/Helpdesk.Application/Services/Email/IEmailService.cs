namespace Helpdesk.Application.Services.Email;

public interface IEmailService
{
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

/// <summary>Indicates that a successful send call means durable queue acceptance.</summary>
public interface IDurableEmailService
{
    bool QueuesDelivery { get; }
}
