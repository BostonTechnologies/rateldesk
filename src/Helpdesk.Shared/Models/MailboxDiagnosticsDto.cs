namespace Helpdesk.Shared.Models;

public sealed record MailboxWorkerStatus(bool DeploymentPermitsIngestion, bool InstanceRunning,
    string State, string? BlockedBy, long? ControlVersion, long? LastHeartbeatUnixMilliseconds);

public sealed record MailboxIngestionDiagnosticsDto(Guid MailboxId, bool Initialized,
    long? LastTestUnixMilliseconds, long? TestedVersion, long? LastSyncUnixMilliseconds,
    long? NextRetryUnixMilliseconds, string? ErrorCode, bool HasCheckpoint,
    long SyncRequestedVersion, long SyncCompletedVersion,
    long? LastSyncCommandUnixMilliseconds, string? LastSyncCommandErrorCode,
    long? BaselineCompletedUnixMilliseconds, long? LastAttemptUnixMilliseconds, string CurrentStage);

public sealed record MailboxSyncRequestResult(Guid MailboxId, long RequestVersion, string Status);

public sealed record MailboxReceiptDiagnosticsDto(Guid Id, InboundReceiptOutcome Outcome,
    string? Reason, string? OrganizationId, string? TicketId, long CreatedUnixMilliseconds,
    int Attempts, bool Acknowledged);

public sealed record MailboxEffectiveStatusDto(Guid MailboxId, string State, string? BlockedBy,
    bool DeploymentPermitsIngestion, bool InstanceRunning, bool MailboxEnabled, bool IncomingEnabled,
    bool BaselineComplete, bool LeaseActive, long? LastSuccessfulPollUnixMilliseconds,
    long? NextRetryUnixMilliseconds, int CapturedCount, int SkippedInitialCount,
    int HeldCount);

public sealed record MailboxDiagnosticsResponse(MailboxIngestionDiagnosticsDto? State,
    IReadOnlyList<MailboxReceiptDiagnosticsDto> Receipts, MailboxEffectiveStatusDto? EffectiveStatus);
