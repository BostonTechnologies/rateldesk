using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.Codex;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class CodexConnectionIsolationTests(ChatPostgresFixture database) : IClassFixture<ChatPostgresFixture>
{
    [Fact]
    public async Task Connection_and_profile_metadata_require_the_original_owner_and_tenant()
    {
        await using var db = database.Context();
        await using var host = new CodexAppServerHost(Options.Create(new CodexAppServerOptions()));
        var connections = new CodexConnections(db, host, TimeProvider.System);
        var profiles = new OpenAiChatProfiles(db, new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions())),
            new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider()), new ConfigurationBuilder().Build(), TimeProvider.System,
            Substitute.For<IChatProviderMcpAuthority>(), connections, host);
        var connection = new CodexConnection { Id = Guid.NewGuid(), HostId = Guid.NewGuid(), OwnerId = "original-operator",
            OrganizationId = "org", State = CodexConnectionState.Connected, AccountLabel = "operator@example.test",
            StartedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10) };
        var profile = new OpenAiChatProfile { Id = Guid.NewGuid(), CodexConnectionId = connection.Id, Revision = 1, Enabled = true };
        var settings = new OpenAiChatProfileSettings("Synthetic personal Codex", true, "org", "fixture-model", "",
            null, null, null, "https://mcp.example.com/mcp", ["helpdesk_incidents.get"], false, ChatProviderEnvironment.None, null, null)
        { CodexConnectionId = connection.Id };
        db.AddRange(connection, profile, new OpenAiChatProfileRevision { ProfileId = profile.Id, Revision = 1,
            SettingsJson = System.Text.Json.JsonSerializer.Serialize(settings) });
        await db.SaveChangesAsync();

        Assert.Empty(await connections.ListAsync("other-operator", "org", default));
        Assert.Empty(await connections.ListAsync("original-operator", "different-org", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => connections.StatusAsync(connection.Id, "other-operator", "org", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => connections.StatusAsync(connection.Id, "original-operator", "different-org", default));
        Assert.Single(await connections.ListAsync("original-operator", "org", default));
        Assert.DoesNotContain(await profiles.ListAsync(default, "other-operator"), item => item.Id == profile.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => profiles.GetAsync(profile.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => profiles.GetCodexAsync(profile.Id, "other-operator", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => profiles.SetDefaultAsync(profile.Id, default, "other-operator"));
        Assert.Equal(ChatProviderKind.CodexAppServer, (await profiles.GetCodexAsync(profile.Id, "original-operator", default)).Profile.Kind);
    }
}
