using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Connectivity;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class NetclawPairingLegacyPostgresRaceTests
{
    private const string PairAndSavePath = "/api/v1/admin/netclaw/pair-and-save";

    [Fact]
    public async Task Serializable_ownership_review_rejects_same_count_replacement_then_only_one_contender_pairs()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16").Build();
        await postgres.StartAsync();
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("synthetic-tenant");
        var connectionString = postgres.GetConnectionString();
        var dbOptions = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var initializer = new HelpdeskDbContext(dbOptions, tenant, new HttpContextAccessor()))
            await initializer.Database.EnsureCreatedAsync();

        var gate = new LegacyOwnershipUpdateGate();
        await using var harness = await EndpointHarness.CreateAsync(connectionString, tenant, gate);
        harness.AuthorizeAsAdmin();
        var original = CreateConversation("original-legacy-ticket", "synthetic-original-session");
        var originalTranscript = new AiAssistantChatEvent
        {
            ConversationId = original.Id,
            Sequence = 1,
            Type = "user",
            Text = "Original synthetic transcript"
        };
        await harness.SeedAsync([original], [originalTranscript]);

        var pairRequest = new PairNetclawDeviceDto
        {
            PairingCode = "synthetic-one-time-code",
            ExpectedRevision = 0,
            Endpoint = "https://netclaw.example.test"
        };
        using var firstReviewResponse = await harness.Client.PostAsJsonAsync(PairAndSavePath, pairRequest);
        Assert.Equal(HttpStatusCode.Conflict, firstReviewResponse.StatusCode);
        var firstReview = await firstReviewResponse.Content.ReadFromJsonAsync<NetclawLegacySessionReviewConflictDto>();
        Assert.NotNull(firstReview);
        Assert.Equal("legacy_ownership_confirmation_required", firstReview.Code);
        Assert.Equal(1, firstReview.LegacyConversationCount);
        await harness.PairingService.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<NetclawPairingTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        var replacement = CreateConversation("replacement-legacy-ticket", "synthetic-replacement-session");
        await using (var editor = harness.CreateDbContext())
        {
            var persistedOriginal = await editor.Set<AiAssistantChatConversation>().SingleAsync(item => item.Id == original.Id);
            persistedOriginal.ProviderProfileFingerprint = "synthetic-historical-profile";
            editor.Set<AiAssistantChatConversation>().Add(replacement);
            editor.Set<AiAssistantChatEvent>().Add(new AiAssistantChatEvent
            {
                ConversationId = replacement.Id,
                Sequence = 1,
                Type = "user",
                Text = "Replacement synthetic transcript"
            });
            await editor.SaveChangesAsync();
        }

        pairRequest.LegacyOwnershipReviewToken = firstReview.LegacyOwnershipReviewToken;
        using var staleReviewResponse = await harness.Client.PostAsJsonAsync(PairAndSavePath, pairRequest);
        Assert.Equal(HttpStatusCode.Conflict, staleReviewResponse.StatusCode);
        var staleReview = await staleReviewResponse.Content.ReadFromJsonAsync<NetclawLegacySessionReviewConflictDto>();
        Assert.NotNull(staleReview);
        Assert.Equal("legacy_session_conflict", staleReview.Code);
        Assert.Equal(1, staleReview.LegacyConversationCount);
        Assert.NotEqual(firstReview.LegacyOwnershipReviewToken, staleReview.LegacyOwnershipReviewToken);
        await harness.PairingService.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<NetclawPairingTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await using (var verification = harness.CreateDbContext())
        {
            Assert.Empty(await verification.NetclawConnectivitySettings.ToListAsync());
            Assert.Empty(await verification.ActivityLogs.ToListAsync());
            Assert.Null((await verification.Set<AiAssistantChatConversation>()
                .SingleAsync(item => item.Id == replacement.Id)).ProviderProfileFingerprint);
        }

        pairRequest.LegacyOwnershipReviewToken = null;
        using var refreshedReviewResponse = await harness.Client.PostAsJsonAsync(PairAndSavePath, pairRequest);
        Assert.Equal(HttpStatusCode.Conflict, refreshedReviewResponse.StatusCode);
        var refreshedReview = await refreshedReviewResponse.Content.ReadFromJsonAsync<NetclawLegacySessionReviewConflictDto>();
        Assert.NotNull(refreshedReview);
        Assert.Equal(1, refreshedReview.LegacyConversationCount);
        Assert.NotEqual(firstReview.LegacyOwnershipReviewToken, refreshedReview.LegacyOwnershipReviewToken);

        pairRequest.LegacyOwnershipReviewToken = refreshedReview.LegacyOwnershipReviewToken;
        harness.PairingService.ExchangeCodeAsync(
                Arg.Any<NetclawPairingTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => VerifyOwnershipWasCommittedBeforeExchangeAsync(
                harness,
                original.Id,
                replacement.Id,
                originalTranscript.Text,
                "Replacement synthetic transcript"));
        var secondRequest = ClonePairRequest(pairRequest);
        var firstContender = harness.Client.PostAsJsonAsync(PairAndSavePath, pairRequest);
        var secondContender = harness.Client.PostAsJsonAsync(PairAndSavePath, secondRequest);
        try
        {
            await gate.BothUpdatesEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            gate.ReleaseUpdates.TrySetResult();
        }

        using var firstResponse = await firstContender.WaitAsync(TimeSpan.FromSeconds(20));
        using var secondResponse = await secondContender.WaitAsync(TimeSpan.FromSeconds(20));
        var responses = new[] { firstResponse, secondResponse };
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        using var losingBody = JsonDocument.Parse(await responses.Single(response => response.StatusCode == HttpStatusCode.Conflict).Content.ReadAsStringAsync());
        Assert.Equal("legacy_session_conflict", losingBody.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, losingBody.RootElement.GetProperty("legacyConversationCount").GetInt32());
        await harness.PairingService.Received(1).ExchangeCodeAsync(
            Arg.Any<NetclawPairingTarget>(), "synthetic-one-time-code", Arg.Any<CancellationToken>());

        await using var finalState = harness.CreateDbContext();
        var savedProfile = await finalState.NetclawConnectivitySettings.SingleAsync();
        Assert.Equal(1, savedProfile.Revision);
        Assert.True(savedProfile.LastTestSucceeded);
        var conversations = await finalState.Set<AiAssistantChatConversation>().AsNoTracking().ToListAsync();
        var savedOriginal = Assert.Single(conversations, item => item.Id == original.Id);
        var savedReplacement = Assert.Single(conversations, item => item.Id == replacement.Id);
        Assert.Equal("synthetic-historical-profile", savedOriginal.ProviderProfileFingerprint);
        Assert.Equal(savedProfile.ProfileFingerprint, savedReplacement.ProviderProfileFingerprint);
        Assert.Equal("synthetic-original-session", savedOriginal.AiAssistantSessionId);
        Assert.Equal("synthetic-replacement-session", savedReplacement.AiAssistantSessionId);
        var transcripts = await finalState.Set<AiAssistantChatEvent>().AsNoTracking().ToListAsync();
        Assert.Equal("Original synthetic transcript", Assert.Single(transcripts, item => item.ConversationId == original.Id).Text);
        Assert.Equal("Replacement synthetic transcript", Assert.Single(transcripts, item => item.ConversationId == replacement.Id).Text);
        var audit = Assert.Single(await finalState.ActivityLogs.ToListAsync());
        Assert.Contains("BoundConversations=1", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-one-time-code", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-paired-device-token", audit.Message, StringComparison.Ordinal);
    }

    private static AiAssistantChatConversation CreateConversation(string ticketId, string sessionId)
        => new()
        {
            OrganizationId = "synthetic-tenant",
            TicketId = ticketId,
            TicketType = "incidents",
            AiAssistantSessionId = sessionId
        };

    private static PairNetclawDeviceDto ClonePairRequest(PairNetclawDeviceDto request)
        => new()
        {
            PairingCode = request.PairingCode,
            ExpectedRevision = request.ExpectedRevision,
            Endpoint = request.Endpoint,
            LegacyOwnershipReviewToken = request.LegacyOwnershipReviewToken,
            IdleMinutes = request.IdleMinutes,
            ConnectionCapacity = request.ConnectionCapacity,
            TurnInactivityTimeout = request.TurnInactivityTimeout,
            ActivityHeartbeatInterval = request.ActivityHeartbeatInterval
        };

    private static async Task<string> VerifyOwnershipWasCommittedBeforeExchangeAsync(
        EndpointHarness harness,
        Guid originalConversationId,
        Guid replacementConversationId,
        string originalTranscript,
        string replacementTranscript)
    {
        await using var observer = harness.CreateDbContext();
        var profile = await observer.NetclawConnectivitySettings.SingleOrDefaultAsync();
        Assert.Null(profile);

        var replacement = await observer.Set<AiAssistantChatConversation>().AsNoTracking()
            .SingleAsync(item => item.Id == replacementConversationId);
        Assert.False(string.IsNullOrWhiteSpace(replacement.ProviderProfileFingerprint));
        var original = await observer.Set<AiAssistantChatConversation>().AsNoTracking()
            .SingleAsync(item => item.Id == originalConversationId);
        Assert.Equal("synthetic-historical-profile", original.ProviderProfileFingerprint);
        var transcripts = await observer.Set<AiAssistantChatEvent>().AsNoTracking().ToListAsync();
        Assert.Equal(originalTranscript, Assert.Single(transcripts, item => item.ConversationId == originalConversationId).Text);
        Assert.Equal(replacementTranscript, Assert.Single(transcripts, item => item.ConversationId == replacementConversationId).Text);
        Assert.Empty(await observer.Set<AiAssistantChatConversation>().AsNoTracking()
            .Where(item => item.ProviderProfileFingerprint == null &&
                           item.AiAssistantSessionId != null &&
                           item.AiAssistantSessionId != "")
            .ToListAsync());
        var audit = Assert.Single(await observer.ActivityLogs.AsNoTracking().ToListAsync());
        Assert.Contains("BoundConversations=1", audit.Message, StringComparison.Ordinal);
        Assert.Contains($"ProviderFingerprint={replacement.ProviderProfileFingerprint}", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-one-time-code", audit.Message, StringComparison.Ordinal);
        return "synthetic-paired-device-token";
    }

    private sealed class LegacyOwnershipUpdateGate : DbCommandInterceptor
    {
        private int updateCount;
        public TaskCompletionSource BothUpdatesEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUpdates { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("ProviderProfileFingerprint", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref updateCount) <= 2)
            {
                if (Volatile.Read(ref updateCount) == 2)
                    BothUpdatesEntered.TrySetResult();
                await ReleaseUpdates.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class EndpointHarness : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly DbContextOptions<HelpdeskDbContext> dbOptions;
        private readonly ITenantContext tenant;

        private EndpointHarness(
            WebApplication app,
            HttpClient client,
            DbContextOptions<HelpdeskDbContext> dbOptions,
            ITenantContext tenant,
            INetclawPairingService pairingService)
        {
            this.app = app;
            Client = client;
            this.dbOptions = dbOptions;
            this.tenant = tenant;
            PairingService = pairingService;
        }

        public HttpClient Client { get; }
        public INetclawPairingService PairingService { get; }

        public static async Task<EndpointHarness> CreateAsync(
            string connectionString,
            ITenantContext tenant,
            DbCommandInterceptor interceptor)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HelpdeskDb"] = connectionString
            });
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(tenant);
            var dbOptionsBuilder = new DbContextOptionsBuilder<HelpdeskDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(interceptor);
            var dbOptions = dbOptionsBuilder.Options;
            builder.Services.AddDbContext<HelpdeskDbContext>(options =>
                options.UseNpgsql(connectionString).AddInterceptors(interceptor));
            var secrets = new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider(NullLoggerFactory.Instance));
            var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions()));
            builder.Services.AddSingleton<IAiAssistantChatRuntimeState>(runtime);
            builder.Services.AddScoped<IIntegrationProviderSettingsService>(services => new IntegrationProviderSettingsService(
                services.GetRequiredService<HelpdeskDbContext>(),
                services.GetRequiredService<IConfiguration>(),
                secrets,
                Options.Create(new AiAssistantChatOptions()),
                new DatabaseOptions { Provider = "PostgreSql" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance,
                runtimeState: runtime));

            var pairingService = Substitute.For<INetclawPairingService>();
            pairingService.ExchangeCodeAsync(Arg.Any<NetclawPairingTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult("synthetic-paired-device-token"));
            builder.Services.AddSingleton(pairingService);
            var chatClientFactory = Substitute.For<IAiAssistantChatClientFactory>();
            chatClientFactory.Create().Returns(new SuccessfulChatClient());
            builder.Services.AddSingleton(chatClientFactory);
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "AdminTest";
                options.DefaultChallengeScheme = "AdminTest";
            }).AddScheme<AuthenticationSchemeOptions, AdminTestAuthHandler>("AdminTest", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("HelpdeskAdmin", policy =>
            {
                policy.AddAuthenticationSchemes("AdminTest");
                policy.RequireAuthenticatedUser();
                policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin);
            }));

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapNetclawConnectivityEndpoints();
            await app.StartAsync();
            var client = app.GetTestClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            return new EndpointHarness(app, client, dbOptions, tenant, pairingService);
        }

        public HelpdeskDbContext CreateDbContext()
            => new(dbOptions, tenant, new HttpContextAccessor());

        public async Task SeedAsync(
            IReadOnlyCollection<AiAssistantChatConversation> conversations,
            IReadOnlyCollection<AiAssistantChatEvent> events)
        {
            await using var db = CreateDbContext();
            db.Set<AiAssistantChatConversation>().AddRange(conversations);
            db.Set<AiAssistantChatEvent>().AddRange(events);
            await db.SaveChangesAsync();
        }

        public void AuthorizeAsAdmin()
            => Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("AdminTest", "admin");

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class SuccessfulChatClient : IAiAssistantChatClient
    {
        public bool IsConnected => true;

        public Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> output, CancellationToken ct)
            => Task.FromResult(new SessionEnsureResult("synthetic-ensured-session", Created: false));

        public Task SendAsync(string sessionId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AdminTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var authorization) ||
                !string.Equals(authorization.Scheme, "AdminTest", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "synthetic-admin"),
                new Claim(ClaimTypes.Role, HelpdeskPermissions.HelpdeskAdmin)
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
