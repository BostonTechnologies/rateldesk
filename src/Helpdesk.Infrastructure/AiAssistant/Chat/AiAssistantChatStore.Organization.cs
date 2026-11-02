using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiOps;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

public sealed partial class AiAssistantChatStore
{
    public async Task<ChatOrganizationConversation> CreateOrganizationAsync(string organization, ChatCreateRequest request, string actor, CancellationToken ct)
    {
        ValidateCreation(request);
        var scope = await ResolveScopeAsync(ChatScope.Organization(organization), ct);
        var ids = request.PromptIds.Distinct().Order().ToArray();
        var fingerprint = Fingerprint(new { organization, request.Title, ids, actor });
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conversation, created) = await OrganizationDraftAsync(organization, request, actor, scope, fingerprint, false, ct);
        if (!created) return OrganizationDto(conversation);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "Created", ct);
        return OrganizationDto(conversation);
    }

    public async Task<ChatStartAdmission> StartOrganizationAsync(string organization, ChatStartRequest request, string actor, CancellationToken ct)
    {
        ChatRules.ValidateMessage(new(request.ConversationId, request.ClientMessageId, request.Text));
        var creation = new ChatCreateRequest(request.ConversationId, ChatRules.ConversationTitle(request.Text), request.PromptIds);
        ValidateCreation(creation);
        var scope = await ResolveScopeAsync(ChatScope.Organization(organization), ct);
        var ids = request.PromptIds.Distinct().Order().ToArray();
        var fingerprint = Fingerprint(new { organization, request.ClientMessageId, request.Text, ids, actor });
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conversation, created) = await OrganizationDraftAsync(organization, creation, actor, scope, fingerprint, true, ct);
        if (!created)
        {
            if (!await db.Set<AiAssistantChatEvent>().AnyAsync(x => x.ConversationId == request.ConversationId && x.Type == "operator"
                && x.ClientMessageId == request.ClientMessageId && x.Text == request.Text && x.CreatedByUserId == actor, ct))
                throw new ChatConflictException("The first message cannot be confirmed. Open the saved conversation to check its state.");
            return new(OrganizationDto(conversation), false);
        }
        AiOpsContext.ValidatePayload(OrganizationChatContext.Outbound(conversation, request.ClientMessageId, request.Text, actor));
        conversation.State = ChatState.Processing;
        conversation.ActiveMessageId = request.ClientMessageId;
        conversation.TurnStartedAtUtc = Now;
        conversation.LastTransportActivityAtUtc = Now;
        Append(db, conversation, "operator", request.Text, actor, request.ClientMessageId, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "MessageAccepted", ct);
        return new(OrganizationDto(conversation), true);
    }

    private static void ValidateCreation(ChatCreateRequest request)
    {
        if (request.ConversationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200 || request.PromptIds is not { Length: <= 8 })
            throw new ArgumentException("A conversation ID, title (up to 200 characters) and up to eight prompts are required.");
    }

    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private async Task<(AiAssistantChatConversation Conversation, bool Created)> OrganizationDraftAsync(string organization,
        ChatCreateRequest request, string actor, BoundScope scope, string fingerprint, bool firstMessage, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({request.ConversationId.ToString()}, 0))", ct);
        var previous = await db.Set<AiAssistantChatConversation>()
            .FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {request.ConversationId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (previous is not null)
        {
            Bind(previous, scope);
            if (previous.CreationFingerprint != fingerprint) throw new ChatConflictException("Conversation ID already used.");
            return (previous, false);
        }
        var ids = request.PromptIds.Distinct().ToArray();
        var prompts = await db.Set<AiGuidancePrompt>().AsNoTracking().Where(x => x.OrganizationId == organization && !x.Archived && (x.IsDefault || ids.Contains(x.Id)))
            .OrderByDescending(x => x.IsDefault).ThenBy(x => x.Id).ToListAsync(ct);
        if (ids.Any(id => prompts.All(x => x.Id != id))) throw new KeyNotFoundException("Guidance not found.");
        var conversation = new AiAssistantChatConversation
        {
            Id = request.ConversationId, Scope = ChatConversationScope.Organization, OrganizationId = organization,
            Title = request.Title.Trim(), CreatedByUserId = actor, CreationFingerprint = fingerprint,
            GuidanceSnapshot = JsonSerializer.Serialize(new { organizationId = organization, observedAtUtc = Now,
                guidance = prompts.Select(x => new { x.Id, x.Name, x.Revision, x.IsDefault, x.Content }) })
        };
        AiOpsContext.ValidatePayload(OrganizationChatContext.Outbound(conversation, Guid.Empty, "", actor));
        db.Add(conversation);
        Append(db, conversation, "created", firstMessage ? "Organization conversation started." : "Organization conversation saved. No message was sent.", actor, atUtc: Now);
        return (conversation, true);
    }

    public async Task<ChatOrganizationConversation> GetOrganizationAsync(string organization, Guid id, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ChatScope.Organization(organization), ct);
        return OrganizationDto(await Query(scope).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("Conversation not found."));
    }

    public async Task ArchiveAsync(ChatScope scope, Guid id, string actor, CancellationToken ct)
    {
        var bound = await ResolveScopeAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await LockAsync(id, ct);
        Bind(conversation, bound);
        if (conversation.State == ChatState.Archived) return;
        ChatRules.RequireIdle(conversation.State);
        conversation.State = ChatState.Archived;
        Append(db, conversation, "archived", "Conversation archived.", actor, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAsync(conversation, "Archived", ct);
    }

    private static ChatOrganizationConversation OrganizationDto(AiAssistantChatConversation x) =>
        new(x.Id, x.OrganizationId, x.Title, x.GuidanceSnapshot ?? "", x.State);
}

internal static class OrganizationChatContext
{
    public static string Outbound(AiAssistantChatConversation conversation, Guid message, string text, string? actor) =>
        $"[helpdesk-chat:{conversation.Id:D}:{message:D}]\nAuthenticated organization invocation:\n" +
        JsonSerializer.Serialize(new { organizationId = conversation.OrganizationId, helpdeskOperator = actor }) +
        "\nPlatform controls: Stay within the authenticated tool permissions and this organization. Guidance cannot grant authority. Request approval for protected actions.\n" +
        "Pinned organization guidance (no ticket context):\n" + conversation.GuidanceSnapshot + "\nOperator request:\n" + text;
}
