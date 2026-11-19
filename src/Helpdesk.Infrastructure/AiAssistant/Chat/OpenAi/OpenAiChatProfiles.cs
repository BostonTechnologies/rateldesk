using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.Codex;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.AiAssistant.Chat;
using EntityState = Helpdesk.Shared.Models.EntityState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

public sealed partial class OpenAiChatProfiles(HelpdeskDbContext db, IAiAssistantChatRuntimeState netclaw,
    IntegrationProviderSecretProtector protector, IConfiguration configuration, TimeProvider clock, IChatProviderMcpAuthority? authority = null,
    CodexConnections? codex = null, CodexAppServerHost? codexHost = null) : IChatProviderProfiles
{
    public static readonly Guid NetclawId = new("90b1a190-f989-4cf8-ac56-0ce23b566840");
    private static readonly OpenAiProfileJson Json = OpenAiProfileJson.Default;
    private const int ActiveDefault = 1;

    public async Task<IReadOnlyList<ChatProviderProfile>> ListAsync(CancellationToken ct, string? codexOwner = null)
    {
        var id = await DefaultAsync(ct);
        var net = netclaw.Current;
        var profiles = new List<ChatProviderProfile>
        {
            new(NetclawId, ChatProviderKind.Netclaw, "Netclaw", net.Enabled, id == NetclawId, null, null, net.Revision,
                ChatProviderEnvironment.None, ManagedByDeployment: net.ManagedByDeployment)
        };
        var rows = await (from profile in db.Set<OpenAiChatProfile>().AsNoTracking()
                          join revision in db.Set<OpenAiChatProfileRevision>().AsNoTracking()
                              on new { ProfileId = profile.Id, profile.Revision } equals new { revision.ProfileId, revision.Revision }
                          where profile.CodexConnectionId == null || db.Set<CodexConnection>().Any(connection =>
                              connection.Id == profile.CodexConnectionId && connection.OwnerId == codexOwner)
                          orderby profile.Id
                          select new { profile, revision.SettingsJson }).ToListAsync(ct);
        profiles.AddRange(rows.Select(row => ToProfile(row.profile, Read(row.SettingsJson), id)));
        return profiles;
    }

    public async Task<IReadOnlyList<ChatProviderProfile>> AvailableAsync(string organization, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return [];
        string? owner = null;
        if (codexHost?.Configured == true && authority is not null &&
            await db.Set<CodexConnection>().AnyAsync(row => row.OrganizationId == organization && row.State == CodexConnectionState.Connected, ct))
        {
            try { owner = await authority.ResolveOperatorAsync(organization, ct); }
            catch (ChatConflictException) { owner = null; }
        }
        var profiles = (await ListAsync(ct, owner)).Where(x => x.Enabled && (x.OrganizationId is null || x.OrganizationId == organization)).ToList();
        var activeConnections = owner is null ? [] : await db.Set<CodexConnection>().Where(row => row.OwnerId == owner &&
            row.OrganizationId == organization && row.State == CodexConnectionState.Connected).Select(row => row.Id).ToArrayAsync(ct);
        if (profiles.Any(row => row.Kind == ChatProviderKind.CodexAppServer))
        {
            var linked = await db.Set<OpenAiChatProfile>().Where(row => row.CodexConnectionId != null &&
                activeConnections.Contains(row.CodexConnectionId.Value)).Select(row => row.Id).ToListAsync(ct);
            profiles.RemoveAll(row => row.Kind == ChatProviderKind.CodexAppServer && !linked.Contains(row.Id));
        }
        if (profiles.Count == 0 || profiles.Any(x => x.IsDefault)) return profiles;
        var configured = await DefaultAsync(ct);
        if (await db.Set<OpenAiChatProfile>().AnyAsync(row => row.Id == configured && row.CodexConnectionId != null, ct))
            return profiles;
        var fallback = profiles.FindIndex(row => row.Kind != ChatProviderKind.CodexAppServer);
        if (fallback >= 0) profiles[fallback] = profiles[fallback] with { IsDefault = true };
        return profiles;
    }

    public async Task BindNewAsync(AiAssistantChatConversation conversation, Guid? selectedProfileId, CancellationToken ct)
    {
        var profiles = await AvailableAsync(conversation.OrganizationId, ct);
        var profile = selectedProfileId is { } id ? profiles.SingleOrDefault(x => x.Id == id) : profiles.SingleOrDefault(x => x.IsDefault);
        if (profile is null)
            throw new ChatConflictException("No selected AI provider profile is available for this organization.");

        conversation.ProviderKind = profile.Kind;
        conversation.ProviderProfileId = profile.Id;
        conversation.ProviderRevision = profile.Revision;
        if (profile.Kind == ChatProviderKind.Netclaw)
            conversation.ProviderProfileFingerprint = netclaw.Current.ProfileFingerprint;
        else
            conversation.ProviderActorId = await (authority ?? throw new InvalidOperationException("The AI tool authority is unavailable."))
                .ResolveOperatorAsync(conversation.OrganizationId, ct);
    }

    public async Task EnsureAdmissionAsync(AiAssistantChatConversation conversation, string actor, CancellationToken ct)
    {
        if (conversation.ProviderKind == ChatProviderKind.Netclaw)
        {
            if (!netclaw.Current.Enabled)
                throw new ChatConflictException("Enable Netclaw to send to this conversation.");
            return;
        }
        await EnsureActorAsync(conversation, actor, ct);
        if (!await IsCurrentAsync(conversation, ct))
            throw new ChatConflictException("This provider profile changed or is disabled. Start a new conversation using an available profile.");
    }

    public async Task EnsureActorAsync(AiAssistantChatConversation conversation, string actor, CancellationToken ct)
    {
        if (conversation.ProviderKind == ChatProviderKind.Netclaw) return;
        if (conversation.ProviderKind is not (ChatProviderKind.OpenAiAgents or ChatProviderKind.CodexAppServer))
            throw new ChatConflictException("The original conversation provider is unsupported.");
        if (conversation.CreatedByUserId != actor)
            throw new ChatConflictException("This conversation is bound to its original operator. Start a new conversation for your own tool access.");
        if (authority is null || conversation.ProviderActorId != await authority.ResolveOperatorAsync(conversation.OrganizationId, ct))
            throw new ChatConflictException("The original operator account is no longer authorized.");
    }

    internal Task<bool> IsCurrentAsync(AiAssistantChatConversation row, CancellationToken ct)
        => db.Set<OpenAiChatProfile>().AnyAsync(x => x.Id == row.ProviderProfileId && x.Revision == row.ProviderRevision && x.Enabled &&
            (row.ProviderKind == ChatProviderKind.OpenAiAgents && x.CodexConnectionId == null ||
             row.ProviderKind == ChatProviderKind.CodexAppServer && db.Set<CodexConnection>().Any(connection =>
                 connection.Id == x.CodexConnectionId && connection.State == CodexConnectionState.Connected &&
                 connection.OwnerId == row.ProviderActorId && connection.OrganizationId == row.OrganizationId)), ct);

    public async Task<ChatProviderProfile?> DescribeAsync(AiAssistantChatConversation conversation, CancellationToken ct)
    {
        if (conversation.ProviderKind is not (ChatProviderKind.OpenAiAgents or ChatProviderKind.CodexAppServer)) return null;
        if (conversation.ProviderKind == ChatProviderKind.CodexAppServer &&
            (authority is null || conversation.ProviderActorId != await authority.ResolveOperatorAsync(conversation.OrganizationId, ct)))
            throw new KeyNotFoundException("ChatGPT conversation not found.");
        var (settings, revision) = await ResolveAsync(conversation, ct);
        var current = await db.Set<OpenAiChatProfile>().AsNoTracking().SingleAsync(item => item.Id == revision.ProfileId, ct);
        var pinned = new OpenAiChatProfile { Id = revision.ProfileId, Revision = revision.Revision,
            Enabled = await IsCurrentAsync(conversation, ct), CodexConnectionId = settings.CodexConnectionId,
            LastVerifiedAtUtc = current.Revision == revision.Revision ? current.LastVerifiedAtUtc : null,
            LastVerification = current.Revision == revision.Revision ? current.LastVerification : null };
        return ToProfile(pinned, settings, await DefaultAsync(ct));
    }

    public async Task<OpenAiChatProfileEditor> GetAsync(Guid id, CancellationToken ct)
    {
        var profile = await db.Set<OpenAiChatProfile>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new KeyNotFoundException("Provider profile not found.");
        if (profile.CodexConnectionId is not null) throw new KeyNotFoundException("Provider profile not found.");
        var revision = await GetRevisionAsync(id, profile.Revision, ct);
        var settings = Read(revision.SettingsJson);
        return Editor(profile, revision, settings, await DefaultAsync(ct));
    }

    public async Task<OpenAiChatProfileEditor> SaveAsync(OpenAiChatProfileUpdate update, CancellationToken ct)
    {
        if (update.Id == Guid.Empty || update.Id == NetclawId || update.ExpectedRevision < 0)
            throw new ArgumentException("Choose a valid provider profile identity and revision.");
        if (!db.Database.IsNpgsql() && update.Settings.Enabled)
            throw new ArgumentException("Native AI Providers require PostgreSQL.");
        var profile = await db.Set<OpenAiChatProfile>().SingleOrDefaultAsync(row => row.Id == update.Id, ct);
        if (profile?.CodexConnectionId is not null || update.Settings.CodexConnectionId is not null || update.Settings.ReasoningEffort is not null)
            throw new ArgumentException("Use the ChatGPT setup to edit a Codex runtime profile.");
        if ((profile?.Revision ?? 0) != update.ExpectedRevision)
            throw new ChatConflictException("The provider profile changed. Reload it before saving.");
        var previous = profile is null ? null : await GetRevisionAsync(profile.Id, profile.Revision, ct);
        var oldSettings = previous is null ? null : Read(previous.SettingsJson);
        if (oldSettings is not null && oldSettings.OrganizationId != update.Settings.OrganizationId)
            throw new ArgumentException("Create a separate provider profile for a different organization.");
        var action = KeyAction(update.Credential, update.Settings.KeyReference, oldSettings);
        var settings = Validate(update.Settings with
        {
            KeyReference = action switch
            {
                OpenAiKeyAction.Replace or OpenAiKeyAction.Clear => "",
                OpenAiKeyAction.Keep => oldSettings?.KeyReference ?? "",
                _ => update.Settings.KeyReference
            }
        });
        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(x => x.Id == settings.OrganizationId && x.State == EntityState.Enabled, ct))
            throw new ArgumentException("Choose an enabled organization.");

        var key = ReadSetupKey(action, update.Credential, settings.KeyReference, profile, previous);
        if (settings.Enabled && !ValidKey(key))
            throw new ArgumentException("Connect a valid OpenAI project key before enabling this profile.");
        if (update.SkillArchive is not null && (update.ClearSkillArchive || settings.SkillBundle is not null ||
            settings.Environment != ChatProviderEnvironment.OpenAiHosted))
            throw new ArgumentException("Choose a single skill source for the hosted profile.");
        var skillArchive = settings.Environment != ChatProviderEnvironment.OpenAiHosted || settings.SkillBundle is not null || update.ClearSkillArchive
            ? null : update.SkillArchive ?? previous?.SkillArchive;
        var bundle = skillArchive is null ? OpenAiSkillBundle.Load(configuration, settings.SkillBundle) : OpenAiSkillBundle.ReadArchive(skillArchive);

        if (profile is null)
        {
            profile = new OpenAiChatProfile { Id = update.Id };
            db.Add(profile);
        }
        if (action == OpenAiKeyAction.Replace)
        {
            profile.WebKeyId = Guid.NewGuid();
            profile.ProtectedWebKey = protector.Protect(key, WebKeyBinding(profile.Id, profile.WebKeyId.Value));
        }
        else if (action is OpenAiKeyAction.Clear or OpenAiKeyAction.UseDeploymentReference)
        {
            profile.WebKeyId = null;
            profile.ProtectedWebKey = null;
        }

        profile.Revision++;
        profile.Enabled = settings.Enabled;
        profile.LastVerification = null;
        profile.LastVerifiedAtUtc = null;
        var revision = new OpenAiChatProfileRevision
        {
            ProfileId = profile.Id,
            Revision = profile.Revision,
            SettingsJson = JsonSerializer.Serialize(settings, Json.OpenAiChatProfileSettings),
            SkillBundleHash = bundle?.Sha256,
            SkillArchive = skillArchive,
            WebKeyId = profile.WebKeyId,
            ProtectedKey = profile.WebKeyId is null
                ? protector.Protect(key, KeyBinding(profile.Id, profile.Revision)) : ""
        };
        db.Add(revision);

        if (action == OpenAiKeyAction.Clear && previous is not null)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Set<OpenAiChatProfileRevision>().Where(row => row.ProfileId == profile.Id && row.ProtectedKey != "")
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ProtectedKey, ""), ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return Editor(profile, revision, settings, await DefaultAsync(ct));
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken ct, string? codexOwner = null)
    {
        if (DeploymentDefault is not null) throw new ChatConflictException("The default profile is managed by deployment configuration.");
        var enabled = id == NetclawId ? netclaw.Current.Enabled
            : await db.Set<OpenAiChatProfile>().AnyAsync(x => x.Id == id && x.Enabled && (x.CodexConnectionId == null ||
                db.Set<CodexConnection>().Any(connection => connection.Id == x.CodexConnectionId &&
                    connection.OwnerId == codexOwner && connection.State == CodexConnectionState.Connected)), ct);
        if (!enabled) throw new KeyNotFoundException("An enabled provider profile is required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(794, 2)", ct);
        var preference = await db.Set<ChatProviderPreference>().FindAsync([ActiveDefault], ct);
        if (preference is null) db.Add(new ChatProviderPreference { Id = ActiveDefault, ProfileId = id });
        else preference.ProfileId = id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    internal async Task<(OpenAiChatProfileSettings Settings, OpenAiChatProfileRevision Revision)> ResolveAsync(
        AiAssistantChatConversation conversation, CancellationToken ct)
    {
        if (conversation.ProviderKind is not (ChatProviderKind.OpenAiAgents or ChatProviderKind.CodexAppServer) ||
            conversation.ProviderProfileId is not { } id ||
            conversation.ProviderRevision is not { } revision)
            throw new ChatConflictException("This conversation lacks its original OpenAI profile binding.");
        var row = await GetRevisionAsync(id, revision, ct);
        var settings = Read(row.SettingsJson);
        if ((settings.CodexConnectionId is not null) != (conversation.ProviderKind == ChatProviderKind.CodexAppServer))
            throw new ChatConflictException("The pinned AI provider kind changed.");
        if (!string.Equals(settings.OrganizationId, conversation.OrganizationId, StringComparison.Ordinal))
            throw new ChatConflictException("The OpenAI profile is bound to another organization.");
        return (settings, row);
    }

    internal async Task<OpenAiAgentsConfiguration> CreateConfigurationAsync(OpenAiChatProfileSettings settings,
        OpenAiChatProfileRevision revision, string? mcpToken, CancellationToken ct)
    {
        if (settings.CodexConnectionId is not null) throw new ChatConflictException("Use the Codex App Server for this profile.");
        var profile = revision.WebKeyId is null ? null : await db.Set<OpenAiChatProfile>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == revision.ProfileId, ct);
        var key = ReadPinnedKey(profile, revision);
        if (!ValidKey(key)) throw new ChatConflictException("The pinned OpenAI key is unavailable.");
        var hosted = settings.Environment == ChatProviderEnvironment.OpenAiHosted;
        var bundle = mcpToken is null ? null : revision.SkillArchive is { } archive
            ? OpenAiSkillBundle.ReadArchive(archive) : OpenAiSkillBundle.Load(configuration, settings.SkillBundle);
        if (bundle is not null && !string.Equals(bundle.Sha256, revision.SkillBundleHash, StringComparison.Ordinal))
            throw new ChatConflictException("The configured skill bundle changed. Save a new profile revision.");
        var environment = hosted ? new AgentEnvironment("openai_hosted", "small", new("disabled"),
            settings.EnvironmentTemplateId, bundle is null ? null : [bundle.Plugin])
            : new AgentEnvironment("none");
        var tools = new List<object>();
        if (mcpToken is not null)
        {
            var names = settings.ReadOperations.Select(operation => operation[..operation.LastIndexOf('.')])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            tools.Add(new AgentMcpTool("rateldesk", new(settings.McpResource, "Bearer " + mcpToken), names));
            if (settings.ApproveInternalWorklogs) tools.Add(OpenAiWorklogAction.Definition);
        }
        return new(key, settings.Model, settings.Instructions, settings.Project, settings.OpenAiOrganization, environment, tools.ToArray(), settings.SubagentLimit);
    }

    public async Task<string> VerifyAsync(Guid id, OpenAiAgentsHttpClient http, CancellationToken ct)
    {
        var profile = await db.Set<OpenAiChatProfile>().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new KeyNotFoundException("Provider profile not found.");
        var row = await GetRevisionAsync(id, profile.Revision, ct);
        var settings = Read(row.SettingsJson);
        var config = await CreateConfigurationAsync(settings, row, null, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var adapter = new OpenAiAgentsClient(http.Client, config);
        var result = await adapter.CheckReadAccessAsync(deadline.Token);
        profile.LastVerifiedAtUtc = clock.GetUtcNow();
        profile.LastVerification = result;
        await db.SaveChangesAsync(ct);
        return result;
    }

    private Guid? DeploymentDefault => Guid.TryParse(configuration["AiProviders:DefaultProfileId"], out var id) ? id : null;

    private async Task<Guid> DefaultAsync(CancellationToken ct)
        => DeploymentDefault ?? await db.Set<ChatProviderPreference>().Where(row => row.Id == ActiveDefault)
            .Select(row => (Guid?)row.ProfileId).SingleOrDefaultAsync(ct) ?? NetclawId;

    private async Task<OpenAiChatProfileRevision> GetRevisionAsync(Guid id, int revision, CancellationToken ct)
        => await db.Set<OpenAiChatProfileRevision>().AsNoTracking().SingleOrDefaultAsync(
            row => row.ProfileId == id && row.Revision == revision, ct)
            ?? throw new KeyNotFoundException("The pinned OpenAI profile revision is unavailable.");

    private static OpenAiChatProfileSettings Read(string json) => JsonSerializer.Deserialize(json, Json.OpenAiChatProfileSettings)
        ?? throw new InvalidDataException("The saved OpenAI profile is unreadable.");

    private static ChatProviderProfile ToProfile(OpenAiChatProfile row, OpenAiChatProfileSettings settings, Guid defaultId)
        => new(row.Id, settings.CodexConnectionId is null ? ChatProviderKind.OpenAiAgents : ChatProviderKind.CodexAppServer, settings.Name, row.Enabled, row.Id == defaultId, settings.Model, settings.OrganizationId,
            row.Revision, settings.Environment, row.LastVerifiedAtUtc, row.LastVerification);

    private static OpenAiChatProfileSettings Validate(OpenAiChatProfileSettings settings)
    {
        if (!Label(settings.Name, 100) || !Label(settings.OrganizationId, 128) || settings.KeyReference is null ||
            (settings.KeyReference.Length > 0 && !Identifier(settings.KeyReference, 64)) ||
            !Identifier(settings.Model, 128) || (settings.Project is not null && !Identifier(settings.Project, 128)) ||
            (settings.OpenAiOrganization is not null && !Identifier(settings.OpenAiOrganization, 128)) ||
            settings.Instructions?.Length > 12000 || settings.ConcurrentSessions is < 1 or > 25 ||
            settings.ObservationMinutes is < 1 or > 240 || settings.SubagentLimit is < 1 or > 12)
            throw new ArgumentException("Check the profile name, key reference, model, scope, project and limits.");
        var resource = CanonicalResource(settings.McpResource)
            ?? throw new ArgumentException("Use the publicly reachable canonical HTTPS /mcp resource.");
        if (settings.ReadOperations is not { Length: > 0 and <= 32 } ||
            settings.ReadOperations.Any(x => !Label(x, 128) || !x.StartsWith("helpdesk_", StringComparison.Ordinal) ||
                x.LastIndexOf('.') <= 0))
            throw new ArgumentException("Select at least one permitted read operation.");
        if (!Enum.IsDefined(settings.Environment) || (settings.Environment == ChatProviderEnvironment.None &&
            (settings.EnvironmentTemplateId is not null || settings.SkillBundle is not null)) ||
            (settings.EnvironmentTemplateId is not null && !Identifier(settings.EnvironmentTemplateId, 128)) ||
            (settings.SkillBundle is not null && !Identifier(settings.SkillBundle, 128)))
            throw new ArgumentException("Check the environment, template and versioned skill bundle.");
        return settings with { Name = settings.Name.Trim(), McpResource = resource,
            ReadOperations = settings.ReadOperations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
    }

    internal static bool Identifier(string? value, int max) => value is { Length: > 0 } && value.Length <= max &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool Label(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static bool ValidKey(string? key) => key is { Length: >= 16 and <= 2048 } &&
        key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string KeyBinding(Guid id, int revision) => $"OpenAI Agents:{id:D}:{revision}";
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

[JsonSerializable(typeof(OpenAiChatProfileSettings))]
[JsonSerializable(typeof(ChatProviderEventMetadata))]
internal sealed partial class OpenAiProfileJson : JsonSerializerContext;
