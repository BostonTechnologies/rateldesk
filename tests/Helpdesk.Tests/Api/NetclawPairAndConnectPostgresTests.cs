using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
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
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class NetclawPairAndConnectPostgresTests
{
    private const string PairAndSavePath = "/api/v1/admin/netclaw/pair-and-save";
    private const string DaemonAddress = "http://10.99.10.129:5199";
    private const string CanonicalEndpoint = DaemonAddress + "/hub/session";
    private const string PairingCode = "synthetic-beta6-one-time-code";
    private const string DeviceToken = "synthetic-beta6-paired-device-token";

    [Fact]
    public async Task First_run_pairs_the_daemon_root_saves_applies_and_authenticates_before_returning_success()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var harness = await PairAndConnectHarness.CreateAsync(timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, PairAndSavePath)
        {
            Content = JsonContent.Create(new
            {
                pairingCode = PairingCode,
                expectedRevision = 0,
                endpoint = DaemonAddress
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Test", "admin");

        using var response = await harness.Client.SendAsync(request, timeout.Token);
        var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = JsonSerializer.Deserialize<NetclawConnectivitySettingsDto>(responseBody, JsonSerializerOptions.Web);
        Assert.NotNull(profile);
        Assert.True(profile.Enabled);
        Assert.True(profile.RuntimeSupported);
        Assert.True(profile.AllowPrivateHttp);
        Assert.Equal(CanonicalEndpoint, profile.Endpoint);
        Assert.Equal(1, profile.Revision);
        Assert.True(profile.HasDeviceToken);
        Assert.True(profile.LastTestSucceeded);
        Assert.NotNull(profile.LastAppliedAtUtc);
        Assert.NotNull(profile.LastTestedAtUtc);
        Assert.DoesNotContain(PairingCode, responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceToken, responseBody, StringComparison.Ordinal);

        await using var db = harness.CreateDbContext();
        var stored = await db.NetclawConnectivitySettings.AsNoTracking().SingleAsync(timeout.Token);
        Assert.Equal(CanonicalEndpoint, stored.Endpoint);
        Assert.True(stored.Enabled);
        Assert.True(stored.AllowPrivateHttp);
        Assert.Equal(1, stored.Revision);
        Assert.NotNull(stored.LastAppliedAtUtc);
        Assert.True(stored.LastTestSucceeded);
        var protectedToken = stored.ProtectedDeviceToken
            ?? throw new InvalidOperationException("The first-run pair did not persist a protected device token.");
        Assert.NotEqual(DeviceToken, protectedToken);
        Assert.Equal(DeviceToken, harness.SecretProtector.Unprotect(protectedToken));
        Assert.DoesNotContain(protectedToken, responseBody, StringComparison.Ordinal);

        Assert.Equal(CanonicalEndpoint, harness.Runtime.Current.Endpoint);
        Assert.True(harness.Runtime.Current.Enabled);
        Assert.Equal(1, harness.Runtime.Current.Revision);
        var clientSnapshot = Assert.Single(harness.ClientFactory.CreatedSnapshots);
        Assert.Equal(CanonicalEndpoint, clientSnapshot.Endpoint);
        Assert.Equal(1, clientSnapshot.Revision);
        Assert.True(clientSnapshot.Enabled);

        var exchange = Assert.Single(harness.Observations.PairingExchanges);
        Assert.Equal("POST", exchange.Method);
        Assert.Equal(DaemonAddress + "/api/pair/exchange", exchange.RequestUri);
        Assert.Equal(PairingCode, exchange.PairingCode);

        var ensureSession = Assert.Single(harness.Observations.HubCalls,
            call => string.Equals(call.Method, "EnsureSession", StringComparison.Ordinal));
        Assert.Equal("paired-device", ensureSession.User);
        Assert.Equal("signalr", ensureSession.Protocol);
        Assert.Equal(CanonicalEndpoint, ensureSession.RequestUri);
        Assert.Contains(harness.Observations.SignalRRequests,
            request => request.Path.StartsWith("/hub/session", StringComparison.Ordinal) &&
                       request.Authorization == $"Bearer {DeviceToken}");

        var connectionAttempts = harness.Observations.ConnectionAttempts.ToArray();
        Assert.NotEmpty(connectionAttempts);
        Assert.All(connectionAttempts, attempt =>
        {
            Assert.Equal(IPAddress.Parse("10.99.10.129"), attempt.Address);
            Assert.Equal(5199, attempt.TargetPort);
        });

        Assert.Equal(1, harness.ClientFactory.DisposalCount);
        Assert.True(harness.ClientFactory.DisposalSequence < harness.Observations.PairResponseSequence);
        Assert.Equal((int)HttpStatusCode.OK, harness.Observations.PairResponseStatus);
        await harness.Observations.HubDisconnected.Task.WaitAsync(timeout.Token);
        Assert.True(harness.Observations.HubDisconnectSequence > 0);

        var audit = Assert.Single(await db.ActivityLogs.AsNoTracking().ToListAsync(timeout.Token));
        Assert.DoesNotContain(PairingCode, audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceToken, audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(protectedToken, audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PairingCode, harness.Logs.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceToken, harness.Logs.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain(protectedToken, harness.Logs.Combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_beta3_sessions_require_review_before_exchange_and_are_bound_without_losing_transcripts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var harness = await PairAndConnectHarness.CreateAsync(timeout.Token);
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "admin");
        var first = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "beta3-ticket-a",
            TicketType = "incidents",
            AiAssistantSessionId = "beta3-remote-session-a",
            ProviderProfileFingerprint = null
        };
        var second = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "beta3-ticket-b",
            TicketType = "incidents",
            AiAssistantSessionId = "beta3-remote-session-b",
            ProviderProfileFingerprint = null
        };
        var transcripts = new[]
        {
            new AiAssistantChatEvent
            {
                ConversationId = first.Id,
                Sequence = 1,
                Type = "user",
                Text = "Synthetic beta.3 transcript A"
            },
            new AiAssistantChatEvent
            {
                ConversationId = second.Id,
                Sequence = 1,
                Type = "user",
                Text = "Synthetic beta.3 transcript B"
            }
        };
        await using (var seedDb = harness.CreateDbContext())
        {
            seedDb.Set<AiAssistantChatConversation>().AddRange(first, second);
            seedDb.Set<AiAssistantChatEvent>().AddRange(transcripts);
            await seedDb.SaveChangesAsync(timeout.Token);
        }

        using var reviewRequest = new HttpRequestMessage(HttpMethod.Post, PairAndSavePath)
        {
            Content = JsonContent.Create(new
            {
                pairingCode = PairingCode,
                expectedRevision = 0,
                endpoint = DaemonAddress
            })
        };
        reviewRequest.Headers.Authorization = new AuthenticationHeaderValue("Test", "admin");
        using var reviewResponse = await harness.Client.SendAsync(reviewRequest, timeout.Token);
        var reviewBody = await reviewResponse.Content.ReadAsStringAsync(timeout.Token);

        Assert.Equal(HttpStatusCode.Conflict, reviewResponse.StatusCode);
        Assert.DoesNotContain(PairingCode, reviewBody, StringComparison.Ordinal);
        var review = JsonSerializer.Deserialize<NetclawLegacySessionReviewConflictDto>(reviewBody, JsonSerializerOptions.Web);
        Assert.NotNull(review);
        Assert.Equal("legacy_ownership_confirmation_required", review.Code);
        Assert.Equal(CanonicalEndpoint, review.CanonicalEndpoint);
        Assert.Equal(DaemonAddress, review.DaemonAddress);
        Assert.Equal(2, review.LegacyConversationCount);
        Assert.False(string.IsNullOrWhiteSpace(review.LegacyOwnershipReviewToken));
        Assert.Empty(harness.Observations.PairingExchanges);
        Assert.Empty(harness.Observations.HubCalls);

        await using (var unreviewedDb = harness.CreateDbContext())
        {
            Assert.Empty(await unreviewedDb.NetclawConnectivitySettings.AsNoTracking().ToListAsync(timeout.Token));
            Assert.Empty(await unreviewedDb.ActivityLogs.AsNoTracking().ToListAsync(timeout.Token));
            var unbound = await unreviewedDb.Set<AiAssistantChatConversation>()
                .AsNoTracking()
                .OrderBy(conversation => conversation.TicketId)
                .ToListAsync(timeout.Token);
            Assert.Equal(new[] { first.Id, second.Id }, unbound.Select(conversation => conversation.Id).ToArray());
            Assert.All(unbound, conversation => Assert.Null(conversation.ProviderProfileFingerprint));
        }

        using var confirmedRequest = new HttpRequestMessage(HttpMethod.Post, PairAndSavePath)
        {
            Content = JsonContent.Create(new
            {
                pairingCode = PairingCode,
                expectedRevision = 0,
                endpoint = DaemonAddress,
                legacyOwnershipReviewToken = review.LegacyOwnershipReviewToken
            })
        };
        confirmedRequest.Headers.Authorization = new AuthenticationHeaderValue("Test", "admin");
        using var confirmedResponse = await harness.Client.SendAsync(confirmedRequest, timeout.Token);
        var confirmedBody = await confirmedResponse.Content.ReadAsStringAsync(timeout.Token);

        Assert.Equal(HttpStatusCode.OK, confirmedResponse.StatusCode);
        var profile = JsonSerializer.Deserialize<NetclawConnectivitySettingsDto>(confirmedBody, JsonSerializerOptions.Web);
        Assert.NotNull(profile);
        Assert.Equal(CanonicalEndpoint, profile.Endpoint);
        Assert.Equal(1, profile.Revision);
        Assert.True(profile.Enabled);
        Assert.True(profile.RuntimeSupported);
        Assert.True(profile.AllowPrivateHttp);
        Assert.True(profile.LastTestSucceeded);
        Assert.DoesNotContain(PairingCode, confirmedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceToken, confirmedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(review.LegacyOwnershipReviewToken, confirmedBody, StringComparison.Ordinal);

        Assert.Equal(CanonicalEndpoint, harness.Runtime.Current.Endpoint);
        Assert.True(harness.Runtime.Current.Enabled);
        Assert.Equal(1, harness.Runtime.Current.Revision);
        var clientSnapshot = Assert.Single(harness.ClientFactory.CreatedSnapshots);
        Assert.Equal(CanonicalEndpoint, clientSnapshot.Endpoint);
        Assert.Equal(1, clientSnapshot.Revision);
        var ensureSession = Assert.Single(harness.Observations.HubCalls,
            call => string.Equals(call.Method, "EnsureSession", StringComparison.Ordinal));
        Assert.Equal("paired-device", ensureSession.User);
        Assert.Equal("signalr", ensureSession.Protocol);
        Assert.Equal(CanonicalEndpoint, ensureSession.RequestUri);
        var exchange = Assert.Single(harness.Observations.PairingExchanges);
        Assert.Equal(DaemonAddress + "/api/pair/exchange", exchange.RequestUri);
        Assert.Equal(PairingCode, exchange.PairingCode);
        Assert.Equal(1, harness.ClientFactory.DisposalCount);
        Assert.True(harness.ClientFactory.DisposalSequence < harness.Observations.PairResponseSequence);
        Assert.Equal((int)HttpStatusCode.OK, harness.Observations.PairResponseStatus);
        await harness.Observations.HubDisconnected.Task.WaitAsync(timeout.Token);

        await using var db = harness.CreateDbContext();
        var savedConversations = await db.Set<AiAssistantChatConversation>()
            .AsNoTracking()
            .OrderBy(conversation => conversation.TicketId)
            .ToListAsync(timeout.Token);
        Assert.Equal(new[] { first.Id, second.Id }, savedConversations.Select(conversation => conversation.Id).ToArray());
        Assert.Equal(new[] { "beta3-remote-session-a", "beta3-remote-session-b" },
            savedConversations.Select(conversation => conversation.AiAssistantSessionId).ToArray());
        Assert.All(savedConversations,
            conversation => Assert.Equal(profile.ProfileFingerprint, conversation.ProviderProfileFingerprint));

        var expectedTranscripts = transcripts.OrderBy(item => item.ConversationId).ToArray();
        var savedTranscripts = await db.Set<AiAssistantChatEvent>()
            .AsNoTracking()
            .OrderBy(item => item.ConversationId)
            .ToListAsync(timeout.Token);
        Assert.Equal(expectedTranscripts.Select(item => item.Id).ToArray(), savedTranscripts.Select(item => item.Id).ToArray());
        Assert.Equal(expectedTranscripts.Select(item => item.ConversationId).ToArray(), savedTranscripts.Select(item => item.ConversationId).ToArray());
        Assert.Equal(expectedTranscripts.Select(item => item.Sequence).ToArray(), savedTranscripts.Select(item => item.Sequence).ToArray());
        Assert.Equal(expectedTranscripts.Select(item => item.Type).ToArray(), savedTranscripts.Select(item => item.Type).ToArray());
        Assert.Equal(expectedTranscripts.Select(item => item.Text).ToArray(), savedTranscripts.Select(item => item.Text).ToArray());

        var audit = Assert.Single(await db.ActivityLogs.AsNoTracking().ToListAsync(timeout.Token));
        Assert.Equal("synthetic-admin", audit.UserId);
        Assert.Contains("BoundConversations=2", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PairingCode, audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceToken, audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(review.LegacyOwnershipReviewToken, audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PairingCode, harness.Logs.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceToken, harness.Logs.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain(review.LegacyOwnershipReviewToken, harness.Logs.Combined, StringComparison.Ordinal);
    }

    internal sealed class PairAndConnectHarness : IAsyncDisposable
    {
        private readonly DbContextOptions<HelpdeskDbContext> dbOptions;
        private readonly ITenantContext tenant;

        private PairAndConnectHarness(
            PostgreSqlContainer postgres,
            WebApplication daemon,
            WebApplication api,
            HttpClient client,
            HttpClient pairingHttpClient,
            DbContextOptions<HelpdeskDbContext> dbOptions,
            ITenantContext tenant,
            IntegrationProviderSecretProtector secretProtector,
            AiAssistantChatRuntimeState runtime,
            TrackedRuntimeClientFactory clientFactory,
            DaemonObservations observations,
            CapturingLogProvider logs)
        {
            Postgres = postgres;
            Daemon = daemon;
            Api = api;
            Client = client;
            PairingHttpClient = pairingHttpClient;
            this.dbOptions = dbOptions;
            this.tenant = tenant;
            SecretProtector = secretProtector;
            Runtime = runtime;
            ClientFactory = clientFactory;
            Observations = observations;
            Logs = logs;
        }

        public PostgreSqlContainer Postgres { get; }
        public WebApplication Daemon { get; }
        public WebApplication Api { get; }
        public HttpClient Client { get; }
        public HttpClient PairingHttpClient { get; }
        public IntegrationProviderSecretProtector SecretProtector { get; }
        public AiAssistantChatRuntimeState Runtime { get; }
        public TrackedRuntimeClientFactory ClientFactory { get; }
        public DaemonObservations Observations { get; }
        public CapturingLogProvider Logs { get; }

        public static async Task<PairAndConnectHarness> CreateAsync(CancellationToken cancellationToken)
        {
            var postgres = new PostgreSqlBuilder("postgres:16").Build();
            WebApplication? daemon = null;
            WebApplication? api = null;
            HttpClient? pairingHttpClient = null;
            HttpClient? client = null;
            try
            {
                await postgres.StartAsync(cancellationToken);
                var observations = new DaemonObservations();
                var daemonBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
                daemonBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                daemonBuilder.Services.AddSingleton(observations);
                daemonBuilder.Services.AddSignalR();
                daemonBuilder.Services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Device";
                        options.DefaultChallengeScheme = "Device";
                    })
                    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>("Device", _ => { });
                daemonBuilder.Services.AddAuthorization();

                daemon = daemonBuilder.Build();
                daemon.UseAuthentication();
                daemon.UseAuthorization();
                daemon.UseWebSockets();
                daemon.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments("/hub/session"))
                    {
                        observations.SignalRRequests.Enqueue(new SignalRRequestObservation(
                            context.Request.Path.ToString() + context.Request.QueryString.ToString(),
                            context.Request.Headers.Authorization.ToString()));
                    }

                    await next();
                });
                daemon.MapPost("/api/pair/exchange", async context =>
                {
                    using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                    var code = body.RootElement.GetProperty("code").GetString()
                        ?? throw new InvalidOperationException("The pairing exchange did not include a code.");
                    observations.PairingExchanges.Enqueue(new PairingExchangeObservation(
                        context.Request.Method,
                        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}",
                        code));
                    await context.Response.WriteAsJsonAsync(new { token = DeviceToken }, context.RequestAborted);
                });
                daemon.MapHub<AuthenticatedNetclawHub>("/hub/session").RequireAuthorization();
                await daemon.StartAsync(cancellationToken);

                var daemonAddress = daemon.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                    .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?
                    .Addresses.SingleOrDefault()
                    ?? throw new InvalidOperationException("The test daemon did not publish a Kestrel address.");
                var localPort = new Uri(daemonAddress).Port;
                var hooks = new IntegrationConnectionHooks(
                    (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("10.99.10.129")]),
                    async (address, port, ct) =>
                    {
                        observations.ConnectionAttempts.Enqueue(new ConnectionAttempt(address, port));
                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, localPort), ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    });

                pairingHttpClient = new HttpClient(IntegrationSafeHttpMessageHandler.CreateCore(
                    allowPrivateHttp: false,
                    protocolEndpoint: null,
                    hooks));
                var pairingService = new NetclawPairingService(pairingHttpClient);
                var secretProtector = new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider(NullLoggerFactory.Instance));
                var connectionString = postgres.GetConnectionString();
                var dbOptions = new DbContextOptionsBuilder<HelpdeskDbContext>().UseNpgsql(connectionString).Options;
                var tenant = Substitute.For<ITenantContext>();
                tenant.TenantId.Returns("synthetic-tenant");
                tenant.UserId.Returns("synthetic-admin");
                tenant.IsHelpdeskAdmin.Returns(true);
                await using (var initializer = new HelpdeskDbContext(dbOptions, tenant, new HttpContextAccessor()))
                    await initializer.Database.EnsureCreatedAsync(cancellationToken);

                var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions()));
                var clientFactory = new TrackedRuntimeClientFactory(runtime, hooks, observations);
                var logs = new CapturingLogProvider();
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:HelpdeskDb"] = connectionString
                    })
                    .Build();
                var apiBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
                apiBuilder.WebHost.UseTestServer();
                apiBuilder.Logging.ClearProviders();
                apiBuilder.Logging.SetMinimumLevel(LogLevel.Trace);
                apiBuilder.Logging.AddProvider(logs);
                apiBuilder.Services.AddHttpContextAccessor();
                apiBuilder.Services.AddSingleton(tenant);
                apiBuilder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseNpgsql(connectionString));
                apiBuilder.Services.AddSingleton<IAiAssistantChatRuntimeState>(runtime);
                apiBuilder.Services.AddSingleton<IAiAssistantChatClientFactory>(clientFactory);
                apiBuilder.Services.AddSingleton<INetclawPairingService>(pairingService);
                apiBuilder.Services.AddScoped<IIntegrationProviderSettingsService>(services =>
                    new IntegrationProviderSettingsService(
                        services.GetRequiredService<HelpdeskDbContext>(),
                        configuration,
                        secretProtector,
                        Options.Create(new AiAssistantChatOptions()),
                        new DatabaseOptions { Provider = "PostgreSql" },
                        TimeProvider.System,
                        services.GetRequiredService<ILogger<IntegrationProviderSettingsService>>(),
                        runtimeState: runtime));
                apiBuilder.Services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                    })
                    .AddScheme<AuthenticationSchemeOptions, AdministratorAuthenticationHandler>("Test", _ => { });
                apiBuilder.Services.AddAuthorization(options => options.AddPolicy("HelpdeskAdmin", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin);
                }));

                api = apiBuilder.Build();
                api.UseAuthentication();
                api.UseAuthorization();
                api.Use(async (context, next) =>
                {
                    await next();
                    if (string.Equals(context.Request.Path.Value, PairAndSavePath, StringComparison.Ordinal))
                        observations.MarkPairResponse((int)context.Response.StatusCode);
                });
                api.MapNetclawConnectivityEndpoints();
                await api.StartAsync(cancellationToken);
                client = api.GetTestClient();
                client.Timeout = TimeSpan.FromSeconds(45);

                return new PairAndConnectHarness(
                    postgres,
                    daemon,
                    api,
                    client,
                    pairingHttpClient,
                    dbOptions,
                    tenant,
                    secretProtector,
                    runtime,
                    clientFactory,
                    observations,
                    logs);
            }
            catch
            {
                client?.Dispose();
                if (api is not null) await api.DisposeAsync();
                pairingHttpClient?.Dispose();
                if (daemon is not null) await daemon.DisposeAsync();
                await postgres.DisposeAsync();
                throw;
            }
        }

        public HelpdeskDbContext CreateDbContext()
            => new(dbOptions, tenant, new HttpContextAccessor());

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await Api.StopAsync();
            }
            finally
            {
                await Api.DisposeAsync();
            }

            PairingHttpClient.Dispose();
            try
            {
                await Daemon.StopAsync();
            }
            finally
            {
                await Daemon.DisposeAsync();
            }

            await Postgres.DisposeAsync();
        }
    }

    internal sealed class DaemonObservations
    {
        private long sequence;
        private long pairResponseSequence;
        private long hubDisconnectSequence;
        private int pairResponseStatus;

        public ConcurrentQueue<PairingExchangeObservation> PairingExchanges { get; } = new();
        public ConcurrentQueue<SignalRRequestObservation> SignalRRequests { get; } = new();
        public ConcurrentQueue<HubCallObservation> HubCalls { get; } = new();
        public ConcurrentQueue<ConnectionAttempt> ConnectionAttempts { get; } = new();
        public TaskCompletionSource HubDisconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long PairResponseSequence => Volatile.Read(ref pairResponseSequence);
        public long HubDisconnectSequence => Volatile.Read(ref hubDisconnectSequence);
        public int PairResponseStatus => Volatile.Read(ref pairResponseStatus);

        public void MarkPairResponse(int status)
        {
            Volatile.Write(ref pairResponseStatus, status);
            Interlocked.Exchange(ref pairResponseSequence, Interlocked.Increment(ref sequence));
        }

        public void MarkHubDisconnected()
        {
            Interlocked.Exchange(ref hubDisconnectSequence, Interlocked.Increment(ref sequence));
            HubDisconnected.TrySetResult();
        }

        public long NextSequence() => Interlocked.Increment(ref sequence);
    }

    internal sealed class TrackedRuntimeClientFactory(
        AiAssistantChatRuntimeState runtime,
        IntegrationConnectionHooks hooks,
        DaemonObservations observations)
        : IAiAssistantChatClientFactory, IAiAssistantChatRuntimeClientFactory
    {
        private readonly ConcurrentQueue<AiAssistantChatRuntimeSnapshot> snapshots = new();
        private int disposalCount;
        private long disposalSequence;

        public IReadOnlyCollection<AiAssistantChatRuntimeSnapshot> CreatedSnapshots => snapshots.ToArray();
        public int DisposalCount => Volatile.Read(ref disposalCount);
        public long DisposalSequence => Volatile.Read(ref disposalSequence);

        public IAiAssistantChatClient Create() => Create(runtime.Current);

        public IAiAssistantChatClient Create(AiAssistantChatRuntimeSnapshot snapshot)
        {
            snapshots.Enqueue(snapshot);
            return new TrackedClient(new AiAssistantSignalRChatClient(snapshot, hooks), this);
        }

        private void RecordDisposal()
        {
            Interlocked.Increment(ref disposalCount);
            Interlocked.Exchange(ref disposalSequence, observations.NextSequence());
        }

        private sealed class TrackedClient(AiAssistantSignalRChatClient client, TrackedRuntimeClientFactory owner)
            : IAiAssistantChatClient
        {
            public bool IsConnected => client.IsConnected;

            public Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> output, CancellationToken ct)
                => client.ConnectAsync(sessionId, output, ct);

            public Task SendAsync(string sessionId, string text, CancellationToken ct)
                => client.SendAsync(sessionId, text, ct);

            public Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct)
                => client.RespondAsync(sessionId, callId, key, ct);

            public async ValueTask DisposeAsync()
            {
                await client.DisposeAsync();
                owner.RecordDisposal();
            }
        }
    }

    private sealed class AuthenticatedNetclawHub(DaemonObservations observations) : Hub
    {
        public Task<SessionEnsureResult> EnsureSession(string? sessionId, string protocol)
        {
            var request = Context.GetHttpContext()?.Request;
            observations.HubCalls.Enqueue(new HubCallObservation(
                "EnsureSession",
                Context.User?.Identity?.Name ?? string.Empty,
                protocol,
                request is null ? string.Empty : $"{request.Scheme}://{request.Host}{request.Path}"));
            return Task.FromResult(new SessionEnsureResult("authenticated-daemon-session", sessionId is null));
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            observations.MarkHubDisconnected();
            return base.OnDisconnectedAsync(exception);
        }
    }

    private sealed class DeviceTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorization) ||
                !string.Equals(authorization.ToString(), $"Bearer {DeviceToken}", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.Fail("Invalid Netclaw device token."));

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "paired-device"),
                    new Claim(ClaimTypes.Name, "paired-device")
                ],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class AdministratorAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var authorization) ||
                !string.Equals(authorization.Scheme, "Test", StringComparison.Ordinal) ||
                !string.Equals(authorization.Parameter, "admin", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "synthetic-admin"),
                    new Claim(ClaimTypes.Name, "synthetic-admin"),
                    new Claim(ClaimTypes.Role, HelpdeskPermissions.HelpdeskAdmin)
                ],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    internal sealed class CapturingLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> entries = new();

        public string Combined => string.Join(Environment.NewLine, entries);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue($"{categoryName}: {formatter(state, exception)}");
        }
    }

    internal sealed record PairingExchangeObservation(string Method, string RequestUri, string PairingCode);
    internal sealed record SignalRRequestObservation(string Path, string Authorization);
    internal sealed record HubCallObservation(string Method, string User, string Protocol, string RequestUri);
    internal sealed record ConnectionAttempt(IPAddress Address, int TargetPort);
}
