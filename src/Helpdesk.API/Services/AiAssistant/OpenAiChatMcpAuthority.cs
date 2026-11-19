using System.Net;
using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services.AiAssistant;

internal sealed class OpenAiChatMcpAuthority(IIntegrationCredentialOwnerResolver operators,
    IHttpContextAccessor context, ICurrentUserAccessService access, IntegrationCredentialIssuer issuer,
    RatelDeskIdentityDbContext identity, HelpdeskDbContext db, IRequestSender commands, TimeProvider clock) : IChatProviderMcpAuthority
{
    private static readonly string[] TicketPermissions =
    [
        HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentManager,
        HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager,
        HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeManager
    ];

    internal static IReadOnlyList<McpPolicyOperation> ReadOperations { get; } = McpPolicyDefinitionService.Operations
        .Where(operation => operation.Effect == "read" && operation.Family is "Incidents" or "Requests" or "Changes" or "Tickets")
        .ToArray();

    public async Task<string> ResolveOperatorAsync(string organization, CancellationToken ct)
    {
        var principal = context.HttpContext?.User;
        if (principal is null || await operators.ResolveAsync(principal, ct) is not { } owner)
            throw new ChatConflictException("Use an enabled RatelDesk account to control this provider session.");
        var profile = await access.ResolveAsync(principal, ct);
        if (!TicketPermissions.Any(permission => profile.HasPermission(permission, organization)))
            throw new ChatConflictException("This account cannot operate an AI provider session in this organization.");
        return owner.UserId;
    }

    public async Task<IssuedChatMcpGrant> IssueAsync(AiAssistantChatConversation conversation, OpenAiChatProfileSettings profile,
        Guid credentialId, CancellationToken ct)
    {
        var binding = await db.Set<OpenAiChatSessionGrant>().AsNoTracking().SingleAsync(row =>
            row.CredentialId == credentialId && row.ConversationId == conversation.Id, ct);
        if (conversation.ProviderActorId is not { } ownerId || conversation.OrganizationId != profile.OrganizationId)
            throw new ChatConflictException("The OpenAI session has no authorized actor or organization.");

        var principal = await LoadAccountAsync(ownerId, ct);
        var current = await access.ResolveAsync(principal, ct);
        var permissions = TicketPermissions.Where(permission => current.HasPermission(permission, conversation.OrganizationId)).ToArray();
        if (!current.IsAuthenticated || permissions.Length == 0)
            throw new ChatConflictException("The session operator no longer has organization access.");
        if (profile.ReadOperations.Except(ReadOperations.Select(operation => operation.Key), StringComparer.Ordinal).Any())
            throw new ChatConflictException("The AI Providers profile contains an unsupported MCP operation.");

        var now = clock.GetUtcNow();
        var validated = new ValidatedCredentialGrant(ownerId, $"OpenAI session {conversation.Id:D}", "mcp",
            conversation.OrganizationId, permissions, 1, profile.McpResource) { ExpiresAtUtc = binding.ExpiresAtUtc };
        var (record, delivery) = issuer.Create(validated, now, credentialId);
        var (policy, audit) = McpPolicyDefinitionService.Create(record,
            [new("AI provider session reads", "organization", conversation.OrganizationId, "Allow",
                permissions, profile.ReadOperations, binding.ExpiresAtUtc.UtcDateTime, [])], ownerId, now.UtcDateTime);

        db.Add(policy);
        db.Add(audit);
        await db.SaveChangesAsync(ct);
        issuer.Track(record);
        await identity.SaveChangesAsync(ct);
        return new(delivery.Secret);
    }

    public async Task<bool> IsGrantUsableAsync(AiAssistantChatConversation conversation, Guid credentialId, CancellationToken ct)
    {
        var credential = await ReadCredentialAsync(credentialId, ct);
        if (credential is null || credential.OwnerUserId != conversation.ProviderActorId ||
            credential.OrganizationId != conversation.OrganizationId ||
            !await ValidateBindingAsync(db, credential, ct))
            return false;
        var current = await LoadCredentialAccessAsync(credential, ct);
        return current.IsAuthenticated && current.AllowedOrganizationIds.Contains(conversation.OrganizationId) && current.Permissions.Count > 0;
    }

    public async Task<ProviderDecision> WriteInternalNoteAsync(AiAssistantChatConversation conversation, Guid credentialId,
        string incidentId, string note, CancellationToken ct)
    {
        if (conversation.ProviderKind != ChatProviderKind.OpenAiAgents ||
            !await IsGrantUsableAsync(conversation, credentialId, ct) || conversation.ProviderActorId is not { } actorId)
            return new(false, "The session's tool authority has expired or changed.");
        var principal = await LoadAccountAsync(actorId, ct);
        var credential = await ReadCredentialAsync(credentialId, ct);
        if (credential is null || !(await LoadCredentialAccessAsync(credential, ct)).CanManageIncident(conversation.OrganizationId))
            return new(false, "The original operator no longer has permission to add an incident note.");
        var ticket = await db.Incidents.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == incidentId && row.OrganizationId == conversation.OrganizationId, ct);
        if (ticket is null || (conversation.TicketId is { } anchor && anchor != ticket.Id))
            return new(false, "The incident is outside this conversation's authorized scope.");

        var log = await commands.Send(new CreateWorkLogCommand(ticket.Id, 0,
            WebUtility.HtmlEncode(note).ReplaceLineEndings("<br>"), actorId, principal.Identity?.Name,
            NotifyCustomer: false, EventType: TimelineEventType.SystemNotification, IsInternalNote: true,
            AuthorizedTicket: ticket), ct);
        return new(true, $"Added internal note {log.Id} to incident {ticket.Id}.");
    }

    public async Task RevokeAsync(Guid credentialId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await identity.IntegrationCredentials.Where(row => row.Id == credentialId && row.RevokedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.RevokedAtUtc, now), ct);
    }

    private Task<IntegrationCredential?> ReadCredentialAsync(Guid id, CancellationToken ct) =>
        identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.Purpose == "mcp" &&
            row.RevokedAtUtc == null && row.ExpiresAtUtc > clock.GetUtcNow(), ct);

    private Task<CurrentUserAccessProfile> LoadCredentialAccessAsync(IntegrationCredential credential, CancellationToken ct)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, credential.OwnerUserId),
            new("auth_mode", "integration"),
            new("integration_credential_id", credential.Id.ToString("N")),
            new("integration_purpose", "mcp"),
            new("integration_organization_id", credential.OrganizationId ?? ""),
            new("mcp_resource_uri", credential.McpResourceUri ?? "")
        };
        claims.AddRange(credential.Permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(permission => new Claim("integration_permission", permission)));
        return access.ResolveAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "OpenAiMcpGrant")), ct);
    }

    private async Task<ClaimsPrincipal> LoadAccountAsync(string id, CancellationToken ct)
    {
        var user = await identity.Users.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.IsEnabled && !row.IsDeleted, ct);
        return user is null ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.DisplayName ?? user.UserName ?? user.Id),
            new Claim("auth_mode", "local")
        ], "RatelDeskAccount"));
    }

    internal static Task<bool> IsSessionCredentialAsync(HelpdeskDbContext db, Guid credentialId, CancellationToken ct) =>
        db.Set<OpenAiChatSessionGrant>().AnyAsync(grant => grant.CredentialId == credentialId, ct);

    internal static async Task<bool> ValidateBindingAsync(HelpdeskDbContext db, IntegrationCredential credential, CancellationToken ct)
    {
        var grant = await db.Set<OpenAiChatSessionGrant>().AsNoTracking().SingleOrDefaultAsync(
            row => row.CredentialId == credential.Id, ct);
        if (grant is null) return true;
        var now = DateTimeOffset.UtcNow;
        if (grant.ExpiresAtUtc <= now || credential.ExpiresAtUtc <= now || credential.RevokedAtUtc is not null)
            return false;
        var binding = await (from conversation in db.Set<AiAssistantChatConversation>().AsNoTracking()
                             join profile in db.Set<OpenAiChatProfile>().AsNoTracking() on conversation.ProviderProfileId equals profile.Id
                             where conversation.Id == grant.ConversationId &&
                                   (conversation.ProviderKind == ChatProviderKind.OpenAiAgents && profile.CodexConnectionId == null ||
                                    conversation.ProviderKind == ChatProviderKind.CodexAppServer && db.Set<CodexConnection>().Any(connection =>
                                        connection.Id == profile.CodexConnectionId && connection.State == CodexConnectionState.Connected &&
                                        connection.OwnerId == credential.OwnerUserId && connection.OrganizationId == credential.OrganizationId)) &&
                                   conversation.ProviderActorId == credential.OwnerUserId && conversation.OrganizationId == credential.OrganizationId &&
                                   conversation.State != ChatState.Archived && profile.Enabled && conversation.ProviderRevision == profile.Revision
                             join revision in db.Set<OpenAiChatProfileRevision>().AsNoTracking()
                                 on new { ProfileId = profile.Id, profile.Revision } equals new { revision.ProfileId, revision.Revision }
                             select revision.SettingsJson).SingleOrDefaultAsync(ct);
        if (binding is null) return false;
        var policy = await db.Set<McpAccessPolicy>().AsNoTracking().SingleOrDefaultAsync(row => row.CredentialId == credential.Id, ct);
        if (policy is not { State: "Active" } || policy.ExpiresUtc <= now.UtcDateTime) return false;

        using var settings = System.Text.Json.JsonDocument.Parse(binding);
        var resource = settings.RootElement.GetProperty(nameof(OpenAiChatProfileSettings.McpResource)).GetString();
        var reads = settings.RootElement.GetProperty(nameof(OpenAiChatProfileSettings.ReadOperations)).EnumerateArray()
            .Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        var definition = McpPolicyDefinitionDocumentReader.Read(policy.DefinitionJson);
        return resource == credential.McpResourceUri && definition is not null && definition.Resource == resource &&
            definition.Scopes.Single() is { AllAccess: false, Scope: "organization", Effect: "Allow" } scope &&
            scope.OrganizationId == credential.OrganizationId && scope.ExpiresUtc > now.UtcDateTime &&
            scope.Operations.Length > 0 && scope.Operations.All(reads.Contains);
    }
}
