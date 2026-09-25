namespace Helpdesk.Shared.Models;

public sealed record MailboxUncertainRetryPreview(Guid TimelineId, bool CanRetry, string Status,
    Guid? MailboxId, string? MailboxAddress, MailboxOutgoingTransport? Transport,
    string[] Recipients, long? Fence, long? OriginalOutgoingVersion, long? CurrentOutgoingVersion);

public sealed record ConfirmMailboxUndeliveredRetryRequest(long ExpectedFence, long ExpectedOutgoingVersion,
    string EvidenceReference, bool ConfirmedNoDelivery);
