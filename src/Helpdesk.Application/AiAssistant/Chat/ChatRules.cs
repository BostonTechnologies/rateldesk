using Helpdesk.Shared.AiAssistant.Chat;
using System.Text;

namespace Helpdesk.Application.AiAssistant.Chat;

public sealed class ChatConflictException(string message) : Exception(message);
public sealed record ChatStartAdmission(ChatOrganizationConversation Conversation, bool Accepted);

public static class ChatRules
{
    public static void RequireIdle(ChatState state)
    {
        if (state != ChatState.Idle)
            throw new ChatConflictException("The conversation already has an active or unresolved turn.");
    }

    public static void ValidateMessage(ChatMessageRequest request)
    {
        if (request.ClientMessageId == Guid.Empty || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 16000)
            throw new ArgumentException("A message ID and between 1 and 16000 characters are required.");
    }

    public static string ConversationTitle(string message)
    {
        var normalized = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var runes = normalized.EnumerateRunes().Take(81).ToArray();
        return string.Concat(runes.Take(80).Select(rune => rune.ToString())) + (runes.Length > 80 ? "…" : "");
    }

    public static void ValidateApproval(AiAssistantChatInteraction interaction, string selectedKey, IReadOnlyList<ChatOption> options)
    {
        if (interaction.SelectedKey is not null)
            throw new ChatConflictException("This approval has already been answered.");
        if (!options.Any(x => string.Equals(x.Key, selectedKey, StringComparison.Ordinal)))
            throw new ArgumentException("Select one of the offered options.");
    }
}

public interface IAiAssistantChatStore
{
    Task<IReadOnlyList<ChatConversationSummary>> HistoryAsync(ChatScope scope, int skip, string? search, bool archived, CancellationToken ct);
    Task<ChatOrganizationConversation> CreateOrganizationAsync(string organization, ChatCreateRequest request, string actor, CancellationToken ct);
    Task<ChatStartAdmission> StartOrganizationAsync(string organization, ChatStartRequest request, string actor, CancellationToken ct)
        => throw new NotSupportedException("This chat store does not support atomic first-message admission.");
    Task<ChatOrganizationConversation> GetOrganizationAsync(string organization, Guid id, CancellationToken ct);
    Task ArchiveAsync(ChatScope scope, Guid id, string actor, CancellationToken ct);
    Task RequestRecoveryAsync(ChatScope scope, Guid conversation, string actor, CancellationToken ct);
    Task StopWaitingAsync(ChatScope scope, ChatStopWaitingRequest request, string actor, CancellationToken ct);
    Task AbandonAsync(ChatScope scope, ChatAbandonRequest request, string actor, CancellationToken ct);
    Task<ChatSnapshot> LoadAsync(ChatScope scope, Guid? conversation, long after, string actor, CancellationToken ct);
    Task<bool> AcceptMessageAsync(ChatScope scope, ChatMessageRequest request, string actor, CancellationToken ct);
    Task AcceptApprovalAsync(ChatScope scope, string callId, ChatApprovalRequest request, string actor, CancellationToken ct);
    Task<IReadOnlyList<ChatConversationSummary>> HistoryAsync(string type, string ticket, int skip, CancellationToken ct);
    Task RequestRecoveryAsync(string type, string ticket, Guid conversation, string actor, CancellationToken ct);
    Task StopWaitingAsync(string type, string ticket, ChatStopWaitingRequest request, string actor, CancellationToken ct);
    Task AbandonAsync(string type, string ticket, ChatAbandonRequest request, string actor, CancellationToken ct);
    Task<ChatSnapshot> LoadAsync(string type, string ticket, Guid? conversation, long after, string actor, CancellationToken ct);
    Task<bool> AcceptMessageAsync(string type, string ticket, ChatMessageRequest request, string actor, CancellationToken ct);
    Task AcceptApprovalAsync(string type, string ticket, string callId, ChatApprovalRequest request, string actor, CancellationToken ct);
    Task<ChatSnapshot> NewAsync(string type, string ticket, Guid conversation, string actor, CancellationToken ct);
}

public interface IAiAssistantChatTransport
{
    bool ConnectionObservationsAvailable => false;
    ChatSessionConnection? ObserveConnection(Guid conversation) => null;
    Task ReconfigureAsync(CancellationToken ct);
    Task ReconfigureAsync(AiAssistantChatRuntimeSnapshot snapshot, CancellationToken ct)
        => ReconfigureAsync(ct);
    async Task<bool> TryReconfigureAsync(AiAssistantChatRuntimeSnapshot snapshot, CancellationToken ct)
    {
        await ReconfigureAsync(snapshot, ct);
        return true;
    }
    Task ReconcileAsync(Guid conversation, CancellationToken ct);
    Task RetireAsync(Guid conversation, CancellationToken ct);
    Task SendAsync(Guid conversation, Guid messageId, string text, CancellationToken ct);
    Task RespondAsync(Guid conversation, string callId, string key, CancellationToken ct);
}
