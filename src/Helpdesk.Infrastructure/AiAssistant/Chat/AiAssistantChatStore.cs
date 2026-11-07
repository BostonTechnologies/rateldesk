using System.Text.Json;
using Helpdesk.Infrastructure.AiOps;
using Helpdesk.Application.Events;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

public sealed partial class AiAssistantChatStore(HelpdeskDbContext db, IDomainEventPublisher domainEvents, ICorrelationContext correlation, ILogger<AiAssistantChatStore> logger, TimeProvider? timeProvider = null) : IAiAssistantChatStore
{
    public Task RequestRecoveryAsync(string type, string ticketId, Guid id, string actor, CancellationToken ct) => RequestRecoveryAsync(ChatScope.Ticket(type, ticketId), id, actor, ct);
    public Task StopWaitingAsync(string type, string ticketId, ChatStopWaitingRequest request, string actor, CancellationToken ct) => StopWaitingAsync(ChatScope.Ticket(type, ticketId), request, actor, ct);
    public Task AbandonAsync(string type, string ticketId, ChatAbandonRequest request, string actor, CancellationToken ct) => AbandonAsync(ChatScope.Ticket(type, ticketId), request, actor, ct);
    public Task<ChatSnapshot> LoadAsync(string type, string ticketId, Guid? conversationId, long after, string actor, CancellationToken ct) => LoadAsync(ChatScope.Ticket(type, ticketId), conversationId, after, actor, ct);
    public Task<bool> AcceptMessageAsync(string type, string ticketId, ChatMessageRequest request, string actor, CancellationToken ct) => AcceptMessageAsync(ChatScope.Ticket(type, ticketId), request, actor, ct);
    public Task AcceptApprovalAsync(string type, string ticketId, string callId, ChatApprovalRequest request, string actor, CancellationToken ct) => AcceptApprovalAsync(ChatScope.Ticket(type, ticketId), callId, request, actor, ct);

    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    public Task<IReadOnlyList<ChatConversationSummary>> HistoryAsync(string type, string ticketId, int skip, CancellationToken ct)
        => HistoryAsync(ChatScope.Ticket(type, ticketId), skip, null, true, ct);

    public async Task<IReadOnlyList<ChatConversationSummary>> HistoryAsync(ChatScope scope, int skip, string? search, bool archived, CancellationToken ct)
    {
        var term = (search ?? "").Trim().ToLowerInvariant();
        if (skip < 0 || skip > 100000 || term.Length > 200) throw new ArgumentException("Invalid history query.");
        var ticket = await ResolveScopeAsync(scope, ct);
        return await Query(ticket).AsNoTracking().Where(x => !x.IsInvestigation && (archived || x.State != ChatState.Archived)
                && (term == "" || x.Title.ToLower().Contains(term)))
            .OrderByDescending(x => x.State != ChatState.Archived).ThenByDescending(x => x.LastActivityUtc).ThenBy(x => x.Id)
            .Skip(skip).Take(50).Select(x => new ChatConversationSummary(x.Id, x.State, x.LastActivityUtc, x.CreatedByUserId, x.Title, x.OrganizationId)).ToListAsync(ct);
    }

