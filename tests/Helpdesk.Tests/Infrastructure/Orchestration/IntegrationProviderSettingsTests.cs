using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class IntegrationProviderSettingsTests
{
    [Fact]
    public void Canonical_configuration_takes_precedence_over_legacy_aliases()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:BaseUrl"] = "https://canonical.example.test",
                ["Orchestrator:Enabled"] = "false",
                ["Orchestration:Provider:BaseUrl"] = "https://legacy.example.test",
                ["Orchestration:Provider:Enabled"] = "true",
                ["Netclaw:Endpoint"] = "https://canonical.example.test/hub/session",
                ["AiAssistantChat:Endpoint"] = "https://legacy.example.test/hub/session"
            })
            .Build();

        var orchestrator = IntegrationConfigurationAliases.ReadOrchestrator(configuration);
        var netclaw = IntegrationConfigurationAliases.ReadNetclaw(configuration);

        Assert.False(orchestrator.Enabled);
        Assert.Equal("https://canonical.example.test", orchestrator.BaseUrl);
        Assert.Equal("https://canonical.example.test/hub/session", netclaw.Endpoint);
    }

    [Fact]
    public void Higher_priority_legacy_configuration_can_override_packaged_canonical_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:Enabled"] = "false",
                ["Orchestrator:BaseUrl"] = "",
                ["Orchestrator:ProviderName"] = "NetRatel orchestrator"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestration:Provider:Enabled"] = "true",
                ["Orchestration:Provider:BaseUrl"] = "https://legacy.example.test",
                ["M2M:ClientId"] = "legacy-client",
                ["M2M:ClientSecret"] = "legacy-secret"
            })
            .Build();

        var orchestrator = IntegrationConfigurationAliases.ReadOrchestrator(configuration);

        Assert.True(orchestrator.Enabled);
        Assert.Equal("https://legacy.example.test", orchestrator.BaseUrl);
        Assert.Equal("legacy-client", orchestrator.ClientId);
        Assert.Equal("legacy-secret", orchestrator.ClientSecret);
    }

    [Fact]
    public void Explicit_canonical_disable_wins_over_lower_priority_legacy_enable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestration:Provider:Enabled"] = "true",
                ["Orchestration:Provider:BaseUrl"] = "https://legacy.example.test"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:Enabled"] = "false",
                ["Orchestrator:BaseUrl"] = "https://disabled.example.test"
            })
            .Build();

        var orchestrator = IntegrationConfigurationAliases.ReadOrchestrator(configuration);

        Assert.False(orchestrator.Enabled);
        Assert.Equal("https://disabled.example.test", orchestrator.BaseUrl);
    }

    [Fact]
    public async Task Deployment_reads_do_not_invent_a_saved_timestamp()
    {
        await using var fixture = await Fixture.CreateAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:BaseUrl"] = "https://netratel.example.test"
            })
            .Build();
        var service = fixture.CreateService(configuration);

        var first = await service.GetOrchestratorSettingsAsync();
        var second = await service.GetOrchestratorSettingsAsync();

        Assert.True(first.ManagedByDeployment);
        Assert.Null(first.UpdatedAtUtc);
        Assert.Null(second.UpdatedAtUtc);
    }

    [Fact]
    public async Task Provider_secrets_are_protected_and_revision_conflicts_are_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            Enabled = true,
            BaseUrl = "https://netratel.example.test",
            Authority = "https://netratel.example.test",
            ClientId = "rateldesk-beta4",
            ClientSecret = "synthetic-secret",
            Scope = "netratel.api"
        });

        Assert.Equal(1, saved.Revision);
        Assert.True(saved.HasClientSecret);
        var stored = await fixture.Db.M2MConnectivitySettings.SingleAsync();
        Assert.NotEqual("synthetic-secret", stored.ProtectedClientSecret);
        Assert.NotEqual(string.Empty, stored.ProtectedClientSecret);

        var resolved = await service.GetResolvedOrchestratorSettingsAsync();
        Assert.Equal("synthetic-secret", resolved.ClientSecret);
        Assert.Equal("/internal/health", resolved.HealthPath);

        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() =>
            service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
            {
                ExpectedRevision = 0,
                Enabled = false
            }));
    }

    [Fact]
    public async Task Persisted_netclaw_profile_is_stored_but_not_enabled_on_sqlite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var saved = await service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            Enabled = true,
            Endpoint = "https://netclaw.example.test/hub/session",
            DeviceToken = "synthetic-device-token"
        });

        Assert.True(saved.Enabled);
        Assert.False(saved.RuntimeSupported);
        Assert.Contains("PostgreSQL", saved.RuntimeIssue, StringComparison.Ordinal);
        Assert.False(fixture.ChatOptions.Value.Enabled);
        Assert.NotEqual("synthetic-device-token", (await fixture.Db.NetclawConnectivitySettings.SingleAsync()).ProtectedDeviceToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public HelpdeskDbContext Db { get; }
        public IOptions<AiAssistantChatOptions> ChatOptions { get; } = Options.Create(new AiAssistantChatOptions());
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection().Build();

        private Fixture(SqliteConnection connection, HelpdeskDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("test");
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
            var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public IntegrationProviderSettingsService CreateService(IConfiguration? configuration = null)
            => new(
                Db,
                configuration ?? Configuration,
                new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
                ChatOptions,
                new DatabaseOptions { Provider = "Sqlite" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
