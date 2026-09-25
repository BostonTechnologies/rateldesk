namespace Helpdesk.Shared.Models;

public sealed record MailboxRouteRetryPreview(Guid TimelineId, bool CanRetry, string Status,
    Guid? OriginalMailboxId, string? OriginalMailboxAddress, Guid? CurrentMailboxId,
    string? CurrentMailboxAddress, MailboxOutgoingTransport? CurrentTransport,
    string[] Recipients, long? Fence, long? CurrentMailboxVersion, long? CurrentOutgoingVersion);

public sealed record ConfirmMailboxRouteRetryRequest(long ExpectedFence, Guid ExpectedOriginalMailboxId,
    Guid ExpectedCurrentMailboxId, long ExpectedMailboxVersion, long ExpectedOutgoingVersion,
    bool ConfirmedNewSender);
