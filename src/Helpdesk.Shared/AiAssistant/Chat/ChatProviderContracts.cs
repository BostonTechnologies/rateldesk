namespace Helpdesk.Shared.AiAssistant.Chat;

public enum ChatProviderKind { Netclaw = 0, OpenAiAgents = 1, CodexAppServer = 2 }
public enum ChatProviderEnvironment { None, OpenAiHosted }
public enum OpenAiKeyAction { Keep, Replace, Clear, UseDeploymentReference }
public enum OpenAiKeySource { None, Web, Deployment }

public sealed class OpenAiProjectKeyChange
{
    public OpenAiKeyAction Action { get; init; }
    public string? ApiKey { get; init; }
}

public sealed record ChatProviderProfile(Guid Id, ChatProviderKind Kind, string Name, bool Enabled,
    bool IsDefault, string? Model, string? OrganizationId, int Revision, ChatProviderEnvironment Environment,
    DateTimeOffset? LastVerifiedAtUtc = null, string? LastVerification = null, bool ManagedByDeployment = false);

public sealed record OpenAiChatProfileSettings(string Name, bool Enabled, string OrganizationId, string Model,
    string KeyReference, string? Project, string? OpenAiOrganization, string? Instructions,
    string McpResource, string[] ReadOperations, bool ApproveInternalWorklogs,
    ChatProviderEnvironment Environment, string? EnvironmentTemplateId, string? SkillBundle,
    int ConcurrentSessions = 4, int ObservationMinutes = 30, int SubagentLimit = 4)
{
    public Guid? CodexConnectionId { get; init; }
    public string? ReasoningEffort { get; init; }
}

public sealed record OpenAiChatProfileUpdate(Guid Id, int ExpectedRevision, OpenAiChatProfileSettings Settings)
{
    public OpenAiProjectKeyChange? Credential { get; init; }
    public byte[]? SkillArchive { get; init; }
    public bool ClearSkillArchive { get; init; }
}

public sealed record OpenAiChatProfileEditor(ChatProviderProfile Profile, OpenAiChatProfileSettings Settings)
{
    public OpenAiKeySource KeySource { get; init; }
    public string? UploadedSkillSha256 { get; init; }
}

public sealed record OpenAiProjectCatalogRequest(int ExpectedRevision, string OrganizationId,
    string? Project, string? OpenAiOrganization, string? KeyReference, OpenAiProjectKeyChange? Credential);
public sealed record OpenAiModelChoice(string Id, string Name, string[] ReasoningEfforts);
public sealed record OpenAiModelCatalog(DateTimeOffset FetchedAtUtc, OpenAiModelChoice[] Models);
public sealed record AiProviderSetupOptions(string[] KeyReferences, string[] McpResources, bool DefaultManagedByDeployment, bool CodexDeviceConfigured = false)
{
    public string[] SkillReferences { get; init; } = [];
}
public enum CodexConnectionState { Pending, Connected, Cancelled, Expired, Failed, Disconnected, PlanAccessUnavailable, ReauthenticationRequired }

public sealed record CodexConnectionView(Guid Id, string OrganizationId, CodexConnectionState State, string? AccountLabel,
    string? Plan, DateTimeOffset? ExpiresAtUtc, string? VerificationUrl = null, string? UserCode = null, string? Detail = null);
public sealed record CodexConnectRequest(string OrganizationId);
public sealed record CodexProfileUpdate(Guid Id, int ExpectedRevision, Guid ConnectionId, string Name, bool Enabled,
    string OrganizationId, string Model, string? ReasoningEffort, string McpResource, string[] ReadOperations, int ObservationMinutes = 30);
public sealed record ChatProviderDefault(Guid ProfileId);
public sealed record ChatCancelRequest(Guid ConversationId, Guid ExpectedMessageId);
public sealed record ChatProviderArtifact(Guid Id, string Name, long SizeBytes, string TurnId);
public sealed record ChatProviderEventMetadata(int Version, ChatProviderKind Provider, Guid ProfileId,
    string SessionId, Guid? OperatorMessageId, string? TurnId = null, string? ItemId = null,
    string? SubagentId = null, string? Status = null, string? CallId = null, string? Operation = null,
    DateTimeOffset? ObservedAtUtc = null, ChatProviderTurnUsage? Usage = null);
public sealed record ChatProviderTurnUsage(long InputTokens, long OutputTokens, long TotalTokens);
