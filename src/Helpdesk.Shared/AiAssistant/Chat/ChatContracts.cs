namespace Helpdesk.Shared.AiAssistant.Chat;

public enum ChatState { Idle, Processing, AwaitingApproval, DeliveryUnknown, Archived }

public sealed class AiAssistantChatConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrganizationId { get; set; } = string.Empty;
    public string TicketId { get; set; } = string.Empty;
    public string TicketType { get; set; } = string.Empty;
    public string? AiAssistantSessionId { get; set; }
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
public sealed record ChatSnapshot(Guid ConversationId, ChatState State, long LastSequence, IReadOnlyList<AiAssistantChatEvent> Events);
public sealed record ChatOption(string Key, string Label);
public sealed record ChatActivityMetadata(string DisplayName, string ToolKind, string? ProviderName,
    string? SkillName, long? DurationMs, string Outcome);
public sealed record ChatDelta(Guid ConversationId, string? Text, long AfterSequence = 0);
public sealed record ChatAbandonRequest(Guid ConversationId, Guid ResolutionId, Guid ExpectedMessageId, bool AcknowledgePossibleDelivery);
public sealed record ChatStopWaitingRequest(Guid ConversationId, Guid ExpectedMessageId);
public sealed record ChatConversationSummary(Guid ConversationId, ChatState State, DateTimeOffset LastActivityUtc, string CreatedByUserId);

public sealed record ChatCapabilities(bool Enabled, string? Reason);
