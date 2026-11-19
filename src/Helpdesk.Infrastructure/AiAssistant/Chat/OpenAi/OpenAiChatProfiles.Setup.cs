using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

public sealed partial class OpenAiChatProfiles
{
    public AiProviderSetupOptions SetupOptions(string? publicOrigin)
    {
        var references = configuration.GetSection("AiProviders:OpenAiKeys").GetChildren()
            .Select(section => section.Key).Where(key => Identifier(key, 64)).Order(StringComparer.Ordinal).ToArray();
        var configuredResources = configuration.GetSection("AiProviders:OpenAiMcpResources").GetChildren()
            .Select(section => section.Value);
        var resources = configuredResources.Append(publicOrigin is null ? null : publicOrigin + "/mcp")
            .Select(CanonicalResource).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(references, resources, DeploymentDefault is not null, codexHost?.Configured == true && db.Database.IsNpgsql())
        {
            SkillReferences = configuration.GetSection("AiProviders:SkillBundles").GetChildren().Select(item => item.Key)
                .Where(key => Identifier(key, 128)).Order(StringComparer.Ordinal).ToArray()
        };
    }

    public async Task<OpenAiModelCatalog> ModelsAsync(Guid id, OpenAiProjectCatalogRequest request, OpenAiAgentsHttpClient http, CancellationToken ct)
    {
        if (id == Guid.Empty || id == NetclawId || request.ExpectedRevision < 0)
            throw new ArgumentException("Choose a valid provider profile identity and revision.");
        if ((request.Project is not null && !Identifier(request.Project, 128)) ||
            (request.OpenAiOrganization is not null && !Identifier(request.OpenAiOrganization, 128)))
            throw new ArgumentException("Enter a valid OpenAI project and organization identifier.");

        var profile = await db.Set<OpenAiChatProfile>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (profile?.CodexConnectionId is not null) throw new KeyNotFoundException("Provider profile not found.");
        if ((profile?.Revision ?? 0) != request.ExpectedRevision)
            throw new ChatConflictException("The provider profile changed. Reload it before checking models.");
        var previous = profile is null ? null : await GetRevisionAsync(profile.Id, profile.Revision, ct);
        var oldSettings = previous is null ? null : Read(previous.SettingsJson);
        if (oldSettings is not null && oldSettings.OrganizationId != request.OrganizationId)
            throw new KeyNotFoundException("Provider profile not found.");
        var action = KeyAction(request.Credential, request.KeyReference, oldSettings);
        var key = ReadSetupKey(action, request.Credential, request.KeyReference, profile, previous);
        if (!ValidKey(key))
            throw new ArgumentException("Enter a project key or select a configured key reference to check models.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        return await OpenAiAgentsClient.ReadModelsAsync(http.Client, key, request.Project, request.OpenAiOrganization,
            clock, deadline.Token);
    }

    private static OpenAiChatProfileEditor Editor(OpenAiChatProfile profile, OpenAiChatProfileRevision revision,
        OpenAiChatProfileSettings settings, Guid defaultId) => new(ToProfile(profile, settings, defaultId), settings)
        {
            KeySource = revision.WebKeyId is not null ? OpenAiKeySource.Web
                : string.IsNullOrEmpty(settings.KeyReference) ? OpenAiKeySource.None : OpenAiKeySource.Deployment,
            UploadedSkillSha256 = revision.SkillArchive is null ? null : revision.SkillBundleHash
        };

    private static OpenAiKeyAction KeyAction(OpenAiProjectKeyChange? change, string? reference, OpenAiChatProfileSettings? previous)
    {
        var action = change?.Action ?? (string.IsNullOrEmpty(reference) || reference == previous?.KeyReference
            ? OpenAiKeyAction.Keep : OpenAiKeyAction.UseDeploymentReference);
        if (!Enum.IsDefined(action) || (action != OpenAiKeyAction.Replace && change?.ApiKey is not null))
            throw new ArgumentException("Select a valid project key operation.");
        return action;
    }

    private string ReadSetupKey(OpenAiKeyAction action, OpenAiProjectKeyChange? change, string? reference,
        OpenAiChatProfile? profile, OpenAiChatProfileRevision? previous)
    {
        switch (action)
        {
            case OpenAiKeyAction.Replace:
                if (!ValidKey(change?.ApiKey))
                    throw new ArgumentException("Enter a valid OpenAI project API key.");
                return change!.ApiKey!;
            case OpenAiKeyAction.UseDeploymentReference:
                if (!Identifier(reference, 64) || !ValidKey(configuration[$"AiProviders:OpenAiKeys:{reference}"]))
                    throw new ArgumentException("The selected deployment key reference is unavailable or invalid.");
                return configuration[$"AiProviders:OpenAiKeys:{reference}"]!;
            case OpenAiKeyAction.Keep when previous is not null:
                return ReadPinnedKey(profile, previous);
            default:
                return "";
        }
    }

    private string ReadPinnedKey(OpenAiChatProfile? profile, OpenAiChatProfileRevision revision)
    {
        if (revision.WebKeyId is not { } keyId)
            return protector.Unprotect(revision.ProtectedKey, KeyBinding(revision.ProfileId, revision.Revision));
        return profile?.WebKeyId == keyId && profile.ProtectedWebKey is { } protectedKey
            ? protector.Unprotect(protectedKey, WebKeyBinding(revision.ProfileId, keyId)) : "";
    }

    internal static string? CanonicalResource(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback || !uri.Host.Contains('.', StringComparison.Ordinal) ||
            uri.UserInfo.Length != 0 || uri.PathAndQuery.TrimEnd('/') != "/mcp" || uri.Fragment.Length != 0)
            return null;
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string WebKeyBinding(Guid profile, Guid generation) => $"OpenAI project:{profile:D}:{generation:D}";
}
