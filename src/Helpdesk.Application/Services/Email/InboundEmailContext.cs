using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Services.Email;

public sealed record InboundEmailContext(
    string InternetMessageId,
    string? GraphMessageId,
    Guid? MailboxId,
    string? MailboxTenantId,
    string MailboxAddress,
    string FromEmail,
    string? FromDisplayName,
    IReadOnlyList<string> ToRecipients,
    IReadOnlyList<string> CcRecipients,
    string Subject,
    string HtmlBody,
    string TextBody,
    DateTimeOffset? ReceivedUtc,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<InboundEmailAttachmentContext> Attachments)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RepeatedHeaders { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    public string? SourceMessageKey { get; init; }
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    public string? ForwardedRequesterEmail { get; init; }
    public string? ForwardedRequesterName { get; init; }
    public string? ForwardedRequesterTenantId { get; init; }
}

public sealed record InboundEmailAttachmentContext(
    string Name,
    string ContentType,
    string? ContentId,
    byte[]? ContentBytes,
    string? AttachmentId = null,
    bool? IsInline = null);

public sealed record InboundEmailRuleProcessingResult(
    bool Handled,
    bool StopDefaultProcessing,
    Ticket? Ticket,
    string? HoldReason = null);

public sealed record ForwardedEmailParseResult(
    Helpdesk.Shared.Enums.ForwardedEmailParseStatus Status,
    string? OriginalFromEmail,
    string? OriginalFromDisplayName,
    string? OriginalTo,
    DateTimeOffset? OriginalDate,
    string? OriginalSubject,
    string? OriginalBodyHtml,
    string? OriginalBodyText,
    double Confidence)
{
    public bool HasConfidentRequester =>
        Status == Helpdesk.Shared.Enums.ForwardedEmailParseStatus.Parsed &&
        Confidence >= 0.7 &&
        !string.IsNullOrWhiteSpace(OriginalFromEmail);
}
