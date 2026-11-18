namespace Helpdesk.Shared.AiAssistant.Chat;

public enum ChatProviderKind { Netclaw, OpenAiAgents }
public enum ChatProviderEnvironment { None, OpenAiHosted }

public sealed record ChatProviderProfile(Guid Id, ChatProviderKind Kind, string Name, bool Enabled,
    bool IsDefault, string? Model, string? OrganizationId, int Revision, ChatProviderEnvironment Environment,
    DateTimeOffset? LastVerifiedAtUtc = null, string? LastVerification = null, bool ManagedByDeployment = false);

public sealed record OpenAiChatProfileSettings(string Name, bool Enabled, string OrganizationId, string Model,
    string KeyReference, string? Project, string? OpenAiOrganization, string? Instructions,
    string McpResource, string[] ReadOperations, bool ApproveInternalWorklogs,
    ChatProviderEnvironment Environment, string? EnvironmentTemplateId, string? SkillBundle,
    int ConcurrentSessions = 4, int ObservationMinutes = 30, int SubagentLimit = 4);

public sealed record OpenAiChatProfileUpdate(Guid Id, int ExpectedRevision, OpenAiChatProfileSettings Settings);
public sealed record OpenAiChatProfileEditor(ChatProviderProfile Profile, OpenAiChatProfileSettings Settings);
public sealed record ChatProviderDefault(Guid ProfileId);
public sealed record ChatCancelRequest(Guid ConversationId, Guid ExpectedMessageId);
public sealed record ChatProviderArtifact(Guid Id, string Name, long SizeBytes, string TurnId);
public sealed record ChatProviderEventMetadata(int Version, ChatProviderKind Provider, Guid ProfileId,
    string SessionId, Guid? OperatorMessageId, string? TurnId = null, string? ItemId = null,
    string? SubagentId = null, string? Status = null, string? CallId = null, string? Operation = null,
    DateTimeOffset? ObservedAtUtc = null, ChatProviderTurnUsage? Usage = null);
public sealed record ChatProviderTurnUsage(long InputTokens, long OutputTokens, long TotalTokens);
