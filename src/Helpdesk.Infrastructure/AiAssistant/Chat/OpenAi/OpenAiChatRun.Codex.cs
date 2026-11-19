using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.Codex;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

internal sealed partial class OpenAiChatRun
{
    private async Task ProcessCodexAsync(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch,
        OpenAiChatProfileSettings profile, CancellationToken ct)
    {
        if (codex?.Configured != true || profile.CodexConnectionId is not { } connectionId)
            throw new ChatConflictException("The pinned Codex runtime is unavailable.");
        var connection = await db.Set<CodexConnection>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == connectionId &&
            row.OwnerId == conversation.ProviderActorId && row.OrganizationId == conversation.OrganizationId, ct);
        if (connection is not { State: CodexConnectionState.Connected })
            throw new ChatConflictException("The original ChatGPT connection is unavailable. Reconnect for new work.");
        await using var runtime = await codex.AcquireAsync(connection.Id, connection.HostId, ct);
        if (dispatch.State == OpenAiDispatchState.Queued && !await profiles.IsCurrentAsync(conversation, ct))
        {
            await MarkTerminalAsync(dispatch, "error", "The ChatGPT profile changed before this request was submitted.", ct);
            return;
        }

        var grant = await CodexGrantAsync(conversation, dispatch, profile, ct);
        var token = profiles.ReadCodexGrant(grant);
        if (string.IsNullOrEmpty(token))
            throw new ChatConflictException("The pinned RatelDesk MCP grant could not be restored.");
        var tools = profile.ReadOperations.Select(operation => operation[..operation.LastIndexOf('.')]).Distinct(StringComparer.Ordinal).ToArray();
        var server = await runtime.OpenAsync(ct, conversation.Id, profile.McpResource, token, tools);
        if (!await CodexConnections.AccountMatchesAsync(connection, server, ct))
            throw new ChatConflictException("The original ChatGPT account is no longer available on this runtime.");

        var session = new CodexAppServerSession(server, profile, dispatch.MessageId) { LastTurnId = dispatch.TurnId };
        var openedThread = false;
        if (conversation.AiAssistantSessionId is null)
        {
            if (dispatch.State != OpenAiDispatchState.Queued)
                throw new ChatConflictException("No Codex thread was confirmed for this dispatch.");
            var thread = await session.OpenThreadAsync(ct);
            var saved = await ChangeAsync(dispatch, (row, current) =>
            {
                if (row.State != OpenAiDispatchState.Queued || row.CancelRequestedAtUtc is not null ||
                    current.AiAssistantSessionId is not null) return false;
                current.AiAssistantSessionId = thread;
                return true;
            }, ct);
            if (!saved) return;
            conversation.AiAssistantSessionId = thread;
            openedThread = true;
        }

        await ObserveAsync(session, conversation, dispatch, profile, ct, openedThread);
    }

    private async Task<OpenAiChatSessionGrant> CodexGrantAsync(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch,
        OpenAiChatProfileSettings profile, CancellationToken ct)
    {
        var previous = await db.Set<OpenAiChatSessionGrant>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.ConversationId == conversation.Id, ct);
        if (previous is not null) return previous;
        if (dispatch.State != OpenAiDispatchState.Queued)
            throw new ChatConflictException("The original RatelDesk tool grant is unavailable.");

        var grant = new OpenAiChatSessionGrant { ConversationId = conversation.Id, CredentialId = Guid.NewGuid(), ExpiresAtUtc = Now.AddHours(8) };
        var reserved = await ChangeAsync(dispatch, (row, _) =>
        {
            if (row.State != OpenAiDispatchState.Queued || row.CancelRequestedAtUtc is not null) return false;
            db.Add(grant);
            return true;
        }, ct);
        if (!reserved) throw new ChatConflictException("The Codex request changed before granting MCP access.");
        var issued = await authority.IssueAsync(conversation, profile, grant.CredentialId, ct);
        grant.ProtectedCodexToken = profiles.ProtectCodexGrant(grant, issued.Token);
        var stored = await db.Set<OpenAiChatSessionGrant>()
            .Where(row => row.ConversationId == grant.ConversationId && row.CredentialId == grant.CredentialId && row.ProtectedCodexToken == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ProtectedCodexToken, grant.ProtectedCodexToken), ct);
        if (stored != 1)
            throw new ChatConflictException("The RatelDesk MCP grant could not be saved.");
        return grant;
    }
}
