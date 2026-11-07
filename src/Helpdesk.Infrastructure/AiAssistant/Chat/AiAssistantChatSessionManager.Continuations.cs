using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Helpdesk.Infrastructure.Persistence;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

public sealed partial class AiAssistantChatSessionManager
{
    private static string[] ChildIds(ChatTurnCompletion? completion) => completion?.PendingChildRunIds ?? completion?.OpenChildRunIds ?? [];
    private static bool HasChildren(ChatTurnCompletion? completion) => ChildIds(completion).Length > 0;

    internal static async Task<ChatTurnCompletion?> LatestCompletionAsync(
        HelpdeskDbContext db, AiAssistantChatConversation conversation, CancellationToken ct)
    {
        var json = await db.Set<AiAssistantChatEvent>().AsNoTracking()
            .Where(x => x.ConversationId == conversation.Id && x.Type == "turn_completed" && x.Sequence <= conversation.LastSequence)
            .OrderByDescending(x => x.Sequence).Select(x => x.MetadataJson).FirstOrDefaultAsync(ct);
        var completion = ChatContinuationSafety.Read(json);
        return completion?.OperatorMessageId == conversation.ActiveMessageId ? completion : null;
    }

    private async Task<bool> CompleteNativeTurnAsync(HelpdeskDbContext db, AiAssistantChatConversation conversation,
        Owner owner, JsonElement output, CancellationToken ct)
    {
        var completion = ObserveCompletion(conversation, owner, output);
        if (completion is null) return false;
        if (owner.Draft.Length > 0)
            AppendReply(db, conversation, owner, owner.Draft.ToString());
        var approvals = await db.Set<AiAssistantChatInteraction>()
            .AnyAsync(x => x.ConversationId == conversation.Id && x.SelectedKey == null, ct);
        conversation.State = approvals ? ChatState.AwaitingApproval
            : HasChildren(completion) ? ChatState.WaitingForChildren : ChatState.Idle;
        AiAssistantChatStore.Append(db, conversation, "turn_completed", CompletionDescription(completion), atUtc: Now);
        db.Set<AiAssistantChatEvent>().Local.Single(x => x.ConversationId == conversation.Id && x.Sequence == conversation.LastSequence)
            .MetadataJson = JsonSerializer.Serialize(completion);
        owner.Continuation = completion;
        owner.LastCompletedTurn = completion.TurnNumber ?? owner.LastCompletedTurn;
        conversation.RecoveryResult = CompletionRecovery(completion);
        // Mark terminal owners before committing Idle, so an immediately admitted
        // newer message cannot reuse the old subscription during its disposal.
        owner.Retired = conversation.State is ChatState.Idle or ChatState.DeliveryUnknown;
        return true;
    }

    private ChatTurnCompletion? ObserveCompletion(AiAssistantChatConversation conversation, Owner owner, JsonElement output)
    {
        var previous = owner.Continuation;
        var completion = ChatContinuationSafety.Parse(output, owner.MessageId
            ?? throw new InvalidOperationException("Native output has no admitted operator request."), previous?.ObservationExpiresAtUtc);
        // Completion ordinals are native identities, unlike text. Equal answers
        // in two different turns are legitimate and must both remain in history.
        if (completion.TurnNumber is { } ordinal && owner.LastCompletedTurn is { } last && ordinal <= last)
            return null;
        if (HasChildren(previous) && completion.TurnNumber is null)
            throw new InvalidOperationException("A child follow-up omitted its native turn identity.");
        completion = PreserveUnresolvedChildren(completion, previous);
        return HasChildren(completion) ? completion with
        {
            ObservationExpiresAtUtc = completion.ObservationExpiresAtUtc
                ?? (conversation.TurnStartedAtUtc ?? Now) + ChatContinuationSafety.ObservationLimit
        } : completion;
    }

