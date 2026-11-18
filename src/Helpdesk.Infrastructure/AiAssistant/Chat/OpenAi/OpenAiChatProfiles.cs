using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.AiAssistant.Chat;
using EntityState = Helpdesk.Shared.Models.EntityState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

public sealed class OpenAiChatProfiles(HelpdeskDbContext db, IAiAssistantChatRuntimeState netclaw,
    IntegrationProviderSecretProtector protector, IConfiguration configuration, TimeProvider clock, IChatProviderMcpAuthority? authority = null) : IChatProviderProfiles
{
    public static readonly Guid NetclawId = new("90b1a190-f989-4cf8-ac56-0ce23b566840");
    private static readonly OpenAiProfileJson Json = OpenAiProfileJson.Default;
    private const int ActiveDefault = 1;

    public async Task<IReadOnlyList<ChatProviderProfile>> ListAsync(CancellationToken ct)
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
                          orderby profile.Id
                          select new { profile, revision.SettingsJson }).ToListAsync(ct);
        profiles.AddRange(rows.Select(row => ToProfile(row.profile, Read(row.SettingsJson), id)));
        return profiles;
    }

    public async Task<IReadOnlyList<ChatProviderProfile>> AvailableAsync(string organization, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return [];
        var profiles = (await ListAsync(ct)).Where(x => x.Enabled && (x.OrganizationId is null || x.OrganizationId == organization)).ToArray();
        if (profiles.Length == 0 || profiles.Any(x => x.IsDefault)) return profiles;
        profiles[0] = profiles[0] with { IsDefault = true };
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
        if (conversation.ProviderKind != ChatProviderKind.OpenAiAgents) return;
        if (conversation.CreatedByUserId != actor)
            throw new ChatConflictException("This conversation is bound to its original operator. Start a new conversation for your own tool access.");
        if (authority is null || conversation.ProviderActorId != await authority.ResolveOperatorAsync(conversation.OrganizationId, ct))
            throw new ChatConflictException("The original operator account is no longer authorized.");
    }

    internal Task<bool> IsCurrentAsync(AiAssistantChatConversation row, CancellationToken ct)
        => db.Set<OpenAiChatProfile>().AnyAsync(x => x.Id == row.ProviderProfileId && x.Revision == row.ProviderRevision && x.Enabled, ct);

    public async Task<ChatProviderProfile?> DescribeAsync(AiAssistantChatConversation conversation, CancellationToken ct)
    {
        if (conversation.ProviderKind != ChatProviderKind.OpenAiAgents) return null;
        var (settings, revision) = await ResolveAsync(conversation, ct);
        var current = await db.Set<OpenAiChatProfile>().AsNoTracking().SingleAsync(item => item.Id == revision.ProfileId, ct);
        var pinned = new OpenAiChatProfile { Id = revision.ProfileId, Revision = revision.Revision,
            Enabled = current.Revision == revision.Revision && current.Enabled,
            LastVerifiedAtUtc = current.Revision == revision.Revision ? current.LastVerifiedAtUtc : null,
            LastVerification = current.Revision == revision.Revision ? current.LastVerification : null };
        return ToProfile(pinned, settings, await DefaultAsync(ct));
    }

    public async Task<OpenAiChatProfileEditor> GetAsync(Guid id, CancellationToken ct)
    {
        var profile = await db.Set<OpenAiChatProfile>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new KeyNotFoundException("Provider profile not found.");
        var revision = await GetRevisionAsync(id, profile.Revision, ct);
        var settings = Read(revision.SettingsJson);
        return new(ToProfile(profile, settings, await DefaultAsync(ct)), settings);
    }

    public async Task<OpenAiChatProfileEditor> SaveAsync(OpenAiChatProfileUpdate update, CancellationToken ct)
    {
        if (update.Id == Guid.Empty || update.Id == NetclawId || update.ExpectedRevision < 0)
            throw new ArgumentException("Choose a valid provider profile identity and revision.");
        if (!db.Database.IsNpgsql() && update.Settings.Enabled)
            throw new ArgumentException("Native AI Providers require PostgreSQL.");
        var settings = Validate(update.Settings);
        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(x => x.Id == settings.OrganizationId && x.State == EntityState.Enabled, ct))
            throw new ArgumentException("Choose an enabled organization.");
        var profile = await db.Set<OpenAiChatProfile>().SingleOrDefaultAsync(row => row.Id == update.Id, ct);
        if ((profile?.Revision ?? 0) != update.ExpectedRevision)
            throw new ChatConflictException("The provider profile changed. Reload it before saving.");

        var key = configuration[$"AiProviders:OpenAiKeys:{settings.KeyReference}"];
        if (settings.Enabled && !ValidKey(key))
            throw new ArgumentException("The selected OpenAI key reference is unavailable or invalid.");
        var bundle = OpenAiSkillBundle.Load(configuration, settings.SkillBundle);
        if (profile is null)
        {
            profile = new OpenAiChatProfile { Id = update.Id };
            db.Add(profile);
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
            ProtectedKey = protector.Protect(ValidKey(key) ? key! : "", KeyBinding(profile.Id, profile.Revision))
        };
        db.Add(revision);
        await db.SaveChangesAsync(ct);
        return new(ToProfile(profile, settings, await DefaultAsync(ct)), settings);
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken ct)
    {
        if (DeploymentDefault is not null) throw new ChatConflictException("The default profile is managed by deployment configuration.");
        var enabled = id == NetclawId ? netclaw.Current.Enabled
            : await db.Set<OpenAiChatProfile>().AnyAsync(x => x.Id == id && x.Enabled, ct);
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
        if (conversation.ProviderKind != ChatProviderKind.OpenAiAgents || conversation.ProviderProfileId is not { } id ||
            conversation.ProviderRevision is not { } revision)
            throw new ChatConflictException("This conversation lacks its original OpenAI profile binding.");
        var row = await GetRevisionAsync(id, revision, ct);
        var settings = Read(row.SettingsJson);
        if (!string.Equals(settings.OrganizationId, conversation.OrganizationId, StringComparison.Ordinal))
            throw new ChatConflictException("The OpenAI profile is bound to another organization.");
        return (settings, row);
    }

    internal OpenAiAgentsConfiguration CreateConfiguration(OpenAiChatProfileSettings settings, OpenAiChatProfileRevision revision, string? mcpToken)
    {
        var key = protector.Unprotect(revision.ProtectedKey, KeyBinding(revision.ProfileId, revision.Revision));
        if (!ValidKey(key)) throw new ChatConflictException("The pinned OpenAI key is unavailable.");
        var hosted = settings.Environment == ChatProviderEnvironment.OpenAiHosted;
        var bundle = mcpToken is null ? null : OpenAiSkillBundle.Load(configuration, settings.SkillBundle);
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

    public async Task<string> VerifyAsync(Guid id, IHttpClientFactory http, CancellationToken ct)
    {
        var profile = await db.Set<OpenAiChatProfile>().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new KeyNotFoundException("Provider profile not found.");
        var row = await GetRevisionAsync(id, profile.Revision, ct);
        var settings = Read(row.SettingsJson);
        var config = CreateConfiguration(settings, row, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = http.CreateClient(OpenAiAgentsClient.ClientName);
        var adapter = new OpenAiAgentsClient(client, config);
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
        => new(row.Id, ChatProviderKind.OpenAiAgents, settings.Name, row.Enabled, row.Id == defaultId, settings.Model, settings.OrganizationId,
            row.Revision, settings.Environment, row.LastVerifiedAtUtc, row.LastVerification);

    private static OpenAiChatProfileSettings Validate(OpenAiChatProfileSettings settings)
    {
        if (!Label(settings.Name, 100) || !Label(settings.OrganizationId, 128) || !Identifier(settings.KeyReference, 64) ||
            !Identifier(settings.Model, 128) || (settings.Project is not null && !Identifier(settings.Project, 128)) ||
            (settings.OpenAiOrganization is not null && !Identifier(settings.OpenAiOrganization, 128)) ||
            settings.Instructions?.Length > 12000 || settings.ConcurrentSessions is < 1 or > 25 ||
            settings.ObservationMinutes is < 1 or > 240 || settings.SubagentLimit is < 1 or > 12)
            throw new ArgumentException("Check the profile name, key reference, model, scope, project and limits.");
        if (!Uri.TryCreate(settings.McpResource, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback || !uri.Host.Contains('.', StringComparison.Ordinal) ||
            uri.UserInfo.Length != 0 || uri.PathAndQuery.TrimEnd('/') != "/mcp" || uri.Fragment.Length != 0)
            throw new ArgumentException("Use the publicly reachable canonical HTTPS /mcp resource.");
        if (settings.ReadOperations is not { Length: > 0 and <= 32 } ||
            settings.ReadOperations.Any(x => !Label(x, 128) || !x.StartsWith("helpdesk_", StringComparison.Ordinal) ||
                x.LastIndexOf('.') <= 0))
            throw new ArgumentException("Select at least one permitted read operation.");
        if (!Enum.IsDefined(settings.Environment) || (settings.Environment == ChatProviderEnvironment.None &&
            (settings.EnvironmentTemplateId is not null || settings.SkillBundle is not null)) ||
            (settings.EnvironmentTemplateId is not null && !Identifier(settings.EnvironmentTemplateId, 128)) ||
            (settings.SkillBundle is not null && !Identifier(settings.SkillBundle, 128)))
            throw new ArgumentException("Check the environment, template and versioned skill bundle.");
        return settings with { Name = settings.Name.Trim(), McpResource = uri.AbsoluteUri.TrimEnd('/'),
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
