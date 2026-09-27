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
    private const string ConfirmOwnerPath = "/api/v1/admin/netclaw/legacy-sessions/confirm-owner";

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

        private EndpointHarness(SqliteConnection connection, WebApplication app, HttpClient client, ITenantContext tenant, IAiAssistantChatClientFactory clientFactory)
        {
            this.connection = connection;
            this.app = app;
            Client = client;
            this.tenant = tenant;
            ClientFactory = clientFactory;
        }

        public HttpClient Client { get; }
        public IAiAssistantChatClientFactory ClientFactory { get; }

        public static async Task<EndpointHarness> CreateAsync(
            CancellationToken cancellationToken,
            bool deploymentAllowsPrivateHttp = false)
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
            builder.Services.AddScoped<IIntegrationProviderSettingsService>(services => new IntegrationProviderSettingsService(
                services.GetRequiredService<HelpdeskDbContext>(),
                services.GetRequiredService<IConfiguration>(),
                new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
                Options.Create(new AiAssistantChatOptions()),
                new DatabaseOptions { Provider = "Sqlite" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance));
            var clientFactory = Substitute.For<IAiAssistantChatClientFactory>();
            builder.Services.AddSingleton(clientFactory);
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
            return new EndpointHarness(connection, app, client, tenant, clientFactory);
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
