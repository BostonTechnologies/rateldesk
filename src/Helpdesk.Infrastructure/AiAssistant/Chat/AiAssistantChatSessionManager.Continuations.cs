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
            .Where(x => x.ConversationId == conversation.Id && x.CallId == null && (x.Type == "turn_completed" || x.Type == ScheduledObservationEnded)
                && x.Sequence <= conversation.LastSequence)
            .OrderByDescending(x => x.Sequence).Select(x => x.MetadataJson).FirstOrDefaultAsync(ct);
        var completion = ChatContinuationSafety.Read(json);
        return completion?.OperatorMessageId == conversation.ActiveMessageId ? completion : null;
    }

    private async Task<bool> CompleteNativeTurnAsync(HelpdeskDbContext db, AiAssistantChatConversation conversation,
        Owner owner, NetclawOutput output, CancellationToken ct)
    {
        if (output.HasReminderSource && owner.BackgroundReports.Length > 0 &&
            CompleteBackgroundReport(db, conversation, owner, output))
            return true;
        var completion = ObserveCompletion(conversation, owner, output, out var rejection);
        if (completion is null)
        {
            ObserveLifecycle("terminal_rejected", conversation, owner, rejection);
            ResetTurnBuffers(owner);
            return false;
        }
        if (completion.SourceReminderId is not null)
        {
            if (completion.TurnOutcome == "completed" && !completion.QuietEnd && owner.HasFinalText)
                AppendReply(db, conversation, owner, owner.StagedReplies[^1], completion);
        }
        else
        {
            foreach (var reply in owner.StagedReplies)
                AppendReply(db, conversation, owner, reply, completion);
            if (owner.Draft.Length > 0)
                AppendReply(db, conversation, owner, owner.Draft.ToString(), completion);
        }
        var approvals = await db.Set<AiAssistantChatInteraction>()
            .AnyAsync(x => x.ConversationId == conversation.Id && x.SelectedKey == null, ct);
        conversation.State = approvals ? ChatState.AwaitingApproval : StateAfterCompletion(completion);
        if (conversation.State == ChatState.Idle && owner.BackgroundReports.Length > 0)
            conversation.State = ChatState.WaitingForScheduledReports;
        AiAssistantChatStore.Append(db, conversation, "turn_completed", CompletionDescription(completion),
            atUtc: Now, metadata: JsonSerializer.Serialize(completion));
        owner.Continuation = completion;
        owner.LastCompletedTurn = completion.TurnNumber ?? owner.LastCompletedTurn;
        conversation.RecoveryResult = CompletionRecovery(completion);
        // Mark terminal owners before committing Idle, so an immediately admitted
        // newer message cannot reuse the old subscription during its disposal.
        owner.Retired = conversation.State is ChatState.Idle or ChatState.DeliveryUnknown;
        return true;
    }

    private static ChatState StateAfterCompletion(ChatTurnCompletion completion) =>
        HasPendingReports(completion) ? ChatState.WaitingForScheduledReports
        : HasChildren(completion) ? ChatState.WaitingForChildren
        : completion.ScheduledWorkUnconfirmed ? ChatState.DeliveryUnknown : ChatState.Idle;

    private ChatTurnCompletion? ObserveCompletion(AiAssistantChatConversation conversation, Owner owner, NetclawOutput output,
        out string? rejection)
    {
        rejection = null;
        var previous = owner.Continuation;
        var completion = output.Complete(owner.MessageId
            ?? throw new InvalidOperationException("Native output has no admitted operator request."), previous?.ObservationExpiresAtUtc);
        if (completion.TurnNumber is { } ordinal && (owner.LastCompletedTurn ?? owner.BaselineTurn) is { } last && ordinal <= last)
        {
            rejection = "stale_ordinal";
            return null;
        }
        var matched = MatchScheduledReport(previous, output, conversation.TurnStartedAtUtc, out var fireAt);
        if (output.HasReminderSource && (matched is null || completion.TurnNumber is null || !owner.SendStarted))
        {
            rejection = "unmatched_reminder_fire";
            return null;
        }
        if (!output.HasReminderSource && HasScheduledWork(previous) && !HasChildren(previous)
            && conversation.State is ChatState.WaitingForScheduledReports or ChatState.DeliveryUnknown)
        {
            rejection = "missing_reminder_fire_key";
            return null;
        }
        if (HasPendingWork(previous) && completion.TurnNumber is null)
        {
            rejection = "missing_follow_up_ordinal";
            return null;
        }
        var deadline = completion.ObservationExpiresAtUtc
            ?? (conversation.TurnStartedAtUtc ?? Now) + ChatContinuationSafety.ObservationLimit;
        completion = CompleteScheduledWork(owner, completion, matched, fireAt, output.ReminderFireKey, deadline);
        completion = PreserveUnresolvedChildren(completion, previous);
        return HasPendingWork(completion) ? completion with { ObservationExpiresAtUtc = deadline } : completion;
    }

    private static ChatTurnCompletion PreserveUnresolvedChildren(ChatTurnCompletion completion, ChatTurnCompletion? previous)
    {
        if (HasChildren(previous) && (!completion.ChildSnapshotKnown || completion.TurnOutcome == "failed"
            || completion.SourceReminderId is not null))
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
        if (HasPendingReports(completion)) description += $" {completion.ScheduledReports!.Count(report => !report.Unconfirmed)} scheduled report(s) pending.";
        if (completion.ScheduledWorkUnconfirmed) description += " A scheduled report remains unconfirmed.";
        return description;
    }

    private static string? CompletionRecovery(ChatTurnCompletion completion)
    {
        if (HasPendingReports(completion))
            return $"The parent turn ended. RatelDesk is watching the original SignalR session for {completion.ScheduledReports!.Count(report => !report.Unconfirmed)} scheduled report(s) and {ChildIds(completion).Length} delegated follow-up(s) until {completion.ObservationExpiresAtUtc:O}."
                + (completion.ScheduledWorkUnconfirmed ? $" {ScheduledResultUnknown}" : "");
        if (completion.ScheduledWorkUnconfirmed) return ScheduledResultUnknown;
        if (!HasChildren(completion)) return null;
        return completion.TurnOutcome == "failed" || !completion.ChildSnapshotKnown
            ? "A native turn failed or omitted its child snapshot. Child follow-ups remain unresolved; RatelDesk keeps observing the original request until its deadline. Nothing is rerun."
            : $"Waiting for {ChildIds(completion).Length} delegated follow-up(s) on the original request. Observation ends at {completion.ObservationExpiresAtUtc:O}; no new message is sent.";
    }

    private void AppendReply(HelpdeskDbContext db, AiAssistantChatConversation conversation, Owner owner, string text,
        ChatTurnCompletion? completion = null)
    {
        var association = new ChatOutputAssociation(owner.MessageId
            ?? throw new InvalidOperationException("Native reply has no admitted operator request."), owner.LastCompletedTurn,
            completion?.TurnNumber, completion?.SourceReminderId);
        AiAssistantChatStore.Append(db, conversation, "assistant", Bounded(text, 128000),
            atUtc: Now, metadata: JsonSerializer.Serialize(association));
    }

    internal async Task SweepPendingContinuationsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var attachedIds = owners.Keys.ToArray();
        var candidates = await db.Set<AiAssistantChatConversation>().AsNoTracking()
            .Where(x => x.ProviderKind == ChatProviderKind.Netclaw && (x.State == ChatState.WaitingForChildren || x.State == ChatState.WaitingForScheduledReports || x.State == ChatState.AwaitingApproval
                || x.State == ChatState.Processing || x.State == ChatState.DeliveryUnknown && attachedIds.Contains(x.Id)))
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var id in candidates)
        {
            Owner? retire = null;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var current = await db.Set<AiAssistantChatConversation>()
                    .FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
                owners.TryGetValue(id, out var owner);
                if (owner?.BackgroundReports.Length > 0 || current.State == ChatState.WaitingForScheduledReports)
                {
                    var attached = IsObserving(owner);
                    if (await EndBackgroundObserversAsync(db, current, attached ? "The observation deadline expired."
                        : "The original observation was interrupted.", expiredOnly: attached, ct))
                    {
                        await db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                        feed.Publish(new(id, null));
                        continue;
                    }
                }
                var pending = await LatestCompletionAsync(db, current, ct);
                if (current.State is not (ChatState.WaitingForChildren or ChatState.WaitingForScheduledReports or ChatState.AwaitingApproval
                    or ChatState.Processing or ChatState.DeliveryUnknown) || !HasPendingWork(pending))
                    continue;
                var expired = pending!.ObservationExpiresAtUtc <= Now;
                // An inconclusive reattachment still consumes an owner. Its
                // original deadline applies even though ordinary outputs remain
                // fenced by DeliveryUnknown. Avoid repeated uncertainty events
                // for already detached unresolved conversations.
                if (current.State == ChatState.DeliveryUnknown && (!expired || owner is null)) continue;
                if (!expired && IsObserving(owner)) continue;
                if (HasPendingReports(pending))
                {
                    var ended = EndScheduledObservation(pending);
                    RecordScheduledObservationEnd(db, current, ended, expired
                        ? "The scheduled report observation window expired." : "The original SignalR observation was interrupted.");
                    if (current.State != ChatState.AwaitingApproval || HasChildren(pending))
                        current.State = ChatState.DeliveryUnknown;
                    if (owner is not null) owner.Continuation = ended;
                }
                else
                {
                    current.State = ChatState.DeliveryUnknown;
                    current.RecoveryResult = expired
                        ? "The one-hour delegated-work observation limit expired. Child outcomes remain unknown; nothing was resent or cancelled remotely."
                        : "The delegated-work transport was interrupted. Child outcomes remain unknown; nothing was resent or cancelled remotely.";
                    AiAssistantChatStore.Append(db, current, "delivery_unknown", current.RecoveryResult, atUtc: Now);
                }
                if (current.State != ChatState.AwaitingApproval || !IsObserving(owner))
                {
                    retire = owner;
                    if (owner is not null) owner.Retired = true;
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                if (owner is not null)
                    ObserveLifecycle("follow_up_observation_ended", current, owner, expired ? "expired" : "disconnected");
            }
            if (retire is not null && owners.TryRemove(new KeyValuePair<Guid, Owner>(id, retire)))
                await RetireAsync(retire);
            feed.Publish(new(id, null));
        }
    }

    private static bool IsObserving(Owner? owner) => owner is not null && !owner.Retired && !owner.Consumer.IsCompleted
        && owner.Client.IsConnected && !owner.Overflowed;
}
