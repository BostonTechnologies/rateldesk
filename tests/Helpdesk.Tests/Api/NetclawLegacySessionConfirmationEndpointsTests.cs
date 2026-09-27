using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed class NetclawLegacySessionConfirmationEndpointsTests
{
    private const string SettingsPath = "/api/v1/admin/netclaw";
    private const string ConfirmOwnerPath = "/api/v1/admin/netclaw/legacy-sessions/confirm-owner";
    private const string PairAndSavePath = "/api/v1/admin/netclaw/pair-and-save";

    [Fact]
    public async Task Pairing_preflight_blocks_code_exchange_when_legacy_sessions_need_owner_confirmation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token);
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "pairing-blocker-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-legacy-session"
        };
        await harness.SeedAsync([legacy], [], timeout.Token);
        harness.AuthorizeAs("admin");

        using var response = await harness.Client.PostAsJsonAsync(PairAndSavePath, new PairNetclawDeviceDto
        {
            PairingCode = "synthetic-one-time-code",
            ExpectedRevision = 0,
            Enabled = true,
            Instance = "dev",
            Endpoint = "https://new-provider.example.test/hub/session"
        }, timeout.Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.Contains("unbound_legacy_sessions", body, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-one-time-code", body, StringComparison.Ordinal);
        await harness.PairingService.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<NetclawPairingTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await using var db = harness.CreateDbContext();
        Assert.Empty(await db.NetclawConnectivitySettings.ToListAsync(timeout.Token));
    }

    [Fact]
    public async Task Pair_and_save_stores_a_protected_token_and_returns_a_redacted_profile()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token);
        harness.AuthorizeAs("admin");

        using var response = await harness.Client.PostAsJsonAsync(PairAndSavePath, new PairNetclawDeviceDto
        {
            PairingCode = "synthetic-one-time-code",
            ExpectedRevision = 0,
            Enabled = true,
            Instance = "dev",
            Endpoint = "https://netclaw.example.test/hub/session"
        }, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.DoesNotContain("synthetic-one-time-code", body, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-paired-device-token", body, StringComparison.Ordinal);
        var profile = JsonSerializer.Deserialize<NetclawConnectivitySettingsDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(profile);
        Assert.Equal(1, profile.Revision);
        Assert.True(profile.HasDeviceToken);
        Assert.Null(typeof(NetclawConnectivitySettingsDto).GetProperty("DeviceToken"));
        await harness.PairingService.Received(1).ExchangeCodeAsync(
            Arg.Is<NetclawPairingTarget>(target =>
                target.SessionEndpoint.AbsoluteUri == "https://netclaw.example.test/hub/session" && !target.AllowPrivateHttp),
            "synthetic-one-time-code",
            Arg.Any<CancellationToken>());

        await using var db = harness.CreateDbContext();
        var stored = await db.NetclawConnectivitySettings.SingleAsync(timeout.Token);
        Assert.NotEqual("synthetic-paired-device-token", stored.ProtectedDeviceToken);
        Assert.DoesNotContain("synthetic-paired-device-token", stored.ProtectedDeviceToken, StringComparison.Ordinal);
        var audit = await db.ActivityLogs.Select(log => log.Message).ToListAsync(timeout.Token);
        Assert.Single(audit);
        Assert.DoesNotContain("synthetic-one-time-code", string.Join('\n', audit), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-paired-device-token", string.Join('\n', audit), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pairing_the_same_saved_provider_allows_unbound_sessions_without_assigning_their_history()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token);
        harness.AuthorizeAs("admin");
        const string endpoint = "https://current-provider.example.test/hub/session";
        using (var initialSave = await harness.Client.PutAsJsonAsync(SettingsPath, new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = true,
            Instance = "dev",
            Endpoint = endpoint,
            DeviceToken = "synthetic-old-device-token"
        }, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.OK, initialSave.StatusCode);
        }

        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "same-profile-pairing-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-same-profile-legacy-session"
        };
        var transcript = new AiAssistantChatEvent
        {
            ConversationId = legacy.Id,
            Sequence = 1,
            Type = "user",
            Text = "Synthetic transcript remains unmodified"
        };
        await harness.SeedAsync([legacy], [transcript], timeout.Token);

        using var response = await harness.Client.PostAsJsonAsync(PairAndSavePath, new PairNetclawDeviceDto
        {
            PairingCode = "synthetic-one-time-code",
            ExpectedRevision = 1,
            Enabled = true,
            Instance = "dev",
            Endpoint = endpoint
        }, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<NetclawConnectivitySettingsDto>(timeout.Token);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Revision);
        await harness.PairingService.Received(1).ExchangeCodeAsync(
            Arg.Any<NetclawPairingTarget>(), "synthetic-one-time-code", Arg.Any<CancellationToken>());

        await using var db = harness.CreateDbContext();
        var persisted = await db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(timeout.Token);
        Assert.Equal("synthetic-same-profile-legacy-session", persisted.AiAssistantSessionId);
        Assert.Null(persisted.ProviderProfileFingerprint);
        Assert.Equal("Synthetic transcript remains unmodified",
            (await db.Set<AiAssistantChatEvent>().AsNoTracking().SingleAsync(timeout.Token)).Text);
    }

    [Fact]
    public async Task Pair_and_save_remains_admin_only()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token);
        harness.AuthorizeAs("technician");

        using var response = await harness.Client.PostAsJsonAsync(PairAndSavePath, new PairNetclawDeviceDto
        {
            PairingCode = "synthetic-one-time-code",
            ExpectedRevision = 0,
            Enabled = true,
            Instance = "dev",
            Endpoint = "https://netclaw.example.test/hub/session"
        }, timeout.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await harness.PairingService.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<NetclawPairingTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task First_save_with_unbound_legacy_sessions_returns_specific_conflict_without_changing_history()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token);
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "beta4-first-save-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-legacy-session"
        };
        var transcript = new AiAssistantChatEvent
        {
            ConversationId = legacy.Id,
            Sequence = 1,
            Type = "user",
            Text = "Synthetic transcript remains attached"
        };
        await harness.SeedAsync([legacy], [transcript], timeout.Token);
        harness.AuthorizeAs("admin");

        using var response = await harness.Client.PutAsJsonAsync(SettingsPath, new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Endpoint = "https://new-provider.example.test/hub/session",
            DeviceToken = "synthetic-device-token"
        }, timeout.Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        Assert.Equal("unbound_legacy_sessions", body.GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic-device-token", await response.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
        await using var db = harness.CreateDbContext();
        Assert.Empty(await db.NetclawConnectivitySettings.ToListAsync(timeout.Token));
        var persisted = await db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(timeout.Token);
        Assert.Null(persisted.ProviderProfileFingerprint);
        Assert.Equal("synthetic-legacy-session", persisted.AiAssistantSessionId);
        Assert.Equal("Synthetic transcript remains attached",
            (await db.Set<AiAssistantChatEvent>().AsNoTracking().SingleAsync(timeout.Token)).Text);
        Assert.Empty(await db.ActivityLogs.ToListAsync(timeout.Token));
    }

    [Fact]
    public async Task First_save_recovers_through_explicit_historical_owner_confirmation_and_saves_the_same_draft()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token);
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "beta4-recovery-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-legacy-session"
        };
        var transcript = new AiAssistantChatEvent
        {
            ConversationId = legacy.Id,
            Sequence = 1,
            Type = "user",
            Text = "Synthetic transcript survives ownership recovery"
        };
        await harness.SeedAsync([legacy], [transcript], timeout.Token);
        harness.AuthorizeAs("admin");
        var draft = new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = true,
            Instance = "dev",
            Endpoint = "https://new-provider.example.test/hub/session",
            DeviceToken = "synthetic-device-token"
        };

        using (var blockedSave = await harness.Client.PutAsJsonAsync(SettingsPath, draft, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.Conflict, blockedSave.StatusCode);
            var blockedBody = await blockedSave.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            Assert.Equal("unbound_legacy_sessions", blockedBody.GetProperty("code").GetString());
        }

        using var listResponse = await harness.Client.GetAsync("/api/v1/admin/netclaw/legacy-sessions/unbound", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var unbound = await listResponse.Content.ReadFromJsonAsync<NetclawUnboundLegacySessionDto[]>(timeout.Token);
        Assert.DoesNotContain("synthetic-legacy-session", await listResponse.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
        var listedSession = Assert.Single(unbound!);
        Assert.Equal(legacy.Id, listedSession.ConversationId);
        Assert.Equal("synthetic-tenant", listedSession.OrganizationId);
        Assert.Equal("beta4-recovery-ticket", listedSession.TicketId);
        Assert.Equal("incidents", listedSession.TicketType);

        using var confirmationResponse = await harness.Client.PostAsJsonAsync(ConfirmOwnerPath, new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = "dev",
            HistoricalEndpoint = "http://10.23.45.67/hub/session",
            AllowPrivateHttp = true,
            ExpectedEligibleConversations = 1,
            ConversationIds = [listedSession.ConversationId]
        }, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var confirmation = await confirmationResponse.Content.ReadFromJsonAsync<NetclawLegacySessionConfirmationDto>(timeout.Token);
        Assert.NotNull(confirmation);
        Assert.Equal(1, confirmation.BoundConversations);

        using var savedResponse = await harness.Client.PutAsJsonAsync(SettingsPath, draft, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var saved = await savedResponse.Content.ReadFromJsonAsync<NetclawConnectivitySettingsDto>(timeout.Token);
        Assert.NotNull(saved);
        Assert.Equal(1, saved.Revision);
        Assert.Equal("https://new-provider.example.test/hub/session", saved.Endpoint);
        Assert.DoesNotContain("synthetic-device-token", await savedResponse.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);

        await using var db = harness.CreateDbContext();
        var persisted = await db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(timeout.Token);
        Assert.Equal("synthetic-legacy-session", persisted.AiAssistantSessionId);
        Assert.Equal(confirmation.ProviderProfileFingerprint, persisted.ProviderProfileFingerprint);
        Assert.Equal("Synthetic transcript survives ownership recovery",
            (await db.Set<AiAssistantChatEvent>().AsNoTracking().SingleAsync(timeout.Token)).Text);
        var audit = await db.ActivityLogs.OrderBy(log => log.Id).ToListAsync(timeout.Token);
        Assert.Equal(2, audit.Count);
        Assert.DoesNotContain("synthetic-device-token", string.Join('\n', audit.Select(log => log.Message)), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-legacy-session", string.Join('\n', audit.Select(log => log.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_netclaw_save_revision_returns_specific_conflict_code()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token);
        await using (var db = harness.CreateDbContext())
        {
            db.NetclawConnectivitySettings.Add(new NetclawConnectivitySettings
            {
                ProviderKey = "Netclaw",
                Revision = 2,
                Endpoint = "https://saved-provider.example.test/hub/session",
                ProfileFingerprint = "synthetic-profile"
            });
            await db.SaveChangesAsync(timeout.Token);
        }
        harness.AuthorizeAs("admin");

        using var response = await harness.Client.PutAsJsonAsync(SettingsPath, new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 1,
            Endpoint = "https://draft-provider.example.test/hub/session"
        }, timeout.Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        Assert.Equal("configuration_revision_conflict", body.GetProperty("code").GetString());
        await using var verification = harness.CreateDbContext();
        Assert.Equal(2, (await verification.NetclawConnectivitySettings.SingleAsync(timeout.Token)).Revision);
    }

    [Fact]
    public async Task Stale_runtime_revision_returns_specific_conflict_code()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions()));
        runtime.Publish(new AiAssistantChatRuntimeSnapshot(
            Enabled: false,
            Instance: "dev",
            Endpoint: "https://runtime-provider.example.test/hub/session",
            DeviceToken: string.Empty,
            AllowPrivateHttp: false,
            IdleMinutes: 15,
            ConnectionCapacity: 25,
            TurnInactivityTimeout: TimeSpan.FromMinutes(5),
            ActivityHeartbeatInterval: TimeSpan.FromSeconds(15),
            ProfileFingerprint: "synthetic-runtime-profile",
            Revision: 4,
            Source: "database",
            ManagedByDeployment: false,
            SourceKey: "database"));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token, runtimeState: runtime);
        await using (var db = harness.CreateDbContext())
        {
            db.NetclawConnectivitySettings.Add(new NetclawConnectivitySettings
            {
                ProviderKey = "Netclaw",
                Revision = 1,
                Endpoint = "https://saved-provider.example.test/hub/session",
                ProfileFingerprint = "synthetic-saved-profile"
            });
            await db.SaveChangesAsync(timeout.Token);
        }
        harness.AuthorizeAs("admin");

        using var response = await harness.Client.PutAsJsonAsync(SettingsPath, new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 1,
            Endpoint = "https://new-provider.example.test/hub/session"
        }, timeout.Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        Assert.Equal("runtime_revision_conflict", body.GetProperty("code").GetString());
        await using var verification = harness.CreateDbContext();
        var persisted = await verification.NetclawConnectivitySettings.SingleAsync(timeout.Token);
        Assert.Equal(2, persisted.Revision);
        Assert.Null(persisted.LastAppliedAtUtc);
    }

    [Theory]
    [InlineData("http://10.23.45.67/hub/session")]
    [InlineData("http://[fd12:3456:789a::42]/hub/session")]
    public async Task Authenticated_admin_can_confirm_private_http_legacy_owner_for_selected_session(string historicalEndpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token);
        var selected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "selected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session-a"
        };
        var unselected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "unselected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session-b"
        };
        await harness.SeedAsync(
            [selected, unselected],
            [
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
                }
            ],
            timeout.Token);
        harness.AuthorizeAs("admin");

        using var response = await harness.Client.PostAsJsonAsync(ConfirmOwnerPath, new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = "dev",
            HistoricalEndpoint = historicalEndpoint,
            AllowPrivateHttp = true,
            ExpectedEligibleConversations = 1,
            ConversationIds = [selected.Id]
        }, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var confirmation = await response.Content.ReadFromJsonAsync<NetclawLegacySessionConfirmationDto>(timeout.Token);
        Assert.NotNull(confirmation);
        Assert.Equal(1, confirmation.BoundConversations);
        await using var db = harness.CreateDbContext();
        var persistedSelected = await db.Set<AiAssistantChatConversation>().AsNoTracking()
            .SingleAsync(conversation => conversation.Id == selected.Id, timeout.Token);
        var persistedUnselected = await db.Set<AiAssistantChatConversation>().AsNoTracking()
            .SingleAsync(conversation => conversation.Id == unselected.Id, timeout.Token);
        Assert.Equal("synthetic-remote-session-a", persistedSelected.AiAssistantSessionId);
        Assert.Equal(confirmation.ProviderProfileFingerprint, persistedSelected.ProviderProfileFingerprint);
        Assert.Equal("synthetic-remote-session-b", persistedUnselected.AiAssistantSessionId);
        Assert.Null(persistedUnselected.ProviderProfileFingerprint);
        var transcript = await db.Set<AiAssistantChatEvent>().AsNoTracking().ToListAsync(timeout.Token);
        Assert.Equal("Synthetic transcript to preserve", Assert.Single(transcript, chatEvent => chatEvent.ConversationId == selected.Id).Text);
        Assert.Equal("Unselected transcript to preserve", Assert.Single(transcript, chatEvent => chatEvent.ConversationId == unselected.Id).Text);
        var audit = await db.ActivityLogs.SingleAsync(timeout.Token);
        Assert.Equal("synthetic-admin", audit.UserId);
        Assert.Contains("HistoricalInstance=dev", audit.Message, StringComparison.Ordinal);
        Assert.Contains($"HistoricalEndpoint={historicalEndpoint}", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-device-token", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-remote-session-a", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-remote-session-b", audit.Message, StringComparison.Ordinal);
        Assert.Single(await db.ActivityLogs.ToListAsync(timeout.Token));
        Assert.Empty(harness.ClientFactory.ReceivedCalls());
    }

    [Fact]
    public async Task Private_http_confirmation_requires_explicit_opt_in_and_authenticated_admin()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token, deploymentAllowsPrivateHttp: true);
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session"
        };
        var originalTranscript = new AiAssistantChatEvent
        {
            ConversationId = legacy.Id,
            Sequence = 1,
            Type = "user",
            Text = "Synthetic transcript remains unchanged"
        };
        await harness.SeedAsync([legacy], [originalTranscript], timeout.Token);
        var omittedOptInBody = JsonSerializer.Serialize(new
        {
            historicalInstance = "dev",
            historicalEndpoint = "http://10.23.45.67/hub/session",
            expectedEligibleConversations = 1,
            conversationIds = new[] { legacy.Id }
        });

        using (var unauthenticated = await harness.Client.PostAsync(
                   ConfirmOwnerPath,
                   new StringContent(omittedOptInBody, Encoding.UTF8, "application/json"),
                   timeout.Token))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        harness.AuthorizeAs("technician");
        using (var nonAdmin = await harness.Client.PostAsync(
                   ConfirmOwnerPath,
                   new StringContent(omittedOptInBody, Encoding.UTF8, "application/json"),
                   timeout.Token))
            Assert.Equal(HttpStatusCode.Forbidden, nonAdmin.StatusCode);

        harness.AuthorizeAs("admin");
        using var response = await harness.Client.PostAsync(
            ConfirmOwnerPath,
            new StringContent(omittedOptInBody, Encoding.UTF8, "application/json"),
            timeout.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        Assert.Equal("invalid_legacy_provider", body.GetProperty("code").GetString());
        await using var db = harness.CreateDbContext();
        var persisted = await db.Set<AiAssistantChatConversation>().AsNoTracking()
            .SingleAsync(conversation => conversation.Id == legacy.Id, timeout.Token);
        Assert.Equal("synthetic-remote-session", persisted.AiAssistantSessionId);
        Assert.Null(persisted.ProviderProfileFingerprint);
        Assert.Equal("Synthetic transcript remains unchanged",
            (await db.Set<AiAssistantChatEvent>().AsNoTracking().SingleAsync(timeout.Token)).Text);
        Assert.Empty(await db.ActivityLogs.ToListAsync(timeout.Token));
        Assert.Empty(harness.ClientFactory.ReceivedCalls());
    }

    [Fact]
    public async Task Null_conversation_ids_return_bad_request()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await EndpointHarness.CreateAsync(timeout.Token);
        harness.AuthorizeAs("admin");
        using var content = new StringContent(
            """{"historicalInstance":"dev","historicalEndpoint":"https://provider-a.example.test/hub/session","expectedEligibleConversations":1,"conversationIds":null}""",
            Encoding.UTF8,
            "application/json");

        using var response = await harness.Client.PostAsync(ConfirmOwnerPath, content, timeout.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        Assert.Equal("invalid_legacy_provider", body.GetProperty("code").GetString());
    }

    private sealed class EndpointHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly WebApplication app;
        private readonly ITenantContext tenant;

        private EndpointHarness(
            SqliteConnection connection,
            WebApplication app,
            HttpClient client,
            ITenantContext tenant,
            IAiAssistantChatClientFactory clientFactory,
            INetclawPairingService pairingService)
        {
            this.connection = connection;
            this.app = app;
            Client = client;
            this.tenant = tenant;
            ClientFactory = clientFactory;
            PairingService = pairingService;
        }

        public HttpClient Client { get; }
        public IAiAssistantChatClientFactory ClientFactory { get; }
        public INetclawPairingService PairingService { get; }

        public static async Task<EndpointHarness> CreateAsync(
            CancellationToken cancellationToken,
            bool deploymentAllowsPrivateHttp = false,
            IAiAssistantChatRuntimeState? runtimeState = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            if (deploymentAllowsPrivateHttp)
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Netclaw:AllowPrivateHttp"] = "true"
                });
            }
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("synthetic-tenant");
            tenant.UserId.Returns("synthetic-admin");
            tenant.IsHelpdeskAdmin.Returns(true);
            builder.Services.AddSingleton(tenant);
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(connection));
            if (runtimeState is not null) builder.Services.AddSingleton(runtimeState);
            builder.Services.AddScoped<IIntegrationProviderSettingsService>(services => new IntegrationProviderSettingsService(
                services.GetRequiredService<HelpdeskDbContext>(),
                services.GetRequiredService<IConfiguration>(),
                new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
                Options.Create(new AiAssistantChatOptions()),
                new DatabaseOptions { Provider = "Sqlite" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance,
                runtimeState: services.GetService<IAiAssistantChatRuntimeState>()));
            var clientFactory = Substitute.For<IAiAssistantChatClientFactory>();
            builder.Services.AddSingleton(clientFactory);
            var pairingService = Substitute.For<INetclawPairingService>();
            pairingService.ExchangeCodeAsync(
                    Arg.Any<NetclawPairingTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult("synthetic-paired-device-token"));
            builder.Services.AddSingleton(pairingService);
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, ConfirmationTestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("HelpdeskAdmin", policy =>
            {
                policy.AddAuthenticationSchemes("Test");
                policy.RequireAuthenticatedUser();
                policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin);
            }));

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapNetclawConnectivityEndpoints();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await db.Database.EnsureCreatedAsync(cancellationToken);
            }

            await app.StartAsync(cancellationToken);
            var client = app.GetTestClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            return new EndpointHarness(connection, app, client, tenant, clientFactory, pairingService);
        }

        public HelpdeskDbContext CreateDbContext()
            => new(
                new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options,
                tenant,
                new HttpContextAccessor());

        public async Task SeedAsync(
            IReadOnlyCollection<AiAssistantChatConversation> conversations,
            IReadOnlyCollection<AiAssistantChatEvent> events,
            CancellationToken cancellationToken)
        {
            await using var db = CreateDbContext();
            db.Set<AiAssistantChatConversation>().AddRange(conversations);
            db.Set<AiAssistantChatEvent>().AddRange(events);
            await db.SaveChangesAsync(cancellationToken);
        }

        public void AuthorizeAs(string identity)
            => Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", identity);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ConfirmationTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var authorization) ||
                !string.Equals(authorization.Scheme, "Test", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var (userId, role) = authorization.Parameter switch
            {
                "admin" => ("synthetic-admin", HelpdeskPermissions.HelpdeskAdmin),
                "technician" => ("synthetic-technician", "Technician"),
                _ => ("synthetic-user", "User")
            };
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