    private static ChatTurnCompletion PreserveUnresolvedChildren(ChatTurnCompletion completion, ChatTurnCompletion? previous)
    {
        if (HasChildren(previous) && (!completion.ChildSnapshotKnown || completion.TurnOutcome == "failed"))
        {
            var unresolved = ChildIds(previous).Union(ChildIds(completion), StringComparer.Ordinal).ToArray();
            if (unresolved.Length > ChatContinuationSafety.MaximumChildRuns)
                throw new InvalidOperationException("Unresolved native child runs exceeded the observation budget.");
            completion = completion with { PendingChildRunIds = unresolved };
        }
        return completion;
    }

    private static string CompletionDescription(ChatTurnCompletion completion)
    {
        var description = completion.TurnOutcome == "failed" ? "Native turn failed."
            : completion.QuietEnd ? "Native turn deliberately ended without a reply."
            : completion.TurnOutcome == "skipped" ? "Native turn was skipped." : "Native turn completed.";
        if (HasChildren(completion)) description += $" {ChildIds(completion).Length} child follow-up(s) remain unresolved.";
        return description;
    }

    private static string? CompletionRecovery(ChatTurnCompletion completion)
    {
        if (!HasChildren(completion)) return null;
        return completion.TurnOutcome == "failed" || !completion.ChildSnapshotKnown
            ? "A native turn failed or omitted its child snapshot. Child follow-ups remain unresolved; RatelDesk keeps observing the original request until its deadline. Nothing is rerun."
            : $"Waiting for {ChildIds(completion).Length} delegated follow-up(s) on the original request. Observation ends at {completion.ObservationExpiresAtUtc:O}; no new message is sent.";
    }

    private void AppendReply(HelpdeskDbContext db, AiAssistantChatConversation conversation, Owner owner, string text)
    {
        AiAssistantChatStore.Append(db, conversation, "assistant", Bounded(text, 128000), atUtc: Now);
        db.Set<AiAssistantChatEvent>().Local.Single(x => x.ConversationId == conversation.Id && x.Sequence == conversation.LastSequence)
            .MetadataJson = JsonSerializer.Serialize(new ChatOutputAssociation(owner.MessageId
                ?? throw new InvalidOperationException("Native reply has no admitted operator request."), owner.LastCompletedTurn));
    }

    internal async Task SweepPendingChildrenAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var attachedIds = owners.Keys.ToArray();
        var candidates = await db.Set<AiAssistantChatConversation>().AsNoTracking()
            .Where(x => x.State == ChatState.WaitingForChildren || x.State == ChatState.AwaitingApproval
                || x.State == ChatState.Processing || x.State == ChatState.DeliveryUnknown && attachedIds.Contains(x.Id))
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var id in candidates)
        {
            var retire = false;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var current = await db.Set<AiAssistantChatConversation>()
                    .FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
                var pending = await LatestCompletionAsync(db, current, ct);
                if (current.State is not (ChatState.WaitingForChildren or ChatState.AwaitingApproval or ChatState.Processing or ChatState.DeliveryUnknown) || !HasChildren(pending))
                    continue;
                var expired = pending!.ObservationExpiresAtUtc <= Now;
                owners.TryGetValue(id, out var owner);
                // An inconclusive reattachment still consumes an owner. Its
                // original deadline applies even though ordinary outputs remain
                // fenced by DeliveryUnknown. Avoid repeated uncertainty events
                // for already detached unresolved conversations.
                if (current.State == ChatState.DeliveryUnknown && (!expired || owner is null)) continue;
                if (!expired && IsObserving(owner)) continue;
                current.State = ChatState.DeliveryUnknown;
                current.RecoveryResult = expired
                    ? "The one-hour delegated-work observation limit expired. Child outcomes remain unknown; nothing was resent or cancelled remotely."
                    : "The delegated-work transport was interrupted. Child outcomes remain unknown; nothing was resent or cancelled remotely.";
                AiAssistantChatStore.Append(db, current, "delivery_unknown", current.RecoveryResult, atUtc: Now);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                retire = true;
            }
            if (retire) await RetireAsync(id, ct);
        }
    }

    private static bool IsObserving(Owner? owner) => owner is not null && !owner.Retired && !owner.Consumer.IsCompleted
        && owner.Client.IsConnected && !owner.Overflowed;
}
