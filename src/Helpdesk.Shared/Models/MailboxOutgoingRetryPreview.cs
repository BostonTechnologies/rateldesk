namespace Helpdesk.Shared.Models;

public sealed record MailboxOutgoingRetryPreview(Guid TimelineId, bool CanRetry, string Status,
    Guid? MailboxId, string? MailboxAddress, MailboxOutgoingTransport? Transport,
    long? OriginalOutgoingVersion, long? CurrentOutgoingVersion)
{
    public long? OriginalMailboxVersion { get; init; }
    public long? CurrentMailboxVersion { get; init; }
    public long? Fence { get; init; }
    public string[] Recipients { get; init; } = [];
}

public sealed record ConfirmMailboxOutgoingRetryRequest(long ExpectedOutgoingVersion, bool Confirmed)
{
    public Guid? ExpectedMailboxId { get; init; }
    public long? ExpectedMailboxVersion { get; init; }
    public long? ExpectedFence { get; init; }
}
