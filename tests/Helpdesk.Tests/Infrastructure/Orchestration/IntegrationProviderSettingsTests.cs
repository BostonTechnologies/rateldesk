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
    public void Packaged_appsettings_defaults_are_database_managed()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Helpdesk.sln")))
            repository = repository.Parent;
        var path = Path.Combine(repository?.FullName ?? throw new InvalidOperationException("Repository root was not found."), "src", "Helpdesk.API", "appsettings.json");
        Assert.True(File.Exists(path), $"Expected the repository appsettings file at {path}.");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();

        Assert.False(IntegrationConfigurationAliases.HasDeploymentOrchestratorConfiguration(configuration));
        Assert.False(IntegrationConfigurationAliases.HasDeploymentNetclawConfiguration(configuration));
    }

    [Fact]
    public void Explicit_empty_operator_values_are_authoritative()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:Enabled"] = "",
                ["Orchestrator:BaseUrl"] = ""
            })
            .Build();

        Assert.True(IntegrationConfigurationAliases.HasDeploymentOrchestratorConfiguration(configuration));
        Assert.Equal("Orchestrator", IntegrationConfigurationAliases.GetDeploymentSourceKey(configuration, netclaw: false));
    }

    [Fact]
    public void Malformed_deployment_values_fail_with_the_controlling_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Netclaw:Enabled"] = "sometimes"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => IntegrationConfigurationAliases.ReadNetclaw(configuration));

        Assert.Contains("Netclaw:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sometimes", exception.Message, StringComparison.Ordinal);
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
    public async Task Retained_secret_cannot_cross_an_authenticated_destination_boundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            Enabled = true,
            BaseUrl = "https://provider-a.example.test",
            Authority = "https://provider-a.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = saved.Revision,
            Enabled = true,
            BaseUrl = "https://provider-b.example.test",
            Authority = "https://provider-b.example.test",
            ClientId = "client-a"
        }));

        var metadata = await service.GetOrchestratorSettingsAsync();
        Assert.True(metadata.HasClientSecret);
        Assert.Equal("configured", metadata.SecretState);
    }

    [Fact]
    public async Task Same_authenticated_destination_may_retain_secret_without_decrypting_metadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        var updated = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = saved.Revision,
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            RemoteSystemName = "renamed"
        });

        Assert.True(updated.HasClientSecret);
        Assert.Equal("configured", updated.SecretState);
        Assert.Equal("secret-a", (await service.GetResolvedOrchestratorSettingsAsync()).ClientSecret);
    }

    [Fact]
    public async Task Draft_diagnostic_resolves_a_candidate_without_persisting_or_rebinding_a_secret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        var draft = await service.ResolveOrchestratorDraftAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = saved.Revision,
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            RemoteSystemName = "draft-only"
        });

        Assert.Equal("draft", draft.Source);
        Assert.Equal(saved.Revision, draft.Revision);
        Assert.Equal("secret-a", draft.ClientSecret);
        Assert.Equal("draft-only", draft.RemoteSystemName);
        var stored = await fixture.Db.M2MConnectivitySettings.SingleAsync();
        Assert.Equal(saved.Revision, stored.Revision);
        Assert.Equal("https://provider.example.test", stored.RemoteBaseUrl);
        Assert.Null(stored.LastTestedAtUtc);
    }

    [Fact]
    public async Task Draft_diagnostic_rejects_a_changed_destination_without_a_replacement_secret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            Enabled = true,
            BaseUrl = "https://provider-a.example.test",
            Authority = "https://provider-a.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveOrchestratorDraftAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = saved.Revision,
            Enabled = true,
            BaseUrl = "https://provider-b.example.test",
            Authority = "https://provider-b.example.test",
            ClientId = "client-a"
        }));

        Assert.Equal(saved.Revision, (await fixture.Db.M2MConnectivitySettings.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Netclaw_draft_diagnostic_does_not_apply_or_persist_candidate_settings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var draft = await service.ResolveNetclawDraftAsync(new UpdateNetclawConnectivitySettingsDto
        {
            Enabled = false,
            Endpoint = "https://netclaw.example.test/hub/session",
            DeviceToken = "draft-device-token",
            Instance = "dev"
        });

        Assert.Equal("draft", draft.Source);
        Assert.Equal("draft-device-token", draft.DeviceToken);
        Assert.Empty(await fixture.Db.NetclawConnectivitySettings.ToListAsync());
        Assert.False(fixture.ChatOptions.Value.Enabled);
    }

    [Fact]
    public async Task Missing_data_protection_key_is_safe_and_exposed_as_unavailable_only_when_resolved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var protectingProvider = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
        var service = fixture.CreateService(protectionProvider: protectingProvider);
        await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        var metadataService = fixture.CreateService(protectionProvider: new EphemeralDataProtectionProvider(NullLoggerFactory.Instance));
        var metadata = await metadataService.GetOrchestratorSettingsAsync();
        Assert.True(metadata.HasClientSecret);
        Assert.Equal("configured", metadata.SecretState);

        var resolved = await metadataService.GetResolvedOrchestratorSettingsAsync();
        Assert.True(resolved.SecretUnavailable);
        Assert.Equal("unavailable", resolved.SecretState);
        Assert.Null(resolved.ClientSecret);
    }

    [Fact]
    public async Task Concurrent_first_profile_creation_has_one_winner()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"rateldesk-provider-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("test");
            await using (var initializer = new HelpdeskDbContext(options, tenant, new HttpContextAccessor()))
                await initializer.Database.EnsureCreatedAsync();

            await using var firstDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            await using var secondDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var protection = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
            var first = CreateService(firstDb, configuration, protection);
            var second = CreateService(secondDb, configuration, protection);
            var request = new UpdateOrchestrationConnectivitySettingsDto
            {
                Enabled = false,
                BaseUrl = "https://provider.example.test"
            };

            var saves = new[]
            {
                first.UpdateOrchestratorSettingsAsync(request),
                second.UpdateOrchestratorSettingsAsync(request)
            };
            try { await Task.WhenAll(saves); }
            catch (Exception)
            {
                var failedSave = saves.Single(x => x.IsFaulted);
                Assert.IsType<IntegrationProviderConfigurationConflictException>(failedSave.Exception?.InnerException);
            }

            await using var verification = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            Assert.Single(await verification.M2MConnectivitySettings.ToListAsync());
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Test_result_for_an_old_revision_cannot_overwrite_current_profile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var first = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            Enabled = false,
            BaseUrl = "https://provider.example.test"
        });
        var oldFingerprint = first.ProfileFingerprint;
        var current = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = first.Revision,
            Enabled = false,
            BaseUrl = "https://provider.example.test",
            RemoteSystemName = "new profile"
        });

        Assert.False(await service.RecordOrchestratorTestAsync(first.Revision, oldFingerprint, succeeded: true));
        var stored = await fixture.Db.M2MConnectivitySettings.SingleAsync();
        Assert.Equal(current.Revision, stored.Revision);
        Assert.Null(stored.LastTestedAtUtc);
        Assert.Null(stored.LastTestSucceeded);
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

    private static IntegrationProviderSettingsService CreateService(
        HelpdeskDbContext db,
        IConfiguration configuration,
        IDataProtectionProvider protectionProvider)
        => new(
            db,
            configuration,
            new IntegrationProviderSecretProtector(protectionProvider),
            Options.Create(new AiAssistantChatOptions()),
            new DatabaseOptions { Provider = "Sqlite" },
            TimeProvider.System,
            NullLogger<IntegrationProviderSettingsService>.Instance);

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

        public IntegrationProviderSettingsService CreateService(
            IConfiguration? configuration = null,
            IDataProtectionProvider? protectionProvider = null)
            => new(
                Db,
                configuration ?? Configuration,
                new IntegrationProviderSecretProtector(protectionProvider ?? new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
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
