using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

internal sealed partial class OpenAiChatRun
{
    private static readonly string DecisionOptions = JsonSerializer.Serialize(new ChatOption[]
    {
        new("approve_once", "Allow once"), new("deny", "Deny")
    });

    private async Task<bool> SyncPendingDecisionsAsync(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch,
        OpenAiChatProfileSettings profile, ProviderPendingAction[] pending, CancellationToken ct)
    {
        var existing = await db.Set<OpenAiChatDecision>().Where(row => row.ConversationId == conversation.Id &&
            row.State != OpenAiDecisionState.Answered && row.State != OpenAiDecisionState.Closed).ToListAsync(ct);
        var ids = pending.Select(DecisionId).ToHashSet(StringComparer.Ordinal);
        foreach (var previous in existing.Where(row => !ids.Contains(row.CallId)))
        {
            previous.State = OpenAiDecisionState.Closed;
            await CloseLocalApprovalAsync(conversation, previous, "This request is no longer pending in OpenAI.", ct);
        }

        var awaiting = false;
        foreach (var action in pending.Where(item => item.Kind != ProviderActionKind.Unsupported).DistinctBy(DecisionId))
        {
            var id = DecisionId(action);
            var decision = existing.SingleOrDefault(row => row.CallId == id);
            var arguments = OpenAiWorklogAction.CanonicalArguments(action);
            var hash = OpenAiChatProfiles.Hash(arguments);
            if (decision is null)
            {
                if (await db.Set<OpenAiChatDecision>().AnyAsync(row => row.ConversationId == conversation.Id && row.CallId == id, ct))
                    continue;
                decision = await CreateDecisionAsync(conversation, dispatch, profile, action, id, arguments, hash, ct);
            }
            if (decision.State == OpenAiDecisionState.Pending && (decision.ExpiresAtUtc <= Now || decision.ArgumentHash != hash))
            {
                decision.State = OpenAiDecisionState.AnswerReady;
                decision.Approved = false;
                decision.Result = "The approval expired or the provider changed its arguments.";
                await CloseLocalApprovalAsync(conversation, decision, decision.Result, ct);
            }
            awaiting |= decision.State == OpenAiDecisionState.Pending;
        }
        return awaiting;
    }

    private async Task<OpenAiChatDecision> CreateDecisionAsync(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch,
        OpenAiChatProfileSettings profile, ProviderPendingAction action, string id, string arguments, string hash, CancellationToken ct)
    {
        var description = ApprovalDescription(profile, action, arguments);
        if (description is not null &&
            !await authority.IsGrantUsableAsync(conversation, await CredentialIdAsync(conversation.Id, ct), ct))
            description = null;
        var row = new OpenAiChatDecision
        {
            ConversationId = conversation.Id, CallId = id, RemoteActionId = action.Id, TurnId = action.TurnId,
            ActorId = dispatch.ActorId, Arguments = arguments, ArgumentHash = hash, Kind = (int)action.Kind,
            Operation = action.Kind == ProviderActionKind.Function ? OpenAiWorklogAction.Operation : ChatOutputSafety.Identifier(action.Name),
            ExpiresAtUtc = Now.AddMinutes(10), State = description is null ? OpenAiDecisionState.AnswerReady : OpenAiDecisionState.Pending,
            Approved = description is null ? false : null, Result = description is null ? "RatelDesk policy does not allow this request." : null
        };
        db.Add(row);
        if (description is not null)
        {
            db.Add(new AiAssistantChatInteraction { ConversationId = conversation.Id, CallId = id, OptionsJson = DecisionOptions });
            AiAssistantChatStore.Append(db, conversation, "approval_request", description, callId: id, options: DecisionOptions, atUtc: Now);
            AttachMetadata(conversation, dispatch, action.TurnId, status: "pending", call: action.Id);
        }
        return row;
    }

    private static string? ApprovalDescription(OpenAiChatProfileSettings profile, ProviderPendingAction action, string arguments)
    {
        if (arguments.Length == 0) return null;
        if (action.Kind == ProviderActionKind.Function && action.Name == OpenAiWorklogAction.Function && profile.ApproveInternalWorklogs &&
            OpenAiWorklogAction.Read(action.Arguments) is { } note && ProviderText(note.Note, 4000) == note.Note)
            return $"Add an internal note to incident {note.IncidentId} and refresh its activity time. The customer will not be notified.\n\n{note.Note}";
        if (action.Kind == ProviderActionKind.McpApproval && action.ServerLabel == "rateldesk" &&
            profile.ReadOperations.Any(operation => operation.StartsWith(action.Name + ".", StringComparison.Ordinal)) &&
            ProviderText(arguments, 16000) == arguments)
            return $"Authorize OpenAI to request a RatelDesk read through {ChatOutputSafety.Identifier(action.Name)}.\n\n{arguments}";
        return null;
    }

    private async Task CloseLocalApprovalAsync(AiAssistantChatConversation conversation, OpenAiChatDecision decision, string reason, CancellationToken ct)
    {
        var item = await db.Set<AiAssistantChatInteraction>().FindAsync([conversation.Id, decision.CallId], ct);
        if (item is null || item.SelectedKey is not null) return;
        item.SelectedKey = "closed";
        AiAssistantChatStore.Append(db, conversation, "approval_closed", reason, callId: decision.CallId, atUtc: Now);
    }

