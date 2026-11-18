using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Application.AiAssistant.Chat;

public interface IChatProviderProfiles
{
    Task<IReadOnlyList<ChatProviderProfile>> AvailableAsync(string organization, CancellationToken ct);
    Task BindNewAsync(AiAssistantChatConversation conversation, Guid? selectedProfileId, CancellationToken ct);
    Task EnsureActorAsync(AiAssistantChatConversation conversation, string actor, CancellationToken ct);
    Task EnsureAdmissionAsync(AiAssistantChatConversation conversation, string actor, CancellationToken ct);
}

public interface IChatProviderMcpAuthority
{
    Task<string> ResolveOperatorAsync(string organization, CancellationToken ct);
    Task<bool> IsGrantUsableAsync(AiAssistantChatConversation conversation, Guid credentialId, CancellationToken ct);
    Task<ProviderDecision> WriteInternalNoteAsync(AiAssistantChatConversation conversation, Guid credentialId,
        string incidentId, string note, CancellationToken ct);
    Task<IssuedChatMcpGrant> IssueAsync(AiAssistantChatConversation conversation, OpenAiChatProfileSettings profile,
        Guid credentialId, CancellationToken ct);
    Task RevokeAsync(Guid credentialId, CancellationToken ct);
}

public sealed class IssuedChatMcpGrant(string token)
{
    public string Token { get; } = token;
}
