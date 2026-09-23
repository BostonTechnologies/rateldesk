namespace Helpdesk.Shared.Models;

public sealed record MailboxOutgoingRetryPreview(Guid TimelineId, bool CanRetry, string Status,
    Guid? MailboxId, string? MailboxAddress, MailboxOutgoingTransport? Transport,
    long? OriginalOutgoingVersion, long? CurrentOutgoingVersion);

public sealed record ConfirmMailboxOutgoingRetryRequest(long ExpectedOutgoingVersion, bool Confirmed);
