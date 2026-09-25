namespace Helpdesk.Shared.Models;

public sealed class InboundMessageReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MailboxId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string TransportKey { get; set; } = string.Empty;
    public long ConfigurationVersion { get; set; }
    public string? InternetMessageId { get; set; }
    public string? OrganizationId { get; set; }
    public string? TicketId { get; set; }
    public InboundReceiptOutcome Outcome { get; set; }
    public string? Reason { get; set; }
    public bool Acknowledged { get; set; }
    public InboundAcknowledgmentStatus AcknowledgmentStatus { get; set; }
    public Guid? AcknowledgmentClaimId { get; set; }
    public long? AcknowledgmentClaimExpiresUnixMilliseconds { get; set; }
    public int AcknowledgmentAttempts { get; set; }
    public long? AcknowledgmentNextRetryUnixMilliseconds { get; set; }
    public string? AcknowledgmentErrorCode { get; set; }
    public string AcknowledgmentTargetFingerprint { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public long CreatedUnixMilliseconds { get; set; }
    public long UpdatedUnixMilliseconds { get; set; }
    public string ProtectedEnvelope { get; set; } = string.Empty;
    public Guid? HistoricalImportRequestId { get; set; }
    public long? HistoricalImportRequestedUnixMilliseconds { get; set; }
    public long? HistoricalImportCompletedUnixMilliseconds { get; set; }
    public string? HistoricalImportErrorCode { get; set; }
}

public sealed class MailboxIngestionState
{
    public Guid MailboxId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string? Cursor { get; set; }
    public bool Initialized { get; set; }
    public long? BaselineCompletedUnixMilliseconds { get; set; }
    public long? LastAttemptUnixMilliseconds { get; set; }
    public string CurrentStage { get; set; } = "Never";
    public long? LastTestUnixMilliseconds { get; set; }
    public long? TestedVersion { get; set; }
    public long? LastSyncUnixMilliseconds { get; set; }
    public long? NextRetryUnixMilliseconds { get; set; }
    public string? ErrorCode { get; set; }
    public long SyncRequestedVersion { get; set; }
    public long SyncCompletedVersion { get; set; }
    public long? LastSyncCommandUnixMilliseconds { get; set; }
    public string? LastSyncCommandErrorCode { get; set; }
}
