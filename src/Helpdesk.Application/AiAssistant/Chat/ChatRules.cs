using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Application.AiAssistant.Chat;

public sealed class ChatConflictException(string message) : Exception(message);

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
    Task ReconfigureAsync(CancellationToken ct);
    Task ReconfigureAsync(AiAssistantChatRuntimeSnapshot snapshot, CancellationToken ct)
        => ReconfigureAsync(ct);
    Task ReconcileAsync(Guid conversation, CancellationToken ct);
    Task RetireAsync(Guid conversation, CancellationToken ct);
    Task SendAsync(Guid conversation, Guid messageId, string text, CancellationToken ct);
    Task RespondAsync(Guid conversation, string callId, string key, CancellationToken ct);
}
