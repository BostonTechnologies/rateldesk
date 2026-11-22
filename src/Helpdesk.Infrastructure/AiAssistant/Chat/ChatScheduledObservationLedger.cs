using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
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

    public static bool AdmitsReply(ChatTurnCompletion? completion) =>
        completion is { TurnOutcome: "completed", ChildSnapshotKnown: true }
        && (completion.PendingChildRunIds ?? completion.OpenChildRunIds) is { Length: 0 };
}
