using Helpdesk.Application.Orchestration;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.AiAssistant.Chat;
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
            ExpectedRevision = 0,
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
            ExpectedRevision = 0,
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
            ExpectedRevision = 0,
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
            ExpectedRevision = 0,
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
            ExpectedRevision = 0,
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
            ExpectedRevision = 0,
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
            ExpectedRevision = 0,
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
                ExpectedRevision = 0,
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
            ExpectedRevision = 0,
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
            ExpectedRevision = 0,
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

    [Fact]
    public async Task Legacy_session_owner_must_be_explicitly_confirmed_before_database_provider_change()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-a",
            ProviderProfileFingerprint = null
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync();
        var protection = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
        var service = fixture.CreateService(protectionProvider: protection);

        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() => service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Endpoint = "https://provider-b.example.test/hub/session",
            DeviceToken = "synthetic-token-b"
        }));
        var confirmed = await service.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = "dev",
            HistoricalEndpoint = "https://provider-a.example.test/hub/session",
            ExpectedEligibleConversations = 1,
            ConversationIds = [legacy.Id]
        }, "synthetic-admin");
        Assert.Equal(1, confirmed.BoundConversations);
        Assert.Equal(confirmed.ProviderProfileFingerprint,
            (await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).ProviderProfileFingerprint);
        var audit = await fixture.Db.ActivityLogs.SingleAsync();
        Assert.Equal("synthetic-admin", audit.UserId);
        Assert.Contains("HistoricalInstance=dev", audit.Message, StringComparison.Ordinal);
        Assert.Contains("HistoricalEndpoint=https://provider-a.example.test/hub/session", audit.Message, StringComparison.Ordinal);
        Assert.Contains($"ProviderFingerprint={confirmed.ProviderProfileFingerprint}", audit.Message, StringComparison.Ordinal);
        Assert.Contains("BoundConversations=1", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", audit.Message, StringComparison.Ordinal);

        var providerA = await service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Endpoint = "https://provider-a.example.test/hub/session",
            DeviceToken = "synthetic-token-a"
        });
        Assert.Equal(confirmed.ProviderProfileFingerprint, providerA.ProfileFingerprint);
        var rotatedA = await service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = providerA.Revision,
            Endpoint = providerA.Endpoint,
            DeviceToken = "synthetic-token-a-rotated"
        });
        Assert.Equal(providerA.ProfileFingerprint, rotatedA.ProfileFingerprint);

        var providerB = await service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = rotatedA.Revision,
            Endpoint = "https://provider-b.example.test/hub/session",
            DeviceToken = "synthetic-token-b"
        });
        var restartedService = fixture.CreateService(protectionProvider: protection);
        var resolvedB = await restartedService.GetResolvedNetclawSettingsAsync();
        Assert.Equal(providerB.ProfileFingerprint, resolvedB.ProfileFingerprint);
        Assert.False(resolvedB.CanAdoptLegacySessions);
        Assert.NotEqual(resolvedB.ProfileFingerprint,
            (await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).ProviderProfileFingerprint);
    }

    [Fact]
    public async Task Legacy_confirmation_rejects_null_conversation_ids()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "https://provider-a.example.test/hub/session",
                ExpectedEligibleConversations = 1,
                ConversationIds = null!
            },
            "synthetic-admin"));

        Assert.Equal("ConversationIds", exception.ParamName);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync());
    }

    [Fact]
    public async Task Legacy_confirmation_rejects_duplicate_conversation_ids()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var conversationId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "https://provider-a.example.test/hub/session",
                ExpectedEligibleConversations = 1,
                ConversationIds = [conversationId, conversationId]
            },
            "synthetic-admin"));

        Assert.Equal("ConversationIds", exception.ParamName);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync());
    }

    [Fact]
    public async Task Legacy_confirmation_binds_only_selected_ids_and_leaves_other_sessions_unbound()
    {
        await using var fixture = await Fixture.CreateAsync();
        var selected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "selected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-a"
        };
        var unselected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "unselected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-b"
        };
        fixture.Db.Set<AiAssistantChatConversation>().AddRange(selected, unselected);
        await fixture.Db.SaveChangesAsync();
        var service = fixture.CreateService();

        var confirmed = await service.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = "dev",
            HistoricalEndpoint = "https://provider-a.example.test/hub/session",
            ExpectedEligibleConversations = 1,
            ConversationIds = [selected.Id]
        }, "synthetic-admin");

        Assert.Equal(1, confirmed.BoundConversations);
        var persisted = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .OrderBy(conversation => conversation.Id).ToListAsync();
        Assert.Equal(confirmed.ProviderProfileFingerprint,
            persisted.Single(conversation => conversation.Id == selected.Id).ProviderProfileFingerprint);
        Assert.Null(persisted.Single(conversation => conversation.Id == unselected.Id).ProviderProfileFingerprint);
        Assert.Equal(unselected.Id,
            Assert.Single(await service.GetUnboundNetclawLegacySessionsAsync()).ConversationId);
    }

    [Fact]
    public async Task Legacy_confirmation_rejects_endpoint_credentials_without_audit_record()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-a"
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "https://synthetic-user:synthetic-password@provider-a.example.test/hub/session",
                ExpectedEligibleConversations = 1,
                ConversationIds = [legacy.Id]
            },
            "synthetic-admin"));

        Assert.Equal("HistoricalEndpoint", exception.ParamName);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync());
    }

    [Fact]
    public async Task Deployment_owned_provider_cannot_claim_unbound_or_confirmed_legacy_sessions_by_endpoint_alone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-a"
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync();
        var configurationA = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Netclaw:Endpoint"] = "https://provider-a.example.test/hub/session",
            ["Netclaw:Enabled"] = "true",
            ["Netclaw:DeviceToken"] = "synthetic-token-a"
        }).Build();
        var serviceA = fixture.CreateService(configurationA);
        Assert.False((await serviceA.GetResolvedNetclawSettingsAsync()).CanAdoptLegacySessions);
        var confirmed = await serviceA.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalEndpoint = "https://provider-a.example.test/hub/session",
            ExpectedEligibleConversations = 1,
            ConversationIds = [legacy.Id]
        }, "synthetic-admin");
        Assert.Equal(confirmed.ProviderProfileFingerprint, (await serviceA.GetResolvedNetclawSettingsAsync()).ProfileFingerprint);

        var configurationB = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Netclaw:Endpoint"] = "https://provider-b.example.test/hub/session",
            ["Netclaw:Enabled"] = "true",
            ["Netclaw:DeviceToken"] = "synthetic-token-b"
        }).Build();
        var resolvedB = await fixture.CreateService(configurationB).GetResolvedNetclawSettingsAsync();
        Assert.False(resolvedB.CanAdoptLegacySessions);
        Assert.NotEqual(confirmed.ProviderProfileFingerprint, resolvedB.ProfileFingerprint);
        Assert.Equal(confirmed.ProviderProfileFingerprint,
            (await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).ProviderProfileFingerprint);
    }

    [Fact]
    public async Task Newer_netclaw_revision_wins_when_an_older_runtime_apply_resumes()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"rateldesk-runtime-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("test");
            await using (var initializer = new HelpdeskDbContext(options, tenant, new HttpContextAccessor()))
                await initializer.Database.EnsureCreatedAsync();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var protection = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
            var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions()));
            var transport = new BlockingRuntimeTransport(runtime);
            await using var firstDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            await using var secondDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            var first = CreateService(firstDb, configuration, protection, transport, runtime);
            var second = CreateService(secondDb, configuration, protection, transport, runtime);

            var older = first.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
            {
                ExpectedRevision = 0,
                Enabled = false,
                Endpoint = "https://provider-a.example.test/hub/session"
            });
            await transport.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var newer = await second.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
            {
                ExpectedRevision = 1,
                Enabled = false,
                Endpoint = "https://provider-b.example.test/hub/session"
            });
            transport.ReleaseFirstApply.TrySetResult();

            await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() => older);
            Assert.Equal(2, newer.Revision);
            Assert.NotNull(newer.LastAppliedAtUtc);
            Assert.Equal(2, runtime.Current.Revision);
            Assert.Equal("https://provider-b.example.test/hub/session", runtime.Current.Endpoint);
            await using var verification = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            Assert.NotNull((await verification.NetclawConnectivitySettings.SingleAsync()).LastAppliedAtUtc);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static IntegrationProviderSettingsService CreateService(
        HelpdeskDbContext db,
        IConfiguration configuration,
        IDataProtectionProvider protectionProvider,
        IAiAssistantChatTransport? chatRuntime = null,
        IAiAssistantChatRuntimeState? runtimeState = null)
        => new(
            db,
            configuration,
            new IntegrationProviderSecretProtector(protectionProvider),
            Options.Create(new AiAssistantChatOptions()),
            new DatabaseOptions { Provider = "Sqlite" },
            TimeProvider.System,
            NullLogger<IntegrationProviderSettingsService>.Instance,
            chatRuntime,
            runtimeState);

    private sealed class BlockingRuntimeTransport(IAiAssistantChatRuntimeState runtime) : IAiAssistantChatTransport
    {
        public TaskCompletionSource FirstApplyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstApply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReconfigureAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<bool> TryReconfigureAsync(AiAssistantChatRuntimeSnapshot snapshot, CancellationToken ct)
        {
            if (snapshot.Revision == 1)
            {
                FirstApplyEntered.TrySetResult();
                await ReleaseFirstApply.Task.WaitAsync(ct);
            }

            return runtime.TryPublish(snapshot);
        }

        public Task ReconcileAsync(Guid conversation, CancellationToken ct) => Task.CompletedTask;
        public Task RetireAsync(Guid conversation, CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(Guid conversation, Guid messageId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(Guid conversation, string callId, string key, CancellationToken ct) => Task.CompletedTask;
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
