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
    /// <summary>The existing delivery history owned by this request; queue acceptance must not complete it.</summary>
    public Guid? TimelineDeliveryId { get; init; }
    public string? WorkLogId { get; init; }
    /// <summary>Reject a direct operator send before queueing when no sender is ready.</summary>
    public bool RequireReadySender { get; init; }
}

public sealed class EmailSenderUnavailableException(string reasonCode) : Exception("No ready sender is available for this direct email.")
{
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>Indicates that a successful send call means durable queue acceptance.</summary>
public interface IDurableEmailService
{
    bool QueuesDelivery { get; }

    /// <summary>
    /// Reconciles an interrupted send against the exact history owner. Null means
    /// ownership cannot be established, so callers must retain the current history.
    /// </summary>
    Task<bool?> OwnsDeliveryHistoryAsync(Guid deliveryEventId, CancellationToken ct = default) =>
        Task.FromResult<bool?>(null);
}
