using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
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
        var ids = await query.Where(x => x.State != ChatState.Archived && x.ProviderProfileFingerprint == profile.ProfileFingerprint)
            .OrderByDescending(x => x.LastActivityUtc).Select(x => x.Id).Take(50).ToArrayAsync(ct);
        var sessions = ids.Select(transport.ObserveConnection).OfType<ChatSessionConnection>().ToArray();
        var verified = profile.Source == "database"
            ? await db.NetclawConnectivitySettings.AsNoTracking()
                .Where(x => x.ProviderKey == "Netclaw" && x.Revision == profile.Revision && x.ProfileFingerprint == profile.ProfileFingerprint)
                .Select(x => new { x.LastTestedAtUtc, x.LastTestSucceeded }).FirstOrDefaultAsync(ct)
            : null;
        var access = await accessService.ResolveAsync(context.User, ct);
        var endpoint = Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Authority) : null;
        return Results.Ok(new ChatConnectionStatus(organization, db.Database.IsNpgsql() && profile.Enabled, endpoint,
            profile.Source, profile.Revision, verified?.LastTestedAtUtc, verified?.LastTestSucceeded, DateTimeOffset.UtcNow,
            transport.ConnectionObservationsAvailable, sessions, access.IsHelpdeskAdmin ? "/admin/automation/integration/netclaw" : null));
    }
}
