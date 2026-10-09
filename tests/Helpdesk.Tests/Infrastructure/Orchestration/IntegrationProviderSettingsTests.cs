using Helpdesk.API.Authentication;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Services;
using Helpdesk.Shared.Models;
using System.Text.Json;
using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class IntegrationProviderSettingsTests
{
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
    public async Task Pairing_preflight_rechecks_the_current_revision_after_loading_a_tracked_draft()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"rateldesk-netclaw-revision-race-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("test");
        var baseOptions = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(connectionString)
            .Options;
        try
        {
            await using (var initializer = new HelpdeskDbContext(baseOptions, tenant, new HttpContextAccessor()))
            {
                await initializer.Database.EnsureCreatedAsync();
                initializer.NetclawConnectivitySettings.Add(new NetclawConnectivitySettings
                {
                    ProviderKey = "Netclaw",
                    Revision = 1,
                    Endpoint = "https://saved-provider.example.test/hub/session",
                    ProfileFingerprint = "synthetic-profile-revision-1"
                });
                await initializer.SaveChangesAsync();
            }

            async Task AdvanceProfileRevisionAsync(CancellationToken cancellationToken)
            {
                await using var concurrentEditor = new HelpdeskDbContext(baseOptions, tenant, new HttpContextAccessor());
                var stored = await concurrentEditor.NetclawConnectivitySettings.SingleAsync(cancellationToken);
                stored.Revision = 2;
                stored.ProfileFingerprint = "synthetic-profile-revision-2";
                await concurrentEditor.SaveChangesAsync(cancellationToken);
            }

            var interceptor = new AdvanceProfileRevisionBeforeSecondReadInterceptor(AdvanceProfileRevisionAsync);
            var interceptedOptions = new DbContextOptionsBuilder<HelpdeskDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using var serviceDb = new HelpdeskDbContext(interceptedOptions, tenant, new HttpContextAccessor());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            using var currentOptions = CreateCurrentOptions(configuration);
            var service = new IntegrationProviderSettingsService(
                serviceDb,
                configuration,
                new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
                Options.Create(new AiAssistantChatOptions()),
                new DatabaseOptions { Provider = "PostgreSql" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance);

            var exception = await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() =>
                service.ResolveNetclawPairingTargetAsync(new UpdateNetclawConnectivitySettingsDto
                {
                    ExpectedRevision = 1,
                    Enabled = true,
                    Instance = "dev",
                    Endpoint = "https://new-provider.example.test",
                    DeviceToken = "synthetic-preflight-token"
                }, "synthetic-admin", legacyOwnershipReviewToken: null));

            Assert.Equal("configuration_revision_conflict", exception.Code);
            Assert.Equal(2, interceptor.ProfileRevision);
            await using var verification = new HelpdeskDbContext(baseOptions, tenant, new HttpContextAccessor());
            Assert.Equal(2, (await verification.NetclawConnectivitySettings.SingleAsync()).Revision);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
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

    [Theory]
    [InlineData("http://10.23.45.67/hub/session")]
    [InlineData("http://[fd12:3456:789a::42]/hub/session")]
    public async Task Legacy_confirmation_accepts_explicit_private_http_and_preserves_only_selected_history(string historicalEndpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await Fixture.CreateAsync();
        var selected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "selected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session-a",
            ProviderProfileFingerprint = null
        };
        var unselected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "unselected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session-b",
            ProviderProfileFingerprint = null
        };
        fixture.Db.Set<AiAssistantChatConversation>().AddRange(selected, unselected);
        fixture.Db.Set<AiAssistantChatEvent>().AddRange(
            new AiAssistantChatEvent
            {
                ConversationId = selected.Id,
                Sequence = 1,
                Type = "user",
                Text = "Synthetic transcript to preserve"
            },
            new AiAssistantChatEvent
            {
                ConversationId = unselected.Id,
                Sequence = 1,
                Type = "user",
                Text = "Unselected transcript to preserve"
            });
        await fixture.Db.SaveChangesAsync(timeout.Token);
        var service = fixture.CreateService();

        var confirmed = await service.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = "dev",
            HistoricalEndpoint = historicalEndpoint,
            AllowPrivateHttp = true,
            ExpectedEligibleConversations = 1,
            ConversationIds = [selected.Id]
        }, "synthetic-admin", timeout.Token);

        Assert.Equal(1, confirmed.BoundConversations);
        var conversations = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .OrderBy(conversation => conversation.Id).ToListAsync(timeout.Token);
        var persistedSelected = Assert.Single(conversations, conversation => conversation.Id == selected.Id);
        var persistedUnselected = Assert.Single(conversations, conversation => conversation.Id == unselected.Id);
        Assert.Equal("synthetic-remote-session-a", persistedSelected.AiAssistantSessionId);
        Assert.Equal(confirmed.ProviderProfileFingerprint, persistedSelected.ProviderProfileFingerprint);
        Assert.Equal("synthetic-remote-session-b", persistedUnselected.AiAssistantSessionId);
        Assert.Null(persistedUnselected.ProviderProfileFingerprint);
        var transcript = await fixture.Db.Set<AiAssistantChatEvent>().AsNoTracking().ToListAsync(timeout.Token);
        Assert.Equal("Synthetic transcript to preserve",
            Assert.Single(transcript, chatEvent => chatEvent.ConversationId == selected.Id).Text);
        Assert.Equal("Unselected transcript to preserve",
            Assert.Single(transcript, chatEvent => chatEvent.ConversationId == unselected.Id).Text);
        var audit = await fixture.Db.ActivityLogs.SingleAsync(timeout.Token);
        Assert.Equal("synthetic-admin", audit.UserId);
        Assert.Contains("Legacy Netclaw session owner confirmed", audit.Message, StringComparison.Ordinal);
        Assert.Contains($"HistoricalEndpoint={historicalEndpoint}", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-device-token", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-remote-session-a", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-remote-session-b", audit.Message, StringComparison.Ordinal);
        Assert.Single(await fixture.Db.ActivityLogs.ToListAsync(timeout.Token));
    }

    [Fact]
    public async Task Legacy_confirmation_private_http_without_opt_in_leaves_session_and_audit_unchanged()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session",
            ProviderProfileFingerprint = null
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync(timeout.Token);
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "http://10.23.45.67/hub/session",
                AllowPrivateHttp = false,
                ExpectedEligibleConversations = 1,
                ConversationIds = [legacy.Id]
            },
            "synthetic-admin", timeout.Token));

        Assert.Equal("HistoricalEndpoint", exception.ParamName);
        var persisted = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .SingleAsync(conversation => conversation.Id == legacy.Id, timeout.Token);
        Assert.Equal("synthetic-remote-session", persisted.AiAssistantSessionId);
        Assert.Null(persisted.ProviderProfileFingerprint);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync(timeout.Token));
    }

    [Theory]
    [InlineData("http://203.0.113.42/hub/session", "dev", true)]
    [InlineData("https://provider-a.example.test/wrong-path", "dev", false)]
    [InlineData("https://provider-a.example.test/hub/session", "production", false)]
    public async Task Legacy_confirmation_keeps_historical_endpoint_validation(
        string historicalEndpoint,
        string historicalInstance,
        bool allowPrivateHttp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session",
            ProviderProfileFingerprint = null
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync(timeout.Token);
        var service = fixture.CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = historicalInstance,
            HistoricalEndpoint = historicalEndpoint,
            AllowPrivateHttp = allowPrivateHttp,
            ExpectedEligibleConversations = 1,
            ConversationIds = [legacy.Id]
        }, "synthetic-admin", timeout.Token));

        var persisted = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .SingleAsync(conversation => conversation.Id == legacy.Id, timeout.Token);
        Assert.Null(persisted.ProviderProfileFingerprint);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync(timeout.Token));
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
    public async Task Legacy_confirmation_rejects_a_changed_eligible_count_without_binding_or_audit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await Fixture.CreateAsync();
        var eligible = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "eligible-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-eligible-session"
        };
        var alreadyBound = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "already-bound-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-bound-session",
            ProviderProfileFingerprint = "previous-provider-fingerprint"
        };
        fixture.Db.Set<AiAssistantChatConversation>().AddRange(eligible, alreadyBound);
        await fixture.Db.SaveChangesAsync(timeout.Token);
        var service = fixture.CreateService();

        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "https://provider-a.example.test/hub/session",
                ExpectedEligibleConversations = 2,
                ConversationIds = [eligible.Id, alreadyBound.Id]
            },
            "synthetic-admin", timeout.Token));

        var persisted = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .ToDictionaryAsync(conversation => conversation.Id, timeout.Token);
        Assert.Null(persisted[eligible.Id].ProviderProfileFingerprint);
        Assert.Equal("previous-provider-fingerprint", persisted[alreadyBound.Id].ProviderProfileFingerprint);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync(timeout.Token));
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
            using var currentOptions = CreateCurrentOptions(configuration);
            await using var firstDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            await using var secondDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            var first = CreateService(firstDb, configuration, protection, currentOptions, transport, runtime);
            var second = CreateService(secondDb, configuration, protection, currentOptions, transport, runtime);

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

    private static ServiceProvider CreateCurrentOptions(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddRatelDeskServiceIdentity(configuration);
        return services.BuildServiceProvider();
    }

    private static IntegrationProviderSettingsService CreateService(
        HelpdeskDbContext db,
        IConfiguration configuration,
        IDataProtectionProvider protectionProvider,
        ServiceProvider currentOptions,
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

    private sealed class AdvanceProfileRevisionBeforeSecondReadInterceptor(
        Func<CancellationToken, Task> advanceProfileRevision) : DbCommandInterceptor
    {
        private int profileReadCount;
        private int profileRevision;

        public int ProfileRevision => Volatile.Read(ref profileRevision);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("NetclawConnectivitySettings", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref profileReadCount) == 2)
            {
                await advanceProfileRevision(cancellationToken);
                Volatile.Write(ref profileRevision, 2);
            }

            return result;
        }
    }

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
        private readonly ServiceProvider currentOptions;
        private readonly List<ServiceProvider> alternateOptions = [];
        public HelpdeskDbContext Db { get; }
        public IOptions<AiAssistantChatOptions> ChatOptions { get; } = Options.Create(new AiAssistantChatOptions());
        public IConfigurationRoot Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceIdentity:Audience"] = "rateldesk.services", ["ServiceIdentity:InstanceId"] = "local-instance",
            ["ServiceIdentity:ApiBaseUrl"] = "https://api.local.example.test", ["ServiceIdentity:WebBaseUrl"] = "https://local.example.test",
        }).Build();
        public IOptionsMonitor<ServiceIdentityOptions> IdentityOptions => currentOptions.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>();

        private Fixture(SqliteConnection connection, HelpdeskDbContext db)
        {
            this.connection = connection;
            Db = db;
            currentOptions = CreateCurrentOptions(Configuration);
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
        {
            var serviceConfiguration = configuration ?? Configuration;
            var serviceOptions = currentOptions;
            if (!ReferenceEquals(serviceConfiguration, Configuration))
            {
                serviceOptions = CreateCurrentOptions(serviceConfiguration);
                alternateOptions.Add(serviceOptions);
            }
            return new(
                Db,
                serviceConfiguration,
                new IntegrationProviderSecretProtector(protectionProvider ?? new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
                ChatOptions,
                new DatabaseOptions { Provider = "Sqlite" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var options in alternateOptions) options.Dispose();
            currentOptions.Dispose();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
