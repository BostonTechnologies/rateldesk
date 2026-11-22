using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

internal sealed record ChatReceiverHandoff(Guid PreviousMessageId);

// The conversation row lock serializes every writer. A single durable checkpoint
// carries older registrations independently of the currently admitted message.
internal static class ChatScheduledObservationLedger
{
    private const string Checkpoint = "scheduled_report_bindings";

    public static async Task<ChatScheduledBinding[]> LoadAsync(HelpdeskDbContext db,
        AiAssistantChatConversation conversation, CancellationToken ct)
    {
        var json = await db.Set<AiAssistantChatEvent>().AsNoTracking()
            .Where(item => item.ConversationId == conversation.Id && item.Type == Checkpoint && item.Sequence <= conversation.LastSequence)
            .OrderByDescending(item => item.Sequence).Select(item => item.MetadataJson).FirstOrDefaultAsync(ct);
        return json is null ? [] : JsonSerializer.Deserialize<ChatScheduledBinding[]>(json) ?? [];
    }

    public static void Save(HelpdeskDbContext db, AiAssistantChatConversation conversation,
        IEnumerable<ChatScheduledBinding> bindings, DateTimeOffset now)
        => AiAssistantChatStore.Append(db, conversation, Checkpoint, "Scheduled report observation updated.",
            atUtc: now, metadata: JsonSerializer.Serialize(bindings));

    public static async Task<bool> CanReplyToScheduledParentAsync(HelpdeskDbContext db,
        AiAssistantChatConversation conversation, ChatTurnCompletion? parent, CancellationToken ct)
    {
        if (conversation.State is not (ChatState.WaitingForScheduledReports or ChatState.DeliveryUnknown)
            || parent is null || !ChildrenSettled(parent)) return false;
        if (conversation.State == ChatState.WaitingForScheduledReports && parent.SourceReminderId is null) return AdmitsReply(parent);
        if (conversation.State == ChatState.DeliveryUnknown && !parent.ScheduledWorkUnconfirmed) return false;
        if (await db.Set<AiAssistantChatInteraction>().AnyAsync(item =>
            item.ConversationId == conversation.Id && item.SelectedKey == null, ct)) return false;
        // A saved decision after the terminal can itself have an uncertain RPC outcome.
        // Never use the earlier completed parent to admit a reply across that boundary.
        var events = db.Set<AiAssistantChatEvent>().Where(item =>
            item.ConversationId == conversation.Id && item.Sequence <= conversation.LastSequence);
        var admission = events.Where(item => item.Type == "operator" && item.ClientMessageId == conversation.ActiveMessageId);
        var terminals = events.Where(item => item.Type == "turn_completed" && item.CallId == null
            && item.Sequence > admission.Max(input => (long?)input.Sequence))
            .OrderByDescending(item => item.Sequence).Select(item => new { item.Sequence, item.MetadataJson });
        long? foregroundTerminal = null;
        await foreach (var terminal in terminals.AsAsyncEnumerable().WithCancellation(ct))
        {
            var confirmed = ChatContinuationSafety.Read(terminal.MetadataJson);
            if (confirmed is null || confirmed.OperatorMessageId != conversation.ActiveMessageId) return false;
            if (confirmed.SourceReminderId is not null) continue;
            if (AdmitsReply(confirmed)) foregroundTerminal = terminal.Sequence;
            break;
        }
        return foregroundTerminal.HasValue && !await events.AnyAsync(item =>
            (item.Type == "approval_request" || item.Type == "approval_response") && item.Sequence > foregroundTerminal.Value, ct);
    }

    private static bool ChildrenSettled(ChatTurnCompletion completion) => completion.ChildSnapshotKnown
        && (completion.PendingChildRunIds ?? completion.OpenChildRunIds) is { Length: 0 };

    public static bool AdmitsReply(ChatTurnCompletion? completion) =>
        completion is { TurnOutcome: "completed" } && ChildrenSettled(completion);
}
