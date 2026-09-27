using System.Net;
using System.Text.Json;
using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class SavedDiagnosticSupersessionEndpointsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Saved_test_reports_superseded_profile_without_retrying_provider_or_recording_stale_success(bool netclaw, bool postgres)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"rateldesk-saved-diagnostic-{Guid.NewGuid():N}.db");
        PostgreSqlContainer? container = null;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("test");
        var protection = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
        var protector = new IntegrationProviderSecretProtector(protection);
        var diagnostic = new BlockingDiagnostic();
        var events = new CountingEvents();
        try
        {
            if (postgres)
            {
                container = new PostgreSqlBuilder("postgres:16").Build();
                await container.StartAsync();
            }
            var connectionString = postgres ? container!.GetConnectionString() : $"Data Source={databasePath}";
            var optionsBuilder = new DbContextOptionsBuilder<HelpdeskDbContext>();
            if (postgres) optionsBuilder.UseNpgsql(connectionString);
            else optionsBuilder.UseSqlite(connectionString);
            var options = optionsBuilder.Options;
            await using (var initializer = new HelpdeskDbContext(options, tenant, new HttpContextAccessor()))
            {
                await initializer.Database.EnsureCreatedAsync();
                if (netclaw)
                    initializer.NetclawConnectivitySettings.Add(new NetclawConnectivitySettings
                    {
                        Revision = 1,
                        Enabled = true,
                        Endpoint = "https://provider-a.example.test/hub/session",
                        ProtectedDeviceToken = protector.Protect("synthetic-device-token"),
                        ProfileFingerprint = "profile-a"
                    });
                else
                    initializer.M2MConnectivitySettings.Add(new M2MConnectivitySettings
                    {
                        Revision = 1,
                        ProfileFingerprint = "profile-a"
                    });
                await initializer.SaveChangesAsync();
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(tenant);
            builder.Services.AddDbContext<HelpdeskDbContext>(dbOptions =>
            {
                if (postgres) dbOptions.UseNpgsql(connectionString);
                else dbOptions.UseSqlite(connectionString);
            });
            builder.Services.AddScoped<IIntegrationProviderSettingsService>(services =>
                new IntegrationProviderSettingsService(
                    services.GetRequiredService<HelpdeskDbContext>(),
                    services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
                    protector,
                    Options.Create(new AiAssistantChatOptions()),
                    new DatabaseOptions { Provider = "PostgreSql" },
                    TimeProvider.System,
                    NullLogger<IntegrationProviderSettingsService>.Instance));
            builder.Services.AddSingleton<IOrchestrationConnectivityService>(diagnostic);
            builder.Services.AddSingleton<IAiAssistantChatClientFactory>(diagnostic);
            builder.Services.AddSingleton(Substitute.For<IOrchestrationCatalogService>());
            builder.Services.AddSingleton(Substitute.For<IAutomationBindingService>());
            builder.Services.AddSingleton(Substitute.For<IAutomationBindingSchemaSyncService>());
            builder.Services.AddSingleton(Substitute.For<IAutomationBindingDriftService>());
            builder.Services.AddSingleton(Substitute.For<IAutomationBindingImportService>());
            builder.Services.AddSingleton<IDomainEventPublisher>(events);
            var correlation = Substitute.For<ICorrelationContext>();
            correlation.GetCorrelationId().Returns("test-correlation");
            builder.Services.AddSingleton(correlation);
            builder.Services.AddAuthorization(authorization =>
                authorization.AddPolicy("HelpdeskAdmin", policy => policy.RequireAssertion(_ => true)));

            await using var app = builder.Build();
            app.UseAuthorization();
            app.MapNetclawConnectivityEndpoints();
            app.MapExternalOrchestrationEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var path = netclaw ? "/api/v1/admin/netclaw/test" : "/api/v1/admin/orchestration/test";
            var request = client.PostAsync(path, null);
            try
            {
                await diagnostic.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await using var editor = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
                if (netclaw)
                {
                    var current = await editor.NetclawConnectivitySettings.SingleAsync();
                    current.Revision = 2;
                    current.ProfileFingerprint = "profile-b";
                }
                else
                {
                    var current = await editor.M2MConnectivitySettings.SingleAsync();
                    current.Revision = 2;
                    current.ProfileFingerprint = "profile-b";
                }
                await editor.SaveChangesAsync();
            }
            finally
            {
                diagnostic.Release.TrySetResult();
            }

            using var response = await request.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("diagnostic_superseded", body.RootElement.GetProperty("code").GetString());
            Assert.False(body.RootElement.TryGetProperty("success", out _));
            Assert.Equal(1, diagnostic.Calls);
            Assert.Equal(0, events.Published);

            await using var verification = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            var audit = Assert.Single(await verification.ActivityLogs.ToListAsync());
            Assert.Contains("Superseded=True", audit.Message, StringComparison.Ordinal);
            if (netclaw)
            {
                var current = await verification.NetclawConnectivitySettings.SingleAsync();
                Assert.Equal(2, current.Revision);
                Assert.Null(current.LastTestedAtUtc);
                Assert.Null(current.LastTestSucceeded);
            }
            else
            {
                var current = await verification.M2MConnectivitySettings.SingleAsync();
                Assert.Equal(2, current.Revision);
                Assert.Null(current.LastTestedAtUtc);
                Assert.Null(current.LastTestSucceeded);
            }
        }
        finally
        {
            if (container is not null) await container.DisposeAsync();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class BlockingDiagnostic : IOrchestrationConnectivityService, IAiAssistantChatClientFactory
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAiAssistantChatClient Create() => new BlockingClient(this);

        public Task<OrchestrationConnectivitySettingsDto> GetOrchestrationSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OrchestrationConnectivityTestResultDto> TestOrchestrationConnectivityAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<OrchestrationConnectivityTestResultDto> TestOrchestrationConnectivityAsync(
            OrchestrationResolvedSettings settings,
            CancellationToken cancellationToken = default,
            bool useTokenCache = true)
        {
            await WaitAsync(cancellationToken);
            return new OrchestrationConnectivityTestResultDto { Success = true, Message = "Synthetic diagnostic succeeded." };
        }

        public Task<OrchestrationResolvedSettings> GetResolvedOrchestrationSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class BlockingClient(BlockingDiagnostic diagnostic) : IAiAssistantChatClient
    {
        public bool IsConnected => true;

        public async Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> output, CancellationToken ct)
        {
            await diagnostic.WaitAsync(ct);
            return new SessionEnsureResult("synthetic-session", true);
        }

        public Task SendAsync(string sessionId, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingEvents : IDomainEventPublisher
    {
        private int published;
        public int Published => Volatile.Read(ref published);

        public Task PublishAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            Interlocked.Increment(ref published);
            return Task.CompletedTask;
        }
    }
}
