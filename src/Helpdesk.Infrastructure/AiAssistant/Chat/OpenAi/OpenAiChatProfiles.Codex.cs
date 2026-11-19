using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.Codex;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

public sealed partial class OpenAiChatProfiles
{
    public async Task<OpenAiChatProfileEditor> GetCodexAsync(Guid id, string ownerId, CancellationToken ct)
    {
        var profile = await db.Set<OpenAiChatProfile>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id &&
            db.Set<CodexConnection>().Any(connection => connection.Id == row.CodexConnectionId && connection.OwnerId == ownerId), ct)
            ?? throw new KeyNotFoundException("Provider profile not found.");
        var revision = await GetRevisionAsync(id, profile.Revision, ct);
        return Editor(profile, revision, Read(revision.SettingsJson), await DefaultAsync(ct));
    }

    public async Task<OpenAiChatProfileEditor> SaveCodexAsync(CodexProfileUpdate update, string owner, string[] resources, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() || codex is null || update.Id == Guid.Empty || update.Id == NetclawId || update.ExpectedRevision < 0)
            throw new ArgumentException("Choose a valid Codex runtime profile.");
        var selected = await codex.OwnedAsync(update.ConnectionId, owner, update.OrganizationId, ct);
        var profile = await db.Set<OpenAiChatProfile>().SingleOrDefaultAsync(row => row.Id == update.Id, ct);
        if ((profile?.Revision ?? 0) != update.ExpectedRevision)
            throw new ChatConflictException("The provider profile changed. Reload it before saving.");
        if (profile is not null)
        {
            if (profile.CodexConnectionId is not { } previousId)
                throw new ArgumentException("Choose a new profile for your ChatGPT plan.");
            var previous = await codex.OwnedAsync(previousId, owner, update.OrganizationId, ct);
            if (previousId != selected.Id && previous.State == CodexConnectionState.Connected)
                throw new ChatConflictException("Disconnect the previous ChatGPT connection before replacing it.");
        }
        if (await db.Set<OpenAiChatProfile>().AnyAsync(row => row.CodexConnectionId == selected.Id && row.Id != update.Id, ct))
            throw new ChatConflictException("This ChatGPT connection is already assigned to a profile.");
        if (!resources.Contains(update.McpResource, StringComparer.Ordinal))
            throw new ArgumentException("Select a registered HTTP MCP gateway.");

        var settings = Validate(new(update.Name, update.Enabled, update.OrganizationId, update.Model, "", null, null, null,
            update.McpResource, update.ReadOperations, false, ChatProviderEnvironment.None, null, null, 1, update.ObservationMinutes, 1)
        { CodexConnectionId = selected.Id, ReasoningEffort = update.ReasoningEffort });
        if (settings.Enabled)
        {
            var catalog = await codex.ModelsAsync(selected.Id, owner, selected.OrganizationId, ct);
            var model = catalog.Models.SingleOrDefault(item => item.Id == settings.Model);
            if (model is null || settings.ReasoningEffort is { } effort && !model.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
                throw new ArgumentException("Choose a model and reasoning effort available for this ChatGPT connection.");
        }
        else if (settings.ReasoningEffort is { } effort && !CodexWire.IsReasoningEffort(effort))
        {
            throw new ArgumentException("Choose a supported reasoning effort.");
        }

        if (profile is null)
        {
            profile = new OpenAiChatProfile { Id = update.Id };
            db.Add(profile);
        }
        profile.CodexConnectionId = selected.Id;
        profile.Enabled = settings.Enabled;
        profile.Revision++;
        var revision = new OpenAiChatProfileRevision { ProfileId = profile.Id, Revision = profile.Revision,
            SettingsJson = JsonSerializer.Serialize(settings, Json.OpenAiChatProfileSettings) };
        db.Add(revision);
        await db.SaveChangesAsync(ct);
        return Editor(profile, revision, settings, await DefaultAsync(ct));
    }

    internal string ProtectCodexGrant(OpenAiChatSessionGrant grant, string token) =>
        protector.Protect(token, CodexGrantBinding(grant));
    internal string ReadCodexGrant(OpenAiChatSessionGrant grant) =>
        protector.Unprotect(grant.ProtectedCodexToken ?? "", CodexGrantBinding(grant));
    private static string CodexGrantBinding(OpenAiChatSessionGrant grant) =>
        $"Codex MCP:{grant.ConversationId:D}:{grant.CredentialId:D}";
}
