using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.AiAssistant.Chat.Codex;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

internal sealed partial class OpenAiChatRun
{
    private async Task<bool> ReconcileAsync(IChatProviderSession adapter, AiAssistantChatConversation snapshot,
        OpenAiChatDispatch dispatch, OpenAiChatProfileSettings profile, ProviderSessionHistory history, CancellationToken ct)
    {
        var marker = ChatOutboundMessage.Marker(snapshot.Id, dispatch.MessageId);
        var admissions = history.Items.Where(item => item.Kind == ProviderItemKind.UserMessage && item.SubagentId is null &&
            item.Text.StartsWith(marker, StringComparison.Ordinal) && item.TurnId is not null).Take(2).ToArray();
        var rootId = adapter is CodexAppServerSession && dispatch.TurnId is not null
            ? dispatch.TurnId : admissions.Length == 1 ? admissions[0].TurnId : dispatch.TurnId;
        var root = history.Turns.FirstOrDefault(turn => turn.Id == rootId && turn.SubagentId is null);
        if (root is null)
        {
            await ChangeAsync(dispatch, (row, conversation) =>
            {
                if (history.Status == "failed")
                    Complete(row, conversation, "error", "The OpenAI session failed. Start a new conversation for further work.");
                else
                    Schedule(row, conversation, profile);
            }, ct);
            return history.Status == "failed";
        }
        dispatch.TurnId = root.Id;
        if (adapter is CodexAppServerSession codexSession)
        {
            codexSession.LastTurnId = root.Id;
            if (codexSession.ModelWasRerouted(history.Id, root.Id))
            {
                if (IsRunning(root)) await codexSession.CancelAsync(history.Id, "model-changed", ct);
                await MarkTerminalAsync(dispatch, "error", "Codex reported a different serving model. Review the configured model before continuing.", ct);
                return true;
            }
        }
        var turns = history.Turns.Where(turn => turn.Id == root.Id ||
            root.CreatedAt.HasValue && turn.CreatedAt >= root.CreatedAt).DistinctBy(turn => turn.Id).ToArray();
        var ids = turns.Select(turn => turn.Id).ToHashSet(StringComparer.Ordinal);
        var actions = history.PendingActions.Where(action => ids.Contains(action.TurnId)).ToArray();
        var activeSubagents = turns.Where(turn => turn.SubagentId is not null && IsRunning(turn))
            .Select(turn => turn.SubagentId!).ToHashSet(StringComparer.Ordinal);
        foreach (var child in history.Subagents.Where(child => child.Status == "active" && child.OpenedAt >= root.CreatedAt))
            if (!turns.Any(turn => turn.SubagentId == child.Id)) activeSubagents.Add(child.Id);

        var rootFinished = root.Status is "completed" or "failed" or "cancelled" or "interrupted";
        var pending = actions.Length != 0 || history.PendingActions.Any(action => action.Kind == ProviderActionKind.Unsupported);
        var terminal = history.Status == "failed" || rootFinished && !pending && activeSubagents.Count == 0 &&
            !turns.Any(IsRunning) && history.Status == "idle";
        var capturedArtifacts = terminal && adapter is OpenAiAgentsClient managed && adapter.Features.HasFlag(ChatProviderFeatures.Artifacts)
            ? await GetArtifactsAsync(managed, snapshot, ids, ct) : [];
        var save = await SaveHistoryAsync(snapshot, dispatch, profile, history, root, turns, ids, actions,
            activeSubagents.Count, terminal, capturedArtifacts, ct);
        if (!save || terminal) return true;
        if (adapter is OpenAiAgentsClient managedSession)
            await RespondToPendingActionAsync(managedSession, snapshot, dispatch, actions, ct);
        await SendCancellationAsync(adapter, snapshot, dispatch, ct);

        // Keep the live stream while work is advancing. Pending decisions are
        // durable and can be reconciled in a later slice after a browser reply.
        return pending || activeSubagents.Count != 0 && rootFinished;
    }

