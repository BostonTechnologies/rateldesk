using System.Text.Json;
using Helpdesk.Application.Events;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

public sealed class AiAssistantChatStore(HelpdeskDbContext db, IDomainEventPublisher domainEvents, ICorrelationContext correlation, ILogger<AiAssistantChatStore> logger, TimeProvider? timeProvider = null) : IAiAssistantChatStore
{
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    public async Task<IReadOnlyList<ChatConversationSummary>> HistoryAsync(string type, string ticketId, int skip, CancellationToken ct)
    {
        if (skip < 0) throw new ArgumentException("Invalid history offset.");
        var ticket = await AuthorizeAsync(type, ticketId, ct);
        return await db.Set<AiAssistantChatConversation>().AsNoTracking()
            .Where(x => x.OrganizationId == ticket.OrganizationId && x.TicketId == ticket.Id && x.TicketType == type && !x.IsInvestigation)
            .OrderByDescending(x => x.State != ChatState.Archived).ThenByDescending(x => x.LastActivityUtc).ThenBy(x => x.Id)
            .Skip(skip).Take(50).Select(x => new ChatConversationSummary(x.Id, x.State, x.LastActivityUtc, x.CreatedByUserId)).ToListAsync(ct);
    }

    public async Task RequestRecoveryAsync(string type, string ticketId, Guid id, string actor, CancellationToken ct)
    {
        var ticket = await AuthorizeAsync(type, ticketId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(id, ct);
        Bind(conversation, ticket, type);
        if (conversation.State != ChatState.DeliveryUnknown) throw new ChatConflictException("Only unresolved delivery can be checked.");
        Append(db, conversation, "recovery_requested", "Operator requested session reconciliation without resending the message.", actor, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "RecoveryRequested", ct);
    }

    public async Task StopWaitingAsync(string type, string ticketId, ChatStopWaitingRequest request, string actor, CancellationToken ct)
    {
        if (request.ConversationId == Guid.Empty || request.ExpectedMessageId == Guid.Empty)
            throw new ArgumentException("A conversation ID and expected active message ID are required.");
        var ticket = await AuthorizeAsync(type, ticketId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(request.ConversationId, ct);
        Bind(conversation, ticket, type);
        if (conversation.State != ChatState.Processing || conversation.ActiveMessageId != request.ExpectedMessageId)
            throw new ChatConflictException("The processing turn changed or already completed.");
        conversation.State = ChatState.DeliveryUnknown;
        Append(db, conversation, "delivery_unknown", "Operator stopped waiting locally. The remote assistant may still be processing; this request will not be resent automatically.", actor, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "StopWaiting", ct);
    }

    public async Task AbandonAsync(string type, string ticketId, ChatAbandonRequest request, string actor, CancellationToken ct)
    {
        if (request.ResolutionId == Guid.Empty || request.ExpectedMessageId == Guid.Empty || !request.AcknowledgePossibleDelivery)
            throw new ArgumentException("A resolution ID, expected message ID and acknowledgement of possible remote execution are required.");
        var ticket = await AuthorizeAsync(type, ticketId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ticket.Id}, 0))", ct);
        var conversation = await LockAsync(request.ConversationId, ct);
        Bind(conversation, ticket, type);
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
        var next = new AiAssistantChatConversation { OrganizationId = ticket.OrganizationId!, TicketId = ticket.Id, TicketType = type, CreatedByUserId = actor };
        // Release the filtered active-conversation index before inserting its successor.
        await db.SaveChangesAsync(ct);
        db.Add(next);
        Append(db, next, "created", "New conversation started. No previous message has been resent.", actor, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "DeliveryAbandoned", ct);
    }

    private async Task<Ticket> AuthorizeAsync(string type, string id, CancellationToken ct)
    {
        var ticket = await db.Tickets.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (ticket is null || !(type switch { "incidents" => ticket is Incident, "requests" => ticket is Request, "changes" => ticket is Change, "tasks" => ticket is RequestTask, _ => false }))
            throw new KeyNotFoundException("Ticket not found.");
        return ticket;
    }

    private async Task<AiAssistantChatConversation> LockAsync(Guid id, CancellationToken ct) =>
        await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Conversation not found.");

    private static void Bind(AiAssistantChatConversation conversation, Ticket ticket, string type)
    {
        if (conversation.TicketId != ticket.Id || conversation.OrganizationId != ticket.OrganizationId || conversation.TicketType != type)
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

    public async Task<ChatSnapshot> LoadAsync(string type, string ticketId, Guid? conversationId, long after, string actor, CancellationToken ct)
    {
        var ticket = await AuthorizeAsync(type, ticketId, ct);
        var query = db.Set<AiAssistantChatConversation>().Where(x => x.OrganizationId == ticket.OrganizationId && x.TicketId == ticket.Id && x.TicketType == type);
        var conversation = conversationId.HasValue
            ? await query.SingleOrDefaultAsync(x => x.Id == conversationId, ct)
            : await query.SingleOrDefaultAsync(x => x.State != ChatState.Archived && !x.IsInvestigation, ct);
        if (conversation is null)
        {
            if (conversationId.HasValue) throw new KeyNotFoundException("Conversation not found.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // A transaction-scoped advisory lock serializes first conversation creation per ticket.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ticket.Id}, 0))", ct);
            conversation = await query.SingleOrDefaultAsync(x => x.State != ChatState.Archived && !x.IsInvestigation, ct);
            if (conversation is null)
            {
                conversation = new() { OrganizationId = ticket.OrganizationId!, TicketId = ticket.Id, TicketType = type, CreatedByUserId = actor };
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
        return new(conversation.Id, conversation.State, conversation.LastSequence, events);
    }

    public async Task<bool> AcceptMessageAsync(string type, string ticketId, ChatMessageRequest request, string actor, CancellationToken ct)
    {
        ChatRules.ValidateMessage(request);
        var ticket = await AuthorizeAsync(type, ticketId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(request.ConversationId, ct);
        Bind(conversation, ticket, type);
        var existing = await db.Set<AiAssistantChatEvent>().SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.ClientMessageId == request.ClientMessageId, ct);
        if (existing is not null)
        {
            if (existing.Text != request.Text || existing.CreatedByUserId != actor) throw new ChatConflictException("Message ID already used.");
            return false;
        }
        ChatRules.RequireIdle(conversation.State);
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

    public async Task AcceptApprovalAsync(string type, string ticketId, string callId, ChatApprovalRequest request, string actor, CancellationToken ct)
    {
        var ticket = await AuthorizeAsync(type, ticketId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(request.ConversationId, ct);
        Bind(conversation, ticket, type);
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
        var ticket = await AuthorizeAsync(type, ticketId, ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var old = await LockAsync(id, ct);
            Bind(old, ticket, type);
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
