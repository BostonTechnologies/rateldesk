namespace Helpdesk.Shared.AiAssistant.Chat;

public enum ChatState { Idle, Processing, AwaitingApproval, DeliveryUnknown, Archived, WaitingForChildren, WaitingForScheduledReports }
public enum ChatConversationScope { Ticket, Organization }

public sealed class AiAssistantChatConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsInvestigation { get; set; }
    public ChatConversationScope Scope { get; set; }
    public string Title { get; set; } = "";
    public string? GuidanceSnapshot { get; set; }
    public string? CreationFingerprint { get; set; }
    public DateTimeOffset? RecoveryLastAttemptUtc { get; set; }
    public string? RecoveryResult { get; set; }
    public string OrganizationId { get; set; } = string.Empty;
    public string? TicketId { get; set; }
    public string? TicketType { get; set; }
    public string? AiAssistantSessionId { get; set; }
    public ChatProviderKind ProviderKind { get; set; }
    public Guid? ProviderProfileId { get; set; }
    public int? ProviderRevision { get; set; }
    public string? ProviderActorId { get; set; }
    public string? ProviderProfileFingerprint { get; set; }
    public ChatState State { get; set; }
    public long LastSequence { get; set; }
    public Guid? ActiveMessageId { get; set; }
    public DateTimeOffset LastActivityUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? TurnStartedAtUtc { get; set; }
    public DateTimeOffset? LastTransportActivityAtUtc { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
}

public sealed class AiAssistantChatEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public long Sequence { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public Guid? ClientMessageId { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? CallId { get; set; }
    public string? OptionsJson { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AiAssistantChatInteraction
{
    public Guid ConversationId { get; set; }
    public string CallId { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = "[]";
    public string? SelectedKey { get; set; }
    public string? AnsweredByUserId { get; set; }
}

public sealed record ChatMessageRequest(Guid ConversationId, Guid ClientMessageId, string Text);
public sealed record ChatApprovalRequest(Guid ConversationId, string SelectedKey);
public sealed record ChatNewRequest(Guid ConversationId);
public sealed record ChatNewWithProfileRequest(Guid ConversationId, Guid? ProviderProfileId = null);
public sealed record ChatSnapshot(Guid ConversationId, ChatState State, long LastSequence, IReadOnlyList<AiAssistantChatEvent> Events,
    DateTimeOffset? RecoveryLastAttemptUtc = null, string? RecoveryResult = null, ChatTurnCompletion? Continuation = null,
    Guid? ActiveMessageId = null, ChatProviderKind Provider = ChatProviderKind.Netclaw, Guid? ProviderProfileId = null,
    ChatScheduledBinding[]? BackgroundReports = null);
// Association lives in event metadata, not ClientMessageId: that column identifies
// an admission exactly once through its unique (conversation, message) index.
public sealed record ChatTurnCompletion(Guid OperatorMessageId, int? TurnNumber, string? TurnOutcome,
    bool QuietEnd, string[]? OpenChildRunIds, DateTimeOffset? ObservationExpiresAtUtc = null, int Version = 1,
    bool ChildSnapshotKnown = true, string[]? PendingChildRunIds = null,
    ChatScheduledReport[]? ScheduledReports = null, bool ScheduledWorkUnconfirmed = false, string? SourceReminderId = null);
public sealed record ChatScheduledBinding(DateTimeOffset? ParentStartedAtUtc, ChatTurnCompletion Parent);
public sealed record ChatScheduledReport(string Id, bool Recurring, DateTimeOffset? DueAtUtc,
    long? LastFireAtUnixMs = null, bool Unconfirmed = false);
public sealed record ChatOutputAssociation(Guid OperatorMessageId, int? AfterNativeTurnNumber,
    int? NativeTurnNumber = null, string? SourceReminderId = null);
public sealed record ChatOption(string Key, string Label);
public sealed record ChatActivityMetadata(string DisplayName, string ToolKind, string? ProviderName,
    string? SkillName, long? DurationMs, string Outcome,
    ChatTraceDetails? Details = null, ChatProviderEventMetadata? ProviderEvent = null, ChatOutputAssociation? Association = null,
    ChatChildObservation? Child = null);
public sealed record ChatChildObservation(string? RunId, string? Name, string? State, string? Summary = null, long? DurationMs = null);
public sealed record ChatTraceDetails(int Version = 1, DateTimeOffset? SourceTimestampUtc = null,
    string? Arguments = null, string? Result = null, string? FailureCode = null,
    bool Redacted = false, bool Truncated = false, string? Measures = null, string? ToolName = null);
public sealed record ChatDelta(Guid ConversationId, string? Text, long AfterSequence = 0, Guid? OperatorMessageId = null);
public sealed record ChatAbandonRequest(Guid ConversationId, Guid ResolutionId, Guid ExpectedMessageId, bool AcknowledgePossibleDelivery);
public sealed record ChatStopWaitingRequest(Guid ConversationId, Guid ExpectedMessageId, bool ScheduledOnly = false);
public sealed record ChatConversationSummary(Guid ConversationId, ChatState State, DateTimeOffset LastActivityUtc, string CreatedByUserId,
    string? Title = null, string? OrganizationId = null);
public sealed record ChatCreateRequest(Guid ConversationId, string Title, Guid[] PromptIds, Guid? ProviderProfileId = null);
public sealed record ChatStartRequest(Guid ConversationId, Guid ClientMessageId, string Text, Guid[] PromptIds, Guid? ProviderProfileId = null);
public sealed record ChatOrganizationConversation(Guid ConversationId, string OrganizationId, string Title, string GuidanceSnapshot, ChatState State, ChatProviderKind Provider = ChatProviderKind.Netclaw, Guid? ProviderProfileId = null);

public sealed record ChatCapabilities(bool Enabled, string? Reason, bool CanWrite = true, string? SetupUrl = null, IReadOnlyList<ChatProviderProfile>? Profiles = null, bool CanViewHistory = false);
public enum ChatTransportConnectionState { Unknown, Connected, Disconnected }
public sealed record ChatSessionConnection(Guid ConversationId, string SessionId, ChatTransportConnectionState State,
    DateTimeOffset AuthenticatedAtUtc, DateTimeOffset? LastActivityUtc);
public sealed record ChatConnectionStatus(string OrganizationId, bool Enabled, string? Endpoint, string Source, int Revision, DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded, DateTimeOffset ObservedAtUtc, bool ObservationAvailable,
    IReadOnlyList<ChatSessionConnection> Sessions, string? SetupUrl, ChatProviderProfile? Profile = null);
