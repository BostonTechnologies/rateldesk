using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.AiAssistant.Chat;

public static partial class AiAssistantChatEndpoints
{
    // Called only after the enclosing organization's or ticket's current access check.
    // Reading diagnostics never creates or resumes a native session.
    private static async Task<IResult> ConnectionAsync(ChatScope scope, Guid? selected, HttpContext context,
        HelpdeskDbContext db, IAiAssistantChatRuntimeState runtime, IAiAssistantChatTransport transport,
        ICurrentUserAccessService accessService, CancellationToken ct)
    {
        var profile = runtime.Current;
        var organization = scope.OrganizationId ?? await db.Tickets.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == scope.TicketId).Select(x => x.OrganizationId).SingleAsync(ct);
        var query = db.Set<AiAssistantChatConversation>().AsNoTracking().Where(x => x.OrganizationId == organization && x.Scope == scope.Kind);
        if (scope.Kind == ChatConversationScope.Ticket)
            query = query.Where(x => x.TicketId == scope.TicketId && x.TicketType == scope.TicketType!.ToLowerInvariant());
        if (selected is { } id)
        {
            if (!await query.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            query = query.Where(x => x.Id == id);
        }
        else if (scope.Kind == ChatConversationScope.Ticket)
            query = query.Where(x => false); // Other investigations may have additional pinned-context grants.
        var access = await accessService.ResolveAsync(context.User, ct);
        var providerProfiles = context.RequestServices.GetService<OpenAiChatProfiles>();
        if (providerProfiles is not null && db.Database.IsNpgsql())
        {
            var pinned = selected is null ? null : await query.SingleAsync(ct);
            if (pinned is { ProviderKind: ChatProviderKind.CodexAppServer } && pinned.CreatedByUserId != Actor(context))
                return Results.NotFound();
            var provider = pinned is null ? (await providerProfiles.AvailableAsync(organization, ct)).SingleOrDefault(item => item.IsDefault)
                : await providerProfiles.DescribeAsync(pinned, ct);
            if (provider is { Kind: ChatProviderKind.OpenAiAgents or ChatProviderKind.CodexAppServer })
                return Results.Ok(new ChatConnectionStatus(organization, provider.Enabled, null,
                    provider.Kind == ChatProviderKind.CodexAppServer ? "codex-app-server" : "openai-agents",
                    provider.Revision, provider.LastVerifiedAtUtc, null, DateTimeOffset.UtcNow, false, [],
                    access.IsHelpdeskAdmin ? ProviderSetupUrl : null, provider));
        }
        var ids = await query.Where(x => x.State != ChatState.Archived && x.ProviderProfileFingerprint == profile.ProfileFingerprint)
            .OrderByDescending(x => x.LastActivityUtc).Select(x => x.Id).Take(50).ToArrayAsync(ct);
        var sessions = ids.Select(transport.ObserveConnection).OfType<ChatSessionConnection>().ToArray();
        var verified = profile.Source == "database"
            ? await db.NetclawConnectivitySettings.AsNoTracking()
                .Where(x => x.ProviderKey == "Netclaw" && x.Revision == profile.Revision && x.ProfileFingerprint == profile.ProfileFingerprint)
                .Select(x => new { x.LastTestedAtUtc, x.LastTestSucceeded }).FirstOrDefaultAsync(ct)
            : null;
        var endpoint = Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Authority) : null;
        return Results.Ok(new ChatConnectionStatus(organization, db.Database.IsNpgsql() && profile.Enabled, endpoint,
            profile.Source, profile.Revision, verified?.LastTestedAtUtc, verified?.LastTestSucceeded, DateTimeOffset.UtcNow,
            transport.ConnectionObservationsAvailable, sessions, access.IsHelpdeskAdmin ? ProviderSetupUrl : null));
    }
}
