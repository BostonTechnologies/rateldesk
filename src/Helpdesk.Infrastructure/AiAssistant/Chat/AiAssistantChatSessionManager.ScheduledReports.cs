using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

public sealed partial class AiAssistantChatSessionManager
{
    private const string ScheduledObservationEnded = "scheduled_observation_ended";
    private const string ScheduledResultUnknown =
        "The scheduled report could not be confirmed. Ask your NetClaw administrator to check the originating SignalR session for the report. Session history cannot restore an exact missed report.";

    private static bool HasScheduledWork(ChatTurnCompletion? completion) =>
        completion is { ScheduledWorkUnconfirmed: true } or { ScheduledReports.Length: > 0 };

    private static bool HasPendingReports(ChatTurnCompletion? completion) =>
        completion?.ScheduledReports?.Any(report => !report.Unconfirmed) == true;

    private static bool HasPendingWork(ChatTurnCompletion? completion) =>
        HasChildren(completion) || HasPendingReports(completion);

    private static void ObserveReminderTool(Owner owner, NetclawOutput output)
    {
        if (output.ToolName != "set_reminder") return;
        if (output.Type == "tool_call" && NetclawScheduledReports.RequestsCurrentSession(output.ToolArguments))
        {
            if (output.CallId is { } call && owner.ReminderCalls.Count < NetclawScheduledReports.MaximumReports)
                owner.ReminderCalls.Add(call);
            else
                owner.UnconfirmedReminderRegistration = true;
        }
        if (output.Type != "tool_result") return;

        var currentSession = output.CallId is { } resultCall && owner.ReminderCalls.Remove(resultCall);
        var report = output.ToolFailed ? null : NetclawScheduledReports.ReadRegistration(output.ToolResult);
        if (report is null)
        {
            owner.UnconfirmedReminderRegistration |= currentSession;
            return;
        }
        if (!currentSession || owner.ReminderRegistrations.Count >= NetclawScheduledReports.MaximumReports
            || !owner.ReminderRegistrations.TryAdd(report.Id, report))
            owner.UnconfirmedReminderRegistration = true;
    }

    private static ChatScheduledReport? MatchScheduledReport(ChatTurnCompletion? previous, NetclawOutput output,
        DateTimeOffset? turnStarted, out long fireAt)
    {
        fireAt = 0;
        if (previous?.ScheduledReports is not { Length: > 0 } reports ||
            !NetclawScheduledReports.TryReadFireKey(output.ReminderFireKey, out var id, out fireAt))
            return null;
        var report = reports.FirstOrDefault(candidate => candidate.Id == id && !candidate.Unconfirmed);
        if (report is null || report.LastFireAtUnixMs >= fireAt || fireAt < turnStarted?.ToUnixTimeMilliseconds()
            || fireAt >= previous.ObservationExpiresAtUtc?.ToUnixTimeMilliseconds())
            return null;
        // The receipt reports the due second. The native fire key identifies
        // the actual occurrence in milliseconds, independent of dispatch time.
        return report.Recurring || report.DueAtUtc?.ToUnixTimeSeconds() == fireAt / 1000 ? report : null;
    }

    private bool CompleteBackgroundReport(HelpdeskDbContext db, AiAssistantChatConversation conversation,
        Owner owner, NetclawOutput output)
    {
        var matches = owner.BackgroundReports.Where(binding => binding.Parent.ObservationExpiresAtUtc > Now &&
            MatchScheduledReport(binding.Parent, output, binding.ParentStartedAtUtc, out _) is not null).Take(2).ToArray();
        if (matches.Length != 1 || output.TurnOrdinal is not { } ordinal || ordinal <= owner.LastCompletedTurn) return false;
        var binding = matches[0];
        var parent = binding.Parent;
        var matched = MatchScheduledReport(parent, output, binding.ParentStartedAtUtc, out var fireAt)!;
        var terminal = output.Complete(parent.OperatorMessageId, parent.ObservationExpiresAtUtc);
        var hasReply = terminal.TurnOutcome == "completed" && !terminal.QuietEnd && owner.HasFinalText;
        var reports = parent.ScheduledReports!.Where(report => report != matched).ToList();
        if (matched.Recurring || !hasReply)
            reports.Add(matched with { LastFireAtUnixMs = fireAt, Unconfirmed = !matched.Recurring && !hasReply });
        terminal = terminal with { SourceReminderId = output.ReminderFireKey, ScheduledReports = reports.ToArray(),
            ScheduledWorkUnconfirmed = parent.ScheduledWorkUnconfirmed || !hasReply };
        if (hasReply)
        {
            var association = new ChatOutputAssociation(parent.OperatorMessageId, parent.TurnNumber, ordinal, output.ReminderFireKey);
            AiAssistantChatStore.Append(db, conversation, "assistant", owner.StagedReplies[^1],
                atUtc: Now, metadata: JsonSerializer.Serialize(association));
        }
        AiAssistantChatStore.Append(db, conversation, "turn_completed", CompletionDescription(terminal),
            callId: output.ReminderFireKey, atUtc: Now, metadata: JsonSerializer.Serialize(terminal));
        owner.BackgroundReports = owner.BackgroundReports.Where(item => item != binding)
            .Concat(HasPendingReports(terminal) ? [binding with { Parent = terminal }] : []).ToArray();
        ChatScheduledObservationLedger.Save(db, conversation, owner.BackgroundReports, Now);
        owner.LastCompletedTurn = ordinal;
        if (conversation.State == ChatState.WaitingForScheduledReports && owner.BackgroundReports.Length == 0
            && !HasPendingWork(owner.Continuation))
        {
            conversation.State = ChatState.Idle;
            owner.Retired = true;
        }
        return true;
    }