    private async Task<bool> SaveHistoryAsync(AiAssistantChatConversation snapshot, OpenAiChatDispatch dispatch,
        OpenAiChatProfileSettings profile, ProviderSessionHistory history, ProviderTurn root, ProviderTurn[] turns,
        HashSet<string> ids, ProviderPendingAction[] actions, int openChildren, bool terminal,
        OpenAiChatArtifact[] capturedArtifacts, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(snapshot.Id, ct);
        if (conversation.State == ChatState.Archived || conversation.ActiveMessageId != dispatch.MessageId) return false;
        var row = await db.Set<OpenAiChatDispatch>().SingleAsync(item =>
            item.ConversationId == conversation.Id && item.MessageId == dispatch.MessageId, ct);
        if (row.State is OpenAiDispatchState.Completed or OpenAiDispatchState.Rejected) return false;
        row.TurnId = root.Id;
        row.State = OpenAiDispatchState.Admitted;
        var receipts = await db.Set<OpenAiChatEventReceipt>().AsNoTracking().Where(item => item.ConversationId == conversation.Id)
            .Select(item => item.RemoteKey).ToHashSetAsync(ct);
        PersistItems(conversation, row, history.Items.Where(item => item.TurnId is { } turn && ids.Contains(turn)), receipts);
        PersistTurns(conversation, row, history, turns, receipts);

        foreach (var artifact in capturedArtifacts)
        {
            db.Add(artifact);
            AppendProviderEvent(conversation, row, "file", $"Saved artifact {artifact.Name}.",
                turn: artifact.TurnId, item: artifact.RemoteId, status: "saved");
        }

        var awaiting = await SyncPendingDecisionsAsync(conversation, row, profile, terminal ? [] : actions, ct);
        if (terminal)
        {
            var failed = history.Status == "failed" || root.Status == "failed";
            var outcome = failed ? "failed" : root.Status == "interrupted" && row.CancelRequestedAtUtc is not null ? "cancelled" : root.Status;
            var label = outcome switch
            {
                "failed" => "The provider reported a failed turn.",
                "cancelled" => "The provider confirmed that the turn was cancelled.",
                "interrupted" => "The Codex turn was interrupted. Review the saved conversation before starting another turn.",
                _ => "The provider finished the turn."
            };
            if (turns.Any(turn => turn.SubagentId is not null && turn.Status is "failed" or "cancelled"))
                label += " Review the separate delegated outcomes.";
            Complete(row, conversation, outcome switch { "failed" or "interrupted" => "error", "cancelled" => "turn_cancelled", _ => "turn_completed" }, label);
            AttachMetadata(conversation, row, root.Id, status: outcome);
        }
        else
        {
            Schedule(row, conversation, profile);
            if (row.NextObservationAtUtc != DateTimeOffset.MaxValue && !await IsStoppedAsync(conversation, dispatch, ct))
            {
                conversation.State = awaiting ? ChatState.AwaitingApproval : openChildren > 0 && root.Status == "completed"
                    ? ChatState.WaitingForChildren : ChatState.Processing;
                conversation.RecoveryResult = conversation.State == ChatState.WaitingForChildren
                    ? $"OpenAI has {openChildren} outstanding delegated turn(s)." : null;
            }
        }
        AiAssistantChatStore.RecordTransportActivity(conversation, Now);
        var changed = db.ChangeTracker.HasChanges();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        snapshot.LastSequence = conversation.LastSequence;
        if (changed) feed.Publish(new(conversation.Id, null));
        return true;
    }