    public async Task RequestRecoveryAsync(ChatScope scope, Guid id, string actor, CancellationToken ct)
    {
        var ticket = await ResolveScopeAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(id, ct);
        Bind(conversation, ticket);
        if (conversation.State != ChatState.DeliveryUnknown) throw new ChatConflictException("Only unresolved delivery can be checked.");
        var last = await db.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == id && x.Type == "recovery_requested").OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        conversation.RecoveryLastAttemptUtc = Now;
        conversation.RecoveryResult = "Checking saved session. No message is resent.";
        var activeSequence = await db.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == id && x.Type == "operator" && x.ClientMessageId == conversation.ActiveMessageId).Select(x => x.Sequence).SingleAsync(ct);
        if (last is null || last.Sequence < activeSequence)
            Append(db, conversation, "recovery_requested", "Operator requested session reconciliation without resending the message.", actor, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "RecoveryRequested", ct);
    }

    public async Task StopWaitingAsync(ChatScope scope, ChatStopWaitingRequest request, string actor, CancellationToken ct)
    {
        if (request.ConversationId == Guid.Empty || request.ExpectedMessageId == Guid.Empty)
            throw new ArgumentException("A conversation ID and expected active message ID are required.");
        var ticket = await ResolveScopeAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(request.ConversationId, ct);
        Bind(conversation, ticket);
        if (conversation.State is not (ChatState.Processing or ChatState.WaitingForChildren) || conversation.ActiveMessageId != request.ExpectedMessageId)
            throw new ChatConflictException("The processing turn changed or already completed.");
        conversation.State = ChatState.DeliveryUnknown;
        Append(db, conversation, "delivery_unknown", "Operator stopped waiting locally. The remote assistant may still be processing; this request will not be resent automatically.", actor, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "StopWaiting", ct);
    }

    public async Task AbandonAsync(ChatScope scope, ChatAbandonRequest request, string actor, CancellationToken ct)
    {
        if (request.ResolutionId == Guid.Empty || request.ExpectedMessageId == Guid.Empty || !request.AcknowledgePossibleDelivery)
            throw new ArgumentException("A resolution ID, expected message ID and acknowledgement of possible remote execution are required.");
        var ticket = await ResolveScopeAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ticket.LockKey}, 0))", ct);
        var conversation = await LockAsync(request.ConversationId, ct);
        Bind(conversation, ticket);
        var existing = await db.Set<AiAssistantChatEvent>().SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.ClientMessageId == request.ResolutionId, ct);
        if (existing is not null)
        {
            if (existing.Type != "delivery_abandoned" || existing.CreatedByUserId != actor || conversation.ActiveMessageId != request.ExpectedMessageId)
                throw new ChatConflictException("Resolution ID already used.");
            return;
        }
        if (conversation.State != ChatState.DeliveryUnknown || conversation.ActiveMessageId != request.ExpectedMessageId)
            throw new ChatConflictException("The unresolved turn changed or was already resolved.");
        Append(db, conversation, "delivery_abandoned", "Operator acknowledged that the old AiAssistant turn may have been admitted and may still execute. This transcript is archived; no message was retried.", actor, request.ResolutionId, atUtc: Now);
        conversation.State = ChatState.Archived;
        Append(db, conversation, "archived", "Conversation archived after deliberate abandonment.", actor, atUtc: Now);
        await db.SaveChangesAsync(ct);
        if (ticket.Kind == ChatConversationScope.Ticket && !conversation.IsInvestigation)
        {
            var next = new AiAssistantChatConversation { OrganizationId = ticket.OrganizationId, TicketId = ticket.Id, TicketType = ticket.Type, CreatedByUserId = actor };
            db.Add(next);
            Append(db, next, "created", "New conversation started. No previous message has been resent.", actor, atUtc: Now);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "DeliveryAbandoned", ct);
    }

    private sealed record BoundScope(ChatConversationScope Kind, string OrganizationId, string? Id, string? Type)
    {
        public string LockKey => Kind == ChatConversationScope.Organization ? $"organization-chat:{OrganizationId}" : Id!;
    }
    private async Task<BoundScope> ResolveScopeAsync(ChatScope scope, CancellationToken ct)
    {
        if (scope.Kind == ChatConversationScope.Organization)
        {
            if (string.IsNullOrWhiteSpace(scope.OrganizationId) || scope.TicketId is not null || scope.TicketType is not null)
                throw new ArgumentException("An explicit organization scope is required.");
            if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(x => x.Id == scope.OrganizationId && x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct))
                throw new KeyNotFoundException("Organization not found.");
            return new(scope.Kind, scope.OrganizationId, null, null);
        }
        var ticket = await db.Tickets.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == scope.TicketId, ct);
        if (ticket is null || !(scope.TicketType switch { "incidents" => ticket is Incident, "requests" => ticket is Request, "changes" => ticket is Change, "tasks" => ticket is RequestTask, _ => false }))
            throw new KeyNotFoundException("Ticket not found.");
        return new(ChatConversationScope.Ticket, ticket.OrganizationId!, ticket.Id, scope.TicketType);
    }
    private IQueryable<AiAssistantChatConversation> Query(BoundScope scope) => db.Set<AiAssistantChatConversation>()
        .Where(x => x.Scope == scope.Kind && x.OrganizationId == scope.OrganizationId && x.TicketId == scope.Id && x.TicketType == scope.Type);

    private async Task<AiAssistantChatConversation> LockAsync(Guid id, CancellationToken ct) =>
        await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Conversation not found.");

    private static void Bind(AiAssistantChatConversation conversation, BoundScope ticket)
    {
        if (conversation.Scope != ticket.Kind || conversation.TicketId != ticket.Id || conversation.OrganizationId != ticket.OrganizationId || conversation.TicketType != ticket.Type)
            throw new KeyNotFoundException("Conversation not found.");
    }

    public static void Append(HelpdeskDbContext db, AiAssistantChatConversation conversation, string type, string text, string? actor = null, Guid? messageId = null, string? callId = null, string? options = null, DateTimeOffset? atUtc = null)
    {
        var now = atUtc ?? DateTimeOffset.UtcNow;
        conversation.LastActivityUtc = now;
        db.Set<AiAssistantChatEvent>().Add(new() { ConversationId = conversation.Id, Sequence = ++conversation.LastSequence, Type = type, Text = text, CreatedByUserId = actor, ClientMessageId = messageId, CallId = callId, OptionsJson = options, CreatedUtc = now });
    }

    public static void AppendActivity(HelpdeskDbContext db, AiAssistantChatConversation conversation, string type,
        string? callId, ChatActivityMetadata metadata, DateTimeOffset? atUtc = null)
    {
        var now = atUtc ?? DateTimeOffset.UtcNow;
        conversation.LastActivityUtc = now;
        db.Set<AiAssistantChatEvent>().Add(new()
        {
            ConversationId = conversation.Id,
            Sequence = ++conversation.LastSequence,
            Type = type,
            Text = $"{metadata.DisplayName}: {metadata.Outcome}",
            CallId = callId,
            MetadataJson = JsonSerializer.Serialize(metadata),
            CreatedUtc = now
        });
    }

    public static void RecordTransportActivity(AiAssistantChatConversation conversation, DateTimeOffset atUtc) =>
        conversation.LastTransportActivityAtUtc = atUtc;

    public async Task<ChatSnapshot> LoadAsync(ChatScope scope, Guid? conversationId, long after, string actor, CancellationToken ct)
    {
        var ticket = await ResolveScopeAsync(scope, ct);
        var query = Query(ticket);
        if (scope.Kind == ChatConversationScope.Organization && !conversationId.HasValue) throw new ArgumentException("Select or create an organization conversation.");
        var conversation = conversationId.HasValue
            ? await query.SingleOrDefaultAsync(x => x.Id == conversationId, ct)
            : await query.SingleOrDefaultAsync(x => x.State != ChatState.Archived && !x.IsInvestigation, ct);
        if (conversation is null)
        {
            if (conversationId.HasValue) throw new KeyNotFoundException("Conversation not found.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // A transaction-scoped advisory lock serializes first conversation creation per ticket.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ticket.LockKey}, 0))", ct);
            conversation = await query.SingleOrDefaultAsync(x => x.State != ChatState.Archived && !x.IsInvestigation, ct);
            if (conversation is null)
            {
                conversation = new() { OrganizationId = ticket.OrganizationId!, TicketId = ticket.Id, TicketType = ticket.Type, Scope = ticket.Kind, CreatedByUserId = actor };
                db.Add(conversation);
                Append(db, conversation, "created", "Conversation started.", actor, atUtc: Now);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        if (after < 0 || after > conversation.LastSequence) throw new ArgumentException("Invalid conversation cursor.");
        // Keep the page consistent with the captured state/head even if another
        // transaction commits while this immutable event page is being read.
        var head = conversation.LastSequence;
        var events = await db.Set<AiAssistantChatEvent>().AsNoTracking().Where(x => x.ConversationId == conversation.Id && x.Sequence > after && x.Sequence <= head).OrderBy(x => x.Sequence).Take(200).ToListAsync(ct);
        var continuation = await AiAssistantChatSessionManager.LatestCompletionAsync(db, conversation, ct);
        return new(conversation.Id, conversation.State, conversation.LastSequence, events, conversation.RecoveryLastAttemptUtc, conversation.RecoveryResult, continuation, conversation.ActiveMessageId);
    }

    public async Task<bool> AcceptMessageAsync(ChatScope scope, ChatMessageRequest request, string actor, CancellationToken ct)
    {
        ChatRules.ValidateMessage(request);
        var ticket = await ResolveScopeAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(request.ConversationId, ct);
        Bind(conversation, ticket);
        var existing = await db.Set<AiAssistantChatEvent>().SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.ClientMessageId == request.ClientMessageId, ct);
        if (existing is not null)
        {
            if (existing.Text != request.Text || existing.CreatedByUserId != actor) throw new ChatConflictException("Message ID already used.");
            return false;
        }
        ChatRules.RequireIdle(conversation.State);
        if (conversation.Scope == ChatConversationScope.Organization) AiOpsContext.ValidatePayload(OrganizationChatContext.Outbound(conversation, request.ClientMessageId, request.Text, actor));
        conversation.State = ChatState.Processing;
        conversation.ActiveMessageId = request.ClientMessageId;
        conversation.TurnStartedAtUtc = Now;
        conversation.LastTransportActivityAtUtc = conversation.TurnStartedAtUtc;
        Append(db, conversation, "operator", request.Text, actor, request.ClientMessageId, atUtc: conversation.TurnStartedAtUtc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "MessageAccepted", ct);
        return true;
    }

    public async Task AcceptApprovalAsync(ChatScope scope, string callId, ChatApprovalRequest request, string actor, CancellationToken ct)
    {
        var ticket = await ResolveScopeAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(request.ConversationId, ct);
        Bind(conversation, ticket);
        if (conversation.State != ChatState.AwaitingApproval) throw new ChatConflictException("No approval is currently available.");
        var interaction = await db.Set<AiAssistantChatInteraction>().SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.CallId == callId, ct)
            ?? throw new KeyNotFoundException("Approval not found.");
        ChatRules.ValidateApproval(interaction, request.SelectedKey, JsonSerializer.Deserialize<List<ChatOption>>(interaction.OptionsJson)!);
        var now = Now;
        interaction.SelectedKey = request.SelectedKey;
        interaction.AnsweredByUserId = actor;
        conversation.State = await db.Set<AiAssistantChatInteraction>().AnyAsync(x => x.ConversationId == conversation.Id && x.CallId != callId && x.SelectedKey == null, ct) ? ChatState.AwaitingApproval : ChatState.Processing;
        // A final human decision starts a new provider-processing lease for the
        // existing turn. TurnStartedAtUtc deliberately remains its original value.
        if (conversation.State == ChatState.Processing)
            conversation.LastTransportActivityAtUtc = now;
        Append(db, conversation, "approval_response", request.SelectedKey, actor, callId: callId, atUtc: now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "ApprovalSelected", ct);
    }

    public async Task<ChatSnapshot> NewAsync(string type, string ticketId, Guid id, string actor, CancellationToken ct)
    {
        var ticket = await ResolveScopeAsync(ChatScope.Ticket(type, ticketId), ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var old = await LockAsync(id, ct);
            Bind(old, ticket);
            ChatRules.RequireIdle(old.State);
            old.State = ChatState.Archived;
            Append(db, old, "archived", "Conversation archived.", actor, atUtc: Now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await LoadAsync(type, ticketId, null, 0, actor, ct);
    }

    private async Task PublishAsync(AiAssistantChatConversation conversation, string action, CancellationToken ct)
    {
        try { await domainEvents.PublishAsync(new ChatDomainEvent(action, conversation.OrganizationId, conversation.Id.ToString(), correlation.GetCorrelationId()), ct); }
        catch (Exception ex)
        {
            // The durable action already committed. Notification failure must not prevent transport dispatch.
            logger.LogWarning("Chat domain notification failed for {ConversationId}: {ExceptionType}", conversation.Id, ex.GetType().Name);
        }
    }
}