    private async Task RespondToPendingActionAsync(OpenAiAgentsClient adapter, AiAssistantChatConversation conversation,
        OpenAiChatDispatch dispatch, ProviderPendingAction[] actions, CancellationToken ct)
    {
        var keys = actions.Select(DecisionId).ToArray();
        var row = await db.Set<OpenAiChatDecision>().AsNoTracking().Where(item => item.ConversationId == conversation.Id &&
            keys.Contains(item.CallId) && (item.State == OpenAiDecisionState.Decided || item.State == OpenAiDecisionState.Executing ||
            item.State == OpenAiDecisionState.AnswerReady)).OrderBy(item => item.ExpiresAtUtc).FirstOrDefaultAsync(ct);
        if (row is null) return;
        var action = actions.First(item => DecisionId(item) == row.CallId);
        var matching = row.ArgumentHash == OpenAiChatProfiles.Hash(OpenAiWorklogAction.CanonicalArguments(action));
        if (!matching && row.State == OpenAiDecisionState.AnswerReady && row.Approved == true) return;
        var decision = row.State == OpenAiDecisionState.AnswerReady ? new ProviderDecision(row.Approved == true, row.Result)
            : await ResolveDecisionAsync(conversation, dispatch, row, action, matching, ct);
        if (decision is null || (!matching && decision.Approved)) return;
        await adapter.RespondAsync(conversation.AiAssistantSessionId!, "approval-" + row.CallId, action, decision, ct);
        await ChangeDecisionAsync(dispatch, row.CallId, OpenAiDecisionState.AnswerReady,
            current => current.State = OpenAiDecisionState.Answered, ct);
    }

    private async Task<ProviderDecision?> ResolveDecisionAsync(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch,
        OpenAiChatDecision row, ProviderPendingAction action, bool exact, CancellationToken ct)
    {
        ProviderDecision result;
        var expected = row.State;
        if (row.State == OpenAiDecisionState.Executing)
            result = new(false, "RatelDesk restarted before recording the outcome. Check the incident before taking further action.");
        else if (!exact || row.ActorId != dispatch.ActorId || row.ExpiresAtUtc <= Now || row.Approved != true)
            result = new(false, "The operator denied the request or its approval expired or changed.");
        else
        {
            var credential = await CredentialIdAsync(conversation.Id, ct);
            if (!await authority.IsGrantUsableAsync(conversation, credential, ct))
                result = new(false, "RatelDesk tool authority changed after approval.");
            else if (action.Kind == ProviderActionKind.McpApproval)
                result = new(true);
            else
            {
                var note = OpenAiWorklogAction.Read(action.Arguments);
                if (note is null) return null;
                var claimed = await ChangeDecisionAsync(dispatch, row.CallId, OpenAiDecisionState.Decided,
                    decision => decision.State = OpenAiDecisionState.Executing, ct);
                if (!claimed) return null;
                expected = OpenAiDecisionState.Executing;
                try
                {
                    result = await authority.WriteInternalNoteAsync(conversation, credential, note.IncidentId, note.Note, ct);
                }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("OpenAI approved operation outcome is unknown for {ConversationId}: {FailureType}",
                        conversation.Id, error.GetType().Name);
                    result = new(false, "RatelDesk could not confirm the note outcome. Check the incident before repeating the action.");
                }
            }
        }
        var saved = await ChangeDecisionAsync(dispatch, row.CallId, expected, current =>
        {
            current.State = OpenAiDecisionState.AnswerReady;
            current.Approved = result.Approved;
            current.Result = result.Result;
        }, ct, result.Result);
        return saved ? result : null;
    }

    private async Task<bool> ChangeDecisionAsync(OpenAiChatDispatch dispatch, string id, OpenAiDecisionState expected,
        Action<OpenAiChatDecision> change, CancellationToken ct, string? outcome = null)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(dispatch.ConversationId, ct);
        if (conversation.ActiveMessageId != dispatch.MessageId || conversation.State == ChatState.Archived) return false;
        var decision = await db.Set<OpenAiChatDecision>().SingleAsync(item => item.ConversationId == conversation.Id && item.CallId == id, ct);
        if (decision.State != expected) return false;
        change(decision);
        if (outcome is not null)
            AppendProviderEvent(conversation, dispatch, decision.Approved == true ? "approval_executed" : "approval_denied",
                outcome, turn: decision.TurnId, call: decision.CallId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        feed.Publish(new(conversation.Id, null));
        return true;
    }

    private async Task SendCancellationAsync(IChatProviderSession adapter, AiAssistantChatConversation conversation,
        OpenAiChatDispatch dispatch, CancellationToken ct)
    {
        var requested = await db.Set<OpenAiChatDispatch>().AsNoTracking().AnyAsync(row => row.ConversationId == conversation.Id &&
            row.MessageId == dispatch.MessageId && row.CancelRequestedAtUtc != null && row.CancelSentAtUtc == null, ct);
        if (!requested) return;
        var claimed = await ChangeAsync(dispatch, (row, current) =>
        {
            if (row.CancelRequestedAtUtc is null || row.CancelSentAtUtc is not null || row.State != OpenAiDispatchState.Admitted) return false;
            row.CancelSentAtUtc = Now;
            return true;
        }, ct);
        if (!claimed) return;
        await adapter.CancelAsync(conversation.AiAssistantSessionId!, "cancel-" + dispatch.MessageId.ToString("N"), ct);
        await ChangeAsync(dispatch, (row, current) =>
            AppendProviderEvent(current, row, "cancellation_admitted", "OpenAI accepted the cancellation request. The outcome is pending.", turn: row.TurnId), ct);
    }

    private static string DecisionId(ProviderPendingAction action) =>
        OpenAiChatProfiles.Hash($"{action.Kind}\n{action.TurnId}\n{action.Id}");
}