    private void PersistItems(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch,
        IEnumerable<ProviderItem> items, HashSet<string> receipts)
    {
        foreach (var item in items)
        {
            if (item.Id is not { } id || item.Kind == ProviderItemKind.UserMessage) continue;
            if (item.Kind == ProviderItemKind.AssistantMessage)
            {
                if (item.Status is not ("completed" or "incomplete") || string.IsNullOrWhiteSpace(item.Text) ||
                    !NewReceipt(conversation, receipts, $"message\n{item.SubagentId}\n{id}"))
                    continue;
                var text = ProviderText(item.Text, 128000);
                AppendProviderEvent(conversation, dispatch, "assistant", text,
                    turn: item.TurnId, item: id, child: item.SubagentId, status: item.Status);
            }
            else
            {
                if (!NewReceipt(conversation, receipts, $"tool\n{item.SubagentId}\n{id}\n{item.Status}")) continue;
                var toolName = ChatOutputSafety.Identifier(item.ToolName);
                var status = item.Status is "completed" or "failed" or "cancelled" or "incomplete" ? item.Status : "in progress";
                var activity = new ChatActivityMetadata(toolName, item.ToolKind ?? "tool", ProviderDisplayName(conversation),
                    null, null, status, new(ToolName: toolName, Result: ProviderText(item.Text, 4096)),
                    Metadata(conversation, dispatch, item.TurnId, id, item.SubagentId, status, item.CallId, item.Operation));
                AiAssistantChatStore.AppendActivity(db, conversation, status == "in progress" ? "tool_call" : "tool_result",
                    ChatTraceSafety.CallId(item.CallId) ?? ChatTraceSafety.CallId(id), activity, Now);
            }
        }
    }

    private void PersistTurns(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch, ProviderSessionHistory history,
        IEnumerable<ProviderTurn> turns, HashSet<string> receipts)
    {
        foreach (var turn in turns)
        {
            if (turn.Status is not ("completed" or "failed" or "cancelled") ||
                !NewReceipt(conversation, receipts, $"turn\n{turn.Id}\n{turn.Status}")) continue;
            var child = history.Subagents.FirstOrDefault(agent => agent.Id == turn.SubagentId);
            var title = child is null ? "OpenAI root turn" : $"Delegated turn ({ChatOutputSafety.Identifier(child.Name)})";
            var usage = turn.Usage is { } tokens ? new ChatProviderTurnUsage(tokens.InputTokens, tokens.OutputTokens, tokens.TotalTokens) : null;
            var metadata = Metadata(conversation, dispatch, turn.Id, child: turn.SubagentId, status: turn.Status,
                time: turn.CompletedAt, usage: usage);
            AiAssistantChatStore.AppendActivity(db, conversation, child is null ? "usage" : "subagent", null,
                new(title, child is null ? "usage" : "subagent", ProviderDisplayName(conversation), null, null, turn.Status,
                new(FailureCode: turn.FailureCode is null ? null : ChatOutputSafety.Identifier(turn.FailureCode),
                    Measures: usage is null ? null : $"Input {usage.InputTokens:N0}, output {usage.OutputTokens:N0}, total {usage.TotalTokens:N0} tokens"),
                metadata), Now);
        }
    }

    private void Schedule(OpenAiChatDispatch row, AiAssistantChatConversation conversation, OpenAiChatProfileSettings profile)
    {
        var deadline = (row.DispatchedAtUtc ?? row.AdmittedAtUtc).AddMinutes(profile.ObservationMinutes);
        if (conversation.RecoveryLastAttemptUtc > deadline)
            deadline = conversation.RecoveryLastAttemptUtc.Value.AddMinutes(2);
        if (Now >= deadline)
        {
            row.NextObservationAtUtc = DateTimeOffset.MaxValue;
            Unknown(conversation, "OpenAI observation reached its time limit. Check saved history or cancel the remote turn.");
        }
        else row.NextObservationAtUtc = Now.AddSeconds(conversation.State == ChatState.AwaitingApproval ? 3 : 1);
    }

    private Task<bool> IsStoppedAsync(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch, CancellationToken ct) =>
        db.Set<AiAssistantChatEvent>().AnyAsync(item => item.ConversationId == conversation.Id &&
            item.Type == "observation_stopped" && item.CreatedUtc >= dispatch.AdmittedAtUtc &&
            (conversation.RecoveryLastAttemptUtc == null || item.CreatedUtc > conversation.RecoveryLastAttemptUtc), ct);