    private static ChatTurnCompletion CompleteScheduledWork(Owner owner, ChatTurnCompletion completion,
        ChatScheduledReport? matched, long fireAt, string? fireKey, DateTimeOffset deadline)
    {
        var previous = owner.Continuation;
        var unresolved = previous?.ScheduledReports?.ToList() ?? [];
        var unconfirmed = previous?.ScheduledWorkUnconfirmed == true || owner.UnconfirmedReminderRegistration
            || owner.ReminderCalls.Count > 0;
        if (matched is not null)
        {
            var hasReport = completion.TurnOutcome == "completed" && !completion.QuietEnd && owner.HasFinalText;
            unresolved.Remove(matched);
            if (matched.Recurring || !hasReport)
                unresolved.Add(matched with { LastFireAtUnixMs = fireAt, Unconfirmed = !hasReport && !matched.Recurring });
            unconfirmed |= !hasReport;
        }
        foreach (var report in owner.ReminderRegistrations.Values)
        {
            var prior = unresolved.FindIndex(candidate => candidate.Id == report.Id);
            if (prior >= 0)
                unresolved[prior] = report with { Unconfirmed = true };
            else if (unresolved.Count < NetclawScheduledReports.MaximumReports)
                unresolved.Add(report with { Unconfirmed = report.Unconfirmed || report.DueAtUtc >= deadline || completion.TurnNumber is null });
            else
                unconfirmed = true;
        }
        return completion with
        {
            ScheduledReports = unresolved.Count == 0 && previous?.ScheduledReports is null && matched is null ? null : unresolved.ToArray(),
            ScheduledWorkUnconfirmed = unconfirmed || unresolved.Any(report => report.Unconfirmed),
            SourceReminderId = matched is null ? null : fireKey
        };
    }

    private async Task<bool> EndBackgroundObserversAsync(HelpdeskDbContext db, AiAssistantChatConversation conversation,
        string reason, bool expiredOnly, CancellationToken ct)
    {
        var bindings = await ChatScheduledObservationLedger.LoadAsync(db, conversation, ct);
        var ended = bindings.Where(binding => !expiredOnly || binding.Parent.ObservationExpiresAtUtc <= Now).ToArray();
        if (ended.Length == 0) return false;
        var remaining = bindings.Except(ended).ToArray();
        foreach (var binding in ended)
            AiAssistantChatStore.Append(db, conversation, ScheduledObservationEnded, $"{reason} {ScheduledResultUnknown}",
                callId: "background", atUtc: Now, metadata: JsonSerializer.Serialize(EndScheduledObservation(binding.Parent)));
        ChatScheduledObservationLedger.Save(db, conversation, remaining, Now);
        if (conversation.State == ChatState.WaitingForScheduledReports && remaining.Length == 0)
        {
            var foreground = await LatestCompletionAsync(db, conversation, ct);
            if (ChatScheduledObservationLedger.AdmitsReply(foreground) && !HasPendingWork(foreground))
                conversation.State = ChatState.Idle;
        }
        return true;
    }

    private static ChatTurnCompletion EndScheduledObservation(ChatTurnCompletion completion) => completion with
    {
        ScheduledWorkUnconfirmed = true,
        ScheduledReports = completion.ScheduledReports?.Select(report => report with { Unconfirmed = true }).ToArray()
    };

    private void RecordScheduledObservationEnd(HelpdeskDbContext db, AiAssistantChatConversation conversation,
        ChatTurnCompletion completion, string reason)
    {
        conversation.RecoveryResult = $"{reason} {ScheduledResultUnknown}";
        AiAssistantChatStore.Append(db, conversation, ScheduledObservationEnded, conversation.RecoveryResult,
            atUtc: Now, metadata: JsonSerializer.Serialize(completion));
    }

    private static void StageReply(Owner owner, string text, bool canonical)
    {
        var remaining = 128000 - owner.StagedReplyLength;
        if (owner.StagedReplies.Count >= 64 || remaining <= 0)
            throw new InvalidOperationException("Native replies exceeded the bounded turn buffer.");
        var reply = Bounded(text, remaining);
        owner.StagedReplies.Add(reply);
        owner.StagedReplyLength += reply.Length;
        owner.HasFinalText = canonical;
    }

    private static void ResetTurnBuffers(Owner owner)
    {
        owner.Draft.Clear();
        owner.StagedReplies.Clear();
        owner.StagedReplyLength = 0;
        owner.HasFinalText = false;
        owner.ToolStarts.Clear();
        owner.ReminderCalls.Clear();
        owner.ReminderRegistrations.Clear();
        owner.UnconfirmedReminderRegistration = false;
    }
}
