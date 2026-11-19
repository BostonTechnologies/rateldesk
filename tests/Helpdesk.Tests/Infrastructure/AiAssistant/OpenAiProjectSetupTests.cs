using System.Net;
using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using static Helpdesk.Tests.Infrastructure.AiAssistant.OpenAiAgentsProtocolTests;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class OpenAiProjectSetupTests(ChatPostgresFixture database) : IClassFixture<ChatPostgresFixture>
{
    private const string FirstKey = "synthetic-project-key-one";
    private const string SecondKey = "synthetic-project-key-two";
    private readonly IntegrationProviderSecretProtector protector = new(new EphemeralDataProtectionProvider());
    private readonly AiAssistantChatRuntimeState netclaw = new(Options.Create(new AiAssistantChatOptions()));

    [Fact]
    public async Task Web_key_survives_settings_edits_and_replacement_does_not_retarget_old_revisions()
    {
        await using var db = database.Context();
        var profiles = new OpenAiChatProfiles(db, netclaw, protector, new ConfigurationBuilder().Build(), TimeProvider.System);
        var id = Guid.NewGuid();
        var settings = Settings();
        var change = new OpenAiProjectKeyChange { Action = OpenAiKeyAction.Replace, ApiKey = FirstKey };
        var update = new OpenAiChatProfileUpdate(id, 0, settings) { Credential = change };
        Assert.DoesNotContain(FirstKey, update.ToString(), StringComparison.Ordinal);
        var initial = await profiles.SaveAsync(update, default);
        Assert.Equal(OpenAiKeySource.Web, initial.KeySource);
        Assert.DoesNotContain(FirstKey, JsonSerializer.Serialize(initial), StringComparison.Ordinal);

        var kept = await profiles.SaveAsync(new(id, 1, initial.Settings with { Name = "Edited name" }), default);
        Assert.Equal(OpenAiKeySource.Web, kept.KeySource);
        var old = await db.Set<OpenAiChatProfileRevision>().AsNoTracking().SingleAsync(row => row.ProfileId == id && row.Revision == 1);
        Assert.Empty(old.ProtectedKey);
        Assert.Equal(FirstKey, (await profiles.CreateConfigurationAsync(settings, old, null, default)).ApiKey);

        var saved = await profiles.SaveAsync(new(id, 2, kept.Settings)
        {
            Credential = new() { Action = OpenAiKeyAction.Replace, ApiKey = SecondKey }
        }, default);
        Assert.Equal(3, saved.Profile.Revision);
        Assert.DoesNotContain(SecondKey, JsonSerializer.Serialize(saved), StringComparison.Ordinal);
        var row = await db.Set<OpenAiChatProfile>().AsNoTracking().SingleAsync(profile => profile.Id == id);
        Assert.NotNull(row.ProtectedWebKey);
        Assert.DoesNotContain(SecondKey, row.ProtectedWebKey, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ChatConflictException>(() => profiles.CreateConfigurationAsync(settings, old, null, default));

        var cleared = await profiles.SaveAsync(new(id, 3, saved.Settings with { Enabled = false })
        {
            Credential = new() { Action = OpenAiKeyAction.Clear }
        }, default);
        Assert.Equal(OpenAiKeySource.None, cleared.KeySource);
        Assert.False(cleared.Profile.Enabled);
        row = await db.Set<OpenAiChatProfile>().AsNoTracking().SingleAsync(profile => profile.Id == id);
        Assert.Null(row.ProtectedWebKey);
        Assert.Null(row.WebKeyId);
    }

    [Fact]
    public async Task Deployment_reference_remains_pinned_until_explicitly_reapplied_and_clear_removes_saved_copies()
    {
        await using var db = database.Context();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        configuration["AiProviders:OpenAiKeys:fixture"] = FirstKey;
        var profiles = new OpenAiChatProfiles(db, netclaw, protector, configuration, TimeProvider.System);
        var id = Guid.NewGuid();
        var initial = await profiles.SaveAsync(new(id, 0, Settings() with { KeyReference = "fixture" }), default);
        configuration["AiProviders:OpenAiKeys:fixture"] = SecondKey;

        var kept = await profiles.SaveAsync(new(id, 1, initial.Settings with { Name = "Edited deployment name" }), default);
        var previous = await db.Set<OpenAiChatProfileRevision>().AsNoTracking().SingleAsync(row => row.ProfileId == id && row.Revision == 2);
        Assert.Equal(FirstKey, (await profiles.CreateConfigurationAsync(kept.Settings, previous, null, default)).ApiKey);
        var reapplied = await profiles.SaveAsync(new(id, 2, kept.Settings)
        {
            Credential = new() { Action = OpenAiKeyAction.UseDeploymentReference }
        }, default);
        var latest = await db.Set<OpenAiChatProfileRevision>().AsNoTracking().SingleAsync(row => row.ProfileId == id && row.Revision == 3);
        Assert.Equal(SecondKey, (await profiles.CreateConfigurationAsync(reapplied.Settings, latest, null, default)).ApiKey);
        await profiles.SaveAsync(new(id, 3, reapplied.Settings with { Enabled = false })
        {
            Credential = new() { Action = OpenAiKeyAction.Clear }
        }, default);
        Assert.False(await db.Set<OpenAiChatProfileRevision>().AnyAsync(row => row.ProfileId == id && row.ProtectedKey != ""));
        Assert.Equal(SecondKey, configuration["AiProviders:OpenAiKeys:fixture"]);
    }

    [Fact]
    public async Task Project_catalog_uses_the_selected_project_and_rejects_a_cross_organization_lookup()
    {
        await using var db = database.Context();
        var profiles = new OpenAiChatProfiles(db, netclaw, protector, new ConfigurationBuilder().Build(), TimeProvider.System);
        var id = Guid.NewGuid();
        var change = new OpenAiProjectKeyChange { Action = OpenAiKeyAction.Replace, ApiKey = FirstKey };
        await profiles.SaveAsync(new(id, 0, Settings()) { Credential = change }, default);
        var requests = 0;
        using var http = new OpenAiAgentsHttpClient(Client((request, _) =>
        {
            requests++;
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri?.AbsoluteUri);
            Assert.Equal(FirstKey, request.Headers.Authorization?.Parameter);
            Assert.Equal("project-fixture", Assert.Single(request.Headers.GetValues("OpenAI-Project")));
            Assert.Equal("org-fixture", Assert.Single(request.Headers.GetValues("OpenAI-Organization")));
            Assert.False(request.Headers.Contains("OpenAI-Beta"));
            return Task.FromResult(Response("""
                {"object":"list","data":[null,{"object":"model","id":"fixture-b"},{"object":"model","id":"fixture-a"},
                 {"object":"model","id":"fixture-a"},{"object":"other","id":"not-a-model"},{"object":"model","id":"invalid id"}]}
                """, HttpStatusCode.OK));
        }));
        var lookup = new OpenAiProjectCatalogRequest(1, "org", "project-fixture", "org-fixture", "", null);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => profiles.ModelsAsync(id, lookup with { OrganizationId = "another" }, http, default));
        Assert.Equal(0, requests);
        var result = await profiles.ModelsAsync(id, lookup, http, default);
        Assert.Equal(new[] { "fixture-a", "fixture-b" }, result.Models.Select(model => model.Id));
        Assert.All(result.Models, model => Assert.Empty(model.ReasoningEfforts));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Project_catalog_rejects_the_subscription_response_shape()
    {
        using var http = Client((_, _) => Task.FromResult(Response("""{"models":[{"slug":"fixture","display_name":"Fixture"}]}""")));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OpenAiAgentsClient.ReadModelsAsync(http, FirstKey, null, null, TimeProvider.System, default));
    }

    private static OpenAiChatProfileSettings Settings() => new("Synthetic Agents", true, "org", "fixture-model", "",
        null, null, null, "https://mcp.example.com/mcp", ["helpdesk_incidents.get"], false, ChatProviderEnvironment.None, null, null);
}