    private bool NewReceipt(AiAssistantChatConversation conversation, HashSet<string> known, string identity)
    {
        var key = OpenAiChatProfiles.Hash(identity);
        if (!known.Add(key)) return false;
        db.Add(new OpenAiChatEventReceipt { ConversationId = conversation.Id, RemoteKey = key });
        return true;
    }

    private void AppendProviderEvent(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch, string type,
        string text, string? turn = null, string? item = null, string? child = null, string? status = null, string? call = null)
    {
        AiAssistantChatStore.Append(db, conversation, type, text, callId: call, atUtc: Now);
        AttachMetadata(conversation, dispatch, turn, item, child, status, call);
    }

    private void AttachMetadata(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch, string? turn = null,
        string? item = null, string? child = null, string? status = null, string? call = null)
        => db.Set<AiAssistantChatEvent>().Local.Single(row => row.ConversationId == conversation.Id && row.Sequence == conversation.LastSequence)
            .MetadataJson = JsonSerializer.Serialize(Metadata(conversation, dispatch, turn, item, child, status, call),
                OpenAiProfileJson.Default.ChatProviderEventMetadata);

    private ChatProviderEventMetadata Metadata(AiAssistantChatConversation conversation, OpenAiChatDispatch row,
        string? turn = null, string? item = null, string? child = null, string? status = null,
        string? call = null, string? operation = null, DateTimeOffset? time = null, ChatProviderTurnUsage? usage = null)
        => new(2, conversation.ProviderKind, conversation.ProviderProfileId!.Value, conversation.AiAssistantSessionId!,
            row.MessageId, turn, item, child, status, call, operation, time ?? Now, usage);

    private static string ProviderDisplayName(AiAssistantChatConversation conversation) =>
        conversation.ProviderKind == ChatProviderKind.CodexAppServer ? "Codex App Server" : "OpenAI Agents";

    private const int MaximumArtifactSize = 5 * 1024 * 1024;
    private const int MaximumArtifacts = 10;

    private async Task<OpenAiChatArtifact[]> GetArtifactsAsync(OpenAiAgentsClient adapter, AiAssistantChatConversation conversation,
        HashSet<string> turns, CancellationToken ct)
    {
        var existing = await db.Set<OpenAiChatArtifact>().AsNoTracking().Where(row => row.ConversationId == conversation.Id)
            .Select(row => row.RemoteId).ToListAsync(ct);
        var available = MaximumArtifacts - existing.Count;
        if (available <= 0) return [];
        var remoteSession = conversation.AiAssistantSessionId!;
        var artifacts = await adapter.ListArtifactsAsync(remoteSession, ct);
        var result = new List<OpenAiChatArtifact>();
        foreach (var item in artifacts.Where(item => turns.Contains(item.TurnId) && !existing.Contains(item.Id) &&
            item.SizeBytes is >= 0 and <= MaximumArtifactSize && SafeArtifactPath(item.Path)).Take(available))
        {
            var bytes = await adapter.ReadArtifactAsync(remoteSession, item, MaximumArtifactSize, ct);
            result.Add(new OpenAiChatArtifact { ConversationId = conversation.Id, RemoteId = item.Id,
                TurnId = item.TurnId, Name = ChatOutputSafety.FileName(item.Path), SizeBytes = bytes.Length, Contents = bytes });
        }
        return result.ToArray();
    }

    internal static bool SafeArtifactPath(string path) => path.StartsWith("/workspace/outputs/", StringComparison.Ordinal) &&
        path.Length <= 256 && path["/workspace/outputs/".Length..].Split('/').All(segment =>
            segment is not ("." or ".." or "") && !segment.Contains('\\') && !segment.Contains(':') && !segment.Any(char.IsControl));

    internal static string ProviderText(string? text, int maximum)
    {
        var sanitized = OpenAiCredentialRedaction.Redact(text ?? "");
        return sanitized[..Math.Min(sanitized.Length, maximum)];
    }
}
