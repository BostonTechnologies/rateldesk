using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

public sealed partial class AiAssistantChatStore
{
    private async Task AdmitProviderTurnAsync(AiAssistantChatConversation conversation, Guid messageId, string actor, CancellationToken ct)
    {
        if (profiles is not null) await profiles.EnsureAdmissionAsync(conversation, actor, ct);
        if (!UsesProviderWorker(conversation)) return;
        var profile = conversation.ProviderProfileId?.ToString("D")
            ?? throw new ChatConflictException("The OpenAI profile binding is missing.");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({profile}, 436))", ct);
        var options = (await profiles!.ResolveAsync(conversation, ct)).Settings;
        var active = await db.Set<AiAssistantChatConversation>().CountAsync(row => row.ProviderProfileId == conversation.ProviderProfileId &&
            (row.State == ChatState.Processing || row.State == ChatState.AwaitingApproval || row.State == ChatState.WaitingForChildren
                || row.State == ChatState.WaitingForScheduledReports || row.State == ChatState.DeliveryUnknown), ct);
        if (active >= options.ConcurrentSessions)
            throw new ChatConflictException("The OpenAI profile has reached its active conversation limit.");
        if (conversation.AiAssistantSessionId is not null &&
            !await db.Set<OpenAiChatSessionGrant>().AnyAsync(grant => grant.ConversationId == conversation.Id &&
                grant.ExpiresAtUtc > Now && grant.RemoteDeletedAtUtc == null && grant.CleanupRequestedAtUtc == null, ct))
            throw new ChatConflictException("The remote session's tool grant expired. Start a new conversation to continue.");
        db.Add(new OpenAiChatDispatch { ConversationId = conversation.Id, MessageId = messageId, ActorId = actor,
            State = OpenAiDispatchState.Queued, AdmittedAtUtc = Now });
    }

    private async Task RecordProviderDecisionAsync(AiAssistantChatConversation conversation, string callId, string key, string actor, CancellationToken ct)
    {
        if (!UsesProviderWorker(conversation)) return;
        await profiles!.EnsureAdmissionAsync(conversation, actor, ct);
        if (conversation.ProviderKind != ChatProviderKind.OpenAiAgents) return;
        var decision = await db.Set<OpenAiChatDecision>().SingleOrDefaultAsync(row =>
            row.ConversationId == conversation.Id && row.CallId == callId, ct)
            ?? throw new KeyNotFoundException("The provider decision is unavailable.");
        if (decision.State != OpenAiDecisionState.Pending || decision.ExpiresAtUtc <= Now || decision.ActorId != actor)
            throw new ChatConflictException("This approval expired or is no longer pending.");
        decision.State = OpenAiDecisionState.Decided;
        decision.Approved = key == "approve_once";
        var dispatch = await ActiveDispatchAsync(conversation, ct);
        if (dispatch is not null) dispatch.NextObservationAtUtc = null;
    }

    private static bool UsesProviderWorker(AiAssistantChatConversation conversation) =>
        conversation.ProviderKind is ChatProviderKind.OpenAiAgents or ChatProviderKind.CodexAppServer;

    private Task<OpenAiChatDispatch?> ActiveDispatchAsync(AiAssistantChatConversation conversation, CancellationToken ct) =>
        db.Set<OpenAiChatDispatch>().SingleOrDefaultAsync(row => row.ConversationId == conversation.Id &&
            row.MessageId == conversation.ActiveMessageId, ct);

    public async Task RequestCancellationAsync(ChatScope scope, ChatCancelRequest request, string actor, CancellationToken ct)
    {
        var bound = await ResolveScopeAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockBoundAsync(request.ConversationId, bound, actor, ct);
        if (!UsesProviderWorker(conversation) || conversation.State is ChatState.Idle or ChatState.Archived ||
            request.ExpectedMessageId != conversation.ActiveMessageId)
            throw new ChatConflictException("There is no matching OpenAI turn to cancel.");
        await profiles!.EnsureActorAsync(conversation, actor, ct);
        var dispatch = await ActiveDispatchAsync(conversation, ct)
            ?? throw new ChatConflictException("The active request has no cancellation binding.");
        if (dispatch.CancelRequestedAtUtc is not null) return;
        if (conversation.AiAssistantSessionId is null && dispatch.State != OpenAiDispatchState.Queued)
            throw new ChatConflictException("The remote session is unknown and cannot be cancelled.");
        dispatch.CancelRequestedAtUtc = Now;
        dispatch.NextObservationAtUtc = null;
        Append(db, conversation, "cancellation_requested", "Cancellation requested. RatelDesk is checking the outcome.", actor, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "CancellationRequested", ct);
    }

    public async Task RequestProviderCleanupAsync(ChatScope scope, Guid id, string actor, CancellationToken ct)
    {
        var bound = await ResolveScopeAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockBoundAsync(id, bound, actor, ct);
        if (conversation.ProviderKind != ChatProviderKind.OpenAiAgents || conversation.AiAssistantSessionId is null)
            throw new ChatConflictException("There is no OpenAI session to delete.");
        if (conversation.State is not (ChatState.Idle or ChatState.Archived))
            throw new ChatConflictException("Resolve the active work before deleting its remote session.");
        await profiles!.EnsureActorAsync(conversation, actor, ct);
        var grant = await db.Set<OpenAiChatSessionGrant>().SingleAsync(row => row.ConversationId == id, ct);
        if (grant.CleanupRequestedAtUtc is not null || grant.RemoteDeletedAtUtc is not null) return;
        grant.CleanupRequestedAtUtc = Now;
        Append(db, conversation, "remote_cleanup_requested", "Remote cleanup requested. The saved transcript and downloaded artifacts remain available.", actor, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "CleanupRequested", ct);
    }

    public async Task<IReadOnlyList<ChatProviderArtifact>> ProviderArtifactsAsync(ChatScope scope, Guid id, CancellationToken ct)
    {
        await RequireConversationAsync(scope, id, ct);
        return await db.Set<OpenAiChatArtifact>().AsNoTracking().Where(row => row.ConversationId == id)
            .OrderBy(row => row.Name).Select(row => new ChatProviderArtifact(row.Id, row.Name, row.SizeBytes, row.TurnId)).ToListAsync(ct);
    }

    public async Task<ProviderArtifactDownload> DownloadProviderArtifactAsync(ChatScope scope, Guid id, Guid artifactId, CancellationToken ct)
    {
        await RequireConversationAsync(scope, id, ct);
        return await db.Set<OpenAiChatArtifact>().AsNoTracking()
            .Where(row => row.ConversationId == id && row.Id == artifactId)
            .Select(row => new ProviderArtifactDownload(row.Name, row.Contents)).SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Artifact not found.");
    }

    private async Task RequireConversationAsync(ChatScope scope, Guid id, CancellationToken ct)
    {
        var bound = await ResolveScopeAsync(scope, ct);
        if (!await Query(bound).AnyAsync(row => row.Id == id, ct)) throw new KeyNotFoundException("Conversation not found.");
    }
}
